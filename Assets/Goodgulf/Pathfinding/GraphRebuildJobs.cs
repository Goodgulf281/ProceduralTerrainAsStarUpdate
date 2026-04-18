// ============================================================
// Script:      GraphRebuildJobs.cs
// Episode:     EP## — Pathfinding Performance Optimisations
// Description: Burst-compiled jobs for full and partial graph rebuilds.
//              Changes from previous version:
//                • Both FullGraphRebuildJob and PartialGraphRebuildJob
//                  accept NodesPerMetre so they can convert node-grid
//                  coordinates to world-space positions for heightmap
//                  sampling. This is the Option F plumbing on the job side.
//                • PhysicsObstacleOverlay is now called by TickOverlay()
//                  inside TerrainGraphIntegration rather than directly from
//                  FinaliseJob(), so it has been converted from a static
//                  helper class to a reusable single-node method used by
//                  the streamed overlay (Option A). The static Apply() batch
//                  method is retained for ForceFullRebuildSync().
// Author:      Goodgulf
// Date:        2026-03-14
// ============================================================

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Goodgulf.Pathfinding
{
    // =========================================================================
    // TerrainHeightSample  — blittable chunk metadata passed to Burst jobs
    // =========================================================================

    /// <summary>
    /// Blittable snapshot of one chunk's heightmap metadata.
    /// Populated on the main thread from <c>Terrain.terrainData</c>,
    /// then passed to jobs as a <c>NativeArray&lt;TerrainHeightSample&gt;</c>.
    /// </summary>
    public struct TerrainHeightSample
    {
        /// <summary>World-space XZ origin of the chunk's bottom-left corner.</summary>
        public float2 ChunkOriginXZ;

        /// <summary>World-space XZ dimensions of this chunk.</summary>
        public float2 ChunkSizeXZ;

        /// <summary><c>terrainData.size.y</c> — Y scale applied to normalised height values.</summary>
        public float  TerrainHeight;

        /// <summary>Heightmap resolution (square: HeightmapRes × HeightmapRes samples).</summary>
        public int    HeightmapRes;

        /// <summary>
        /// Offset into the flat Heights NativeArray where this chunk's data begins.
        /// <c>Heights[ChunkIndex + z * HeightmapRes + x]</c> = normalised height [0..1].
        /// </summary>
        public int    ChunkIndex;
    }

    // =========================================================================
    // CopyBufferJob  — blits live → staging before a partial rebuild
    // =========================================================================

    /// <summary>
    /// Stage 1 of a partial rebuild: copies all nodes from the live buffer
    /// into staging so the patch job only needs to overwrite the dirty region.
    /// </summary>
    [BurstCompile]
    public struct CopyBufferJob : IJob
    {
        [ReadOnly]  public NativeArray<NodeData> Src;
        [WriteOnly] public NativeArray<NodeData> Dst;

        public void Execute()
        {
            for (int i = 0; i < Src.Length; i++)
                Dst[i] = Src[i];
        }
    }

    // =========================================================================
    // FullGraphRebuildJob  — recomputes every node
    // =========================================================================

    /// <summary>
    /// Iterates all nodes and recomputes <see cref="NodeData"/> from terrain heightmaps.
    /// Parallelised over the flat node index (IJobParallelFor, batch = 64).
    ///
    /// Resolution note: each node index maps to node-grid coordinates via
    /// <c>nodeX = index % GraphWidth + GraphOrigin.x</c>.
    /// The world-space position is <c>nodeX / NodesPerMetre</c>.
    /// </summary>
    [BurstCompile]
    public struct FullGraphRebuildJob : IJobParallelFor
    {
        // Graph layout
        public int   GraphWidth;
        public int   GraphHeight;
        public int2  GraphOrigin;    // Node-grid origin

        // Option F — resolution
        public float NodesPerMetre;  // Converts node coords to world coords

        // Walkability thresholds
        public float MaxSlopeAngle;
        public float MinWalkableHeight;
        public float MaxWalkableHeight;

        // Heightmap data: Heights[chunk.ChunkIndex + z * res + x] = normalised [0..1]
        [ReadOnly] public NativeArray<float>               Heights;
        [ReadOnly] public NativeArray<TerrainHeightSample> Chunks;

        [WriteOnly] public NativeArray<NodeData> Output;

        public void Execute(int index)
        {
            int nodeX = index % GraphWidth + GraphOrigin.x;
            int nodeZ = index / GraphWidth + GraphOrigin.y;

            // Convert node-grid coords to world-space (centre of cell)
            float nodeSpacing = 1f / math.max(0.001f, NodesPerMetre);
            float worldX      = nodeX * nodeSpacing;
            float worldZ      = nodeZ * nodeSpacing;

            SampleAndWrite(worldX, worldZ, index);
        }

        private void SampleAndWrite(float worldX, float worldZ, int outputIndex)
        {
            float  worldY = 0f;
            float3 normal = new float3(0f, 1f, 0f);
            bool   found  = false;

            for (int c = 0; c < Chunks.Length; c++)
            {
                TerrainHeightSample chunk = Chunks[c];
                float relX = worldX - chunk.ChunkOriginXZ.x;
                float relZ = worldZ - chunk.ChunkOriginXZ.y;

                if (relX < 0f || relZ < 0f ||
                    relX > chunk.ChunkSizeXZ.x || relZ > chunk.ChunkSizeXZ.y)
                    continue;

                int res = chunk.HeightmapRes;

                // Bilinear sample
                float fx = (relX / chunk.ChunkSizeXZ.x) * (res - 1);
                float fz = (relZ / chunk.ChunkSizeXZ.y) * (res - 1);
                int   ix = math.clamp((int)fx, 0, res - 2);
                int   iz = math.clamp((int)fz, 0, res - 2);
                float tx = fx - ix;
                float tz = fz - iz;

                int   b   = chunk.ChunkIndex;
                float h00 = Heights[b +  iz      * res + ix    ];
                float h10 = Heights[b +  iz      * res + ix + 1];
                float h01 = Heights[b + (iz + 1) * res + ix    ];
                float h11 = Heights[b + (iz + 1) * res + ix + 1];

                float normH = math.lerp(math.lerp(h00, h10, tx), math.lerp(h01, h11, tx), tz);
                worldY = normH * chunk.TerrainHeight;

                // Approximate surface normal from partial derivatives
                float cellX = chunk.ChunkSizeXZ.x / (res - 1);
                float cellZ = chunk.ChunkSizeXZ.y / (res - 1);
                float dhDx  = (h10 - h00) * chunk.TerrainHeight / cellX;
                float dhDz  = (h01 - h00) * chunk.TerrainHeight / cellZ;
                normal      = math.normalize(new float3(-dhDx, 1f, -dhDz));

                found = true;
                break;
            }

            if (!found)
            {
                Output[outputIndex] = new NodeData
                {
                    WorldY      = 0f,
                    Normal      = new float3(0f, 1f, 0f),
                    Walkable    = false,
                    GlobalIndex = outputIndex,
                };
                return;
            }

            Output[outputIndex] = new NodeData
            {
                WorldY      = worldY,
                Normal      = normal,
                Walkable    = EvaluateWalkability(worldY, normal),
                GlobalIndex = outputIndex,
            };
        }

        private bool EvaluateWalkability(float worldY, float3 normal)
        {
            if (worldY < MinWalkableHeight || worldY > MaxWalkableHeight) return false;
            float slopeAngle = math.degrees(math.acos(math.clamp(normal.y, 0f, 1f)));
            return slopeAngle <= MaxSlopeAngle;
        }
    }

    // =========================================================================
    // PartialGraphRebuildJob  — recomputes only the dirty region
    // =========================================================================

    /// <summary>
    /// Same logic as <see cref="FullGraphRebuildJob"/> but skips any node whose
    /// node-grid XZ falls outside <c>[DirtyMin, DirtyMax]</c>.
    /// The caller must run <see cref="CopyBufferJob"/> first so untouched nodes
    /// retain their previous values.
    /// </summary>
    [BurstCompile]
    public struct PartialGraphRebuildJob : IJobParallelFor
    {
        public int   GraphWidth;
        public int2  GraphOrigin;

        // Option F
        public float NodesPerMetre;

        // Dirty region (inclusive, node-grid coordinates)
        public int2 DirtyMin;
        public int2 DirtyMax;

        public float MaxSlopeAngle;
        public float MinWalkableHeight;
        public float MaxWalkableHeight;

        [ReadOnly] public NativeArray<float>               Heights;
        [ReadOnly] public NativeArray<TerrainHeightSample> Chunks;

        // Read-write so nodes outside the dirty region survive from CopyBufferJob
        public NativeArray<NodeData> Output;

        public void Execute(int index)
        {
            int nodeX = index % GraphWidth + GraphOrigin.x;
            int nodeZ = index / GraphWidth + GraphOrigin.y;

            // Early exit for nodes outside the dirty region
            if (nodeX < DirtyMin.x || nodeX > DirtyMax.x ||
                nodeZ < DirtyMin.y || nodeZ > DirtyMax.y)
                return;

            float nodeSpacing = 1f / math.max(0.001f, NodesPerMetre);
            float worldX      = nodeX * nodeSpacing;
            float worldZ      = nodeZ * nodeSpacing;

            SampleAndWrite(worldX, worldZ, index);
        }

        private void SampleAndWrite(float worldX, float worldZ, int outputIndex)
        {
            float  worldY = 0f;
            float3 normal = new float3(0f, 1f, 0f);
            bool   found  = false;

            for (int c = 0; c < Chunks.Length; c++)
            {
                TerrainHeightSample chunk = Chunks[c];
                float relX = worldX - chunk.ChunkOriginXZ.x;
                float relZ = worldZ - chunk.ChunkOriginXZ.y;

                if (relX < 0f || relZ < 0f ||
                    relX > chunk.ChunkSizeXZ.x || relZ > chunk.ChunkSizeXZ.y)
                    continue;

                int   res = chunk.HeightmapRes;
                float fx  = (relX / chunk.ChunkSizeXZ.x) * (res - 1);
                float fz  = (relZ / chunk.ChunkSizeXZ.y) * (res - 1);
                int   ix  = math.clamp((int)fx, 0, res - 2);
                int   iz  = math.clamp((int)fz, 0, res - 2);
                float tx  = fx - ix;
                float tz  = fz - iz;

                int   b   = chunk.ChunkIndex;
                float h00 = Heights[b +  iz      * res + ix    ];
                float h10 = Heights[b +  iz      * res + ix + 1];
                float h01 = Heights[b + (iz + 1) * res + ix    ];
                float h11 = Heights[b + (iz + 1) * res + ix + 1];

                float normH = math.lerp(math.lerp(h00, h10, tx), math.lerp(h01, h11, tx), tz);
                worldY = normH * chunk.TerrainHeight;

                float cellX = chunk.ChunkSizeXZ.x / (res - 1);
                float cellZ = chunk.ChunkSizeXZ.y / (res - 1);
                float dhDx  = (h10 - h00) * chunk.TerrainHeight / cellX;
                float dhDz  = (h01 - h00) * chunk.TerrainHeight / cellZ;
                normal      = math.normalize(new float3(-dhDx, 1f, -dhDz));

                found = true;
                break;
            }

            if (!found)
            {
                Output[outputIndex] = new NodeData
                {
                    WorldY      = 0f,
                    Normal      = new float3(0f, 1f, 0f),
                    Walkable    = false,
                    GlobalIndex = outputIndex,
                };
                return;
            }

            float slopeAngle = math.degrees(math.acos(math.clamp(normal.y, 0f, 1f)));
            bool walkable =
                worldY     >= MinWalkableHeight &&
                worldY     <= MaxWalkableHeight &&
                slopeAngle <= MaxSlopeAngle;

            Output[outputIndex] = new NodeData
            {
                WorldY      = worldY,
                Normal      = normal,
                Walkable    = walkable,
                GlobalIndex = outputIndex,
            };
        }
    }

    // =========================================================================
    // PhysicsObstacleOverlay  — retained for ForceFullRebuildSync
    // =========================================================================

    /// <summary>
    /// Synchronous batch physics overlay pass, used only by
    /// <see cref="TerrainGraphIntegration.ForceFullRebuildSync"/>.
    /// The normal runtime path uses the streamed <c>TickOverlay</c> instead
    /// to avoid a main-thread stall (Option A).
    /// </summary>
    public static class PhysicsObstacleOverlay
    {
        private static readonly Collider[] s_buffer = new Collider[8];

        /// <summary>
        /// Marks nodes inside <paramref name="region"/> as unwalkable when a
        /// physics collider overlaps them. Must be called after
        /// <c>JobHandle.Complete()</c> — physics API requires the main thread.
        /// </summary>
        public static void Apply(
            NativeArray<NodeData> stagingBuffer,
            PathfindingGraph      graph,
            DirtyRegion           region,
            float                 overlapRadius,
            LayerMask             obstacleLayers)
        {
            DirtyRegion clamped = graph.ClampRegion(region);
            if (!clamped.IsValid) return;

            float nodeSpacing = graph.Config.NodeSpacing;

            for (int nz = clamped.Min.y; nz <= clamped.Max.y; nz++)
            for (int nx = clamped.Min.x; nx <= clamped.Max.x; nx++)
            {
                int idx = graph.NodeToIndex(nx, nz);
                if (idx < 0) continue;

                NodeData nd = stagingBuffer[idx];
                if (!nd.Walkable) continue;

                float   worldX = nx * nodeSpacing;
                float   worldZ = nz * nodeSpacing;
                Vector3 centre = new Vector3(worldX, nd.WorldY + overlapRadius, worldZ);

                int count = Physics.OverlapSphereNonAlloc(
                    centre, overlapRadius, s_buffer,
                    obstacleLayers, QueryTriggerInteraction.Ignore);

                if (count > 0)
                {
                    nd.Walkable       = false;
                    stagingBuffer[idx] = nd;
                }
            }
        }
    }
}
