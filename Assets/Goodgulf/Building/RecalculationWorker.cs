using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using System.Threading;
using UnityEngine.Serialization;

namespace Goodgulf.Building
{
    /// <summary>
    /// Periodically recalculates structural integrity / distance-to-grounded
    /// for constructed items in one or more BuildingAreas.
    ///
    /// Heavy graph calculations are performed on a worker thread,
    /// while Unity API access is strictly limited to the main thread.
    /// </summary>
    public class RecalculationWorker : MonoBehaviour
    {
        /// <summary>
        /// Time (in seconds) between recalculation runs.
        /// </summary>
        [SerializeField] private float _updatePeriod = 5.0f;

        /// <summary>
        /// Maximum allowed distance from a grounded node.
        /// Items beyond this distance are considered unsupported.
        /// </summary>
        [SerializeField] private float _maxStrength = 6.0f;

        /// <summary>
        /// Public read-only accessor for max strength.
        /// </summary>
        public float MaxStrength { get { return _maxStrength; } }

        [Header("Debug Mode")]
        [SerializeField]
        private bool _debugEnabled = true;

        /// <summary>
        /// List of building areas that need recalculation.
        /// These are set externally by the builder system.
        /// </summary>
        private List<BuildingArea> _buildingAreas;

        /// <summary>
        /// True if there is work to do.
        /// </summary>
        public bool WeHaveInstructions
        {
            get => _buildingAreas.Count > 0;
        }

        /// <summary>
        /// Timer used to trigger periodic recalculation.
        /// </summary>
        private float _timer;

        /// <summary>
        /// Background thread used for heavy graph computations.
        /// </summary>
        private Thread _thread;

        /// <summary>
        /// Indicates whether the worker thread is currently running.
        /// </summary>
        private bool _running = false;

        /// <summary>
        /// Time when the last recalculation started (for debug timing).
        /// </summary>
        private float _lastStartTime;

        /// <summary>
        /// External read/write access to running state.
        /// </summary>
        public bool Running
        {
            get => _running;
            set => _running = value;
        }

        /// <summary>
        /// Singleton instance of the worker.
        /// </summary>
        public static RecalculationWorker Instance { get; private set; }

        private void Awake()
        {
            // Enforce singleton pattern
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;

            // Initialize building area list
            _buildingAreas = new List<BuildingArea>();
        }

        /// <summary>
        /// Sets the list of BuildingAreas that need recalculation.
        /// Overwrites any previous instructions.
        /// </summary>
        public void SetWorkerInstructions(List<BuildingArea> areas)
        {
            _buildingAreas.Clear();
            _buildingAreas.AddRange(areas);
        }

        /// <summary>
        /// Unity Update loop.
        /// Triggers recalculation at fixed intervals if not already running.
        /// </summary>
        void Update()
        {
            _timer += Time.deltaTime;

            // Start recalculation only if:
            // - enough time has passed
            // - no recalculation is currently running
            // - we have building areas to process
            if (_timer > _updatePeriod && !_running && WeHaveInstructions)
            {
                _timer = 0;
                _lastStartTime = Time.time;

                // Create worker thread
                _thread = new Thread(Recalculate);

                if (_debugEnabled)
                    Debug.Log($"Starting Recalculation worker thread at: {_lastStartTime}");

                // IMPORTANT:
                // Grounded checks must run on the MAIN THREAD
                // because they use Unity physics and colliders.
                for (int i = 0; i < _buildingAreas.Count; i++)
                {
                    BuildingArea a = _buildingAreas[i];

                    foreach (ConstructedItem c in a.ConstructedItems)
                    {
                        if (c.Grounded)
                        {
                            bool grounded = false;
                            int j = 0;

                            // Check all mesh colliders until one touches terrain or buildable layer
                            while (j < c.ConstructedItemBehaviour.MeshColliders.Count && !grounded)
                            {
                                grounded =
                                    Builder.Instance.IsTouchingAnyTerrain(
                                        c.ConstructedItemBehaviour.MeshColliders[j]) ||
                                    Builder.Instance.IsTouchingBuildableLayer(
                                        c.ConstructedItemBehaviour.MeshColliders[j]);

                                j++;
                            }

                            c.Grounded = grounded;
                        }
                    }
                }

                // Start worker thread
                _running = true;
                _thread.Start();
            }
        }

        /// <summary>
        /// Worker-thread method.
        /// Builds graph structures and performs Dijkstra calculations.
        /// MUST NOT call Unity API directly.
        /// </summary>
        private void Recalculate()
        {
            // Store all nodes per building area
            Dictionary<string, ConstructedItem[]> buildingAreaAllItems =
                new Dictionary<string, ConstructedItem[]>();

            // Store nodes within range per building area
            Dictionary<string, Dictionary<int, float>> buildingAreaItemsInRange =
                new Dictionary<string, Dictionary<int, float>>();

            for (int i = 0; i < _buildingAreas.Count; i++)
            {
                BuildingArea a = _buildingAreas[i];

                // Build Dijkstra graph: nodeId -> list of edges
                Dictionary<int, List<Edge>> graph = new Dictionary<int, List<Edge>>();

                ConstructedItem[] allItems = a.ConstructedItems;
                buildingAreaAllItems.Add(a.Id, allItems);

                for (int j = 0; j < allItems.Length; j++)
                {
                    ConstructedItem c = allItems[j];
                    graph.Add(c.Id, c.GetAllNeighbourEdges());
                }

                // Extract grounded nodes only
                ConstructedItem[] grounded =
                    allItems.Where(p => p.Grounded == true).ToArray();

                // For each grounded node, find all nodes within maxStrength
                List<Dictionary<int, float>> listAllNodesInRange =
                    new List<Dictionary<int, float>>();

                for (int k = 0; k < grounded.Length; k++)
                {
                    ConstructedItem c = grounded[k];

                    Dictionary<int, float> allNodesInRange =
                        DijkstraUnity.FindNodesWithinDistanceWithDistances(
                            graph, c.Id, _maxStrength);

                    listAllNodesInRange.Add(allNodesInRange);
                }

                // Merge results, keeping the shortest distance per node
                buildingAreaItemsInRange.Add(
                    a.Id,
                    DijkstraUnity.MergeByShortestDistance(listAllNodesInRange));
            }

            // Marshal results back to main thread
            MainThreadDispatcher.Enqueue(() =>
            {
                // Apply calculated distances to ConstructedItems
                for (int i = 0; i < _buildingAreas.Count; i++)
                {
                    BuildingArea a = _buildingAreas[i];

                    Dictionary<int, float> allNodesInRange =
                        buildingAreaItemsInRange[a.Id];

                    ConstructedItem[] allItems =
                        buildingAreaAllItems[a.Id];

                    if (_debugEnabled)
                        Debug.Log(
                            $"Write back results for BuildingArea {i} with Id {a.Id} " +
                            $"and {allNodesInRange.Count} out of {allItems.Length} nodes " +
                            $"in range {_maxStrength} of grounded");

                    for (int j = 0; j < allItems.Length; j++)
                    {
                        ConstructedItem c = allItems[j];

                        if (c.Grounded)
                        {
                            c.Distance = 0.0f;
                        }
                        else if (allNodesInRange.ContainsKey(c.Id))
                        {
                            c.Distance = allNodesInRange[c.Id];
                        }
                        else
                        {
                            c.Distance = float.PositiveInfinity;
                        }
                    }
                }

                // Mark worker as finished
                _running = false;

                if (_debugEnabled)
                    Debug.Log(
                        $"Recalculation for {_buildingAreas.Count} areas ready in: " +
                        $"{Time.time - _lastStartTime} seconds");

                // Post-processing per building area
                for (int i = 0; i < _buildingAreas.Count; i++)
                {
                    if (_debugEnabled)
                        _buildingAreas[i].ShowDistances();

                    _buildingAreas[i].RemoveWeakConstructedItems();
                }
            });
        }
    }
}
