using System;
using System.IO;
using UnityEngine;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Profiling;
using System.Threading;
using UnityEditor;
using UnityEngine.Serialization;
using Goodgulf.TerrainUtils;
using Unity.VisualScripting;
using UnityEngine.UI;

namespace Goodgulf.Building
{
    /// <summary>
    /// Central manager responsible for all building logic:
    /// - Terrain and grid management
    /// - Building area creation
    /// - Constructed item lifecycle
    /// - Player interaction with the building system
    /// </summary>
    public class Builder : MonoBehaviour
    {
        // All available build templates (prefabs + metadata)
        [SerializeField]
        private List<BuildTemplate> _buildItemTemplates;

        // Terrain/grid constants
        public const int TerrainSize = 1000;
        public const int BuildingAreaSize = 100;
        public const int GridSize = TerrainSize / BuildingAreaSize;

        // Prefab used to visually represent a building area
        [SerializeField] 
        private GameObject _buildingAreaPrefab;

        // Worker update timer (used to periodically recalc areas)
        [Header("Worker Timer")]
        [SerializeField] 
        private float _workerTimerDuration = 5.0f;
        private float _workerTimer = 0.0f;

        // Debug toggles
        [Header("Debug Mode")]
        [SerializeField]
        private bool _debugEnabled = true;

        // World and terrain references
        [Header("World")]
        [SerializeField]
        private Vector3 _worldOrigin;
        [SerializeField]
        private Terrain _activeTerrain;
        private Collider _activeTerrainCollider;

        // Build constraints and layers
        [FormerlySerializedAs("_buildigBlockLayers")]
        [Header("Buildable")]
        [SerializeField]
        private LayerMask _buildingBlockLayers;
        [SerializeField]
        private LayerMask _buildableLayers;
        [SerializeField]
        private List<NonBuildableArea> _nonBuildableAreas;
        [SerializeField] 
        private LayerMask _transparentBlockLayer;

        // Magnetic snapping parameters
        [SerializeField] 
        private float _magneticRadius = 0.5f;
        [FormerlySerializedAs("_magenticLayer")] [SerializeField] 
        private LayerMask _magneticLayer;

        // All terrains indexed by grid coordinate
        private Dictionary<Vector2Int, Terrain> _allTerrains;

        /// <summary>
        /// Currently active terrain; also caches its collider
        /// </summary>
        public Terrain ActiveTerrain
        {
            get => _activeTerrain;
            set
            {
                _activeTerrain = value;
                if (_activeTerrain.TryGetComponent<Collider>(out Collider _collider))
                {
                    _activeTerrainCollider = _collider;
                }
                else Debug.LogError($"Builder.Terrain(): ActiveTerrain {_activeTerrain.name} has no Collider");
            }
        }

        // Building areas indexed by GUID
        private Dictionary<string, BuildingArea> _buildingAreasById;

        // All constructed items indexed by global ID
        private Dictionary<int, ConstructedItem> _allConstructedItems;

        // Player references
        private GameObject _player;
        private PlayerBuilderInventoryBridge _playerBuilderInventoryBridge;

        // Global incremental ID for constructed items
        private int _globalID = 0;

        // Ensure the builder does not start before Initialization
        private bool _initialized = false;
        
        // Singleton instance
        public static Builder Instance { get; private set; }

        /// <summary>
        /// Initialize singleton and cache all active terrains
        /// </summary>
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            _buildingAreasById = new Dictionary<string, BuildingArea>();
            _allConstructedItems = new Dictionary<int, ConstructedItem>();
            _allTerrains = new Dictionary<Vector2Int, Terrain>();

        }

        public void CacheTerrains()
        {
            // Cache all active terrains by world index
            Terrain[] terrains = Terrain.activeTerrains;
            _allTerrains.Clear();
            
            foreach (var terrain in terrains)
            {
                Vector2Int terPos = GetTerrainIndex(terrain.transform.position);

                if (_debugEnabled)
                    Debug.Log($"Found terrain: {terrain.name} at {terrain.transform.position} -> index {terPos}");

                _allTerrains.Add(terPos, terrain);
            }
        }
        

        /// <summary>
        /// Draws world origin gizmo for debugging
        /// </summary>
        void OnDrawGizmos()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(_worldOrigin, _worldOrigin + Vector3.up * 20f);
        }

        public void Initialize()
        {
            Debug.Log("<color=yellow>Builder.Initialize(): Invoked</color>");
            
            CacheTerrains();

            if (_activeTerrain && _activeTerrainCollider == null)
            {
                if (_activeTerrain.TryGetComponent<Collider>(out Collider _collider))
                {
                    _activeTerrainCollider = _collider;
                }
                else Debug.LogError($"Builder.Initialize(): ActiveTerrain {_activeTerrain.name} has no Collider");
            }

            if (_player)
            {
                if (_player.TryGetComponent<PlayerBuilderInventoryBridge>(out PlayerBuilderInventoryBridge bridge))
                {
                    _playerBuilderInventoryBridge = bridge;
                }
                else
                    Debug.LogError("Builder.Initialize(): PlayerBuilderInventoryBridge not found");
            }
            
            _initialized = true;
        }
        
        
        public PlayerBuilderInventoryBridge GetPlayerBuilderInventoryBridge()
        {
            return _playerBuilderInventoryBridge;
        }

        /// <summary>
        /// Returns a unique global ID for constructed items, now threadsafe
        /// </summary>
        public int GetNewGlobalID()
        {
            // return _globalID++;
            return Interlocked.Increment(ref _globalID);
        }

        /// <summary>
        /// Fetch a constructed item by ID
        /// </summary>
        public ConstructedItem GetConstructedItem(int itemID)
        {
            _allConstructedItems.TryGetValue(itemID, out ConstructedItem constructedItem);
            return constructedItem;
        }

        /// <summary>
        /// Recalculates which building areas should be processed by the worker,
        /// based on player position (3x3 grid around player).
        /// </summary>
        private void UpdateWorkerAreas()
        {
            if (!_activeTerrain)
            {
                Debug.LogWarning("Builder.UpdateWorkerAreas(): No active terrain found");
                return;
            }

            if (!_activeTerrain.TryGetComponent<BuildableTerrain>(out BuildableTerrain buildableTerrain))
            {
                throw new Exception($"Builder.UpdateWorkerAreas(): active terrain {_activeTerrain.name} has no BuildableTerrain component");
            }

            Dictionary<Vector2Int, BuildingArea> buildingAreas = buildableTerrain.BuildingAreas;
            List<BuildingArea> areas = new List<BuildingArea>();

            Vector2Int playerPos = WorldToGrid(
                _player.transform.position,
                _activeTerrain.transform.position,
                BuildingAreaSize
            );

            // Collect surrounding 3x3 areas
            for (int x = playerPos.x - 1; x <= playerPos.x + 1; x++)
            {
                for (int y = playerPos.y - 1; y <= playerPos.y + 1; y++)
                {
                    if (x >= 0 && x < GridSize && y >= 0 && y < GridSize)
                    {
                        Vector2Int gp = new Vector2Int(x, y);
                        if (buildingAreas.TryGetValue(gp, out var area))
                            areas.Add(area);
                    }
                }
            }

            if (areas.Count > 0)
                RecalculationWorker.Instance.SetWorkerInstructions(areas);
        }

        /// <summary>
        /// Periodically updates worker areas based on timer
        /// </summary>
        private void Update()
        {
            if (!_initialized)
                return;
            
            _workerTimer += Time.deltaTime;
            if (_workerTimer > _workerTimerDuration)
            {
                _workerTimer = 0.0f;
                UpdateWorkerAreas();
            }
        }

        /// <summary>
        /// Assigns the player and determines which terrain they are on
        /// </summary>
        public void SetPlayer(GameObject player)
        {
            _player = player;

            if (_player == null)
                throw new Exception("Builder.SetPlayer(): Player is null");
            
            Vector2Int worldIndex = GetTerrainIndex(player.transform.position);
            
            if(!_allTerrains.TryGetValue(worldIndex, out var terrain))
                throw new KeyNotFoundException($"Builder.SetPlayer(): No terrain found for {worldIndex}");
                
            if (terrain == null)
                Debug.LogError("Builder.SetPlayer(): Terrain not found.");
            else
                ActiveTerrain = terrain;

            if (_player.TryGetComponent<PlayerBuilderInventoryBridge>(out PlayerBuilderInventoryBridge bridge))
            {
                _playerBuilderInventoryBridge = bridge;    
            }
            else throw new Exception($"Builder.SetPlayer(): PlayerBuilderInventoryBridge not found"); 
        }

        /// <summary>
        /// Sets the active terrain and caches its collider
        /// </summary>
        [Obsolete("Use ActiveTerrain instead.")]
        public void SetTerrain(Terrain t)
        {
            _activeTerrain = t;
            _activeTerrainCollider = _activeTerrain.GetComponent<Collider>();
        }

        /// <summary>
        /// Converts a world position into a terrain grid index
        /// </summary>
        private Vector2Int GetTerrainIndex(Vector3 worldPos)
        {
            float terrainSize = TerrainSize; // was hardcoded 1000f;

            int x = Mathf.FloorToInt((worldPos.x - _worldOrigin.x) / terrainSize);
            int z = Mathf.FloorToInt((worldPos.z - _worldOrigin.z) / terrainSize);

            return new Vector2Int(x, z);
        }

        /// <summary>
        /// Converts world position to building-area grid coordinates
        /// </summary>
        public static Vector2Int WorldToGrid(Vector3 worldPos, Vector3 terrainPosition, int buildingAreaSize)
        {
            Vector3 localPos = worldPos - terrainPosition;
            return new Vector2Int(
                Mathf.FloorToInt(localPos.x / buildingAreaSize),
                Mathf.FloorToInt(localPos.z / buildingAreaSize)
            );
        }

        /// <summary>
        /// Converts grid position back to world space
        /// </summary>
        public static Vector3 GridToWorld(Vector2Int gridPos, Vector3 terrainPosition, int buildingAreaSize)
        {
            float x = gridPos.x * buildingAreaSize + buildingAreaSize * 0.5f;
            float z = gridPos.y * buildingAreaSize + buildingAreaSize * 0.5f;
            return terrainPosition + new Vector3(x, 0f, z);
        }



        /// <summary>
        /// Returns an existing BuildingArea for the given world position,
        /// or creates and registers a new one if none exists.
        /// </summary>
        public BuildingArea GetOrCreateBuildingArea(Vector3 worldPosition)
        {
            // Determine which terrain this world position belongs to

            Vector2Int terrainIndex;
            
            terrainIndex = GetTerrainIndex(worldPosition);

            if (!_allTerrains.TryGetValue(terrainIndex, out var terrain))
            {
                throw new KeyNotFoundException($"Builder.GetOrCreateBuildingArea(): Terrain not found for {terrainIndex}");
            }
            else if (_debugEnabled)
                Debug.Log($"Builder.GetOrCreateBuildingArea():We have a terrain {terrain.name} (terrainIndex {terrainIndex}) at world position {worldPosition}");
            
            Dictionary<Vector2Int, BuildingArea> bas;

            // Fetch the BuildableTerrain component which holds building areas
            if (terrain.TryGetComponent<BuildableTerrain>(out BuildableTerrain buildableTerrain))
            {
                if (_debugEnabled)
                    Debug.Log($"Builder.GetOrCreateBuildingArea({terrain.name}): buildableTerrain get Dictionaries");

                bas = buildableTerrain.BuildingAreas;
            }
            else
            {
                throw new Exception($"Builder.GetOrCreateBuildingArea(): BuildableTerrain not found for {terrain.name}");    
            }

            // Convert world position to grid coordinates relative to this terrain
            Vector2Int gridPos = WorldToGrid(worldPosition, terrain.transform.position, BuildingAreaSize);

            if (_debugEnabled)
            {
                Debug.Log($"Builder.GetOrCreateBuildingArea(): on terrain {terrain.name}[{terrainIndex}], world position {worldPosition}, and grid position {gridPos}");

                if (bas.Count == 0)
                {
                    Debug.Log($"Builder.GetOrCreateBuildingArea(): No keys available for {terrain.name}");
                }
                else
                {
                    foreach (var key in bas.Keys)
                    {
                        Debug.Log($"Builder.GetOrCreateBuildingArea(): Available key {key}");
                    }
                }
            }

            // Ensure grid position is within valid bounds
            /*
            if (gridPos.x < 0 || gridPos.y < 0 ||
                gridPos.x >= GridSize || gridPos.y >= GridSize)
            {
                Debug.LogError($"Builder.GetOrCreateBuildingArea(): {gridPos} is out of bounds.");
                return null;
            }
            */

            // Return existing building area if one already exists at this grid position
            if (bas.TryGetValue(gridPos, out var area))
            {
                if (_debugEnabled)
                    Debug.Log($"Builder.GetOrCreateBuildingArea(): <color=green>gridPos is OK and area found</color>");

                return area;
            }

            if (_debugEnabled)
                Debug.Log($"Builder.GetOrCreateBuildingArea(): <color=yellow>gridPos is OK and no area found</color>");

            // Convert grid position back to world position for spawning
            Vector3 spawnPos = GridToWorld(gridPos, terrain.transform.position, BuildingAreaSize);

            if (_debugEnabled)
                Debug.Log($"Builder.GetOrCreateBuildingArea(): spawnPos={spawnPos}");

            // Instantiate the BuildingArea visual prefab
            GameObject go = Instantiate(
                _buildingAreaPrefab,
                spawnPos,
                Quaternion.identity,
                this.transform
            );
            
            // Create and initialize the logical BuildingArea data
            area = new BuildingArea();
            area.Position = spawnPos;
            area.DebugEnabled = _debugEnabled;
            area.Initialize(gridPos, BuildingAreaSize);
            area.Id = System.Guid.NewGuid().ToString();

            // Assign terrain reference to the outline component
            if (go.TryGetComponent<BuildingAreaOutline>(out BuildingAreaOutline outline))
            {
                outline.Terrain = terrain;
                outline.BuildingArea = area;
                outline.SetValid(true);
            }
            else throw new Exception($"Builder.GetOrCreateBuildingArea(): no outline found for the building area prefab");

            
            if (_debugEnabled)
                Debug.Log($"Builder.GetOrCreateBuildingArea(): created area with id ={area.Id}");

            // Register area for lookup by grid position and by ID
            bas.Add(gridPos, area);
            _buildingAreasById.Add(area.Id, area);

            return area;
        }

        /// <summary>
        /// Retrieves a BuildingArea by its unique ID.
        /// </summary>
        public BuildingArea GetBuildingAreaByID(string id)
        {
            if (_buildingAreasById.TryGetValue(id, out var area))
            {
                return area;
            }
            else
            {
                throw new KeyNotFoundException($"Builder.GetBuildingAreaByID(): no area found with id {id}");
            }
        }

        /// <summary>
        /// Returns a BuildTemplate by index, or null if index is invalid.
        /// </summary>
        public BuildTemplate GetBuildItemTemplate(int index)
        {
            if (index < 0 || index >= _buildItemTemplates.Count)
                return null;

            return _buildItemTemplates[index];
        }

        /// <summary>
        /// Instantiates a transparent preview prefab for placement visualization.
        /// </summary>
        public GameObject InstantiateTransparentPrefab(int index, Vector3 position, Quaternion rotation)
        {
            if (index < 0 || index >= _buildItemTemplates.Count)
                return null;

            return Instantiate(_buildItemTemplates[index].transparentPrefab, position, rotation);
        }

        /// <summary>
        /// Debug method that spawns a 10x10 grid of building prefabs for testing.
        /// </summary>
        public void InstantiateBuildingPrefabDebug(int index, Vector3 position, Quaternion rotation)
        {
            BuildTemplate b;
            
            if (index < 0 || index >= _buildItemTemplates.Count)
            {
                throw new Exception($"Builder.InstantiateBuildingPrefabDebug(): {index} is out of bounds.");
            }
            else b = _buildItemTemplates[index]; 

            // Spawn a grid of buildings to test spacing and alignment
            for (int i = 0; i < 10; i++)
            {
                for (int j = 0; j < 10; j++)
                {
                    Vector3 pos = new Vector3(
                        position.x + b.width * i,
                        position.y + b.height * j - j * 0.001f, // slight offset to avoid z-fighting
                        position.z
                    );

                    InstantiateBuildingPrefab(index, pos, rotation, 0);
                }
            }
        }

        /// <summary>
        /// Finds and marks constructed items within the bounds of a transparent
        /// placement prefab so they can be removed or weakened.
        /// </summary>
        /// <param name="instantiatedTransparentPrefab">
        /// The temporary placement prefab used to define the destruction area.
        /// </param>
        public void DestructConstructedItemsAtPos(GameObject instantiatedTransparentPrefab)
        {
            // Safety check: nothing to process
            if (instantiatedTransparentPrefab == null)
            {
                if(_debugEnabled)
                    Debug.LogWarning($"DestructConstructedItemsAtPos(): instantiatedTransparentPrefab is null");
                return;
            }

            // Expecting the prefab to have at least one child containing a collider
            if (instantiatedTransparentPrefab.transform.childCount == 0)
            {
                if(_debugEnabled)
                    Debug.LogWarning($"DestructConstructedItemsAtPos(): instantiatedTransparentPrefab childCount is 0");
                return;
            }

            // Use the first child's collider as the spatial reference
            Collider collider1 = instantiatedTransparentPrefab
                .transform
                .GetChild(0)
                .GetComponent<Collider>();

            // Collect all colliders within a sphere that fully encloses the collider bounds
            Collider[] hits = Physics.OverlapSphere(
                collider1.bounds.center,
                collider1.bounds.extents.magnitude
            );

            // Iterate over all detected colliders
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i];

                if (collider.gameObject.transform.parent == null)
                    continue;
                
                // Constructed item behaviours are expected to live on the parent GameObject
                if (collider.gameObject.transform.parent.TryGetComponent(out ConstructedItemBehaviour cib))
                {
                    // Retrieve the building area this constructed item belongs to
                    BuildingArea area = GetBuildingAreaByID(cib.constructedItem.BuildingAreaId);
                    if (area != null)
                    {
                        // Mark the item as infinitely distant so it is guaranteed to be removed
                        cib.constructedItem.Distance = float.PositiveInfinity;

                        // Remove or weaken the constructed item in its building area
                        area.RemoveWeakConstructedItem(cib.constructedItem);

                        // Visual feedback: mark the affected item
                        cib.SetMeshColor(Color.magenta);
                    }
                    else throw new Exception($"Builder.DestructConstructedItemsAtPos(): no BuildingArea found for ConstructedItemBehaviour {cib.name}");
                }
            }
        }

        /// <summary>
        /// Legacy / unreliable grounding check.
        /// Uses collider bounds intersection with the active terrain collider.
        /// </summary>
        /// <remarks>
        /// This method is discouraged because terrain collider bounds
        /// often extend to the highest terrain point, causing false positives.
        /// </remarks>
        [Obsolete("Use IsTouchingTerrain or IsTouchingAnyTerrain instead.")]
        public bool IsGrounded(Collider collider)
        {
            Debug.LogWarning("Builder.IsGrounded(): do not use this method");

            // Terrain collider bounds include the highest terrain point,
            // so many objects below that height will appear "grounded".
            if (_activeTerrainCollider)
            {
                return collider.bounds.Intersects(_activeTerrainCollider.bounds);
            }
            else
            {
                Debug.LogWarning("Builder.IsGrounded(): terrainCollider == null");
                return false;
            }
        }

        /// <summary>
        /// Checks whether a collider is touching the currently active terrain
        /// by comparing its lowest Y bound to the terrain height.
        /// </summary>
        public bool IsTouchingTerrain(Collider col)
        {
            if (_activeTerrain)
            {
                // Use the collider's center position for terrain sampling
                Vector3 pos = col.bounds.center;

                // Sample terrain height in world space
                float terrainHeight =
                    ActiveTerrain.SampleHeight(pos) + ActiveTerrain.transform.position.y;

                // Touching if the collider bottom is at or below the terrain surface
                return col.bounds.min.y <= terrainHeight;
            }
            else
            {
                Debug.LogWarning("Builder.IsTouchingTerrain(): _terrain == null");
                return false;
            }
        }

        /// <summary>
        /// Checks whether a collider is touching the terrain at its current world position,
        /// taking terrain tiling into account.
        /// </summary>
        public bool IsTouchingAnyTerrain(Collider col)
        {
            Vector3 pos = col.bounds.center;

            // Determine which terrain tile this position belongs to
            Vector2Int index = GetTerrainIndex(pos);
            
            if(!_allTerrains.TryGetValue(index, out var terrain))
                throw new KeyNotFoundException($"Builder.IsTouchingAnyTerrain(): No terrain found for {index}");

            if (terrain != null)
            {
                // Sample terrain height for the specific terrain tile
                float terrainHeight =
                    terrain.SampleHeight(pos) + terrain.transform.position.y;

                // Collider is touching terrain if its bottom is below the surface
                return col.bounds.min.y <= terrainHeight;
            }

            return false;
        }

        /// <summary>
        /// Checks whether a collider overlaps with any collider on a buildable layer.
        /// Trigger colliders are ignored.
        /// </summary>
        public bool IsTouchingBuildableLayer(Collider col)
        {
            return Physics.OverlapBox(
                col.bounds.center,
                col.bounds.extents,
                col.transform.rotation,
                _buildableLayers,
                QueryTriggerInteraction.Ignore
            ).Length > 0;
        }

        
        /// <summary>
        /// Instantiates a building prefab, registers it in a BuildingArea,
        /// detects grounding, snap points, neighbours, and updates distances.
        /// </summary>
        public GameObject InstantiateBuildingPrefab(int index, Vector3 position, Quaternion rotation, int snapPointIndex)
        {
            // Validate template index and player availability
            if (index < 0 || index >= _buildItemTemplates.Count || _player == null)
                throw new Exception($"Builder.InstantiateBuildingPrefab(): invalid parameter {index} or _player is null");

            // Instantiate the building prefab under this Builder transform
            GameObject builtItem = Instantiate(
                _buildItemTemplates[index].buildPrefab,
                position,
                rotation,
                this.transform
            );

            // Rename the mesh child for clarity in the hierarchy
            builtItem.transform.GetChild(0).gameObject.name = builtItem.name + " (mesh)";

            // Retrieve the collider from the mesh child
            if(!builtItem.transform.GetChild(0).TryGetComponent<Collider>(out Collider collider1))
                throw new Exception($"Builder.InstantiateBuildingPrefab(): no Collider on {builtItem.name}'s Child Object 0");

            // Collider collider1 = builtItem.transform.GetChild(0).GetComponent<Collider>();

            // Debug grounding checks
            if (_debugEnabled)
            {
                Debug.Log($"Builder.InstantiateBuildingPrefab(): Is touching buildable layer {IsTouchingBuildableLayer(collider1)}");
                Debug.Log($"Builder.InstantiateBuildingPrefab(): Is touching terrain {IsTouchingAnyTerrain(collider1)}");
            }

            // Determine whether the object is grounded
            bool grounded = IsTouchingAnyTerrain(collider1) || IsTouchingBuildableLayer(collider1);

            if (grounded && _debugEnabled)
                Debug.Log($"Builder.InstantiateBuildingPrefab(): <color=yellow>{builtItem.name} is grounded!</color>");

            ConstructedItem constructedItem;
            int cid = -1;

            // Get or create the BuildingArea for this position
            BuildingArea buildingArea = GetOrCreateBuildingArea(position);
            if (buildingArea != null)
            {
                // Register constructed item in the building area
                constructedItem = buildingArea.AddBuildItem(
                    _buildItemTemplates[index],
                    builtItem,
                    grounded,
                    _debugEnabled
                );

                cid = constructedItem.Id;

                // Rename instance to include its unique ID
                builtItem.name = _buildItemTemplates[index].name + " (" + constructedItem.Id + ")";

                // Store snap point index used
                constructedItem.SnapPointIndex = snapPointIndex;

                // Apply snap point offsets to all submeshes if applicable
                if (constructedItem.ConstructedItemBehaviour != null && snapPointIndex > 0)
                {
                    List<GameObject> submeshes = constructedItem.ConstructedItemBehaviour.MeshObjects;
                    BuildTemplate bt = _buildItemTemplates[constructedItem.BuildTemplateId];

                    for (int i = 0; i < snapPointIndex; i++)
                    {
                        if (i < bt.snapPoints.Count)
                        {
                            for (int j = 0; j < submeshes.Count; j++)
                            {
                                submeshes[j].transform.localPosition += bt.snapPoints[i];
                            }
                        }
                    }
                }

                // Register globally
                _allConstructedItems.Add(constructedItem.Id, constructedItem);
            }
            else
            {
                // Safety check for terrain mismatch or missing building area
                Vector2Int tpos = GetTerrainIndex(position);

                if (_allTerrains.TryGetValue(tpos, out var terrain))
                {
                    if (terrain == _activeTerrain)
                    {
                        throw new Exception($"Builder.InstantiateBuildingPrefab(): no BuildingArea created for {tpos}");                        
                    }
                    else
                    {
                        throw new Exception($"Builder.InstantiateBuildingPrefab(): we crossed into another terrain {terrain.name} for {tpos}");
                    }
                }

                throw new Exception($"Builder.InstantiateBuildingPrefab(): no terrain found for {tpos}");
            }

            // --------------------------------------------------
            // Neighbour detection
            // --------------------------------------------------

            if(_debugEnabled)
                Debug.Log($"Builder.InstantiateBuildingPrefab(): Neighbour detection");
            
            // Find nearby colliders within build radius
            Collider[] hitColliders = Physics.OverlapSphere(
                position,
                _buildItemTemplates[index].radius,
                _buildingBlockLayers,
                QueryTriggerInteraction.Ignore
            );

            for (int i = 0; i < hitColliders.Length; i++)
            {
                Collider collider2 = hitColliders[i];

                // Check bounding-box intersection
                if (collider1.bounds.Intersects(collider2.bounds))
                {
                    // Attempt to retrieve neighbour constructed item

                    if (collider2.gameObject.transform.parent != null)
                    {
                        if (collider2.gameObject.transform.parent.TryGetComponent<ConstructedItemBehaviour>(
                                out ConstructedItemBehaviour cib))
                        {
                            if (cib.id != cid)
                            {
                                // Register mutual neighbours
                                constructedItem.AddNeighbour(cib.constructedItem.Id);
                                cib.constructedItem.AddNeighbour(constructedItem.Id);

                                // Update distance propagation if not grounded
                                if (!constructedItem.Grounded)
                                {
                                    float distance = cib.constructedItem.Distance + constructedItem.Strength;
                                    if (distance < constructedItem.Distance)
                                        constructedItem.Distance = distance;
                                }
                            }
                        }
                    }
                }
            }

            // Visualize distance if debugging
            if (_debugEnabled)
                buildingArea.ShowDistance(constructedItem);

            return builtItem;
        }

        /// <summary>
        /// Checks whether a position overlaps any defined non-buildable area.
        /// </summary>
        public bool IsInNonBuildableArea(Vector3 position)
        {
            bool result = false;
            int i = 0;

            // Iterate through all non-buildable zones
            while (i < _nonBuildableAreas.Count && !result)
            {
                NonBuildableArea nba = _nonBuildableAreas[i];

                // Check for overlaps with transparent block layer
                Collider[] hitColliders = Physics.OverlapSphere(
                    nba.transform.position,
                    nba.Range,
                    _transparentBlockLayer,
                    QueryTriggerInteraction.Ignore
                );

                result = hitColliders.Length > 0;
                i++;
            }

            return result;
        }

        /// <summary>
        /// Pushes terrain downward at the given position.
        /// </summary>
        public void DepressTerrainAtPosition(Vector3 position)
        {
            if (_debugEnabled)
                Debug.Log($"DepressTerrainAtPosition {position}");

            TerrainUtils.TerrainUtils.PushDownTerrain(_activeTerrain, position, 3.0f, 1f);
        }

        /// <summary>
        /// Raises terrain at the given position.
        /// </summary>
        public void RaiseTerrainAtPosition(Vector3 position)
        {
            if (_debugEnabled)
                Debug.Log($"RaiseTerrainAtPosition {position}");

            TerrainUtils.TerrainUtils.PushDownTerrain(_activeTerrain, position, 10.0f, -2f);
        }

        /// <summary>
        /// Saves all BuildingAreas to JSON files.
        /// </summary>
        public void SaveAllBuildingAreas()
        {
            foreach (var area in _buildingAreasById)
            {
                string json = JsonUtility.ToJson(area.Value, true);
                string path = Path.Combine(Application.persistentDataPath, area.Key + ".json");

                File.WriteAllText(path, json);
            }
        }

        /// <summary>
        /// Loads predefined BuildingArea JSON files and reconstructs buildings.
        /// </summary>
        public void LoadBuildingArea()
        {
            string[] files = { "1.json", "2.json", "3.json" };

            foreach (string filename in files)
            {
                string path = Path.Combine(Application.persistentDataPath, filename);
                if (!File.Exists(path))
                    continue;

                Debug.Log($"Builder.LoadBuildingArea(): loading <color=yellow>{filename}</color>");

                string json = File.ReadAllText(path);
                BuildingArea area = JsonUtility.FromJson<BuildingArea>(json);

                _buildingAreasById.Add(area.Id, area);

                // Preserve constructed items before clearing
                ConstructedItem[] constructedItems = area.ConstructedItems;
                area.ClearAllConstructedItems();

                // Re-instantiate all items
                foreach (ConstructedItem c in constructedItems)
                {
                    InstantiateBuildingPrefab(c.BuildTemplateId, c.Position, c.Rotation, 0);
                }
            }
        }

        /// <summary>
        /// Checks whether a magnetic snap point is close to any magnetic collider.
        /// </summary>
        public bool IsMagneticPointClose(GameObject prefabT, out Vector3 distanceClosestPoint)
        {
            // Validate hierarchy
            if (prefabT.transform.childCount > 0)
            {
                GameObject child = prefabT.transform.GetChild(0).gameObject;

                int c = child.transform.childCount;
                for (int i = 0; i < c; i++)
                {
                    GameObject go = child.transform.GetChild(i).gameObject;

                    // Check magnetic overlap
                    Collider[] hitColliders = Physics.OverlapSphere(
                        go.transform.position,
                        _magneticRadius,
                        _magneticLayer,
                        QueryTriggerInteraction.Collide
                    );

                    if (hitColliders.Length > 0)
                    {
                        distanceClosestPoint =
                            go.transform.position - hitColliders[0].transform.position;
                        return true;
                    }
                }
            }

            distanceClosestPoint = Vector3.zero;
            return false;
        }
    }
}