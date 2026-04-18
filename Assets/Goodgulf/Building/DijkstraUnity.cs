using System.Collections.Generic;
using UnityEngine;

namespace Goodgulf.Building
{
    /// <summary>
    /// Represents a directed edge in a graph.
    /// "To" is the destination node, "Cost" is the traversal cost.
    /// </summary>
    public struct Edge
    {
        public int To;         // Destination node index
        public float Cost;     // Cost to travel to the destination

        public Edge(int to, float cost)
        {
            To = to;
            Cost = cost;
        }
    }

    /// <summary>
    /// Simple min-heap (priority queue) implementation used by Dijkstra.
    /// Stores nodes ordered by lowest priority value (distance).
    /// </summary>
    public class MinHeap
    {
        // Internal heap storage: (nodeId, priority)
        private readonly List<(int node, float priority)> heap = new();

        /// <summary>
        /// Number of elements currently in the heap.
        /// </summary>
        public int Count => heap.Count;

        /// <summary>
        /// Adds a node to the heap with the given priority.
        /// </summary>
        public void Push(int node, float priority)
        {
            heap.Add((node, priority));
            HeapifyUp(heap.Count - 1);
        }

        /// <summary>
        /// Removes and returns the node with the lowest priority.
        /// </summary>
        public int Pop()
        {
            int result = heap[0].node;

            // Move last element to root and shrink heap
            heap[0] = heap[^1];
            heap.RemoveAt(heap.Count - 1);

            // Restore heap property
            HeapifyDown(0);
            return result;
        }

        /// <summary>
        /// Moves the element at index i up until heap property is restored.
        /// </summary>
        private void HeapifyUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;

                // Stop if parent already has lower or equal priority
                if (heap[i].priority >= heap[parent].priority)
                    break;

                // Swap with parent
                (heap[i], heap[parent]) = (heap[parent], heap[i]);
                i = parent;
            }
        }

        /// <summary>
        /// Moves the element at index i down until heap property is restored.
        /// </summary>
        private void HeapifyDown(int i)
        {
            while (true)
            {
                int left = i * 2 + 1;
                int right = i * 2 + 2;
                int smallest = i;

                // Find smallest child
                if (left < heap.Count && heap[left].priority < heap[smallest].priority)
                    smallest = left;

                if (right < heap.Count && heap[right].priority < heap[smallest].priority)
                    smallest = right;

                // Stop if heap property is satisfied
                if (smallest == i)
                    break;

                // Swap and continue
                (heap[i], heap[smallest]) = (heap[smallest], heap[i]);
                i = smallest;
            }
        }
    }

    /// <summary>
    /// Static utility class implementing Dijkstra-based pathfinding for Unity.
    /// Uses integer node IDs and adjacency lists.
    /// </summary>
    public static class DijkstraUnity
    {
        /// <summary>
        /// Finds the shortest path between start and goal nodes.
        /// Returns a list of node IDs representing the path, or null if unreachable.
        /// </summary>
        public static List<int> FindPath(
            Dictionary<int, List<Edge>> graph,
            int start,
            int goal)
        {
            var distances = new Dictionary<int, float>(); // Best known distance per node
            var previous = new Dictionary<int, int>();    // Path reconstruction map
            var heap = new MinHeap();

            // Initialize all distances to infinity
            foreach (var node in graph.Keys)
                distances[node] = float.PositiveInfinity;

            distances[start] = 0f;
            heap.Push(start, 0f);

            while (heap.Count > 0)
            {
                int current = heap.Pop();

                // Stop early if goal is reached
                if (current == goal)
                    return ReconstructPath(previous, start, goal);

                // Relax all outgoing edges
                foreach (var edge in graph[current])
                {
                    float newDist = distances[current] + edge.Cost;

                    if (newDist < distances[edge.To])
                    {
                        distances[edge.To] = newDist;
                        previous[edge.To] = current;
                        heap.Push(edge.To, newDist);
                    }
                }
            }

            return null; // No path found
        }

        /// <summary>
        /// Reconstructs the path from start to goal using the "previous" map.
        /// </summary>
        private static List<int> ReconstructPath(
            Dictionary<int, int> previous,
            int start,
            int goal)
        {
            var path = new List<int>();
            int current = goal;

            // Walk backwards from goal to start
            while (current != start)
            {
                path.Add(current);
                current = previous[current];
            }

            path.Add(start);
            path.Reverse();
            return path;
        }

        /// <summary>
        /// Returns only the shortest distance between start and goal.
        /// </summary>
        public static float FindShortestDistance(
            Dictionary<int, List<Edge>> graph,
            int start,
            int goal)
        {
            var distances = new Dictionary<int, float>();
            var heap = new MinHeap();

            foreach (var node in graph.Keys)
                distances[node] = float.PositiveInfinity;

            distances[start] = 0f;
            heap.Push(start, 0f);

            while (heap.Count > 0)
            {
                int current = heap.Pop();

                if (current == goal)
                    return distances[current];

                foreach (var edge in graph[current])
                {
                    float newDist = distances[current] + edge.Cost;

                    if (newDist < distances[edge.To])
                    {
                        distances[edge.To] = newDist;
                        heap.Push(edge.To, newDist);
                    }
                }
            }

            return float.PositiveInfinity; // Goal unreachable
        }

        /// <summary>
        /// Finds both the shortest path and its total distance.
        /// Returns true if a path exists.
        /// </summary>
        public static bool FindPathAndDistance(
            Dictionary<int, List<Edge>> graph,
            int start,
            int goal,
            out List<int> path,
            out float distance)
        {
            var distances = new Dictionary<int, float>();
            var previous = new Dictionary<int, int>();
            var heap = new MinHeap();

            foreach (var node in graph.Keys)
                distances[node] = float.PositiveInfinity;

            distances[start] = 0f;
            heap.Push(start, 0f);

            while (heap.Count > 0)
            {
                int current = heap.Pop();

                if (current == goal)
                {
                    distance = distances[current];
                    path = ReconstructPath(previous, start, goal);
                    return true;
                }

                foreach (var edge in graph[current])
                {
                    float newDist = distances[current] + edge.Cost;

                    if (newDist < distances[edge.To])
                    {
                        distances[edge.To] = newDist;
                        previous[edge.To] = current;
                        heap.Push(edge.To, newDist);
                    }
                }
            }

            path = null;
            distance = float.PositiveInfinity;
            return false;
        }

        /// <summary>
        /// Finds all nodes reachable within a maximum distance from start.
        /// Returns only the node IDs.
        /// </summary>
        public static List<int> FindNodesWithinDistance(
            Dictionary<int, List<Edge>> graph,
            int start,
            float maxDistance)
        {
            var distances = new Dictionary<int, float>();
            var result = new List<int>();
            var heap = new MinHeap();
            var visited = new HashSet<int>();

            foreach (var node in graph.Keys)
                distances[node] = float.PositiveInfinity;

            distances[start] = 0f;
            heap.Push(start, 0f);

            while (heap.Count > 0)
            {
                int current = heap.Pop();

                // Skip already processed nodes
                if (!visited.Add(current))
                    continue;

                float currentDist = distances[current];

                // Early exit due to Dijkstra ordering
                if (currentDist > maxDistance)
                    break;

                result.Add(current);

                foreach (var edge in graph[current])
                {
                    float newDist = currentDist + edge.Cost;

                    if (newDist < distances[edge.To] && newDist <= maxDistance)
                    {
                        distances[edge.To] = newDist;
                        heap.Push(edge.To, newDist);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Same as FindNodesWithinDistance, but also returns each node's distance.
        /// </summary>
        public static Dictionary<int, float> FindNodesWithinDistanceWithDistances(
            Dictionary<int, List<Edge>> graph,
            int start,
            float maxDistance)
        {
            var distances = new Dictionary<int, float>();
            var heap = new MinHeap();
            var visited = new HashSet<int>();

            foreach (var node in graph.Keys)
                distances[node] = float.PositiveInfinity;

            distances[start] = 0f;
            heap.Push(start, 0f);

            while (heap.Count > 0)
            {
                int current = heap.Pop();

                if (!visited.Add(current))
                    continue;

                float currentDist = distances[current];

                if (currentDist > maxDistance)
                    break;

                foreach (var edge in graph[current])
                {
                    float newDist = currentDist + edge.Cost;

                    if (newDist < distances[edge.To] && newDist <= maxDistance)
                    {
                        distances[edge.To] = newDist;
                        heap.Push(edge.To, newDist);
                    }
                }
            }

            // Filter out nodes beyond maxDistance
            var result = new Dictionary<int, float>();
            foreach (var kv in distances)
            {
                if (kv.Value <= maxDistance)
                    result[kv.Key] = kv.Value;
            }

            return result;
        }

        /// <summary>
        /// Merges multiple distance maps, keeping the shortest distance per node.
        /// </summary>
        public static Dictionary<int, float> MergeByShortestDistance(
            IEnumerable<Dictionary<int, float>> dictionaries)
        {
            var result = new Dictionary<int, float>();

            foreach (var dict in dictionaries)
            {
                foreach (var kv in dict)
                {
                    if (result.TryGetValue(kv.Key, out float existing))
                    {
                        if (kv.Value < existing)
                            result[kv.Key] = kv.Value;
                    }
                    else
                    {
                        result[kv.Key] = kv.Value;
                    }
                }
            }

            return result;
        }
    }
}
