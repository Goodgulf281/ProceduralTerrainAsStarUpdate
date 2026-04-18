// ============================================================
// Script:      AStarSearch.cs
// Episode:     EP## — Pathfinding Performance Optimisations
// Description: A* path search over PathfindingGraph.
//              Changes from previous version:
//                • AStarNode stores NodeX/NodeZ (node-grid coords) instead
//                  of WorldX/WorldZ, since the graph no longer assumes
//                  1 node = 1 world metre (Option F).
//                • FindPath() converts world-space inputs to node-grid
//                  coordinates via GraphConfig.WorldToNodeCoord().
//                • ReconstructPath() converts node-grid coords back to
//                  world-space positions via graph.IndexToWorld().
//                • Heuristic is scaled by NodeSpacing so path costs remain
//                  comparable to movement distances regardless of resolution.
//                • FindNearestWalkable() spiral now operates in node-grid
//                  space and uses graph.NodeToIndex().
// Author:      Goodgulf
// Date:        2026-03-14
// ============================================================

using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Goodgulf.Logging;

namespace Goodgulf.Pathfinding
{
    // =========================================================================
    // Path result types
    // =========================================================================

    /// <summary>Outcome of a <see cref="AStarSearch.FindPath"/> call.</summary>
    public enum PathStatus
    {
        /// <summary>A path was found. <c>PathResult.Waypoints</c> is populated.</summary>
        Success,

        /// <summary>Both endpoints are valid but no path exists between them.</summary>
        NoPath,

        /// <summary>One or both endpoints are outside the graph or have no nearby walkable node.</summary>
        InvalidEndpoints,
    }

    /// <summary>Return value from <see cref="AStarSearch.FindPath"/>.</summary>
    public struct PathResult
    {
        /// <summary>Outcome of the search.</summary>
        public PathStatus    Status;

        /// <summary>
        /// World-space waypoints along the path (Y = terrain height at each node).
        /// Null when <see cref="Status"/> is not <see cref="PathStatus.Success"/>.
        /// </summary>
        public List<Vector3> Waypoints;
    }

    // =========================================================================
    // AStarSearch  — synchronous, main-thread A* over PathfindingGraph
    // =========================================================================

    /// <summary>
    /// Stateless A* search that operates over a <see cref="PathfindingGraph"/>.
    ///
    /// Key design points
    /// ─────────────────
    ///  • All internal coordinates are in node-grid space (integer, scaled by
    ///    <c>NodesPerMetre</c>). World-space positions are only used at input
    ///    (snapped to the nearest node) and output (reconstructed waypoints).
    ///  • Static scratch collections are reused across calls to avoid GC allocs.
    ///    This means only one search may run at a time (not thread-safe).
    ///  • The graph's reference counter is incremented before the search and
    ///    decremented in a finally block, allowing the double-buffer swap to be
    ///    safely deferred until all in-flight searches complete.
    /// </summary>
    public static class AStarSearch
    {
        // Reused per-search scratch data (no per-call heap allocation)
        private static readonly Dictionary<int, AStarNode> _openMap   = new(4096);
        private static readonly Dictionary<int, AStarNode> _closedMap = new(4096);
        private static readonly MinHeap<AStarNode>         _openHeap  = new(4096);

        // √2 movement cost for diagonal steps
        private const float DiagonalCost  = 1.4142135f;
        // Penalty per unit of height difference along an edge
        private const float VerticalScale = 0.5f;

        // ==================================================================
        // Public API
        // ==================================================================

        /// <summary>
        /// Finds a path from <paramref name="startWorld"/> to
        /// <paramref name="goalWorld"/> using A*.
        ///
        /// Both positions are world-space; Y is ignored for the search and
        /// sampled from the graph's live buffer for the reconstructed waypoints.
        /// </summary>
        /// <param name="graph">The graph to search.</param>
        /// <param name="startWorld">Agent's current world position.</param>
        /// <param name="goalWorld">Desired destination in world space.</param>
        /// <param name="maxIterations">Safety cap to prevent runaway searches on broken graphs.</param>
        public static PathResult FindPath(
            PathfindingGraph graph,
            Vector3          startWorld,
            Vector3          goalWorld,
            int              maxIterations = 100_000)
        {
            // Convert world-space inputs to node-grid coordinates
            int startNodeX = graph.Config.WorldToNodeCoord(startWorld.x);
            int startNodeZ = graph.Config.WorldToNodeCoord(startWorld.z);
            int goalNodeX  = graph.Config.WorldToNodeCoord(goalWorld.x);
            int goalNodeZ  = graph.Config.WorldToNodeCoord(goalWorld.z);

            int startIdx = graph.NodeToIndex(startNodeX, startNodeZ);
            int goalIdx  = graph.NodeToIndex(goalNodeX,  goalNodeZ);

            GameLogger.Info($"Graph info: width={graph.Width}, height={graph.Height}, nodeSpacing={graph.Config.NodeSpacing}");

            if (startIdx < 0 || goalIdx < 0)
            {
                GameLogger.Info($"Invalid endpoints: startIdx={startIdx}, goalIdx={goalIdx}");
                return new PathResult { Status = PathStatus.InvalidEndpoints };
            }

            // Snap endpoints to nearest walkable node when they land on bad cells
            if (!graph.LiveBuffer[startIdx].Walkable)
            {
                GameLogger.Info($"startIdx={startIdx} is not walkable, searching for nearest walkable node...");
                startIdx = FindNearestWalkable(graph, startNodeX, startNodeZ, 5);
                if (startIdx < 0)
                {
                    GameLogger.Warning($"Failed to find nearest walkable node for startIdx={startIdx}");
                    return new PathResult { Status = PathStatus.InvalidEndpoints };
                }

                int2 snapped = graph.IndexToNode(startIdx);
                startNodeX = snapped.x;
                startNodeZ = snapped.y;
            }

            if (!graph.LiveBuffer[goalIdx].Walkable)
            {
                GameLogger.Info($"goalIdx={goalIdx} is not walkable, searching for nearest walkable node...");
                goalIdx = FindNearestWalkable(graph, goalNodeX, goalNodeZ, 5);
                if (goalIdx < 0)
                {
                    GameLogger.Warning($"Failed to find nearest walkable node for goalIdx={goalIdx}");
                    return new PathResult { Status = PathStatus.InvalidEndpoints };
                }
                int2 snapped = graph.IndexToNode(goalIdx);
                goalNodeX = snapped.x;
                goalNodeZ = snapped.y;
            }

            GameLogger.Info($"Starting A* search: startIdx={startIdx} (nodeX={startNodeX}, nodeZ={startNodeZ}), " +
                            $"goalIdx={goalIdx} (nodeX={goalNodeX}, nodeZ={goalNodeZ}), maxIterations={maxIterations}");

            // Notify graph so the double-buffer swap is deferred until we finish
            graph.IncrementSearchCount();
            try
            {
                return RunAStar(graph, startIdx, goalIdx,
                                startNodeX, startNodeZ,
                                goalNodeX,  goalNodeZ,
                                maxIterations);
            }
            finally
            {
                // Always fires — may trigger a pending buffer swap
                graph.DecrementSearchCount();
            }
        }

        // ==================================================================
        // Internal search
        // ==================================================================

        private static PathResult RunAStar(
            PathfindingGraph graph,
            int startIdx, int goalIdx,
            int startNodeX, int startNodeZ,
            int goalNodeX,  int goalNodeZ,
            int maxIterations)
        {
            _openMap.Clear();
            _closedMap.Clear();
            _openHeap.Clear();

            float nodeSpacing = graph.Config.NodeSpacing;

            var root = new AStarNode
            {
                Index     = startIdx,
                NodeX     = startNodeX,
                NodeZ     = startNodeZ,
                G         = 0f,
                H         = Heuristic(startNodeX, startNodeZ, goalNodeX, goalNodeZ, nodeSpacing),
                ParentIdx = -1,
            };
            root.F = root.H;

            _openHeap.Push(root);
            _openMap[startIdx] = root;

            NativeArray<NodeData> liveBuffer      = graph.LiveBuffer;
            NativeArray<int2>     neighborOffsets  = graph.NeighborOffsets;
            int                   graphWidth       = graph.Width;

            int iterations = 0;

            while (_openHeap.Count > 0 && iterations < maxIterations)
            {
                iterations++;
                AStarNode current = _openHeap.Pop();

                // Stale-entry guard — the node was already expanded with a better G
                if (_closedMap.ContainsKey(current.Index)) continue;
                if (_openMap.TryGetValue(current.Index, out var bestOpen) &&
                    bestOpen.G < current.G) continue;

                _closedMap[current.Index] = current;
                _openMap.Remove(current.Index);

                if (current.Index == goalIdx)
                    return ReconstructPath(graph, current, nodeSpacing);

                // Expand neighbours in node-grid space
                for (int n = 0; n < neighborOffsets.Length; n++)
                {
                    int2 offset  = neighborOffsets[n];
                    int  nnodeX  = current.NodeX + offset.x;
                    int  nnodeZ  = current.NodeZ + offset.y;
                    int  nIdx    = graph.NodeToIndex(nnodeX, nnodeZ);

                    if (nIdx < 0) continue;
                    if (_closedMap.ContainsKey(nIdx)) continue;

                    NodeData nData = liveBuffer[nIdx];
                    if (!nData.Walkable) continue;

                    // Edge cost: base movement distance + vertical penalty
                    bool  isDiag     = offset.x != 0 && offset.y != 0;
                    float baseCost   = (isDiag ? DiagonalCost : 1f) * nodeSpacing;
                    float heightDiff = math.abs(nData.WorldY - liveBuffer[current.Index].WorldY);
                    float edgeCost   = baseCost + heightDiff * VerticalScale;
                    float tentativeG = current.G + edgeCost;

                    if (_openMap.TryGetValue(nIdx, out var existing) &&
                        existing.G <= tentativeG) continue;

                    var neighbor = new AStarNode
                    {
                        Index     = nIdx,
                        NodeX     = nnodeX,
                        NodeZ     = nnodeZ,
                        G         = tentativeG,
                        H         = Heuristic(nnodeX, nnodeZ, goalNodeX, goalNodeZ, nodeSpacing),
                        ParentIdx = current.Index,
                    };
                    neighbor.F = neighbor.G + neighbor.H;

                    _openHeap.Push(neighbor);
                    _openMap[nIdx] = neighbor;
                }
            }

            return new PathResult { Status = PathStatus.NoPath };
        }

        // ==================================================================
        // Path reconstruction
        // ==================================================================

        private static PathResult ReconstructPath(
            PathfindingGraph graph,
            AStarNode        goalNode,
            float            nodeSpacing)
        {
            var waypoints = new List<Vector3>(64);

            AStarNode current = goalNode;
            while (true)
            {
                // Convert node-grid coords to world-space waypoint
                NodeData nd = graph.LiveBuffer[current.Index];
                waypoints.Add(new Vector3(
                    current.NodeX * nodeSpacing,
                    nd.WorldY,
                    current.NodeZ * nodeSpacing));

                if (current.ParentIdx < 0) break;
                if (!_closedMap.TryGetValue(current.ParentIdx, out current)) break;
            }

            waypoints.Reverse();
            StringPull(waypoints);

            return new PathResult
            {
                Status    = PathStatus.Success,
                Waypoints = waypoints,
            };
        }

        // ==================================================================
        // Heuristic — octile distance scaled by node spacing
        // ==================================================================

        /// <summary>
        /// Octile distance in node-grid space, scaled to world-space units
        /// by multiplying by <paramref name="nodeSpacing"/>.
        /// Admissible and consistent for any 8-connected grid.
        /// </summary>
        private static float Heuristic(
            int nodeAX, int nodeAZ,
            int nodeBX, int nodeBZ,
            float nodeSpacing)
        {
            float dx   = math.abs(nodeAX - nodeBX);
            float dz   = math.abs(nodeAZ - nodeBZ);
            float minD = math.min(dx, dz);
            float maxD = math.max(dx, dz);
            return (DiagonalCost * minD + (maxD - minD)) * nodeSpacing;
        }

        // ==================================================================
        // Nearest walkable node  (BFS expanding-ring search)
        // ==================================================================

        /// <summary>
        /// Searches in expanding node-grid rings up to <paramref name="radius"/>
        /// cells wide for the nearest walkable node.
        /// Used to snap endpoints that land on unwalkable cells.
        /// Returns -1 if none found.
        /// </summary>
        private static int FindNearestWalkable(
            PathfindingGraph graph,
            int startNodeX,
            int startNodeZ,
            int radius)
        {
            for (int r = 1; r <= radius; r++)
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                // Perimeter of the ring only
                if (math.abs(dx) != r && math.abs(dz) != r) continue;

                int idx = graph.NodeToIndex(startNodeX + dx, startNodeZ + dz);
                if (idx >= 0 && graph.LiveBuffer[idx].Walkable)
                    return idx;
            }
            return -1;
        }

        // ==================================================================
        // String pulling  — removes collinear waypoints
        // ==================================================================

        private static void StringPull(List<Vector3> waypoints)
        {
            if (waypoints.Count <= 2) return;
            for (int i = waypoints.Count - 2; i >= 1; i--)
            {
                Vector3 a  = waypoints[i - 1];
                Vector3 b  = waypoints[i];
                Vector3 c  = waypoints[i + 1];
                Vector3 ab = (b - a).normalized;
                Vector3 ac = (c - a).normalized;
                if (Vector3.Dot(ab, ac) > 0.999f)
                    waypoints.RemoveAt(i);
            }
        }
    }

    // =========================================================================
    // AStarNode  — heap element and closed-map value
    // =========================================================================

    internal struct AStarNode : System.IComparable<AStarNode>
    {
        /// <summary>Flat node-graph index.</summary>
        public int   Index;

        /// <summary>Node-grid X coordinate (not world metres).</summary>
        public int   NodeX;

        /// <summary>Node-grid Z coordinate (not world metres).</summary>
        public int   NodeZ;

        /// <summary>Actual cost from start (in world-space metres).</summary>
        public float G;

        /// <summary>Heuristic cost to goal (in world-space metres).</summary>
        public float H;

        /// <summary>Priority key: G + H.</summary>
        public float F;

        /// <summary>Flat index of the parent node, or -1 for the start node.</summary>
        public int   ParentIdx;

        public int CompareTo(AStarNode other) => F.CompareTo(other.F);
    }

    // =========================================================================
    // MinHeap<T>  — binary min-heap priority queue
    // =========================================================================

    internal class MinHeap<T> where T : System.IComparable<T>
    {
        private readonly List<T> _data;

        public int Count => _data.Count;

        public MinHeap(int capacity) => _data = new List<T>(capacity);

        public void Clear() => _data.Clear();

        public void Push(T item)
        {
            _data.Add(item);
            SiftUp(_data.Count - 1);
        }

        public T Pop()
        {
            T top  = _data[0];
            int last = _data.Count - 1;
            _data[0] = _data[last];
            _data.RemoveAt(last);
            if (_data.Count > 0) SiftDown(0);
            return top;
        }

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_data[i].CompareTo(_data[parent]) >= 0) break;
                (_data[i], _data[parent]) = (_data[parent], _data[i]);
                i = parent;
            }
        }

        private void SiftDown(int i)
        {
            int n = _data.Count;
            while (true)
            {
                int left     = 2 * i + 1;
                int right    = 2 * i + 2;
                int smallest = i;

                if (left  < n && _data[left ].CompareTo(_data[smallest]) < 0) smallest = left;
                if (right < n && _data[right].CompareTo(_data[smallest]) < 0) smallest = right;
                if (smallest == i) break;

                (_data[i], _data[smallest]) = (_data[smallest], _data[i]);
                i = smallest;
            }
        }
    }
}
