using System;
using UnityEngine;
using System.Collections.Generic;
using TMPro;
using UnityEngine.Serialization;

namespace Goodgulf.Building
{
    /// <summary>
    /// Serializable data container representing a single constructed/buildable item
    /// in the world. This class is purely data-oriented and linked to a
    /// ConstructedItemBehaviour for runtime interaction.
    /// </summary>
    [Serializable]
    public class ConstructedItem
    {
        // Globally unique ID for this constructed item
        public int Id;

        // Whether this item is grounded (connected to terrain/foundation)
        public bool Grounded = false;

        // Distance to the nearest grounded item (used for stability/path calculations)
        public float Distance = float.PositiveInfinity;

        // Reference to the build template used to create this item
        public int BuildTemplateId;
        public string BuildTemplateName;

        // Structural strength value used for graph/path calculations
        public float Strength;

        // World-space transform data
        public Vector3 Position;
        public Quaternion Rotation;

        // Index of the snap point used during placement
        public int SnapPointIndex;

        // List of neighbouring ConstructedItem IDs (graph connections)
        [SerializeField]
        private List<int> _neighbours;

        /// <summary>
        /// Public accessor for neighbour IDs.
        /// </summary>
        public List<int> Neighbours
        {
            get => _neighbours;
            set => _neighbours = value;
        }

        // Runtime behaviour attached to the instantiated GameObject
        private ConstructedItemBehaviour _constructedItemBehaviour;
        public ConstructedItemBehaviour ConstructedItemBehaviour
        {
            get => _constructedItemBehaviour;
            set => _constructedItemBehaviour = value;
        }

        // ID of the BuildingArea this item belongs to
        private string _buildingAreaId;
        public string BuildingAreaId
        {
            get => _buildingAreaId;
            set => _buildingAreaId = value;
        }

        // Debug flag for verbose logging
        private bool _debugEnabled = true;
        public bool DebugEnabled
        {
            get => _debugEnabled;
            set => _debugEnabled = value;
        }

        /// <summary>
        /// Constructor initializes the neighbour list.
        /// </summary>
        public ConstructedItem()
        {
            _neighbours = new List<int>();
        }

        /// <summary>
        /// Adds a neighbour connection by ID if it does not already exist.
        /// </summary>
        /// <param name="id">ID of the neighbouring ConstructedItem</param>
        /// <returns>True if added, false if already present</returns>
        public bool AddNeighbour(int id)
        {
            // Check if neighbour already exists
            bool found = Neighbours.Contains(id);

            // Add neighbour if not found
            if (!found)
            {
                Neighbours.Add(id);
                return true;
            }

            // Log if duplicate neighbour detected
            if (_debugEnabled)
                Debug.Log($"AddNeighbour(): <color=red>neighbour already in the list</color>");

            return false;
        }

        /// <summary>
        /// Removes this item from all its neighbours' neighbour lists.
        /// Handles both local BuildingArea and cross-area cases.
        /// </summary>
        public void RemoveSelfFromNeighbours()
        {
            foreach (int id in Neighbours)
            {
                // Try to find neighbour in the same building area
                BuildingArea area = Builder.Instance.GetBuildingAreaByID(BuildingAreaId);
                ConstructedItem c = area.GetConstructedItem(id);

                if (c != null)
                {
                    c.Neighbours.Remove(this.Id);
                }
                else
                {
                    // Fallback: neighbour exists in a different building area
                    ConstructedItem gc = Builder.Instance.GetConstructedItem(id);
                    if (gc != null)
                    {
                        gc.Neighbours.Remove(this.Id);
                    }
                }
            }
        }

        /// <summary>
        /// Returns all neighbour connections as weighted edges for graph/path algorithms.
        /// </summary>
        public List<Edge> GetAllNeighbourEdges()
        {
            BuildingArea area = Builder.Instance.GetBuildingAreaByID(BuildingAreaId);
            List<Edge> list = new List<Edge>();

            foreach (int constructedItemId in Neighbours)
            {
                ConstructedItem c = area.GetConstructedItem(constructedItemId);
                if (c != null)
                {
                    // Edge cost is based on neighbour strength
                    Edge e = new Edge(constructedItemId, c.Strength);
                    list.Add(e);
                }
                else
                {
                    // Neighbour is not in the same area (unexpected state)
                    if (_debugEnabled)
                        Debug.Log(
                            $"ConstructedItem.GetAllNeighbourEdges(): <color=red> in {area.Id} found an edge for {this.Id} with ID={constructedItemId} into another area </color>");
                }
            }

            return list;
        }
    }

    /// <summary>
    /// Represents a logical building area that groups constructed items together.
    /// Handles lifecycle, stability checks, and cleanup.
    /// </summary>
    [Serializable]
    public class BuildingArea
    {
        // Unique identifier for this building area
        public string Id;

        // World-space center position
        public Vector3 Position;

        // Effective radius of the building area
        public float Range;

        // Grid-based position for spatial partitioning
        [SerializeField]
        private Vector2Int _gridPosition;

        // Size of the building area (grid units)
        [SerializeField]
        private int _size;

        public Vector2Int GridPosition
        {
            get => _gridPosition;
            private set => _gridPosition = value;
        }

        public int Size
        {
            get => _size;
            private set => _size = value;
        }

        // All constructed items in this building area
        [SerializeField]
        private List<ConstructedItem> _constructedItems;

        // Read-only array accessor
        public ConstructedItem[] ConstructedItems => _constructedItems.ToArray();

        // Debug flag
        private bool _debugEnabled = true;
        public bool DebugEnabled
        {
            get => _debugEnabled;
            set => _debugEnabled = value;
        }

        /// <summary>
        /// Constructor initializes internal item list.
        /// </summary>
        public BuildingArea()
        {
            _constructedItems = new List<ConstructedItem>();
        }

        /// <summary>
        /// Initializes grid metadata for this building area.
        /// </summary>
        public void Initialize(Vector2Int gridPos, int size)
        {
            GridPosition = gridPos;
            Size = size;
        }

        /// <summary>
        /// Removes all constructed items from this building area.
        /// </summary>
        public void ClearAllConstructedItems()
        {
            _constructedItems.Clear();
        }

        /// <summary>
        /// Retrieves a constructed item by ID.
        /// </summary>
        public ConstructedItem GetConstructedItem(int id)
        {
            return _constructedItems.Find(x => x.Id == id);
        }

        /// <summary>
        /// Creates and registers a new constructed item based on a build template
        /// and its instantiated GameObject.
        /// </summary>
        public ConstructedItem AddBuildItem(
            BuildTemplate buildTemplate,
            GameObject instantiatedBuildItem,
            bool grounded,
            bool debugEnabled)
        {
            // Create data model
            ConstructedItem constructedItem = new ConstructedItem
            {
                Strength = buildTemplate.strength,
                BuildTemplateId = buildTemplate.id,
                BuildTemplateName = buildTemplate.name,
                DebugEnabled = debugEnabled,
                Position = instantiatedBuildItem.transform.position,
                Rotation = instantiatedBuildItem.transform.rotation,
                Grounded = grounded,
                Id = Builder.Instance.GetNewGlobalID(),
                BuildingAreaId = this.Id
            };

            // Register item
            _constructedItems.Add(constructedItem);

            // Link runtime behaviour
            ConstructedItemBehaviour cib = instantiatedBuildItem.GetComponent<ConstructedItemBehaviour>();
            cib.id = constructedItem.Id;
            cib.buildingArea = this;
            cib.constructedItem = constructedItem;
            cib.DebugEnabled = debugEnabled;

            constructedItem.ConstructedItemBehaviour = cib;

            return constructedItem;
        }

        /// <summary>
        /// Logs detailed information about all constructed items in this area.
        /// </summary>
        public void ListAllConstructedItems()
        {
            Debug.Log($"BuildingArea: ListAllConstructedItems in range {Range}");

            for (int i = 0; i < _constructedItems.Count; i++)
            {
                Debug.Log("***************************************************************************************");
                Debug.Log(
                    $"{i} {_constructedItems[i].BuildTemplateName}[{_constructedItems[i].Id}] at {_constructedItems[i].Position} and grounded {_constructedItems[i].Grounded}");
                Debug.Log($" <color=yellow>distance {_constructedItems[i].Distance}</color>");
                Debug.Log($" has {_constructedItems[i].Neighbours.Count} neighbours:");

                for (int j = 0; j < _constructedItems[i].Neighbours.Count; j++)
                {
                    ConstructedItem c = GetConstructedItem(_constructedItems[i].Neighbours[j]);
                    Debug.Log(
                        $"Neighbour {j} {_constructedItems[i].Neighbours[j]} is grounded {c.Grounded}");
                }
            }
        }

        /// <summary>
        /// Displays distance information for all constructed items.
        /// </summary>
        public void ShowDistances()
        {
            for (int i = 0; i < _constructedItems.Count; i++)
            {
                ShowDistance(_constructedItems[i]);
            }
        }

        /// <summary>
        /// Displays distance info for a single constructed item via its behaviour.
        /// </summary>
        public void ShowDistance(ConstructedItem constructedItem)
        {
            ConstructedItemBehaviour cib = constructedItem.ConstructedItemBehaviour;
            if (cib != null)
                cib.ShowDistance();
        }

        /// <summary>
        /// Removes all constructed items that are not connected to ground
        /// (Distance == Infinity).
        /// </summary>
        public void RemoveWeakConstructedItems()
        {
            for (int i = _constructedItems.Count - 1; i >= 0; i--)
            {
                ConstructedItem c = _constructedItems[i];

                if (float.IsPositiveInfinity(c.Distance))
                {
                    // Remove neighbour references
                    c.RemoveSelfFromNeighbours();
                    c.Neighbours.Clear();

                    // Remove from area
                    _constructedItems.Remove(c);

                    // Trigger visual feedback and destroy
                    c.ConstructedItemBehaviour.SetMeshColor(new Color(0, 0, 1.0f));
                    c.ConstructedItemBehaviour.RemoveConstructedItem(true);
                }
            }
        }

        /// <summary>
        /// Removes weak constructed items from a provided subset list.
        /// </summary>
        public void RemoveWeakConstructedItems(List<ConstructedItem> constructedItems)
        {
            for (int i = constructedItems.Count - 1; i >= 0; i--)
            {
                ConstructedItem c = constructedItems[i];
                if (float.IsPositiveInfinity(c.Distance))
                {
                    RemoveWeakConstructedItem(c);
                }
            }
        }

        /// <summary>
        /// Removes a single weak constructed item from this building area.
        /// </summary>
        public void RemoveWeakConstructedItem(ConstructedItem constructedItem)
        {
            if (float.IsPositiveInfinity(constructedItem.Distance))
            {
                // Remove neighbour references
                constructedItem.RemoveSelfFromNeighbours();
                constructedItem.Neighbours.Clear();

                // Remove from area
                _constructedItems.Remove(constructedItem);

                // Trigger visual feedback and destroy
                constructedItem.ConstructedItemBehaviour.SetMeshColor(Color.yellow);
                constructedItem.ConstructedItemBehaviour.RemoveConstructedItem(false);
            }
        }
    }
}
