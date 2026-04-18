// ============================================================
// Script:      TerrainGraphIntegration.cs
// Episode:     EP## — Pathfinding Performance Optimisations
// Description: Bridge between TerrainStreamingController and PathfindingGraph.
//              Changes from previous version:
//
//              Option A — Async (zero-stall) physics overlay
//                The PhysicsObstacleOverlay pass is now spread across
//                multiple frames instead of running all at once inside
//                FinaliseJob(). MaxOverlayNodesPerFrame controls the
//                budget. The buffer swap is deferred until the overlay
//                pass finishes, so agents always read fully-validated data.
//
//              Option C — Static obstacle cache
//                A bool[] array mirrors the graph's node layout and records
//                which cells were blocked by physics colliders. On partial
//                and full rebuilds the physics pass is skipped entirely for
//                nodes whose obstacle state has not been marked dirty. Call
//                InvalidateObstacleCache(worldMin, worldMax) after placing
//                or destroying any obstacle (e.g. player builds a wall).
//
//              Option F — Configurable graph resolution
//                All graph sizing and DirtyRegion conversion now uses
//                GraphConfig.NodesPerMetre. Set to 0.5 (default) for 2 m
//                cells, or 1.0 for 1 m cells. Graph width/height are
//                calculated in node-grid units, not world units.
//
// Author:      Goodgulf
// Date:        2026-03-14
// ============================================================

using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Goodgulf.TerrainUtils;
using Goodgulf.Logging;

namespace Goodgulf.Pathfinding
{
    /// <summary>
    /// Bridges <see cref="TerrainStreamingController"/> chunk events to the
    /// <see cref="PathfindingGraph"/> with three performance optimisations:
    /// streamed physics overlay (Option A), static obstacle cache (Option C),
    /// and configurable node resolution (Option F).
    /// </summary>
    [RequireComponent(typeof(TerrainStreamingController))]
    public class TerrainGraphIntegration : MonoBehaviour, IDebuggable
    {
        // ==================================================================
        // Inspector fields
        // ==================================================================

        [Header("Graph Config")]
        [SerializeField] private GraphConfig _config = GraphConfig.Default;

        [Header("Async Physics Overlay (Option A)")]
        [Tooltip(
            "Maximum nodes processed per frame during the physics obstacle overlay pass. " +
            "Lower values spread the cost across more frames. " +
            "Recommended: 2000–5000 for 60 fps targets.")]
        [SerializeField, Range(500, 20000)]
        private int _maxOverlayNodesPerFrame = 3000;

        [Header("Static Obstacle Cache (Option C)")]
        [Tooltip(
            "When enabled, the physics OverlapSphere pass is skipped for nodes " +
            "that have not been flagged dirty by InvalidateObstacleCache(). " +
            "Disable only if your obstacle geometry changes without explicit cache invalidation.")]
        [SerializeField]
        private bool _useObstacleCache = true;

        [Header("Debug")]
        [SerializeField] private bool _debugEnabled = false;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;

        [SerializeField, Tooltip("Draw walkable node gizmos in Scene view.")]
        private bool _drawGizmos = false;
        [SerializeField, Tooltip("Maximum nodes drawn per OnDrawGizmosSelected call.")]
        private int  _gizmoNodeLimit = 50_000;

        // ==================================================================
        // Public accessors
        // ==================================================================

        /// <summary>Singleton accessor. Set in Awake, cleared in OnDestroy.</summary>
        public static TerrainGraphIntegration Instance { get; private set; }

        /// <summary>The active pathfinding graph. Null until the first chunks load.</summary>
        public PathfindingGraph Graph { get; private set; }

        /// <summary>
        /// Read-only view of the current config.
        /// To change resolution at runtime, assign a new GraphConfig via
        /// <see cref="RebuildWithNewConfig"/> so the graph is correctly resized.
        /// </summary>
        public GraphConfig Config => _config;

        // ==================================================================
        // Internal state
        // ==================================================================

        private TerrainStreamingController _controller;

        // Known loaded chunks (coord → Terrain component)
        private readonly Dictionary<Vector2Int, Terrain> _knownChunks = new();

        // Dirty regions queued for partial graph rebuild
        private readonly Queue<DirtyRegion> _pendingDirtyRegions = new();

        // ------------------------------------------------------------------
        // Burst job tracking
        // ------------------------------------------------------------------

        private bool      _jobInFlight;
        private JobHandle _currentJobHandle;

        // Native arrays kept alive for the duration of a Burst job
        private NativeArray<float>               _jobHeights;
        private NativeArray<TerrainHeightSample> _jobChunks;
        private bool                             _jobIsPartial;
        private DirtyRegion                      _jobDirtyRegion;

        // Frame counter for rebuild cooldown
        private int _framesSinceLastRebuild;
        private const int RebuildCooldownFrames = 3;

        // ------------------------------------------------------------------
        // Option A — streamed physics overlay state
        // ------------------------------------------------------------------

        // True while the overlay pass is still iterating over the staging buffer
        private bool      _overlayInProgress;
        private int       _overlayCurrentIndex;   // flat index currently being processed
        private int       _overlayEndIndex;        // exclusive end index for this pass
        private DirtyRegion _overlayRegion;        // the region being overlaid

        private int _overlayNodeX;
        private int _overlayNodeZ;

        // ------------------------------------------------------------------
        // Option C — static obstacle cache
        // ------------------------------------------------------------------

        // One bool per node: true = this node has a physics obstacle on it
        // Persists across rebuilds; only cleared by InvalidateObstacleCache()
        private bool[] _obstacleCache;

        // Dirty flags for the obstacle cache: nodes in this set need re-checking
        private readonly HashSet<int> _obstacleDirtyNodes = new();

        // True when a full obstacle cache rebuild has been requested
        private bool _obstacleCacheFullDirty = true;

        private bool _initialised = false;

        // ==================================================================
        // Unity lifecycle
        // ==================================================================

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;

            if (!TryGetComponent(out _controller))
                Debug.LogError("TerrainGraphIntegration.Awake(): TerrainStreamingController not found.", this);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            CompleteCurrentJob(forceComplete: true);
            Graph?.Dispose();
            DisposeJobNativeArrays();

            _controller.onChunkLoadStart.RemoveListener(OnChunkStartLoading);
            _controller.onChunkLoadComplete.RemoveListener(OnChunkFullyLoaded);
        }

        public void InitializeListeners()
        {
            if (_controller == null)
            {
                this.LogError("TerrainStreamingController reference is null.");
                return;
            }
            _controller.onChunkLoadComplete.AddListener(OnChunkFullyLoaded);
            _controller.onChunkLoadStart.AddListener(OnChunkStartLoading);
        }

        public void Initialize()
        {
            _heightmapCache.Clear();
            _heightmapDirty.Clear();
            _initialised = true;

            if (_controller == null)
            {
                this.LogError("TerrainGraphIntegration.Initialize(): TerrainStreamingController reference is null.");
                return;
            }

            Vector2 cs = new Vector2(_controller.ChunkSize(), _controller.ChunkSize());

            // Pass 1 — register all already-loaded chunks into _knownChunks
            foreach (Transform child in _controller.TerrainParent().transform)
            {
                if (!child.TryGetComponent(out Terrain t)) continue;
                Vector3 pos = child.position;
                int cx = Mathf.RoundToInt(pos.x / cs.x);
                int cz = Mathf.RoundToInt(pos.z / cs.y);
                var coord = new Vector2Int(cx, cz);
                _knownChunks[coord] = t;
                _heightmapDirty.Add(coord);  // ensure cache is fresh
            }

            // Pass 2 — build graph from all registered chunks, then do one full rebuild
            if (_knownChunks.Count > 0)
            {
                BuildOrResizeGraph();
                ScheduleFullRebuild();
            }

            this.LogInfo($"TerrainGraphIntegration initialized with {_knownChunks.Count} chunks.");
        }

        private void OnChunkStartLoading(Vector2Int chunkCoord)
        {
            this.LogWarning($"start loading chunkCoord={chunkCoord}");
        }

        private void OnChunkFullyLoaded(Vector2Int chunkCoord)
        {
            if (!_initialised) return;  // ignore events that fire before Initialize()

            if (_controller.TerrainParent() == null) return;

            Vector2 cs = new Vector2(_controller.ChunkSize(), _controller.ChunkSize());
            foreach (Transform child in _controller.TerrainParent().transform)
            {
                if (!child.TryGetComponent(out Terrain t)) continue;
                Vector3 pos = child.position;
                int cx = Mathf.RoundToInt(pos.x / cs.x);
                int cz = Mathf.RoundToInt(pos.z / cs.y);
                if (cx != chunkCoord.x || cz != chunkCoord.y) continue;

                if (!_knownChunks.ContainsKey(chunkCoord))
                {
                    _knownChunks[chunkCoord] = t;
                    _heightmapDirty.Add(chunkCoord);

                    if (Graph == null)
                    {
                        BuildOrResizeGraph();
                        ScheduleFullRebuild();
                    }
                    else if (GraphNeedsResize())
                    {
                        CompleteCurrentJob(forceComplete: true);
                        AbortOverlay();
                        BuildOrResizeGraph();
                        ScheduleFullRebuild();
                    }
                    else
                    {
                        // Graph is large enough — just patch the new chunk's region
                        _pendingDirtyRegions.Enqueue(
                            DirtyRegion.FromChunk(chunkCoord, cs, _config).Expanded(2));
                    }
                }
                break;
            }
        }

        private void Update()
        {
            // Guard against Update running before terrains have been competely generate.
            if(!_initialised)
                return;

            _framesSinceLastRebuild++;

            // 1. Discover chunk changes from the streaming controller
            SyncChunks();

            // 2. Tick the streamed physics overlay (Option A)
            if (_overlayInProgress)
                TickOverlay();

            // 3. Finalise any Burst job that finished last frame
            if (_jobInFlight && _currentJobHandle.IsCompleted)
                FinaliseJob();

            // 4. Schedule pending rebuilds when the system is idle
            if (!_jobInFlight && !_overlayInProgress && _framesSinceLastRebuild >= RebuildCooldownFrames)
            {
                if (_pendingDirtyRegions.Count > 0)
                    SchedulePartialRebuild(MergePendingRegions());
            }
        }


        // ==================================================================
        // Chunk synchronisation
        // ==================================================================

        private void SyncChunks()
        {
            // Only handle chunk removals here — additions are handled by OnChunkFullyLoaded
            var currentChunks = GetLoadedChunksFromController();
            var toRemove = new List<Vector2Int>();
            foreach (var kvp in _knownChunks)
            {
                if (!currentChunks.ContainsKey(kvp.Key))
                    toRemove.Add(kvp.Key);
            }
            foreach (var coord in toRemove)
                _knownChunks.Remove(coord);
        }

        // ==================================================================
        // Graph construction / resize
        // ==================================================================

        private void BuildOrResizeGraph()
        {
            if (_knownChunks.Count == 0) return;

            Vector2 cs = GetChunkSize();

            // Size the graph to cover unloadRadius + 1 chunks around the player
            // so it never needs resizing as the player moves within normal range.
            Vector2Int playerChunk = GetPlayerChunk();
            int margin = _controller.ViewDistance() + 1;

            float worldMinX = (playerChunk.x - margin) * cs.x;
            float worldMinZ = (playerChunk.y - margin) * cs.y;
            float worldMaxX = (playerChunk.x + margin + 1) * cs.x;
            float worldMaxZ = (playerChunk.y + margin + 1) * cs.y;

            int originX = Mathf.FloorToInt(worldMinX * _config.NodesPerMetre) - 2;
            int originZ = Mathf.FloorToInt(worldMinZ * _config.NodesPerMetre) - 2;
            int width   = Mathf.CeilToInt((worldMaxX - worldMinX) * _config.NodesPerMetre) + 4;
            int height  = Mathf.CeilToInt((worldMaxZ - worldMinZ) * _config.NodesPerMetre) + 4;

            width  = Mathf.Clamp(width,  64, 8192);
            height = Mathf.Clamp(height, 64, 8192);

            Graph?.Dispose();
            Graph = new PathfindingGraph(new int2(originX, originZ), width, height, _config);

            _obstacleCache          = new bool[Graph.TotalNodes];
            _obstacleCacheFullDirty = true;

            if (_debugEnabled)
                this.LogVerbose($"TerrainGraphIntegration.BuildOrResizeGraph(): " +
                        $"world bounds ({worldMinX},{worldMinZ})..({worldMaxX},{worldMaxZ}) " +
                        $"origin=({originX},{originZ}) size={width}×{height} nodes " +
                        $"@ {_config.NodesPerMetre} n/m");
        }
        private bool GraphNeedsResize()
        {
            if (Graph == null) return true;
            foreach (var kvp in _knownChunks)
            {
                if (kvp.Value == null) continue;
                Vector3 pos = kvp.Value.transform.position;
                Vector2 cs  = GetChunkSize();
                // Check all four corners of the chunk
                if (!Graph.InBoundsWorld(pos.x,            pos.z) ||
                    !Graph.InBoundsWorld(pos.x + cs.x,     pos.z) ||
                    !Graph.InBoundsWorld(pos.x,             pos.z + cs.y) ||
                    !Graph.InBoundsWorld(pos.x + cs.x,     pos.z + cs.y))
                    return true;
            }
            return false;
        }

        // ==================================================================
        // Full rebuild
        // ==================================================================

        private void ScheduleFullRebuild()
        {
            this.LogInfo("ScheduleFullRebuild");
            if (Graph == null || _knownChunks.Count == 0) {
                this.LogWarning("Graph or known chunks are null/empty");
                return;
            }
            this.LogInfo("Starting full graph rebuild");
            CompleteCurrentJob(forceComplete: true);
            AbortOverlay();

            BuildHeightArrays(out NativeArray<float> heights,
                              out NativeArray<TerrainHeightSample> chunks);

            if(_debugEnabled)
                this.LogVerbose($"<color=green>ScheduleFullRebuild: {chunks.Length} chunks, {heights.Length} height samples</color>");
                
            for (int i = 0; i < chunks.Length; i++)
            {
                TerrainHeightSample s = chunks[i];
                if(_debugEnabled)
                    this.LogVerbose($"  Chunk[{i}]: origin=({s.ChunkOriginXZ.x},{s.ChunkOriginXZ.y}) " +
                        $"size=({s.ChunkSizeXZ.x},{s.ChunkSizeXZ.y}) " +
                        $"res={s.HeightmapRes} terrainHeight={s.TerrainHeight} baseIdx={s.ChunkIndex}");
            }


            _jobHeights   = heights;
            _jobChunks    = chunks;
            _jobIsPartial = false;

            var job = new FullGraphRebuildJob
            {
                GraphWidth        = Graph.Width,
                GraphHeight       = Graph.Height,
                GraphOrigin       = Graph.Origin,
                NodesPerMetre     = _config.NodesPerMetre,
                MaxSlopeAngle     = _config.MaxSlopeAngle,
                MinWalkableHeight = _config.MinWalkableHeight,
                MaxWalkableHeight = _config.MaxWalkableHeight,
                Heights           = heights,
                Chunks            = chunks,
                Output            = Graph.StagingBuffer,
            };

            _currentJobHandle       = job.Schedule(Graph.TotalNodes, 64);
            _jobInFlight            = true;
            _framesSinceLastRebuild = 0;
        }

        // ==================================================================
        // Partial (dirty-region) rebuild
        // ==================================================================

        private void SchedulePartialRebuild(DirtyRegion region)
        {
            this.LogInfo("SchedulePartialRebuild");

            if (Graph == null) {
                this.LogWarning("Graph is null");
                return;
            }

            DirtyRegion clamped = Graph.ClampRegion(region);
            if (!clamped.IsValid) return;

            BuildHeightArrays(out NativeArray<float> heights,
                              out NativeArray<TerrainHeightSample> chunks);

            _jobHeights     = heights;
            _jobChunks      = chunks;
            _jobIsPartial   = true;
            _jobDirtyRegion = clamped;

            // Stage 1: copy live → staging so untouched nodes survive
            var copyJob = new CopyBufferJob
            {
                Src = Graph.LiveBuffer,
                Dst = Graph.StagingBuffer,
            };
            JobHandle copyHandle = copyJob.Schedule();

            // Stage 2: overwrite only the dirty region
            var patchJob = new PartialGraphRebuildJob
            {
                GraphWidth        = Graph.Width,
                GraphOrigin       = Graph.Origin,
                NodesPerMetre     = _config.NodesPerMetre,
                DirtyMin          = clamped.Min,
                DirtyMax          = clamped.Max,
                MaxSlopeAngle     = _config.MaxSlopeAngle,
                MinWalkableHeight = _config.MinWalkableHeight,
                MaxWalkableHeight = _config.MaxWalkableHeight,
                Heights           = heights,
                Chunks            = chunks,
                Output            = Graph.StagingBuffer,
            };

            _currentJobHandle       = patchJob.Schedule(Graph.TotalNodes, 64, copyHandle);
            _jobInFlight            = true;
            _framesSinceLastRebuild = 0;
        }

        // ==================================================================
        // Job finalisation
        // ==================================================================

        private void FinaliseJob()
        {
            _currentJobHandle.Complete();
            _jobInFlight = false;

            // Determine the region the overlay pass needs to cover
            DirtyRegion overlayRegion = _jobIsPartial
                ? _jobDirtyRegion
                : new DirtyRegion
                {
                    Min = Graph.Origin,
                    Max = new int2(Graph.Origin.x + Graph.Width - 1,
                                   Graph.Origin.y + Graph.Height - 1),
                };

            DisposeJobNativeArrays();

            // Option A: begin streamed overlay instead of blocking here
            BeginOverlay(overlayRegion);
        }

        private void CompleteCurrentJob(bool forceComplete)
        {
            if (!_jobInFlight) return;
            if (!forceComplete) return;

            _currentJobHandle.Complete();
            _jobInFlight = false;
            DisposeJobNativeArrays();
        }

        // ==================================================================
        // Option A — Streamed physics overlay
        // ==================================================================

        /// <summary>
        /// Starts the physics overlay pass for <paramref name="region"/>.
        /// Instead of processing all nodes synchronously, the work is spread
        /// across multiple frames via <see cref="TickOverlay"/>.
        /// The graph buffer swap is deferred until the pass completes.
        /// </summary>
        private void BeginOverlay(DirtyRegion region)
        {
            _overlayRegion = Graph.ClampRegion(region);

            if (!_overlayRegion.IsValid)
            {
                Graph.NotifyRebuildComplete();
                return;
            }

            // Store the 2D iteration state instead of a flat index
            _overlayNodeX    = _overlayRegion.Min.x;
            _overlayNodeZ    = _overlayRegion.Min.y;
            _overlayInProgress = true;

            int regionWidth  = _overlayRegion.Max.x - _overlayRegion.Min.x + 1;
            int regionHeight = _overlayRegion.Max.y - _overlayRegion.Min.y + 1;

            this.LogInfo($"TerrainGraphIntegration.BeginOverlay(): region " +
                        $"[{_overlayRegion.Min}..{_overlayRegion.Max}] " +
                        $"{regionWidth}×{regionHeight} = {regionWidth * regionHeight} nodes, " +
                        $"{_maxOverlayNodesPerFrame} nodes/frame.");
        }

        /// <summary>
        /// Processes up to <see cref="_maxOverlayNodesPerFrame"/> nodes per frame.
        /// When all nodes are processed, triggers the graph buffer swap.
        /// </summary>
        private void TickOverlay()
        {
            if (Graph == null) { AbortOverlay(); return; }

            var staging   = Graph.StagingBuffer;
            int processed = 0;

            while (processed < _maxOverlayNodesPerFrame)
            {
                // Advance to next node in region
                if (_overlayNodeX > _overlayRegion.Max.x)
                {
                    _overlayNodeX = _overlayRegion.Min.x;
                    _overlayNodeZ++;
                }

                if (_overlayNodeZ > _overlayRegion.Max.y)
                {
                    FinishOverlay();
                    return;
                }

                int idx = Graph.NodeToIndex(_overlayNodeX, _overlayNodeZ);
                _overlayNodeX++;
                processed++;

                if (idx < 0) continue;

                NodeData nd = staging[idx];

                // Option C: use cache if clean
                if (_useObstacleCache && !_obstacleCacheFullDirty && !_obstacleDirtyNodes.Contains(idx))
                {
                    if (_obstacleCache[idx]) { nd.Walkable = false; staging[idx] = nd; }
                    continue;
                }

                if (!nd.Walkable)
                {
                    if (_useObstacleCache && idx < _obstacleCache.Length)
                        _obstacleCache[idx] = false;
                    continue;
                }

                bool hasObstacle = CheckPhysicsObstacle(nd.WorldY, new int2(_overlayNodeX - 1, _overlayNodeZ));
                if (_useObstacleCache && idx < _obstacleCache.Length)
                    _obstacleCache[idx] = hasObstacle;
                if (hasObstacle) { nd.Walkable = false; staging[idx] = nd; }
            }
        }

        private bool CheckPhysicsObstacle(float worldY, int2 nodeCoord)
        {
            // Convert node coordinates back to world space for the sphere test
            float worldX = nodeCoord.x * _config.NodeSpacing;
            float worldZ = nodeCoord.y * _config.NodeSpacing;

            Vector3 centre = new Vector3(
                worldX,
                worldY + _config.EffectiveObstacleRadius,
                worldZ);

            // We reuse a small static buffer to avoid per-call allocation
            int count = Physics.OverlapSphereNonAlloc(
                centre,
                _config.EffectiveObstacleRadius,
                s_overlapBuffer,
                _config.ObstacleLayers,
                QueryTriggerInteraction.Ignore);

            return count > 0;
        }

        // Shared small buffer for OverlapSphereNonAlloc (8 colliders max per node)
        private static readonly Collider[] s_overlapBuffer = new Collider[8];

        private void FinishOverlay()
        {
            _overlayInProgress   = false;
            _obstacleCacheFullDirty = false;
            _obstacleDirtyNodes.Clear();

            // Now safe to swap — agents read fully-validated data next frame
            Graph.NotifyRebuildComplete();

            this.LogInfo("TerrainGraphIntegration.FinishOverlay(): overlay complete, buffer swapped.");
        }

        private void AbortOverlay()
        {
            _overlayInProgress = false;
        }

        // ==================================================================
        // Height data extraction  (main thread, before scheduling jobs)
        // ==================================================================

        private void BuildHeightArrays(
            out NativeArray<float>               heights,
            out NativeArray<TerrainHeightSample> chunks)
        {
            var chunkList   = new List<TerrainHeightSample>(_knownChunks.Count);
            var heightsList = new List<float>();

            foreach (var kvp in _knownChunks)
            {
                Terrain terrain = kvp.Value;
                if (terrain == null) 
                {
                    this.LogWarning($"TerrainGraphIntegration.BuildHeightArrays(): " +
                                    $"chunk {kvp.Key} has null Terrain reference, skipping.");
                    continue;
                }

                TerrainData td = terrain.terrainData;
                if (td == null) 
                {
                    this.LogWarning($"TerrainGraphIntegration.BuildHeightArrays(): " +
                                    $"chunk {kvp.Key} has null TerrainData reference, skipping.");
                    continue;
                }

                // Option C: use cached heightmap if available
                int      res  = td.heightmapResolution;
                float[,] rawH = GetOrCacheHeightmap(kvp.Key, td, res);

                // Debug.Log($"<color=red>Chunk {kvp.Key}: heightmap[0,0]={rawH[0,0]:F4}  size={td.size}  pos={terrain.transform.position}</color>");
                Debug.Log($"<color=red>BuildHeightArrays chunk {kvp.Key}: " +
              $"pos={terrain.transform.position} " +
              $"size={td.size} " +
              $"res={res} " +
              $"h[0,0]={rawH[0,0]:F4} " +
              $"h[res/2,res/2]={rawH[res/2,res/2]:F4}</color>");

                int baseIdx = heightsList.Count;
                for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    heightsList.Add(rawH[z, x]);

                chunkList.Add(new TerrainHeightSample
                {
                    ChunkOriginXZ = new float2(terrain.transform.position.x,
                                               terrain.transform.position.z),
                    ChunkSizeXZ   = new float2(td.size.x, td.size.z),
                    TerrainHeight = td.size.y,
                    HeightmapRes  = res,
                    ChunkIndex    = baseIdx,
                });

                //if(_debugEnabled)
                //    this.LogInfo($"chunkSize={_controller.ChunkSize()} and td.size={td.size} for chunk {kvp.Key}");
            }

            heights = new NativeArray<float>(heightsList.ToArray(), Allocator.TempJob);
            chunks  = new NativeArray<TerrainHeightSample>(chunkList.ToArray(), Allocator.TempJob);
        }

        // ------------------------------------------------------------------
        // Option C — Heightmap cache
        // Avoids repeated GetHeights() allocations on partial rebuilds.
        // ------------------------------------------------------------------

        private readonly Dictionary<Vector2Int, float[,]> _heightmapCache = new();
        private readonly HashSet<Vector2Int>               _heightmapDirty = new();

        private float[,] GetOrCacheHeightmap(Vector2Int chunkCoord, TerrainData td, int res)
        {
            if (!_heightmapDirty.Contains(chunkCoord) &&
                _heightmapCache.TryGetValue(chunkCoord, out float[,] cached))
                return cached;

            // Re-sample from the TerrainData
            float[,] fresh = td.GetHeights(0, 0, res, res);
            _heightmapCache[chunkCoord] = fresh;
            _heightmapDirty.Remove(chunkCoord);
            return fresh;
        }

        /// <summary>
        /// Marks a chunk's heightmap cache as stale.
        /// Call this after <see cref="StreamingTerrainGeneratorJobs.GenerateChunk"/>
        /// modifies a chunk's terrain data.
        /// </summary>
        public void InvalidateHeightmapCache(Vector2Int chunkCoord) =>
            _heightmapDirty.Add(chunkCoord);

        private void DisposeJobNativeArrays()
        {
            if (_jobHeights.IsCreated) _jobHeights.Dispose();
            if (_jobChunks.IsCreated)  _jobChunks.Dispose();
        }

        // ==================================================================
        // Public API — runtime terrain deformation
        // ==================================================================

        /// <summary>
        /// Schedules a partial graph rebuild covering a world-space AABB.
        /// Call after any runtime terrain modification (explosion, digging, etc.).
        /// </summary>
        public void RequestDirtyRegionRebuild(Vector3 worldMin, Vector3 worldMax)
        {
            _pendingDirtyRegions.Enqueue(
                DirtyRegion.FromWorldAABB(worldMin, worldMax, _config).Expanded(2));
        }

        /// <summary>
        /// Convenience overload for spherical deformation areas.
        /// </summary>
        public void RequestDirtyRegionRebuild(Vector3 worldCenter, float radius)
        {
            Vector3 r3 = new Vector3(radius, radius, radius);
            RequestDirtyRegionRebuild(worldCenter - r3, worldCenter + r3);
        }

        // ==================================================================
        // Option C — Obstacle cache invalidation (public API)
        // ==================================================================

        /// <summary>
        /// Invalidates the obstacle cache for a world-space AABB.
        /// Call this after placing or destroying any static obstacle
        /// (e.g. the player constructs or demolishes a building).
        /// Affected nodes will be re-checked by physics on the next rebuild.
        /// </summary>
        public void InvalidateObstacleCache(Vector3 worldMin, Vector3 worldMax)
        {
            if (Graph == null) return;

            DirtyRegion region = Graph.ClampRegion(
                DirtyRegion.FromWorldAABB(worldMin, worldMax, _config).Expanded(1));

            if (!region.IsValid) return;

            for (int nz = region.Min.y; nz <= region.Max.y; nz++)
            for (int nx = region.Min.x; nx <= region.Max.x; nx++)
            {
                int idx = Graph.NodeToIndex(nx, nz);
                if (idx >= 0)
                    _obstacleDirtyNodes.Add(idx);
            }

            // Also queue a partial rebuild so the graph is updated promptly
            _pendingDirtyRegions.Enqueue(region);

            if (_debugEnabled)
                Debug.Log($"TerrainGraphIntegration.InvalidateObstacleCache(): " +
                          $"invalidated {_obstacleDirtyNodes.Count} nodes in region " +
                          $"[{worldMin}..{worldMax}].");
        }

        /// <summary>
        /// Convenience overload for spherical obstacle invalidation.
        /// </summary>
        public void InvalidateObstacleCache(Vector3 worldCenter, float radius)
        {
            Vector3 r3 = new Vector3(radius, radius, radius);
            InvalidateObstacleCache(worldCenter - r3, worldCenter + r3);
        }

        // ==================================================================
        // Option F — Runtime resolution change
        // ==================================================================

        /// <summary>
        /// Disposes the current graph, applies a new config (e.g. changed
        /// <see cref="GraphConfig.NodesPerMetre"/>), then triggers a full rebuild.
        /// Blocks for one frame while the graph resizes.
        /// </summary>
        public void RebuildWithNewConfig(GraphConfig newConfig)
        {
            _config = newConfig;
            CompleteCurrentJob(forceComplete: true);
            AbortOverlay();
            _heightmapCache.Clear();
            _heightmapDirty.Clear();
            _obstacleCacheFullDirty = true;
            _obstacleDirtyNodes.Clear();
            _pendingDirtyRegions.Clear();
            BuildOrResizeGraph();
            ScheduleFullRebuild();
        }

        // ==================================================================
        // Synchronous force rebuild (editor tooling / loading screens)
        // ==================================================================

        /// <summary>
        /// Forces an immediate full rebuild, blocking until complete.
        /// Prefer <see cref="RequestDirtyRegionRebuild"/> for runtime changes.
        /// </summary>
        public void ForceFullRebuildSync()
        {
            AbortOverlay();
            CompleteCurrentJob(forceComplete: true);
            ScheduleFullRebuild();
            _currentJobHandle.Complete();
            _jobInFlight = false;
            DisposeJobNativeArrays();

            // Run overlay synchronously (no frame budget)
            int saved = _maxOverlayNodesPerFrame;
            _maxOverlayNodesPerFrame = int.MaxValue;
            BeginOverlay(new DirtyRegion
            {
                Min = Graph.Origin,
                Max = new int2(Graph.Origin.x + Graph.Width  - 1,
                               Graph.Origin.y + Graph.Height - 1),
            });
            TickOverlay(); // processes all nodes in a single call
            _maxOverlayNodesPerFrame = saved;
        }

        // ==================================================================
        // Path request entry point (agents call this)
        // ==================================================================

        /// <summary>Requests an A* path between two world-space positions.</summary>
        public PathResult RequestPath(Vector3 from, Vector3 to, int maxIterations = 100_000)
        {
            if (Graph == null)
            {
                this.LogWarning("Graph is null in RequestPath");
                return new PathResult { Status = PathStatus.NoPath };
            }

            return AStarSearch.FindPath(Graph, from, to, maxIterations);
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        private Dictionary<Vector2Int, Terrain> GetLoadedChunksFromController()
        {
            var result = new Dictionary<Vector2Int, Terrain>();
            if (_controller.TerrainParent() == null) return result;

            Vector2 cs = GetChunkSize();
            foreach (Transform child in _controller.TerrainParent().transform)
            {
                if (!child.TryGetComponent(out Terrain t)) continue;
                Vector3 pos = child.position;
                int cx = Mathf.RoundToInt(pos.x / cs.x);
                int cz = Mathf.RoundToInt(pos.z / cs.y);
                result[new Vector2Int(cx, cz)] = t;
            }
            return result;
        }


        private Vector2 GetChunkSize()
        {
            return new Vector2(_controller.ChunkSize(), _controller.ChunkSize());
        }

        private Vector2Int GetPlayerChunk()
        {
            if (_controller.Player() == null) return Vector2Int.zero;

            Vector2 cs = GetChunkSize();
            // Use the player's world position divided by chunk size.
            // This already works correctly as long as chunk coords in
            // _knownChunks were derived the same way (from transform.position / chunkSize).
            return new Vector2Int(
                Mathf.FloorToInt(_controller.Player().position.x / cs.x),
                Mathf.FloorToInt(_controller.Player().position.z / cs.y));
        }
        private DirtyRegion MergePendingRegions()
        {
            DirtyRegion merged = _pendingDirtyRegions.Dequeue();
            while (_pendingDirtyRegions.Count > 0)
            {
                DirtyRegion next = _pendingDirtyRegions.Dequeue();
                merged.Min = new int2(
                    Mathf.Min(merged.Min.x, next.Min.x),
                    Mathf.Min(merged.Min.y, next.Min.y));
                merged.Max = new int2(
                    Mathf.Max(merged.Max.x, next.Max.x),
                    Mathf.Max(merged.Max.y, next.Max.y));
            }
            return merged;
        }

        // ==================================================================
        // Gizmos
        // ==================================================================

#if UNITY_EDITOR
        
        /*
        private void OnDrawGizmosSelected()
        {
            if (!_drawGizmos || Graph == null) return;

            int w = Graph.Width/10;

            int drawn = 0;
            for (int i = 0; i < Graph.TotalNodes && drawn < _gizmoNodeLimit; i++)
            {

                if(i%w!=0)
                    continue;

                NodeData nd = Graph.LiveBuffer[i];
                // if (!nd.Walkable) continue;

                int2    nodeCoord = Graph.IndexToNode(i);
                Vector3 pos       = new Vector3(
                    nodeCoord.x * _config.NodeSpacing,
                    nd.WorldY + 0.1f,
                    nodeCoord.y * _config.NodeSpacing);

                float cubeSize = _config.NodeSpacing * 0.8f;

                if (!nd.Walkable)
                {
                    Gizmos.color = Color.red;
                }
                else
                {
                    Gizmos.color = Color.green;
                }   
                Gizmos.DrawCube(pos, new Vector3(cubeSize, 0.05f, cubeSize));
                drawn++;
            }
        } */
        
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!_drawGizmos || Graph == null) return;
            if (_controller == null) return;

            Transform player = _controller.Player();
            if (player == null) return;

            // Convert player world position to node-grid coordinates
            int playerNodeX = Graph.Config.WorldToNodeCoord(player.position.x);
            int playerNodeZ = Graph.Config.WorldToNodeCoord(player.position.z);

            // Draw a 50×50 node grid centred on the player
            int halfGrid = 25;
            int drawn    = 0;

            for (int dz = -halfGrid; dz <= halfGrid; dz++)
            {
                for (int dx = -halfGrid; dx <= halfGrid; dx++)
                {
                    int nodeX = playerNodeX + dx;
                    int nodeZ = playerNodeZ + dz;

                    int idx = Graph.NodeToIndex(nodeX, nodeZ);
                    if (idx < 0) continue;

                    NodeData nd = Graph.LiveBuffer[idx];

                    float   cubeSize = _config.NodeSpacing * 0.8f;
                    Vector3 pos      = new Vector3(
                        nodeX * _config.NodeSpacing,
                        nd.WorldY + 0.1f,
                        nodeZ * _config.NodeSpacing);

                    Gizmos.color = nd.Walkable ? Color.green : Color.red;
                    Gizmos.DrawCube(pos, new Vector3(cubeSize, 0.05f, cubeSize));
                    drawn++;
                }
            }
        }
#endif
        
#endif
    }
}
