using System;
using UnityEngine;
using System.Collections.Generic;

namespace Goodgulf.TerrainUtils
{
    /// <summary>
    /// Deterministic prefab placement system for terrain chunks.
    /// Uses seeded random generation to ensure consistent placement across platforms.
    /// </summary>
    public class TerrainPrefabPlacer : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private PrefabPlacementConfig placementConfig;
        
        [Header("Terrain Reference")]
        [SerializeField] private Terrain terrain;
        
        [Header("Seed Settings")]
        [SerializeField] private int baseSeed = 12345;
        
        [Header("Parent Transform")]
        [Tooltip("Parent transform to organize spawned objects. If null, uses this transform.")]
        [SerializeField] private Transform spawnParent;
        
        // Cache for placed objects per chunk
        private Dictionary<Vector2Int, List<GameObject>> placedObjectsPerChunk = new Dictionary<Vector2Int, List<GameObject>>();
        
        // Prefab registry for efficient lookups (name -> prefab)
        private Dictionary<int, GameObject> prefabRegistry = new Dictionary<int, GameObject>();
        
        // Track which chunks are loaded from cache
        private HashSet<Vector2Int> cachedChunks = new HashSet<Vector2Int>();
        
        private void Awake()
        {
            if (spawnParent == null)
            {
                spawnParent = transform;
            }
            
            BuildPrefabRegistry();
        }
        
        /// <summary>
        /// Build a registry of prefabs for fast lookup during cache loading.
        /// </summary>
        private void BuildPrefabRegistry()
        {
            prefabRegistry.Clear();
            
            if (placementConfig == null) return;
            
            foreach (var rule in placementConfig.placementRules)
            {
                if (rule.prefab != null)
                {
                    int prefabId = rule.prefab.name.GetHashCode();
                    if (!prefabRegistry.ContainsKey(prefabId))
                    {
                        prefabRegistry[prefabId] = rule.prefab;
                    }
                }
            }
        }
        
        /// <summary>
        /// Place prefabs on a specific terrain chunk using deterministic placement.
        /// Uses persistent caching to avoid regenerating chunks.
        /// </summary>
        /// <param name="chunkX">Chunk X coordinate</param>
        /// <param name="chunkZ">Chunk Z coordinate</param>
        /// <param name="chunkSize">Size of the chunk in world units</param>
        public void PlacePrefabsOnChunk(int chunkX, int chunkZ, float chunkSize)
        {
            if (placementConfig == null)
            {
                Debug.LogError("PrefabPlacementConfig is not assigned!");
                return;
            }
            
            if (terrain == null)
            {
                Debug.LogError("Terrain is not assigned!");
                return;
            }
            
            Vector2Int chunkCoord = new Vector2Int(chunkX, chunkZ);
            
            // Clear existing objects for this chunk if any
            ClearChunk(chunkCoord);
            
            // Try to load from cache first
            if (placementConfig.enablePersistentCache && TryLoadFromCache(chunkCoord, chunkSize))
            {
                cachedChunks.Add(chunkCoord);
                return;
            }
            
            // Generate new placement data
            List<GameObject> chunkObjects = new List<GameObject>();
            placedObjectsPerChunk[chunkCoord] = chunkObjects;
            
            // Calculate chunk world position
            Vector3 chunkWorldPos = new Vector3(chunkX * chunkSize, 0, chunkZ * chunkSize);
            
            // For caching: store placement data
            ChunkPrefabCache cache = null;
            if (placementConfig.enablePersistentCache)
            {
                cache = new ChunkPrefabCache
                {
                    chunkX = chunkX,
                    chunkZ = chunkZ,
                    seed = baseSeed,
                    configHash = placementConfig.GetConfigHash(),
                    timestamp = DateTime.UtcNow.Ticks
                };
            }
            
            // Process each placement rule
            foreach (var rule in placementConfig.placementRules)
            {
                if (rule.prefab == null) continue;
                
                PlacePrefabsForRule(rule, chunkCoord, chunkWorldPos, chunkSize, chunkObjects, cache);
            }
            
            // Save cache if enabled
            if (placementConfig.enablePersistentCache && cache != null)
            {
                ChunkCacheManager.SaveCache(cache);
                cachedChunks.Add(chunkCoord);
            }
        }
        
        /// <summary>
        /// Try to load chunk from cache.
        /// </summary>
        private bool TryLoadFromCache(Vector2Int chunkCoord, float chunkSize)
        {
            // Load cache from disk
            ChunkPrefabCache cache = ChunkCacheManager.LoadCache(chunkCoord.x, chunkCoord.y);
            
            if (cache == null)
            {
                return false;
            }
            
            // Validate cache
            int currentConfigHash = placementConfig.GetConfigHash();
            if (!cache.IsValid(baseSeed, currentConfigHash))
            {
                // Cache is invalid, delete it
                if (placementConfig.invalidateCacheOnConfigChange)
                {
                    ChunkCacheManager.DeleteCache(chunkCoord.x, chunkCoord.y);
                }
                return false;
            }
            
            // Cache is valid, instantiate prefabs from cached data
            List<GameObject> chunkObjects = new List<GameObject>();
            
            foreach (var data in cache.placedPrefabs)
            {
                if (prefabRegistry.TryGetValue(data.prefabId, out GameObject prefab))
                {
                    GameObject instance = Instantiate(prefab, data.position, data.rotation, spawnParent);
                    instance.transform.localScale = data.scale;
                    chunkObjects.Add(instance);
                }
                else
                {
                    Debug.LogWarning($"Prefab with ID {data.prefabId} not found in registry");
                }
            }
            
            placedObjectsPerChunk[chunkCoord] = chunkObjects;
            
            return true;
        }
        
        /// <summary>
        /// Place prefabs for a specific rule within a chunk.
        /// </summary>
        private void PlacePrefabsForRule(
            PrefabPlacementConfig.PrefabPlacementRule rule,
            Vector2Int chunkCoord,
            Vector3 chunkWorldPos,
            float chunkSize,
            List<GameObject> chunkObjects,
            ChunkPrefabCache cache = null)
        {
            // Create deterministic seed for this rule and chunk
            int ruleSeed = GetDeterministicSeed(chunkCoord.x, chunkCoord.y, rule.prefab.GetInstanceID());
            System.Random random = new System.Random(ruleSeed);
            
            // Calculate number of placement attempts based on density and chunk size
            float chunkArea = chunkSize * chunkSize;
            int placementAttempts = Mathf.Min(
                Mathf.CeilToInt(chunkArea * rule.density),
                placementConfig.maxPlacementAttempts
            );
            
            // Track positions for spacing enforcement
            List<Vector3> placedPositions = new List<Vector3>();
            
            // Prefab ID for caching
            int prefabId = rule.prefab.name.GetHashCode();
            
            for (int i = 0; i < placementAttempts; i++)
            {
                // Generate random position within chunk
                float localX = (float)random.NextDouble() * chunkSize;
                float localZ = (float)random.NextDouble() * chunkSize;
                
                Vector3 worldPos = chunkWorldPos + new Vector3(localX, 0, localZ);
                
                // Sample terrain height and normal at this position
                if (!SampleTerrain(worldPos, out float height, out Vector3 normal))
                {
                    continue; // Position outside terrain bounds
                }
                
                worldPos.y = height;
                
                // Check terrain constraints
                if (!MeetsTerrainConstraints(worldPos, normal, rule))
                {
                    continue;
                }
                
                // Check spacing constraint
                if (!MeetsSpacingConstraint(worldPos, placedPositions, rule.minSpacing))
                {
                    continue;
                }
                
                // Check edge constraints if terrain modification is enabled
                bool terrainWillBeModified = false;
                if (rule.modifyTerrain)
                {
                    bool isNearEdge = IsPositionNearChunkEdge(
                        worldPos,
                        chunkWorldPos,
                        chunkSize,
                        rule.terrainModificationRadius
                    );
                    
                    if (isNearEdge)
                    {
                        // Skip placement entirely if configured to do so
                        if (placementConfig.preventPlacementNearEdges)
                        {
                            continue;
                        }
                        // Otherwise, we'll place but skip terrain modification later
                    }
                    else
                    {
                        terrainWillBeModified = true;
                    }
                }
                
                // Calculate rotation
                Quaternion rotation = CalculateRotation(random, normal, rule);
                
                // Calculate scale
                Vector3 scale = CalculateScale(random, rule);
                
                // Apply surface offset
                worldPos.y += rule.surfaceOffset;
                
                // Instantiate prefab
                GameObject instance = Instantiate(rule.prefab, worldPos, rotation, spawnParent);
                instance.transform.localScale = scale;
                
                // Modify terrain if enabled for this rule and not near edge
                if (terrainWillBeModified)
                {
                    ModifyTerrainAroundPrefab(worldPos, rule);
                }
                
                // Add to tracking lists
                chunkObjects.Add(instance);
                placedPositions.Add(worldPos);
                
                // Add to cache if enabled
                if (cache != null)
                {
                    cache.placedPrefabs.Add(new ChunkPrefabCache.PlacedPrefabData
                    {
                        prefabId = prefabId,
                        position = worldPos,
                        rotation = rotation,
                        scale = scale,
                        terrainModified = terrainWillBeModified
                    });
                }
            }
        }
        
        /// <summary>
        /// Generate a deterministic seed based on chunk coordinates and rule identifier.
        /// </summary>
        private int GetDeterministicSeed(int chunkX, int chunkZ, int ruleId)
        {
            // Use a hash-like combination to create unique but deterministic seeds
            int seed = baseSeed;
            seed = seed * 31 + chunkX;
            seed = seed * 31 + chunkZ;
            seed = seed * 31 + ruleId;
            seed = seed * 31 + placementConfig.seedOffset;
            return seed;
        }
        
        /// <summary>
        /// Sample terrain height and normal at a world position.
        /// </summary>
        private bool SampleTerrain(Vector3 worldPos, out float height, out Vector3 normal)
        {
            TerrainData terrainData = terrain.terrainData;
            Vector3 terrainPos = terrain.GetPosition();
            
            // Convert world position to terrain-local position
            Vector3 localPos = worldPos - terrainPos;
            
            // Check if position is within terrain bounds
            if (localPos.x < 0 || localPos.x > terrainData.size.x ||
                localPos.z < 0 || localPos.z > terrainData.size.z)
            {
                height = 0;
                normal = Vector3.up;
                return false;
            }
            
            // Normalize to 0-1 range for terrain sampling
            float normalizedX = localPos.x / terrainData.size.x;
            float normalizedZ = localPos.z / terrainData.size.z;
            
            // Sample height
            height = terrain.SampleHeight(worldPos);
            
            // Sample normal
            normal = terrainData.GetInterpolatedNormal(normalizedX, normalizedZ);
            
            return true;
        }
        
        /// <summary>
        /// Check if a position meets the terrain constraints for a rule.
        /// </summary>
        private bool MeetsTerrainConstraints(
            Vector3 position,
            Vector3 normal,
            PrefabPlacementConfig.PrefabPlacementRule rule)
        {
            // Check height constraints
            if (position.y < rule.minHeight || position.y > rule.maxHeight)
            {
                return false;
            }
            
            // Check slope constraints
            float slope = Vector3.Angle(Vector3.up, normal);
            if (slope < rule.minSlope || slope > rule.maxSlope)
            {
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Check if a position maintains minimum spacing from other placed objects.
        /// </summary>
        private bool MeetsSpacingConstraint(
            Vector3 position,
            List<Vector3> placedPositions,
            float minSpacing)
        {
            if (minSpacing <= 0) return true;
            
            float minSpacingSqr = minSpacing * minSpacing;
            
            foreach (Vector3 placedPos in placedPositions)
            {
                float distanceSqr = (position - placedPos).sqrMagnitude;
                if (distanceSqr < minSpacingSqr)
                {
                    return false;
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// Calculate rotation based on rule settings and terrain normal.
        /// </summary>
        private Quaternion CalculateRotation(
            System.Random random,
            Vector3 normal,
            PrefabPlacementConfig.PrefabPlacementRule rule)
        {
            Quaternion rotation = Quaternion.identity;
            
            // Start with terrain alignment if enabled
            if (rule.alignToTerrainNormal)
            {
                rotation = Quaternion.FromToRotation(Vector3.up, normal);
            }
            
            // Apply random or fixed rotations
            Vector3 eulerRotation = rule.fixedRotation;
            
            if (rule.randomRotationX)
            {
                eulerRotation.x = (float)random.NextDouble() * 360f;
            }
            
            if (rule.randomRotationY)
            {
                eulerRotation.y = (float)random.NextDouble() * 360f;
            }
            
            if (rule.randomRotationZ)
            {
                eulerRotation.z = (float)random.NextDouble() * 360f;
            }
            
            // Combine rotations
            rotation = rotation * Quaternion.Euler(eulerRotation);
            
            return rotation;
        }
        
        /// <summary>
        /// Calculate scale based on rule settings.
        /// </summary>
        private Vector3 CalculateScale(
            System.Random random,
            PrefabPlacementConfig.PrefabPlacementRule rule)
        {
            if (rule.randomScale)
            {
                float scale = Mathf.Lerp(rule.minScale, rule.maxScale, (float)random.NextDouble());
                return Vector3.one * scale;
            }
            
            return Vector3.one;
        }
        
        /// <summary>
        /// Check if a position is near the edge of a chunk, considering the modification radius.
        /// </summary>
        private bool IsPositionNearChunkEdge(
            Vector3 worldPos,
            Vector3 chunkWorldPos,
            float chunkSize,
            float modificationRadius)
        {
            // Calculate position within chunk (0 to chunkSize)
            float localX = worldPos.x - chunkWorldPos.x;
            float localZ = worldPos.z - chunkWorldPos.z;
            
            // Get the buffer distance from config
            float bufferDistance = placementConfig.edgeBufferDistance;
            
            // Take the larger of the two: modification radius or buffer distance
            float effectiveBuffer = Mathf.Max(modificationRadius, bufferDistance);
            
            // Check if within buffer distance of any edge
            bool nearLeftEdge = localX < effectiveBuffer;
            bool nearRightEdge = localX > (chunkSize - effectiveBuffer);
            bool nearBottomEdge = localZ < effectiveBuffer;
            bool nearTopEdge = localZ > (chunkSize - effectiveBuffer);
            
            return nearLeftEdge || nearRightEdge || nearBottomEdge || nearTopEdge;
        }
        
        /// <summary>
        /// Modify terrain height around a prefab placement.
        /// </summary>
        private void ModifyTerrainAroundPrefab(
            Vector3 worldPos,
            PrefabPlacementConfig.PrefabPlacementRule rule)
        {
            TerrainData terrainData = terrain.terrainData;
            Vector3 terrainPos = terrain.GetPosition();
            
            // Convert world position to terrain-local position
            Vector3 localPos = worldPos - terrainPos;
            
            // Calculate target height based on modification type
            float targetHeight = CalculateTargetHeight(worldPos.y, rule);
            
            // Convert to terrain height (0-1 normalized)
            float normalizedTargetHeight = (targetHeight - terrainPos.y) / terrainData.size.y;
            
            // Calculate the affected area in heightmap coordinates
            int heightmapWidth = terrainData.heightmapResolution;
            int heightmapHeight = terrainData.heightmapResolution;
            
            // Convert world radius to heightmap coordinates
            float radiusInHeightmapX = (rule.terrainModificationRadius / terrainData.size.x) * heightmapWidth;
            float radiusInHeightmapZ = (rule.terrainModificationRadius / terrainData.size.z) * heightmapHeight;
            
            // Calculate center position in heightmap coordinates
            int centerX = Mathf.RoundToInt((localPos.x / terrainData.size.x) * heightmapWidth);
            int centerZ = Mathf.RoundToInt((localPos.z / terrainData.size.z) * heightmapHeight);
            
            // Calculate bounds of affected area
            int minX = Mathf.Max(0, centerX - Mathf.CeilToInt(radiusInHeightmapX));
            int maxX = Mathf.Min(heightmapWidth, centerX + Mathf.CeilToInt(radiusInHeightmapX));
            int minZ = Mathf.Max(0, centerZ - Mathf.CeilToInt(radiusInHeightmapZ));
            int maxZ = Mathf.Min(heightmapHeight, centerZ + Mathf.CeilToInt(radiusInHeightmapZ));
            
            // Safety check: ensure we're not at the edge of the terrain itself
            if (minX <= 1 || maxX >= heightmapWidth - 1 || minZ <= 1 || maxZ >= heightmapHeight - 1)
            {
                // Too close to terrain edge, skip modification
                Debug.LogWarning($"Terrain modification skipped at position {worldPos} - too close to terrain boundary");
                return;
            }
            
            // Get current heights
            int width = maxX - minX;
            int height = maxZ - minZ;
            float[,] heights = terrainData.GetHeights(minX, minZ, width, height);
            
            // Modify heights
            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int worldX = minX + x;
                    int worldZ = minZ + z;
                    
                    // Calculate distance from center
                    float dx = (worldX - centerX) / radiusInHeightmapX;
                    float dz = (worldZ - centerZ) / radiusInHeightmapZ;
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);
                    
                    if (distance <= 1f)
                    {
                        // Calculate blend factor based on distance and smoothness
                        float blendFactor = CalculateBlendFactor(distance, rule.modificationSmoothness);
                        
                        // Get current height
                        float currentHeight = heights[z, x];
                        
                        // Apply modification based on type
                        float newHeight = ApplyTerrainModification(
                            currentHeight,
                            normalizedTargetHeight,
                            blendFactor,
                            rule.modificationType
                        );
                        
                        heights[z, x] = newHeight;
                    }
                }
            }
            
            // Apply modified heights back to terrain
            terrainData.SetHeights(minX, minZ, heights);
        }
        
        /// <summary>
        /// Calculate the target height for terrain modification.
        /// </summary>
        private float CalculateTargetHeight(
            float spawnHeight,
            PrefabPlacementConfig.PrefabPlacementRule rule)
        {
            switch (rule.modificationType)
            {
                case PrefabPlacementConfig.TerrainModificationType.FlattenToSpawnPoint:
                case PrefabPlacementConfig.TerrainModificationType.RaiseToSpawnPoint:
                case PrefabPlacementConfig.TerrainModificationType.LowerToSpawnPoint:
                    return spawnHeight;
                
                case PrefabPlacementConfig.TerrainModificationType.FlattenToOffset:
                    return spawnHeight + rule.terrainHeightOffset;
                
                case PrefabPlacementConfig.TerrainModificationType.RaiseByOffset:
                    return spawnHeight + rule.terrainHeightOffset;
                
                default:
                    return spawnHeight;
            }
        }
        
        /// <summary>
        /// Calculate blend factor for smooth terrain transitions.
        /// </summary>
        private float CalculateBlendFactor(float distance, float smoothness)
        {
            if (distance >= 1f) return 0f;
            if (smoothness < 0.01f) return 1f;
            
            // Use smoothstep for natural-looking transitions
            float t = 1f - distance;
            float smoothRange = smoothness;
            
            if (t > 1f - smoothRange)
            {
                // In the transition zone
                float normalizedT = (t - (1f - smoothRange)) / smoothRange;
                // Smoothstep function
                return normalizedT * normalizedT * (3f - 2f * normalizedT);
            }
            else
            {
                // Full effect
                return 1f;
            }
        }
        
        /// <summary>
        /// Apply terrain modification based on the modification type.
        /// </summary>
        private float ApplyTerrainModification(
            float currentHeight,
            float targetHeight,
            float blendFactor,
            PrefabPlacementConfig.TerrainModificationType modificationType)
        {
            switch (modificationType)
            {
                case PrefabPlacementConfig.TerrainModificationType.FlattenToSpawnPoint:
                case PrefabPlacementConfig.TerrainModificationType.FlattenToOffset:
                    // Blend between current and target height
                    return Mathf.Lerp(currentHeight, targetHeight, blendFactor);
                
                case PrefabPlacementConfig.TerrainModificationType.RaiseToSpawnPoint:
                case PrefabPlacementConfig.TerrainModificationType.RaiseByOffset:
                    // Only raise terrain, never lower
                    if (targetHeight > currentHeight)
                    {
                        return Mathf.Lerp(currentHeight, targetHeight, blendFactor);
                    }
                    return currentHeight;
                
                case PrefabPlacementConfig.TerrainModificationType.LowerToSpawnPoint:
                    // Only lower terrain, never raise
                    if (targetHeight < currentHeight)
                    {
                        return Mathf.Lerp(currentHeight, targetHeight, blendFactor);
                    }
                    return currentHeight;
                
                default:
                    return currentHeight;
            }
        }
        
        /// <summary>
        /// Clear all placed objects for a specific chunk.
        /// </summary>
        public void ClearChunk(Vector2Int chunkCoord)
        {
            if (placedObjectsPerChunk.TryGetValue(chunkCoord, out List<GameObject> objects))
            {
                foreach (GameObject obj in objects)
                {
                    if (obj != null)
                    {
                        Destroy(obj);
                    }
                }
                objects.Clear();
                placedObjectsPerChunk.Remove(chunkCoord);
            }
            
            // Remove from cached chunks set
            cachedChunks.Remove(chunkCoord);
        }
        
        /// <summary>
        /// Clear all placed objects across all chunks.
        /// </summary>
        public void ClearAllChunks()
        {
            foreach (var kvp in placedObjectsPerChunk)
            {
                foreach (GameObject obj in kvp.Value)
                {
                    if (obj != null)
                    {
                        Destroy(obj);
                    }
                }
            }
            placedObjectsPerChunk.Clear();
            cachedChunks.Clear();
        }
        
        /// <summary>
        /// Set the base seed for prefab placement.
        /// </summary>
        public void SetBaseSeed(int seed)
        {
            baseSeed = seed;
        }
        
        /// <summary>
        /// Set the placement configuration.
        /// </summary>
        public void SetPlacementConfig(PrefabPlacementConfig config)
        {
            placementConfig = config;
            BuildPrefabRegistry();
        }
        
        /// <summary>
        /// Set the terrain reference.
        /// </summary>
        public void SetTerrain(Terrain terrainRef)
        {
            terrain = terrainRef;
        }
        
        // ===== Cache Management Methods =====
        
        /// <summary>
        /// Clear the persistent cache for a specific chunk.
        /// </summary>
        public void ClearChunkCache(int chunkX, int chunkZ)
        {
            ChunkCacheManager.DeleteCache(chunkX, chunkZ);
            cachedChunks.Remove(new Vector2Int(chunkX, chunkZ));
        }
        
        /// <summary>
        /// Clear all persistent caches.
        /// </summary>
        public void ClearAllCaches()
        {
            ChunkCacheManager.ClearAllCaches();
            cachedChunks.Clear();
        }
        
        /// <summary>
        /// Get the total size of cached data in bytes.
        /// </summary>
        public long GetCacheSizeBytes()
        {
            return ChunkCacheManager.GetTotalCacheSize();
        }
        
        /// <summary>
        /// Get the total size of cached data in megabytes.
        /// </summary>
        public float GetCacheSizeMB()
        {
            return GetCacheSizeBytes() / (1024f * 1024f);
        }
        
        /// <summary>
        /// Check if a chunk is loaded from cache.
        /// </summary>
        public bool IsChunkCached(int chunkX, int chunkZ)
        {
            return cachedChunks.Contains(new Vector2Int(chunkX, chunkZ));
        }
        
        /// <summary>
        /// Get statistics about cache usage.
        /// </summary>
        public string GetCacheStats()
        {
            int totalChunks = placedObjectsPerChunk.Count;
            int cachedCount = cachedChunks.Count;
            float sizeInMB = GetCacheSizeMB();
            
            return $"Loaded Chunks: {totalChunks} | Cached: {cachedCount} | Cache Size: {sizeInMB:F2} MB";
        }
    }
    
}
