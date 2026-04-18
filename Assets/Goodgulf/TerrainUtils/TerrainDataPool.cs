using UnityEngine;
using System.Collections.Generic;

namespace Goodgulf.TerrainUtils
{

    public class TerrainDataPool : MonoBehaviour
    {
        // Singleton instance of the TerrainDataPool
        public static TerrainDataPool Instance;

        [Header("Template")] 
        // Template terrain data used to create new instances in the pool
        public TerrainData template;

        [Header("Pool Settings")] 
        // Number of pre-created terrain data instances in the pool
        public int prewarmCount = 8;

        // Queue to store pooled terrain data instances
        Queue<TerrainData> pool = new();

        void Awake()
        {
            // Set the singleton instance and initialize the pool with prewarmed data
            Instance = this;
            Prewarm();
        }

        void Prewarm()
        {
            // Create and add a specified number of terrain data instances to the pool
            for (int i = 0; i < prewarmCount; i++)
                pool.Enqueue(CreateTerrainData());
        }

        TerrainData CreateTerrainData()
        {
            // Instantiate a new TerrainData object from the template
            TerrainData data = Instantiate(template);
            data.name = "PooledTerrainData";
            
            // 🔒 Explicitly copy layers to ensure individual instances have their own layer settings
            data.terrainLayers = template.terrainLayers;
            
            return data;
        }

        public TerrainData Get()
        {
            // Retrieve an available terrain data instance from the pool or create a new one if empty
            return pool.Count > 0 ? pool.Dequeue() : CreateTerrainData();
        }

        public void Release(TerrainData data)
        {
            // IMPORTANT: Clear heightmap data to prevent unintentional data carry-over
            data.SetHeights(0, 0, new float[data.heightmapResolution, data.heightmapResolution]);
            pool.Enqueue(data);
        }
        
        // Helper method to clear the height data array
        static float[,] ClearHeights(int res)
        {
            return new float[res, res];
        }
    }

}