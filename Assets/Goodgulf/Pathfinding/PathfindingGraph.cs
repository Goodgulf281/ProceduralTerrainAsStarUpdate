// ============================================================
// Script:      PathfindingGraph.cs
// Episode:     EP## — Pathfinding Performance Optimisations
// Description: Core data types and double-buffered graph container.
//              Changes from previous version:
//                • GraphConfig gains NodesPerMetre (Option F).
//                  Defaults to 0.5 (2 m cells). Set to 1.0 for 1 m cells.
//                • DirtyRegion factory methods are resolution-aware.
//                • PathfindingGraph exposes NodeSpacing and helpers to
//                  convert between world-space and node-space at any resolution.
// Author:      Goodgulf
// Date:        2026-03-14
// ============================================================

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Goodgulf.Pathfinding
{
    // =========================================================================
    // NodeData  — blittable, Burst-friendly
    // =========================================================================

    /// <summary>
    /// Per-node record stored flat in the graph's NativeArrays.
    /// One entry per graph cell, which spans <see cref="GraphConfig.NodeSpacing"/> world units.
    /// </summary>
    public struct NodeData
    {
        /// <summary>Sampled terrain height at the node's world-space centre.</summary>
        public float  WorldY;

        /// <summary>Approximated surface normal, derived from heightmap partial derivatives.</summary>
        public float3 Normal;

        /// <summary>Combined walkability flag. False if slope, height, or obstacle checks fail.</summary>
        public bool   Walkable;

        /// <summary>Flat index into the owning NativeArray (cached for self-reference in jobs).</summary>
        public int    GlobalIndex;
    }

    // =========================================================================
    // GraphConfig  — serialisable, passed to jobs by value
    // =========================================================================

    /// <summary>
    /// Inspector-serialisable configuration controlling graph resolution and
    /// walkability evaluation thresholds.
    /// </summary>
    [Serializable]
    public struct GraphConfig
    {
        [Header("Resolution")]
        [Tooltip(
            "Number of graph nodes per world metre. " +
            "0.5 = one node every 2 m (recommended default). " +
            "1.0 = one node every 1 m (higher fidelity, 4× more nodes). " +
            "Changing this at runtime disposes and rebuilds the graph.")]
        [Range(0.25f, 1f)]
        public float NodesPerMetre;

        [Header("Walkability")]
        [Tooltip("Maximum slope angle (degrees) a node can have and still be walkable.")]
        [Range(0f, 89f)]
        public float MaxSlopeAngle;

        [Tooltip("Minimum world-Y height to be considered walkable (water surface exclusion).")]
        public float MinWalkableHeight;

        [Tooltip("Maximum world-Y height to be considered walkable (void exclusion).")]
        public float MaxWalkableHeight;

        [Header("Physics Obstacles")]
        [Tooltip("Sphere radius used by the physics obstacle overlay pass at each node. Auto-calculated from NodeSpacing if left at 0. Override to tune manually.")]
        public float ObstacleCheckRadius;
        
        public float EffectiveObstacleRadius =>
            ObstacleCheckRadius > 0f ? ObstacleCheckRadius : NodeSpacing * 0.5f;
        
        
        [Tooltip("LayerMask for physics-based obstacle detection.")]
        public LayerMask ObstacleLayers;

        [Header("Connectivity")]
        [Tooltip("Whether diagonal movement is allowed between nodes.")]
        public bool AllowDiagonals;

        // ------------------------------------------------------------------
        // Derived helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// World-space distance (in metres) between adjacent nodes.
        /// Equal to <c>1 / NodesPerMetre</c>.
        /// </summary>
        public float NodeSpacing => 1f / Mathf.Max(0.001f, NodesPerMetre);

        /// <summary>
        /// Converts a world-space distance to a node count along one axis.
        /// </summary>
        public int WorldToNodeCount(float worldDistance) =>
            Mathf.Max(1, Mathf.CeilToInt(worldDistance * Mathf.Max(0.001f, NodesPerMetre)));

        /// <summary>
        /// Converts a world-space position component to its nearest node-grid index.
        /// </summary>
        public int WorldToNodeCoord(float worldPos) =>
            Mathf.RoundToInt(worldPos * Mathf.Max(0.001f, NodesPerMetre));

        /// <summary>
        /// Converts a node-grid index back to world-space position (centre of cell).
        /// </summary>
        public float NodeToWorldCoord(int nodeCoord) =>
            nodeCoord * NodeSpacing;

        // ------------------------------------------------------------------
        // Defaults
        // ------------------------------------------------------------------

        /// <summary>
        /// Sensible production defaults.
        /// Resolution is 0.5 nodes/m (one node every 2 m) for performance.
        /// Switch <see cref="NodesPerMetre"/> to 1.0 in the inspector for full precision.
        /// </summary>
        public static GraphConfig Default => new GraphConfig
        {
            NodesPerMetre        = 0.5f,   // Option F default — 4× fewer nodes than 1.0
            MaxSlopeAngle        = 45f,
            MinWalkableHeight    = 0f,
            MaxWalkableHeight    = 10000f,
            ObstacleCheckRadius  = 0.0f,
            ObstacleLayers       = ~0,
            AllowDiagonals       = true,
        };
    }

    // =========================================================================
    // DirtyRegion  — resolution-aware rectangular rebuild AABB
    // =========================================================================

    /// <summary>
    /// An inclusive AABB in <em>node-grid</em> space on the XZ plane.
    /// Node-grid coordinates are <c>floor(worldPos * NodesPerMetre)</c>.
    /// All factory methods require the active <see cref="GraphConfig"/> so they
    /// can convert world distances to node counts correctly at any resolution.
    /// </summary>
    public struct DirtyRegion
    {
        /// <summary>Inclusive minimum node-grid coordinate (XZ).</summary>
        public int2 Min;

        /// <summary>Inclusive maximum node-grid coordinate (XZ).</summary>
        public int2 Max;

        /// <summary>True when Max >= Min on both axes.</summary>
        public bool IsValid => Max.x >= Min.x && Max.y >= Min.y;

        // ------------------------------------------------------------------
        // Factory methods
        // ------------------------------------------------------------------

        /// <summary>
        /// Creates a DirtyRegion from a world-space axis-aligned bounding box.
        /// </summary>
        public static DirtyRegion FromWorldAABB(
            Vector3     worldMin,
            Vector3     worldMax,
            GraphConfig config)
        {
            return new DirtyRegion
            {
                Min = new int2(
                    Mathf.FloorToInt(worldMin.x * config.NodesPerMetre),
                    Mathf.FloorToInt(worldMin.z * config.NodesPerMetre)),
                Max = new int2(
                    Mathf.CeilToInt(worldMax.x  * config.NodesPerMetre),
                    Mathf.CeilToInt(worldMax.z  * config.NodesPerMetre)),
            };
        }

        /// <summary>
        /// Creates a DirtyRegion covering one streaming chunk.
        /// </summary>
        public static DirtyRegion FromChunk(
            Vector2Int  chunkCoord,
            Vector2     chunkSize,
            GraphConfig config)
        {
            return new DirtyRegion
            {
                Min = new int2(
                    Mathf.FloorToInt(chunkCoord.x * chunkSize.x * config.NodesPerMetre),
                    Mathf.FloorToInt(chunkCoord.y * chunkSize.y * config.NodesPerMetre)),
                Max = new int2(
                    Mathf.CeilToInt((chunkCoord.x + 1) * chunkSize.x * config.NodesPerMetre),
                    Mathf.CeilToInt((chunkCoord.y + 1) * chunkSize.y * config.NodesPerMetre)),
            };
        }

        /// <summary>
        /// Expands this region by <paramref name="cells"/> node-grid cells in every direction.
        /// Use to ensure cross-chunk border nodes are included in partial rebuilds.
        /// </summary>
        public DirtyRegion Expanded(int cells) => new DirtyRegion
        {
            Min = new int2(Min.x - cells, Min.y - cells),
            Max = new int2(Max.x + cells, Max.y + cells),
        };
    }

    // =========================================================================
    // PathfindingGraph  — owns the double-buffered NativeArrays
    // =========================================================================

    /// <summary>
    /// Owns the flat, double-buffered graph of <see cref="NodeData"/>.
    ///
    /// Coordinate system
    /// ──────────────────
    ///  Node-grid index  = (nodeZ - origin.y) * width + (nodeX - origin.x)
    ///  Node-grid coord  = floor(worldPos * NodesPerMetre)
    ///  World position   = nodeCoord * NodeSpacing   (centre of cell)
    ///
    /// Buffer A is the "live" buffer read by A* searches.
    /// Buffer B is the "staging" buffer written by rebuild jobs.
    /// The buffers swap atomically once a rebuild completes and no
    /// searches are reading the live buffer (reference-counted).
    ///
    /// This class is NOT Burst-compiled — it lives on the main thread
    /// and passes NativeArray slices to jobs.
    /// </summary>
    public class PathfindingGraph : IDisposable
    {
        // ------------------------------------------------------------------
        // Public read-only properties
        // ------------------------------------------------------------------

        /// <summary>Active graph configuration including resolution settings.</summary>
        public GraphConfig Config    { get; private set; }

        /// <summary>Node-grid origin (minimum XZ corner) of the graph.</summary>
        public int2        Origin    { get; private set; }

        /// <summary>Number of nodes along the X axis.</summary>
        public int         Width     { get; private set; }

        /// <summary>Number of nodes along the Z axis.</summary>
        public int         Height    { get; private set; }

        /// <summary>Total node count (Width × Height).</summary>
        public int         TotalNodes => Width * Height;

        /// <summary>World-space size of each node cell in metres.</summary>
        public float       NodeSpacing => Config.NodeSpacing;

        // ------------------------------------------------------------------
        // Double-buffer state
        // ------------------------------------------------------------------

        // Buffer A — live, read by A* searches
        private NativeArray<NodeData> _bufferA;
        // Buffer B — staging, written by rebuild jobs
        private NativeArray<NodeData> _bufferB;

        // Count of in-flight A* searches currently reading _bufferA
        private int  _activeSearchCount;
        // True when a rebuild completed while searches were running
        private bool _swapPending;

        // ------------------------------------------------------------------
        // Neighbour offsets (computed once, stored in native memory)
        // ------------------------------------------------------------------

        private NativeArray<int2> _neighborOffsets;

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        /// <summary>
        /// Allocates both NativeArrays and neighbour offsets.
        /// <paramref name="origin"/>, <paramref name="width"/>, and
        /// <paramref name="height"/> are in node-grid units, not world units.
        /// </summary>
        public PathfindingGraph(int2 origin, int width, int height, GraphConfig config)
        {
            Origin = origin;
            Width  = width;
            Height = height;
            Config = config;

            _bufferA = new NativeArray<NodeData>(TotalNodes, Allocator.Persistent);
            _bufferB = new NativeArray<NodeData>(TotalNodes, Allocator.Persistent);

            BuildNeighborOffsets(config.AllowDiagonals);
        }

        private void BuildNeighborOffsets(bool diagonals)
        {
            if (_neighborOffsets.IsCreated) _neighborOffsets.Dispose();

            if (diagonals)
            {
                _neighborOffsets = new NativeArray<int2>(8, Allocator.Persistent);
                _neighborOffsets[0] = new int2( 1,  0);
                _neighborOffsets[1] = new int2(-1,  0);
                _neighborOffsets[2] = new int2( 0,  1);
                _neighborOffsets[3] = new int2( 0, -1);
                _neighborOffsets[4] = new int2( 1,  1);
                _neighborOffsets[5] = new int2(-1,  1);
                _neighborOffsets[6] = new int2( 1, -1);
                _neighborOffsets[7] = new int2(-1, -1);
            }
            else
            {
                _neighborOffsets = new NativeArray<int2>(4, Allocator.Persistent);
                _neighborOffsets[0] = new int2( 1,  0);
                _neighborOffsets[1] = new int2(-1,  0);
                _neighborOffsets[2] = new int2( 0,  1);
                _neighborOffsets[3] = new int2( 0, -1);
            }
        }

        // ------------------------------------------------------------------
        // Buffer access
        // ------------------------------------------------------------------

        /// <summary>Live buffer — safe for A* reads. Call <see cref="IncrementSearchCount"/> first.</summary>
        public NativeArray<NodeData> LiveBuffer    => _bufferA;

        /// <summary>Staging buffer — passed to rebuild jobs for writing.</summary>
        public NativeArray<NodeData> StagingBuffer => _bufferB;

        /// <summary>Neighbour offset vectors in node-grid space (Persistent allocation).</summary>
        public NativeArray<int2>     NeighborOffsets => _neighborOffsets;

        // ------------------------------------------------------------------
        // Search reference counting (controls deferred buffer swap)
        // ------------------------------------------------------------------

        /// <summary>
        /// Call before starting any A* search.
        /// Prevents the staging buffer swap until the search completes.
        /// </summary>
        public void IncrementSearchCount() => _activeSearchCount++;

        /// <summary>
        /// Call when an A* search finishes (or is abandoned).
        /// Triggers a pending buffer swap if this was the last active search.
        /// </summary>
        public void DecrementSearchCount()
        {
            _activeSearchCount = Mathf.Max(0, _activeSearchCount - 1);
            if (_swapPending && _activeSearchCount == 0)
                PerformSwap();
        }

        /// <summary>
        /// Called by <see cref="TerrainGraphIntegration"/> once a rebuild job
        /// completes and the physics overlay pass has run.
        /// Swaps buffers immediately if no searches are in flight; otherwise
        /// defers until the last search calls <see cref="DecrementSearchCount"/>.
        /// </summary>
        public void NotifyRebuildComplete()
        {
            if (_activeSearchCount == 0)
                PerformSwap();
            else
                _swapPending = true;
        }

        private void PerformSwap()
        {
            (_bufferA, _bufferB) = (_bufferB, _bufferA);
            _swapPending = false;
        }

        // ------------------------------------------------------------------
        // Coordinate conversion helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Converts world-space XZ to a flat node-grid index.
        /// Returns -1 if the position is outside the graph bounds.
        /// </summary>
        public int WorldToIndex(float worldX, float worldZ)
        {
            int nx = Mathf.RoundToInt(worldX * Config.NodesPerMetre);
            int nz = Mathf.RoundToInt(worldZ * Config.NodesPerMetre);
            return NodeToIndex(nx, nz);
        }

        /// <summary>
        /// Converts node-grid coordinates to a flat index.
        /// Returns -1 if outside the graph bounds.
        /// </summary>
        public int NodeToIndex(int nodeX, int nodeZ)
        {
            int lx = nodeX - Origin.x;
            int lz = nodeZ - Origin.y;
            if (lx < 0 || lz < 0 || lx >= Width || lz >= Height) return -1;
            return lz * Width + lx;
        }

        /// <summary>
        /// Converts node-grid XZ (as int2) to a flat index.
        /// Returns -1 if outside the graph bounds.
        /// </summary>
        public int NodeToIndex(int2 nodeXZ) => NodeToIndex(nodeXZ.x, nodeXZ.y);

        /// <summary>
        /// Converts a flat index back to node-grid XZ coordinates.
        /// </summary>
        public int2 IndexToNode(int index)
        {
            int lx = index % Width;
            int lz = index / Width;
            return new int2(lx + Origin.x, lz + Origin.y);
        }

        /// <summary>
        /// Converts a flat index to the world-space XZ centre of that cell.
        /// </summary>
        public Vector2 IndexToWorld(int index)
        {
            int2 node = IndexToNode(index);
            return new Vector2(node.x * Config.NodeSpacing, node.y * Config.NodeSpacing);
        }

        /// <summary>
        /// Returns true when the world-space position falls within the graph bounds.
        /// </summary>
        public bool InBoundsWorld(float worldX, float worldZ) =>
            WorldToIndex(worldX, worldZ) >= 0;

        /// <summary>
        /// Returns true when the node-grid coordinate falls within the graph bounds.
        /// </summary>
        public bool InBoundsNode(int nodeX, int nodeZ)
        {
            int lx = nodeX - Origin.x;
            int lz = nodeZ - Origin.y;
            return lx >= 0 && lz >= 0 && lx < Width && lz < Height;
        }

        /// <summary>
        /// Returns true when the node-grid coordinate (as int2) falls within bounds.
        /// </summary>
        public bool InBoundsNode(int2 nodeXZ) => InBoundsNode(nodeXZ.x, nodeXZ.y);

        /// <summary>
        /// Returns the world-space XZ centre of the entire graph coverage area.
        /// </summary>
        public Vector3 WorldCenter()
        {
            float cx = (Origin.x + Width  * 0.5f) * Config.NodeSpacing;
            float cz = (Origin.y + Height * 0.5f) * Config.NodeSpacing;
            return new Vector3(cx, 0, cz);
        }

        /// <summary>Clamps a DirtyRegion to the graph's node-grid bounds.</summary>
        public DirtyRegion ClampRegion(DirtyRegion region) => new DirtyRegion
        {
            Min = new int2(
                Mathf.Max(region.Min.x, Origin.x),
                Mathf.Max(region.Min.y, Origin.y)),
            Max = new int2(
                Mathf.Min(region.Max.x, Origin.x + Width  - 1),
                Mathf.Min(region.Max.y, Origin.y + Height - 1)),
        };

        // ------------------------------------------------------------------
        // Read helpers (main thread — reads live buffer)
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the NodeData at a world-space position from the live buffer.
        /// Returns default if out of bounds.
        /// </summary>
        public NodeData GetNode(float worldX, float worldZ)
        {
            int idx = WorldToIndex(worldX, worldZ);
            return idx >= 0 ? _bufferA[idx] : default;
        }

        /// <summary>
        /// Returns true when the node at a world-space position is walkable.
        /// Returns false if out of bounds.
        /// </summary>
        public bool IsWalkable(float worldX, float worldZ)
        {
            int idx = WorldToIndex(worldX, worldZ);
            return idx >= 0 && _bufferA[idx].Walkable;
        }

        // ------------------------------------------------------------------
        // Config update
        // ------------------------------------------------------------------

        /// <summary>
        /// Updates the stored config (e.g. after changing NodesPerMetre at runtime).
        /// Note: does NOT resize the NativeArrays — call
        /// <see cref="TerrainGraphIntegration"/> to rebuild the graph.
        /// </summary>
        public void UpdateConfig(GraphConfig config)
        {
            Config = config;
            BuildNeighborOffsets(config.AllowDiagonals);
        }

        // ------------------------------------------------------------------
        // IDisposable
        // ------------------------------------------------------------------

        /// <summary>Releases all NativeArray allocations.</summary>
        public void Dispose()
        {
            if (_bufferA.IsCreated)         _bufferA.Dispose();
            if (_bufferB.IsCreated)         _bufferB.Dispose();
            if (_neighborOffsets.IsCreated) _neighborOffsets.Dispose();
        }
    }
}
