using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Goodgulf.TerrainUtils
{
    [System.Serializable]
    public class Biome
    {
        public string name;

        [Header("Height Settings")]
        public float baseHeight;        // Flat offset
        public float amplitude;         // Vertical scale
        public float noiseScale;        // Detail frequency

        [Header("FBM")]
        public int octaves = 5;
        public float lacunarity = 2f;
        public float gain = 0.5f;
        
    }
    
    public class StreamingTerrainGeneratorJobs : MonoBehaviour
    {
        [Header("Seed")]
        public int seed = 12345; // Random seed used for terrain generation

        [Header("Biome")]
        public float biomeScale = 0.0002f; // Scale factor for biome noise
        public int biomeCount = 4; // Number of biomes to generate

        public Biome[] biomeDefinitions; // Definitions for each biome type
        
        [Header("Mountains")]
        public float mountainRidgeScale = 0.0006f; // Scale factor for mountain ridges
        public float mountainRidgeStrength = 0.25f; // Determines the prominence of mountain ridges

        [Header("Erosion")]
        public float erosionScale = 0.0012f; // Scale factor for erosion patterns
        public float erosionStrength = 2f; // Strength or intensity of erosion
        public float erosionDepth = 0.08f; // Depth impact of erosion on the terrain

        [Header("Rivers")]
        public float riverScale = 0.0009f; // Scale factor for river paths
        public float riverWidth = 0.03f; // Width of rivers in the terrain
        public float riverDepth = 0.12f; // Depth of rivers in the terrain

        // Method to generate a specific terrain chunk
        public void GenerateChunk(Terrain terrain, Vector2Int chunkCoord)
        {
#if TERRAIN_DEBUG            
            Debug.Log($"Generating chunk {chunkCoord.x}, {chunkCoord.y}");
            Debug.Log($"Chunk data ID={terrain.terrainData.GetInstanceID()}");
#endif            
            TerrainData data = terrain.terrainData; // Retrieve terrain data from the terrain object
            int res = data.heightmapResolution; // Resolution of the heightmap

            // Allocate a native array to store heights for processing in jobs
            NativeArray<float> heights =
                new NativeArray<float>(res * res, Allocator.TempJob);

            Vector3 size = data.size; // Dimensions of the terrain

            // Prepare biome parameters based on definitions
            NativeArray<BiomeParams> biomeParams =
                new NativeArray<BiomeParams>(biomeDefinitions.Length, Allocator.TempJob);
            
            // Populate biome parameters for job calculations
            for (int i = 0; i < biomeDefinitions.Length; i++)
            {
                biomeParams[i] = new BiomeParams
                {
                    baseHeight = biomeDefinitions[i].baseHeight,
                    amplitude  = biomeDefinitions[i].amplitude,
                    noiseScale = biomeDefinitions[i].noiseScale,
                    octaves    = biomeDefinitions[i].octaves,
                    lacunarity = biomeDefinitions[i].lacunarity,
                    gain       = biomeDefinitions[i].gain
                };
            }
            
            // Initialize and configure the terrain height job
            TerrainHeightJob job = new TerrainHeightJob
            {
                resolution = res,
                worldOrigin = new Vector2(
                    chunkCoord.x * size.x,
                    chunkCoord.y * size.z
                ),
                terrainSize = new Vector2(size.x, size.z),

                biomeScale = biomeScale,
                biomeCount = biomeCount,

                mountainRidgeScale = mountainRidgeScale,
                mountainRidgeStrength = mountainRidgeStrength,

                erosionScale = erosionScale,
                erosionStrength = erosionStrength,
                erosionDepth = erosionDepth,

                riverScale = riverScale,
                riverWidth = riverWidth,
                riverDepth = riverDepth,

                heights = heights,
                biomes = biomeParams
            };

            // Schedule and complete the job
            JobHandle handle = job.Schedule(heights.Length, 64);
            handle.Complete();

            // Apply computed heights back to the terrain data
            ApplyHeights(data, heights, res);

            // Dispose of native arrays to free memory
            heights.Dispose();
            biomeParams.Dispose();

            // Refresh the terrain collider with updated data
            TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
            collider.terrainData = data;
        }

        // Converts a flat array of heights into a 2D heightmap and applies it to the terrain
        void ApplyHeights(TerrainData data, NativeArray<float> flatHeights, int res)
        {
            float[,] heights2D = new float[res, res];

            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    heights2D[y, x] = flatHeights[y * res + x];
                }
            }

            // Set heights with delayed level of detail update for performance
            data.SetHeightsDelayLOD(0, 0, heights2D);
        }
    }
}
