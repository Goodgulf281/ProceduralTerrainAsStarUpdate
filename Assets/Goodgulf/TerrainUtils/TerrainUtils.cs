using UnityEngine;

namespace Goodgulf.TerrainUtils
{

    public static class TerrainUtils
    {
        public static void PushDownTerrain(
            Terrain terrain,
            Vector3 worldPos,
            float radius,
            float depth)
        {
            TerrainData data = terrain.terrainData;
            Vector3 terrainPos = terrain.transform.position;

            Debug.Log($"TerrainUtils.PushDownTerrain(): worldPos = {worldPos}");
            
            int heightmapResolution = data.heightmapResolution;

            // Convert world position to normalized terrain coordinates (0–1)
            float normX = (worldPos.x - terrainPos.x) / data.size.x;
            float normZ = (worldPos.z - terrainPos.z) / data.size.z;

            // Convert to heightmap coordinates
            int centerX = Mathf.RoundToInt(normX * (heightmapResolution - 1));
            int centerZ = Mathf.RoundToInt(normZ * (heightmapResolution - 1));

            int radiusInSamples = Mathf.RoundToInt(radius / data.size.x * heightmapResolution);

            int startX = Mathf.Clamp(centerX - radiusInSamples, 0, heightmapResolution - 1);
            int startZ = Mathf.Clamp(centerZ - radiusInSamples, 0, heightmapResolution - 1);
            int size = Mathf.Clamp(radiusInSamples * 2, 1, heightmapResolution);

            float[,] heights = data.GetHeights(startX, startZ, size, size);

            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - size / 2f;
                    float dz = z - size / 2f;
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);

                    if (distance <= radiusInSamples)
                    {
                        float falloff = 1f - (distance / radiusInSamples);
                        heights[z, x] -= depth * falloff / data.size.y;
                    }
                }
            }

            data.SetHeights(startX, startZ, heights);
        }
    }
}