using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace Goodgulf.Building
{
    /// <summary>
    /// Handles all player input related to building:
    /// selecting build templates, positioning, snapping, rotating,
    /// placing, destroying, and terrain modification.
    /// </summary>
    public class BuilderInput : MonoBehaviour
    {
        // -------------------------
        // Debug
        // -------------------------

        [Header("Debug Mode")]
        [SerializeField]
        private bool _debugEnabled = false;

        [SerializeField]
        private GameObject debugPrefab;

        // -------------------------
        // References
        // -------------------------

        [Header("References")]
        [SerializeField]
        private GameObject _player;

        // -------------------------
        // Layer Masks
        // -------------------------

        [Header("Layers")]
        [SerializeField]
        private LayerMask _buildItemsOnly;

        [SerializeField]
        private LayerMask ignoreLayersDrag;

        [SerializeField]
        private LayerMask ignoreLayersDrop;

        [SerializeField]
        private LayerMask raycastLayersDrag;

        [SerializeField]
        private LayerMask raycastLayersDrop;

        // -------------------------
        // Placement Offsets & Rotation
        // -------------------------

        [Header("Offsets and Rotations")]
        [SerializeField]
        private float yOffset = 0.005f;

        // Rotation steps cycled when rotating build previews
        [SerializeField]
        private float[] rotationSteps = { 30f, 15f, 15f, 30f };

        [SerializeField]
        private float gridSize = 0.1f;

        private int rotationStepIndex = 0;

        [SerializeField]
        private float range;

        // -------------------------
        // Snapping Options
        // -------------------------

        [Header("Snapping")]
        [SerializeField]
        private bool _snapToGrid = true;

        [SerializeField]
        private bool _snapPoints = true;

        [SerializeField]
        private bool _snapMagnetics = true;

        // -------------------------
        // Timers & Update Intervals
        // -------------------------

        [Header("Timings")]
        [SerializeField]
        private float _updateDistanceTime = 0.999f;

        [SerializeField]
        private float _updateDistanceRange = 1.0f;

        [SerializeField]
        private float updateTime = 0.1f;

        [SerializeField]
        private float magneticSnapTime = 2.0f;

        [SerializeField]
        private float magneticSnapTimeDelay = 2.0f;

        private float _updateTimer = 0.0f;
        private float _updateDistanceTimer = 0.0f;

        // -------------------------
        // Runtime State
        // -------------------------

        private Camera _mainCamera;
        private bool _building = false;

        private BuildTemplate _selectedBuildTemplate;
        private GameObject instantiatedTransparentPrefab;

        // Prevents rotation unless modifier key is held
        private bool _noRotation = false;

        // Index of currently selected snap point
        private int _snapPointIndex = 0;

        // Magnetic snapping cooldown tracking
        private bool _magneticSnapDone = false;
        private float _mageneticSnapTimer = 0f;

        private PlayerBuilderInventoryBridge _playerBuilderInventoryBridge;

        // Ensure the builder does not start before Initialization
        private bool _initialized = false;
        
        // Singleton instance
        public static BuilderInput Instance { get; private set; }

        /// <summary>
        /// Enforces singleton pattern and caches main camera.
        /// </summary>
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            
        }

        /// <summary>
        /// Initializes builder with player reference and inventory bridge.
        /// </summary>
        public void Start()
        {
/*
            if (_player)
            {
                Builder.Instance.SetPlayer(_player);
                _playerBuilderInventoryBridge = Builder.Instance.GetPlayerBuilderInventoryBridge();
            }
            else
            {
                Debug.LogError("BuilderInput.Start(): Player not assigned to property.");
            }
*/
        }

        public void Initialize()
        {
            Debug.Log("<color=yellow>BuilderInput.Initialize(): Invoked</color>");
            
            _playerBuilderInventoryBridge = Builder.Instance.GetPlayerBuilderInventoryBridge();
            
            _mainCamera = Camera.main;
            
            _initialized = true;            
        }
        
        
        /// <summary>
        /// Handles preview movement, snapping logic, magnetic snapping,
        /// and periodic distance display updates.
        /// </summary>
        public void Update()
        {
            if (!_initialized)
                return;
            
            _updateTimer += Time.deltaTime;
            _updateDistanceTimer += Time.deltaTime;

            // Magnetic snap cooldown timer
            if (_magneticSnapDone)
                _mageneticSnapTimer += Time.deltaTime;
            else
                _mageneticSnapTimer -= Time.deltaTime;

            if (_mageneticSnapTimer >= magneticSnapTime)
                _magneticSnapDone = false;

            // Periodically show distance indicators on nearby constructed items
            if (_updateDistanceTimer > _updateDistanceTime && !_debugEnabled)
            {
                _updateDistanceTimer = 0;

                bool hit;
                Vector3 pos = WhereIsTheCursor(ignoreLayersDrag, raycastLayersDrag, out hit);

                if (hit)
                {
                    Collider[] hitColliders = Physics.OverlapSphere(
                        pos,
                        _updateDistanceRange,
                        _buildItemsOnly,
                        QueryTriggerInteraction.Ignore
                    );

                    foreach (var col in hitColliders)
                    {
                        ConstructedItemBehaviour cib =
                            col.transform.parent.GetComponent<ConstructedItemBehaviour>();

                        if (cib != null)
                            cib.ShowDistance();
                    }
                }
            }

            // Update preview position and snapping
            if (_updateTimer > updateTime)
            {
                _updateTimer = 0;

                if (!instantiatedTransparentPrefab)
                    return;

                bool hit;
                Vector3 pos = WhereIsTheCursor(ignoreLayersDrag, raycastLayersDrag, out hit);

                // Magnetic snapping
                if (_snapMagnetics && !_magneticSnapDone && _mageneticSnapTimer < 0f)
                {
                    if (Builder.Instance.IsMagneticPointClose(
                        instantiatedTransparentPrefab,
                        out Vector3 distanceClosestPoint))
                    {
                        pos -= distanceClosestPoint;
                        instantiatedTransparentPrefab.transform.position -= distanceClosestPoint;

                        _magneticSnapDone = true;
                        _mageneticSnapTimer = 0f;
                    }
                }

                // Grid snapping (disabled while magnetically snapped)
                if (_snapToGrid && !_magneticSnapDone)
                    pos = SnapToGrid(pos);

                if (hit && !_magneticSnapDone)
                    instantiatedTransparentPrefab.transform.position = pos;
            }
        }

        
        /// <summary>
        /// Debug-only build action.
        /// Instantiates multiple building prefabs at the cursor position
        /// using the currently selected build template.
        /// </summary>
        public Vector3 OnDebugBuild()
        {
            if (!_initialized)
                return Vector3.zero;
            
            // Only react when the input action has just started
            // if (!context.started) return;

            Debug.Log("Debug Build");

            // If the recalculation worker is currently running,
            // warn and continue without blocking (debug behavior)
            if (RecalculationWorker.Instance.Running)
            {
                Debug.LogWarning("Debug Build: skip due to recalculation");
            }

            // Ensure we have a valid preview prefab and a selected build template
            if (instantiatedTransparentPrefab != null && _selectedBuildTemplate != null)
            {
                // Raycast from the cursor into the world to find a valid build position
                bool hit;
                Vector3 pos = WhereIsTheCursor(
                    ignoreLayersDrop,    // Layers to ignore
                    raycastLayersDrop,   // Layers allowed to be hit
                    out hit              // Whether a valid surface was hit
                );

                if (hit)
                {
                    // Optionally snap the build position to the grid
                    if (_snapToGrid)
                    {
                        pos = SnapToGrid(pos);
                    }

                    // Apply vertical offset to align the object correctly with the surface
                    pos.y = pos.y - yOffset;
                    // Alternative offset using build template settings:
                    // pos.y = pos.y - yOffset + _selectedBuildTemplate.buildYOffset;

                    // Instantiate the debug build prefab(s) at the calculated position
                    Builder.Instance.InstantiateBuildingPrefabDebug(
                        _selectedBuildTemplate.id,
                        pos,
                        instantiatedTransparentPrefab.transform.rotation
                    );

                    return pos;

                    // Note: The selected build template remains active
                    // to allow repeated debug placement
                }
            }

            // Required data for debug build is missing
            return Vector3.zero;
        }


        /// <summary>
        /// Handles input for depressing the terrain at the cursor position.
        /// Triggered when the assigned input action starts.
        /// </summary>
        public void OnModifyTerrainDown() // InputAction.CallbackContext context
        {
            if (!_initialized)
                return;
            
            // Only respond when the input action has just started
            // and ignore the input if a building action is already in progress
            // if (!context.started || _building) return;

            Debug.Log("OnModifyTerrainDown");

            // Perform a raycast from the cursor into the world to find a valid hit position
            bool hit;
            Vector3 pos = WhereIsTheCursor(
                ignoreLayersDrop,    // Layers to ignore during the raycast
                raycastLayersDrop,   // Layers that are allowed to be hit
                out hit              // Whether the raycast successfully hit something
            );

            // If the cursor raycast hit a valid surface,
            // depress the terrain at that world position
            if (hit)
            {
                Builder.Instance.DepressTerrainAtPosition(pos);
            }
            else
            {
                // Raycast did not hit any valid surface
                Debug.Log("no hit");
            }
        }


        /// <summary>
        /// Handles input for raising the terrain at the cursor position.
        /// Triggered when the assigned input action starts.
        /// </summary>
        public void OnModifyTerrainUp()
        {
            if (!_initialized)
                return;

            // Only react when the input action has just started
            // and ignore input while a building action is in progress
            // if (!context.started || _building) return;

            Debug.Log("OnModifyTerrainUp");

            // Raycast from the cursor into the world to find a valid terrain hit
            bool hit;
            Vector3 pos = WhereIsTheCursor(
                ignoreLayersDrop,    // Layers to ignore during raycast
                raycastLayersDrop,   // Layers allowed to be hit
                out hit              // Whether the raycast hit a valid surface
            );

            // If a valid hit was found, raise the terrain at that position
            if (hit)
            {
                Builder.Instance.RaiseTerrainAtPosition(pos);
            }
            else
            {
                // No valid raycast hit detected
                Debug.Log("no hit");
            }
        }

        /// <summary>
        /// Toggles snapping of build positions to the grid.
        /// </summary>
        public void OnToggleSnapToGrid()
        {
            if (!_initialized)
                return;

            // Only toggle when the input action starts
            // if (!context.started) return;

            // Flip grid snapping state
            _snapToGrid = !_snapToGrid;

            Debug.Log($"BuilderInput.OnSnapToGrid(): set to {_snapToGrid}");
        }

        /// <summary>
        /// Toggles snapping to predefined snap points on build prefabs.
        /// </summary>
        public void OnToggleSnapPoints()
        {
            if (!_initialized)
                return;

            // Only toggle when the input action starts
            // if (!context.started) return;

            // Flip snap-point snapping state
            _snapPoints = !_snapPoints;

            Debug.Log($"BuilderInput.OnToggleSnapPoints(): set to {_snapPoints}");
        }

        /// <summary>
        /// Toggles magnetic snapping to nearby magnetic points.
        /// </summary>
        public void OnToggleMagneticPoints()
        {
            if (!_initialized)
                return;

            // Only toggle when the input action starts
//             if (!context.started) return;

            // Flip magnetic snapping state
            _snapMagnetics = !_snapMagnetics;

            Debug.Log($"BuilderInput.OnToggleMagneticPoints(): set to {_snapMagnetics}");
        }


        
        
        /// <summary>
        /// Handles input for saving all current building areas to disk.
        /// </summary>
        public void OnSave()
        {
            if (!_initialized)
                return;
            
            // Only react when the input action has just started
            // and ignore save requests while building is in progress
            // if (!context.started || _building) return;

            Debug.Log("OnSave");

            // Persist all building areas and their constructed items
            Builder.Instance.SaveAllBuildingAreas();
        }

        /// <summary>
        /// Handles input for loading previously saved building areas from disk.
        /// </summary>
        public void OnLoad()
        {
            if (!_initialized)
                return;

            // Only react when the input action has just started
            // and ignore load requests while building is in progress
            // if (!context.started || _building) return;

            Debug.Log("OnLoad");

            // Load building areas and reconstruct their contents
            Builder.Instance.LoadBuildingArea();
        }

        /// <summary>
        /// Handles input for destructing constructed items at the cursor position.
        /// Uses the transparent preview prefab as the destruction volume.
        /// </summary>
        public void OnDestruct()
        {
            if (!_initialized)
                return;
            
            // Only react when the input action has just started
            // if (!context.started) return;

            Debug.Log("OnDestruct");

            // Warn if the recalculation worker is currently running
            // (destruction is allowed, but may cause delayed updates)
            if (RecalculationWorker.Instance.Running)
            {
                Debug.LogWarning("OnDestruct: skip due to recalculation");
            }

            // Ensure we have both a preview prefab and a selected build template
            if (instantiatedTransparentPrefab != null && _selectedBuildTemplate != null)
            {
                // Raycast from the cursor into the world to determine target position
                bool hit;
                Vector3 pos = WhereIsTheCursor(
                    ignoreLayersDrop,    // Layers to ignore during raycast
                    raycastLayersDrop,   // Layers allowed to be hit
                    out hit              // Whether a valid surface was hit
                );

                if (hit)
                {
                    // Optionally snap the destruction position to the grid
                    if (_snapToGrid)
                    {
                        pos = SnapToGrid(pos);
                    }

                    // Apply vertical offset to match build placement height
                    pos.y = pos.y - yOffset + _selectedBuildTemplate.buildYOffset;

                    // Destroy constructed items overlapping the preview prefab volume
                    Builder.Instance.DestructConstructedItemsAtPos(instantiatedTransparentPrefab);
                }
            }
            else
            {
                // Required data for destruction is missing
                return;
            }
        }

        
        
        /// <summary>
        /// Rounds a position to the nearest grid point.
        /// </summary>
        public Vector3 SnapToGrid(Vector3 position)
        {
            if (gridSize == 0.0f)
                return position;

            return new Vector3(
                Mathf.Round(position.x / gridSize) * gridSize,
                Mathf.Round(position.y / gridSize) * gridSize,
                Mathf.Round(position.z / gridSize) * gridSize
            );
        }

        // Place the build item at the cursor position
        public void OnClick()
        {
            if (!_initialized)
                return;

            // Check if the input action has started
            // if (!context.started) return;

            Debug.Log("OnClick");
            
            // Check if a recalculation is currently running and skip the click action if it is
            if (RecalculationWorker.Instance.Running)
            {
                Debug.LogWarning("OnClick: skip due to recalculation");
            }

            // Check if there is inventory left for the selected build template
            if (_selectedBuildTemplate != null)
            {
                if (!_playerBuilderInventoryBridge.InventoryLeft(_selectedBuildTemplate.id))
                {
                    Debug.LogWarning("BuilderInput.OnClick(): no inventory left");
                    return;
                }
            }

            // Verify if there is an instantiated prefab and a valid build template
            if (instantiatedTransparentPrefab != null && _selectedBuildTemplate != null)
            {
                // If a magnetic snap has been done, instantiate the building prefab at the current prefab position
                if (_magneticSnapDone)
                {
                    Builder.Instance.InstantiateBuildingPrefab(
                        _selectedBuildTemplate.id, 
                        instantiatedTransparentPrefab.transform.position,
                        instantiatedTransparentPrefab.transform.rotation, 
                        _snapPointIndex
                    );

                    // Consume one item from the player's inventory
                    _playerBuilderInventoryBridge.ConsumeItemAmount(_selectedBuildTemplate.id, 1);

                    // Reset the magnetic snap status and timer
                    _magneticSnapDone = false;
                    _mageneticSnapTimer = magneticSnapTime;
                }
                else
                {
                    // Determine where the cursor is pointing in the world while ignoring specific layers
                    bool hit;
                    Vector3 pos = WhereIsTheCursor(ignoreLayersDrop, raycastLayersDrop, out hit);

                    if (hit)
                    {
                        // Optionally snap the position to a grid
                        if (_snapToGrid)
                        {
                            pos = SnapToGrid(pos);
                        }

                        // Adjust the position based on a vertical offset
                        pos.y = pos.y - yOffset;
                        // Note: The commented-out line below suggests additional adjustment may be redundant now
                        // pos.y = pos.y - yOffset + _selectedBuildTemplate.buildYOffset; // this should not be needed with snappoints added to the code

                        // Check if the position is within a non-buildable area
                        if (Builder.Instance.IsInNonBuildableArea(pos))
                        {
                            Debug.Log("BuilderInput.OnClick(): <color=red>trying to build in non buildable area</color>");
                        }
                        else
                        {
                            // Instantiate the building prefab at the calculated position
                            Builder.Instance.InstantiateBuildingPrefab(
                                _selectedBuildTemplate.id, 
                                pos,
                                instantiatedTransparentPrefab.transform.rotation, 
                                _snapPointIndex
                            );

                            // Consume one item from the player's inventory
                            _playerBuilderInventoryBridge.ConsumeItemAmount(_selectedBuildTemplate.id, 1);
                        }
                        // The selected build item remains active to allow additional placements
                    }
                }
            }
            else return; // Exit if there is no instantiated prefab or selected build template

            // Uncomment the following line for debugging screen positions
            // Debug.Log(screenPosition);
        }

        // https://discussions.unity.com/t/checking-if-a-layer-is-in-a-layer-mask/860331/7
        public static bool IsInLayerMask(GameObject obj, LayerMask mask) => (mask.value & (1 << obj.layer)) != 0;
        public static bool IsInLayerMask(int layer, LayerMask mask) => (mask.value & (1 << layer)) != 0;

        /// <summary>
        /// Raycasts from mouse cursor into the world and returns hit position.
        /// </summary>
        private Vector3 WhereIsTheCursor(
            LayerMask layerMaskIgnore,
            LayerMask rayMask,
            out bool hit)
        {
            Vector3 screenPosition = Mouse.current.position.ReadValue();
            Ray ray = _mainCamera.ScreenPointToRay(screenPosition);

            if (Physics.Raycast(ray, out RaycastHit hitData, range, rayMask, QueryTriggerInteraction.Ignore))
            {
                hit = true;
                return hitData.point;
                
                // https://gamedevbeginner.com/how-to-convert-the-mouse-position-to-world-space-in-unity-2d-3d/
            }

            hit = false;
            return Vector3.zero;
        }

        /// <summary>
        /// Spawns a transparent preview prefab for the selected build template.
        /// </summary>
        private GameObject ShowTransparentPrefab(int index)
        {
            bool hit;
            Vector3 pos = WhereIsTheCursor(ignoreLayersDrag, raycastLayersDrag, out hit);

            if (!hit)
                return null;

            return Builder.Instance.InstantiateTransparentPrefab(index, pos, Quaternion.identity);
        }

                
        /// <summary>
        /// Selects a build item from the template list and displays it
        /// as a transparent preview at the cursor position.
        /// The preview position itself is updated elsewhere (e.g. in Update()).
        /// </summary>
        public void OnSelect(int value)
        {
            Debug.Log($"key pressed {value}");
            
            if (!_initialized)
                return;

            // Only react when the input action is fully performed
            // and only while the player is in building mode
            if (!_building) return;

            Debug.Log($"Place a block");

            
            // NOTE:
            // This selection logic is currently hardcoded to input names ("1", "2", "3").
            // It works, but should be refactored later to a data-driven approach.

            // --- Select build template 0 (key "1") ---
            if (value == 1)
            {
                // Remove any existing transparent preview
                if (instantiatedTransparentPrefab != null)
                    Destroy(instantiatedTransparentPrefab);

                // Show transparent preview for template 0
                instantiatedTransparentPrefab = ShowTransparentPrefab(0);

                if (instantiatedTransparentPrefab == null)
                {
                    // Failed to create preview, clear selection
                    _selectedBuildTemplate = null;
                }
                else
                {
                    // Assign selected build template
                    _selectedBuildTemplate = Builder.Instance.GetBuildItemTemplate(0);

                    // Warn if the player has no inventory left for this item
                    if (!_playerBuilderInventoryBridge.InventoryLeft(0))
                    {
                        Debug.LogWarning("BuilderInput.OnSelect(): no inventory left");
                    }
                }
            }
            // --- Select build template 1 (key "2") ---
            else if (value == 2)
            {
                if (instantiatedTransparentPrefab != null)
                    Destroy(instantiatedTransparentPrefab);

                instantiatedTransparentPrefab = ShowTransparentPrefab(1);

                if (instantiatedTransparentPrefab == null)
                {
                    _selectedBuildTemplate = null;
                }
                else
                {
                    _selectedBuildTemplate = Builder.Instance.GetBuildItemTemplate(1);

                    if (!_playerBuilderInventoryBridge.InventoryLeft(1))
                    {
                        Debug.LogWarning("BuilderInput.OnSelect(): no inventory left");
                    }
                }
            }
            // --- Select build template 2 (key "3") ---
            else if (value == 3)
            {
                if (instantiatedTransparentPrefab != null)
                    Destroy(instantiatedTransparentPrefab);

                instantiatedTransparentPrefab = ShowTransparentPrefab(2);

                if (instantiatedTransparentPrefab == null)
                {
                    _selectedBuildTemplate = null;
                }
                else
                {
                    _selectedBuildTemplate = Builder.Instance.GetBuildItemTemplate(2);

                    if (!_playerBuilderInventoryBridge.InventoryLeft(2))
                    {
                        Debug.LogWarning("BuilderInput.OnSelect(): no inventory left");
                    }
                }
            }
            else
            {
                // Any other key clears the current selection
                _selectedBuildTemplate = null;
            }

            // Reset snap-point index whenever a new build item is selected
            _snapPointIndex = 0;
        }

        /// <summary>
        /// Handles input for enabling/disabling "only rotate" mode.
        /// While active, rotation input is allowed without placing items.
        /// </summary>
        public void OnOnlyRotate(bool value)
        {
            if (!_initialized)
                return;

            // When the input starts, enable no-rotation-lock mode
            if (value)
            {
                Debug.Log("OnOnlyRotate.started");
                _noRotation = true;
            }
            else
            // When the input is released, disable no-rotation-lock mode
            {
                Debug.Log("OnOnlyRotate.cancelled");
                _noRotation = false;
            }
        }

        /// <summary>
        /// Returns whether rotation-only mode is currently active.
        /// </summary>
        public bool IsNoRotationActive()
        {
            return _noRotation;
        }

        /// <summary>
        /// Rotates the transparent preview prefab in discrete steps
        /// while rotation-only mode is active.
        /// </summary>
        public void OnRotate(float direction)
        {
            if (!_initialized)
                return;

            // Only respond when the rotation input starts
            //if (!context.started) return;

            // Rotation requires an active transparent preview
            if (!instantiatedTransparentPrefab)
                return;

            // Ignore rotation unless rotation-only mode is enabled
            if (!_noRotation)
                return;

            // Read rotation direction (typically from mouse wheel or axis)
            // float direction = context.ReadValue<float>();

            // Rotate forward through the rotation steps
            if (direction > 0.1f)
            {
                instantiatedTransparentPrefab.transform.Rotate(
                    Vector3.up,
                    rotationSteps[rotationStepIndex]
                );

                rotationStepIndex++;

                // Wrap index if end is reached
                if (rotationStepIndex >= rotationSteps.Length)
                    rotationStepIndex = 0;
            }
            // Rotate backward through the rotation steps
            else if (direction < -0.1f)
            {
                // Wrap index if rotating backwards past start
                if (rotationStepIndex == 0)
                    rotationStepIndex = rotationSteps.Length;

                instantiatedTransparentPrefab.transform.Rotate(
                    Vector3.up,
                    -rotationSteps[rotationStepIndex - 1]
                );

                rotationStepIndex--;
            }
        }

        /// <summary>
        /// Toggles between build mode and walk mode.
        /// Controls cursor visibility, locking, and preview cleanup.
        /// </summary>
        public void OnBuild()
        {
            if (!_initialized)
            {
                Debug.Log("OnBuild(): not initialized");
                return;
            }

            Debug.Log("Toggle build");

            // Toggle build mode state
            _building = !_building;

            if (_building)
            {
                // Enter build mode: unlock cursor for placement
                Cursor.lockState = CursorLockMode.Confined;
                Cursor.visible = true;
            }
            else
            {
                // Exit build mode: lock cursor and clean up state
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;

                // Reset snap point selection
                _snapPointIndex = 0;

                // Remove any active transparent preview
                if (instantiatedTransparentPrefab)
                {
                    Destroy(instantiatedTransparentPrefab);
                }
            }
        }

        /// <summary>
        /// Cycles to the next snap point on the selected build template.
        /// </summary>
        public void OnNextSnapPoint()
        {
            if (!_initialized)
                return;

            // Only react when the input is performed and in build mode
            // if (!context.performed || !_building) return;

            // Snap points must be enabled to proceed
            if (!_snapPoints)
            {
                Debug.Log("BuilderInput.OnNextSnapPoint(): <color=yellow>snapPoints disabled</color>");
                return;
            }

            // Ensure we have a valid preview and selected build template
            if (instantiatedTransparentPrefab != null && _selectedBuildTemplate != null)
            {
                // Snap the preview to the next snap point
                SnapToNext(instantiatedTransparentPrefab);

                // Advance snap-point index and wrap if needed
                _snapPointIndex++;
                if (_snapPointIndex >= _selectedBuildTemplate.snapPoints.Count)
                    _snapPointIndex = 0;
            }
        }
        
        /// <summary>
        /// Cycles to the next snap point on the selected build template.
        /// </summary>
        private void SnapToNext(GameObject go)
        {
            if (_selectedBuildTemplate.snapPoints.Count == 0)
                return;

            Vector3 offset = _selectedBuildTemplate.snapPoints[_snapPointIndex];

            for (int i = 0; i < go.transform.childCount; i++)
                go.transform.GetChild(i).localPosition += offset;
        }
    }
}
