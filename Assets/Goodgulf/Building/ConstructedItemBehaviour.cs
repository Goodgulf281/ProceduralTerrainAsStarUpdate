using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Goodgulf.Building
{
    /// <summary>
    /// Controls visual, physical, and debug behavior for a constructed item in the world.
    /// Handles mesh tracking, color feedback, debug display, and destruction logic.
    /// </summary>
    public class ConstructedItemBehaviour : MonoBehaviour
    {
        // Unique identifier for this constructed item
        private int _id;

        /// <summary>
        /// Public ID accessor. Also mirrors value to debugId for inspector visibility.
        /// </summary>
        public int id
        {
            get => _id;
            set
            {
                _id = value;
                debugId = value;
            }
        }

        // All child GameObjects representing visible meshes
        private List<GameObject> _meshObjects;
        public List<GameObject> MeshObjects => _meshObjects;

        // Colliders associated with mesh objects
        private List<Collider> _meshColliders;
        public List<Collider> MeshColliders => _meshColliders;

        // Materials used by mesh renderers (cached for color manipulation)
        private List<Material> _meshMaterials;
        public List<Material> MeshMaterials => _meshMaterials;

        // Enables verbose logging and debug visuals
        private bool _debugEnabled = true;
        public bool DebugEnabled
        {
            get => _debugEnabled;
            set => _debugEnabled = value;
        }

        // Debug child object containing text
        private GameObject _debugObject;
        public GameObject debugObject => _debugObject;

        // TextMeshPro used for displaying debug info
        private TextMeshPro _debugText;
        public TextMeshPro debugText => _debugText;

        // Area this item belongs to
        private BuildingArea _buildingArea;
        public BuildingArea buildingArea
        {
            get => _buildingArea;
            set { _buildingArea = value; }
        }

        // Data representation of the constructed item
        private ConstructedItem _currentConstructedItem;
        public ConstructedItem constructedItem
        {
            get => _currentConstructedItem;
            set { _currentConstructedItem = value; }
        }

        // Original mesh colors (used for restoring after feedback)
        private List<Color> _originalColors;

        // Colors that were last applied (used for smooth interpolation)
        private List<Color> _lastSetColors;

        // Duration used to smoothly reset mesh colors
        private const float _resetDuration = 2.5f;
        private float _resetTimer;
        private bool _resetNeeded = false;

        // Inspector-visible debug ID
        public int debugId;

        /// <summary>
        /// Collects mesh objects, colliders, materials, and debug elements.
        /// Called once when the object is instantiated.
        /// </summary>
        public void Awake()
        {
            _meshColliders = new List<Collider>();
            _meshObjects = new List<GameObject>();
            _meshMaterials = new List<Material>();
            _originalColors = new List<Color>();
            _lastSetColors = new List<Color>();

            // Sanity check: constructed item must have children
            if (transform.childCount == 0)
            {
                Debug.LogError("ConstructedItemBehaviour.Awake() called with no children");
                return;
            }

            // Iterate through children and categorize them by tag
            for (int i = 0; i < transform.childCount; i++)
            {
                GameObject child = transform.GetChild(i).gameObject;

                // Mesh objects used for visuals and collision
                if (child.CompareTag("BuildItemMesh"))
                {
                    _meshObjects.Add(child);

                    Material m = child.GetComponent<Renderer>().material;
                    Collider col = child.GetComponent<Collider>();

                    _meshMaterials.Add(m);
                    _meshColliders.Add(col);
                    _originalColors.Add(m.color);
                }

                // Debug object for distance / state display
                if (child.CompareTag("BuildItemDebug"))
                {
                    _debugObject = child;
                    _debugText = _debugObject.GetComponent<TextMeshPro>();
                    _debugText.text = "";
                }
            }

            if (_debugEnabled)
            {
                Debug.Log(
                    $"ConstructedItemBehaviour.Awake() #of mesh items={_meshObjects.Count}, " +
                    $"#of colliders={_meshColliders.Count}, #of materials={_meshMaterials.Count}"
                );
            }
        }

        /// <summary>
        /// Handles smooth color reset back to original values over time.
        /// </summary>
        private void Update()
        {
            if (!_resetNeeded)
                return;

            _resetTimer += Time.deltaTime;

            // Gradually interpolate mesh colors back to their original values
            for (int i = 0; i < _originalColors.Count; i++)
            {
                _meshMaterials[i].color =
                    Color.Lerp(_lastSetColors[i], _originalColors[i], _resetTimer / _resetDuration);
            }

            // Finalize reset when duration is exceeded
            if (_resetTimer > _resetDuration)
            {
                _resetTimer = 0.0f;
                _resetNeeded = false;

                for (int i = 0; i < _originalColors.Count; i++)
                {
                    _meshMaterials[i].color = _originalColors[i];
                }

                SetDebugText("");
            }
        }

        /// <summary>
        /// Immediately applies a color to all meshes and schedules a smooth reset.
        /// </summary>
        public void SetMeshColor(Color color)
        {
            _lastSetColors.Clear();

            foreach (Material m in _meshMaterials)
            {
                m.color = color;
                _lastSetColors.Add(color);
            }

            _resetTimer = 0.0f;
            _resetNeeded = true;
        }

        /// <summary>
        /// Updates the debug text if present.
        /// Timed clearing is handled elsewhere.
        /// </summary>
        public void SetDebugText(string text)
        {
            if (_debugText)
                _debugText.text = text;
        }

        /// <summary>
        /// Removes the constructed item from the scene.
        /// Optionally applies physics and delays destruction.
        /// </summary>
        public void RemoveConstructedItem(bool delayed)
        {
            // Enable physics for dramatic destruction effect
            foreach (GameObject go in _meshObjects)
            {
                if (go.TryGetComponent<Rigidbody>(out Rigidbody rb))
                {
                    //if (_debugEnabled)
                    //    Debug.Log("ConstructedItemBehaviour(): found a RigidBody");

                    rb.useGravity = true;
                    rb.constraints = RigidbodyConstraints.None;

                    Vector3 force = new Vector3(
                        Random.Range(-5f, 5f),
                        Random.Range(-5f, 5f),
                        Random.Range(-5f, 5f) * 10.0f
                    );

                    rb.AddForce(force);
                }
            }

            // Remove debug visuals
            if (_debugObject)
                Destroy(_debugObject);

            // Destroy root object, optionally delayed
            if (delayed)
                Destroy(transform.gameObject, 10.0f);
            else
                Destroy(transform.gameObject);
        }

        /// <summary>
        /// Displays distance info and visual feedback based on grounding state.
        /// </summary>
        public void ShowDistance()
        {
            if (_debugEnabled)
            {
                string text = "[" + id + "]-<" + constructedItem.Distance + ">";
                SetDebugText(text);
            }

            // Green when grounded, red intensity based on distance otherwise
            if (constructedItem.Grounded)
            {
                SetMeshColor(new Color(0, 1.0f, 0));
            }
            else
            {
                SetMeshColor(new Color(constructedItem.Distance * 0.1f, 0, 0));
            }
        }
    }
}
