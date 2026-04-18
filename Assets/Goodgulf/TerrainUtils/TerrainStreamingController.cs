using System;
using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Events;
using System.Threading.Tasks;
using System.Linq;

namespace Goodgulf.TerrainUtils
{
    /// <summary>
    /// Advanced terrain streaming controller that manages chunk loading/unloading
    /// around a player or camera position. Uses AsyncTerrainPrefabPlacer for
    /// smooth, frame-drop-free chunk streaming.
    /// Integrates with StreamingTerrainGeneratorJobs for terrain geometry generation.
    /// </summary>
    public class TerrainStreamingController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform player;
        [SerializeField] private Terrain terrainPrefab; // Prefab for terrain chunks
        [SerializeField] private PrefabPlacementConfig prefabPlacementConfig;
        
        [Header("Terrain Generation")]
        [SerializeField] private Goodgulf.TerrainUtils.StreamingTerrainGeneratorJobs terrainGenerator;
        [SerializeField] private int seed = 12345;
        [SerializeField] private float chunkSize = 1000f;
        
        [Header("Streaming Settings")]
        [Tooltip("How many chunks to load around the player (radius)")]
        [SerializeField] private int loadRadius = 3;
        
        [Tooltip("Unload chunks beyond this distance (should be > loadRadius)")]
        [SerializeField] private int unloadRadius = 5;
        
        [Tooltip("Maximum chunks to load per frame (prevents spikes)")]
        [SerializeField] private int maxChunksPerFrame = 3;
        
        [Tooltip("Update streaming every N frames (1 = every frame, 10 = every 10 frames)")]
        [SerializeField] private int updateInterval = 10;
        
        [Header("Performance")]
        [Tooltip("Maximum concurrent async load operations")]
        [SerializeField] private int maxConcurrentLoads = 10;
        
        [Tooltip("Priority loading for chunks closer to player")]
        [SerializeField] private bool usePriorityLoading = true;
        
        [Header("Events")]
        [Tooltip("Called when initial chunks are fully loaded")]
        public UnityEvent onInitialChunksLoaded;
        
        [Tooltip("Called when a chunk starts loading")]
        public UnityEvent<Vector2Int> onChunkLoadStart;
        
        [Tooltip("Called when a chunk finishes loading")]
        public UnityEvent<Vector2Int> onChunkLoadComplete;
        
        [Tooltip("Called when a chunk is unloaded")]
        public UnityEvent<Vector2Int> onChunkUnload;
        
        [Header("Debug")]
        [SerializeField] private bool showDebugGizmos = true;
        [SerializeField] private bool logChunkOperations = false;
        
        [Header("Cache Management")]
        [SerializeField] private bool clearCacheOnStart = false;
        
        // Async prefab placer
        private AsyncTerrainPrefabPlacer asyncPrefabPlacer;
        
        // Track loaded and loading chunks
        private HashSet<Vector2Int> loadedChunks = new HashSet<Vector2Int>();
        private HashSet<Vector2Int> loadingChunks = new HashSet<Vector2Int>();
        private Dictionary<Vector2Int, Task> chunkLoadTasks = new Dictionary<Vector2Int, Task>();
        
        // Track terrain instances per chunk
        private Dictionary<Vector2Int, Terrain> terrainInstances = new Dictionary<Vector2Int, Terrain>();
        
        // Priority queue for chunk loading
        private List<ChunkLoadRequest> pendingLoadRequests = new List<ChunkLoadRequest>();
        
        // Current player chunk position
        private Vector2Int currentPlayerChunk;
        private Vector2Int previousPlayerChunk;
        
        // Frame counter for update interval
        private int frameCounter = 0;
        
        // Initial load tracking
        private bool initialLoadComplete = false;
        private int initialChunkCount = 0;
        
        // Statistics
        private int totalChunksLoaded = 0;
        private int totalChunksUnloaded = 0;
        private float averageLoadTime = 0f;
        private List<float> recentLoadTimes = new List<float>();
        
        private struct ChunkLoadRequest
        {
            public Vector2Int chunkCoord;
            public float distanceToPlayer;
            public int priority;
        }
        
        private void Start()
        {
            InitializeAsyncPlacer();
            
            if (player == null)
            {
                Debug.LogWarning("Player transform not assigned! Using Camera.main");
                if (Camera.main != null)
                {
                    player = Camera.main.transform;
                }
            }

            if (clearCacheOnStart)
            {
                // _ = InitializeAsync();

                ChunkCacheManager.ClearAllCaches();

            }

            // Load initial chunks around player
            UpdatePlayerChunkPosition();
            _ = LoadInitialChunks();
        }
        
        /// <summary>
        /// Initialize the async prefab placer.
        /// </summary>
        private void InitializeAsyncPlacer()
        {
            asyncPrefabPlacer = gameObject.GetComponent<AsyncTerrainPrefabPlacer>();

            if (asyncPrefabPlacer == null)
            {
                Debug.LogError("AsyncTerrainPrefabPlacer not assigned!");
                return;
            }
            // Note: Terrain will be set per-chunk in LoadChunk
            
            // asyncPrefabPlacer.SetPlacementConfig(prefabPlacementConfig);
            // asyncPrefabPlacer.SetBaseSeed(seed);

            if (logChunkOperations)
            {
                Debug.Log("TerrainStreamingController initialized with AsyncTerrainPrefabPlacer");
            }
        }
        
        async Task InitializeAsync()
        {
            try
            {
                await asyncPrefabPlacer.ClearAllCachesAsync();
            }
            catch (Exception e)
            {
                Debug.LogError($"Init failed: {e}");
            }
        }
        
        
        private void Update()
        {
            frameCounter++;
            
            // Update streaming at specified interval
            if (frameCounter >= updateInterval)
            {
                frameCounter = 0;
                UpdateStreaming();
            }
            
            // Process pending load requests
            ProcessPendingLoadRequests();
        }
        
        /// <summary>
        /// Main streaming update - determines which chunks to load/unload.
        /// </summary>
        private void UpdateStreaming()
        {
            if (player == null) return;
            
            UpdatePlayerChunkPosition();
            
            // Check if player moved to a new chunk
            if (currentPlayerChunk != previousPlayerChunk)
            {
                if (logChunkOperations)
                {
                    Debug.Log($"Player moved to chunk {currentPlayerChunk}");
                }
                
                // Queue chunks that need to be loaded
                QueueChunksToLoad();
                
                // Unload distant chunks
                UnloadDistantChunks();
                
                previousPlayerChunk = currentPlayerChunk;
            }
        }
        
        /// <summary>
        /// Update the current player chunk position.
        /// </summary>
        private void UpdatePlayerChunkPosition()
        {
            Vector3 playerPos = player.position;
            currentPlayerChunk = new Vector2Int(
                Mathf.FloorToInt(playerPos.x / chunkSize),
                Mathf.FloorToInt(playerPos.z / chunkSize)
            );
        }
        
        /// <summary>
        /// Load initial chunks around player at startup.
        /// </summary>
        private async Task LoadInitialChunks()
        {
            if (logChunkOperations)
            {
                Debug.Log($"Loading initial chunks around {currentPlayerChunk} (radius: {loadRadius})");
            }
            
            List<ChunkLoadRequest> initialRequests = new List<ChunkLoadRequest>();
            
            // Generate list of chunks to load
            for (int x = -loadRadius; x <= loadRadius; x++)
            {
                for (int z = -loadRadius; z <= loadRadius; z++)
                {
                    Vector2Int chunkCoord = currentPlayerChunk + new Vector2Int(x, z);
                    
                    // Check if within circular radius
                    float distance = new Vector2(x, z).magnitude;
                    if (distance <= loadRadius)
                    {
                        initialRequests.Add(new ChunkLoadRequest
                        {
                            chunkCoord = chunkCoord,
                            distanceToPlayer = distance,
                            priority = Mathf.RoundToInt(distance * 10)
                        });
                    }
                }
            }
            
            // Sort by priority (closest first)
            initialRequests.Sort((a, b) => a.priority.CompareTo(b.priority));
            
            initialChunkCount = initialRequests.Count;
            
            // Load chunks in batches to avoid overwhelming the system
            int batchSize = maxConcurrentLoads;
            for (int i = 0; i < initialRequests.Count; i += batchSize)
            {
                int count = Mathf.Min(batchSize, initialRequests.Count - i);
                List<Task> batchTasks = new List<Task>();
                
                for (int j = 0; j < count; j++)
                {
                    var request = initialRequests[i + j];
                    batchTasks.Add(LoadChunk(request.chunkCoord));
                }
                
                // Wait for this batch to complete before starting next
                await Task.WhenAll(batchTasks);
                
                // Allow a frame to pass for instantiation
                await Task.Yield();
            }
            
            if (logChunkOperations)
            {
                Debug.Log($"Initial chunk loading complete. Loaded {initialRequests.Count} chunks");
            }
            
            // Mark initial load as complete and fire event
            initialLoadComplete = true;

            onInitialChunksLoaded?.Invoke();
        }
        
        /// <summary>
        /// Queue chunks that need to be loaded based on player position.
        /// </summary>
        private void QueueChunksToLoad()
        {
            for (int x = -loadRadius; x <= loadRadius; x++)
            {
                for (int z = -loadRadius; z <= loadRadius; z++)
                {
                    Vector2Int chunkCoord = currentPlayerChunk + new Vector2Int(x, z);
                    
                    // Check if within circular radius
                    float distance = new Vector2(x, z).magnitude;
                    if (distance > loadRadius) continue;
                    
                    // Skip if already loaded or loading
                    if (loadedChunks.Contains(chunkCoord) || loadingChunks.Contains(chunkCoord))
                    {
                        continue;
                    }
                    
                    // Add to pending requests if not already queued
                    if (!pendingLoadRequests.Any(r => r.chunkCoord == chunkCoord))
                    {
                        pendingLoadRequests.Add(new ChunkLoadRequest
                        {
                            chunkCoord = chunkCoord,
                            distanceToPlayer = distance,
                            priority = Mathf.RoundToInt(distance * 10)
                        });
                    }
                }
            }
            
            // Sort by priority if enabled
            if (usePriorityLoading)
            {
                pendingLoadRequests.Sort((a, b) => a.priority.CompareTo(b.priority));
            }
        }
        
        /// <summary>
        /// Process pending chunk load requests, respecting max concurrent loads.
        /// </summary>
        private void ProcessPendingLoadRequests()
        {
            if (pendingLoadRequests.Count == 0) return;
            
            // Count current loading operations
            int currentlyLoading = loadingChunks.Count;
            
            // Calculate how many more we can start
            int availableSlots = Mathf.Min(
                maxConcurrentLoads - currentlyLoading,
                maxChunksPerFrame
            );
            
            if (availableSlots <= 0) return;
            
            // Start loading for available slots
            int loaded = 0;
            while (loaded < availableSlots && pendingLoadRequests.Count > 0)
            {
                var request = pendingLoadRequests[0];
                pendingLoadRequests.RemoveAt(0);
                
                // Double-check it's not already loaded/loading
                if (!loadedChunks.Contains(request.chunkCoord) && 
                    !loadingChunks.Contains(request.chunkCoord))
                {
                    _ = LoadChunk(request.chunkCoord);
                    loaded++;
                }
            }
        }
        
        /// <summary>
        /// Load a single chunk asynchronously.
        /// </summary>
        private async Task LoadChunk(Vector2Int chunkCoord)
        {
            // Mark as loading
            loadingChunks.Add(chunkCoord);
            
            // Fire start event
            onChunkLoadStart?.Invoke(chunkCoord);
            
            float startTime = Time.realtimeSinceStartup;
            
            try
            {
                if (logChunkOperations)
                {
                    Debug.Log($"Loading chunk {chunkCoord}...");
                }
                
                // Step 1: Create or get terrain instance for this chunk
                Terrain terrainInstance = GetOrCreateTerrainInstance(chunkCoord);

                if (terrainInstance == null)
                {
                    Debug.LogError($"Terrain at {chunkCoord} is null");
                    return;
                }
                
                // Step 2: Generate terrain geometry using StreamingTerrainGeneratorJobs
                if (terrainGenerator != null)
                {
                    terrainGenerator.GenerateChunk(terrainInstance, chunkCoord);
                }
                else
                {
                    Debug.LogWarning("StreamingTerrainGeneratorJobs not assigned! Terrain geometry will not be generated.");
                }
                
                // Step 3: Place prefabs on the terrain using async placer
                asyncPrefabPlacer.SetTerrain(terrainInstance);
                await asyncPrefabPlacer.PlacePrefabsOnChunkAsync(
                    chunkCoord.x, 
                    chunkCoord.y, 
                    chunkSize
                );
                
                // Mark as loaded
                loadedChunks.Add(chunkCoord);
                totalChunksLoaded++;
                
                // Track load time
                float loadTime = (Time.realtimeSinceStartup - startTime) * 1000f;
                UpdateLoadTimeStats(loadTime);
                
                if (logChunkOperations)
                {
                    Debug.Log($"Chunk {chunkCoord} loaded in {loadTime:F2}ms " +
                             $"(cached: {asyncPrefabPlacer.IsChunkCached(chunkCoord.x, chunkCoord.y)})");
                }
                
                // Fire complete event
                onChunkLoadComplete?.Invoke(chunkCoord);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to load chunk {chunkCoord}: {e.Message}");
            }
            finally
            {
                // Remove from loading set
                loadingChunks.Remove(chunkCoord);
            }
        }
        
        /// <summary>
        /// Get existing terrain instance or create a new one for the chunk.
        /// </summary>
        private Terrain GetOrCreateTerrainInstance(Vector2Int chunkCoord)
        {
            // Check if terrain instance already exists
            if (terrainInstances.TryGetValue(chunkCoord, out Terrain existingTerrain))
            {
                return existingTerrain;
            }
            
            // Create new terrain instance
            Terrain newTerrain = Instantiate(terrainPrefab, transform);
            
            // Position terrain at chunk location
            Vector3 chunkWorldPos = new Vector3(
                chunkCoord.x * chunkSize,
                0,
                chunkCoord.y * chunkSize
            );
            newTerrain.transform.position = chunkWorldPos;
            newTerrain.name = $"Terrain_Chunk_{chunkCoord.x}_{chunkCoord.y}";
            
            // Get or create TerrainData from pool if available
            if (Goodgulf.TerrainUtils.TerrainDataPool.Instance != null)
            {
                TerrainData pooledData = Goodgulf.TerrainUtils.TerrainDataPool.Instance.Get();
                newTerrain.terrainData = pooledData;
                
                // Update terrain collider reference
                TerrainCollider collider = newTerrain.GetComponent<TerrainCollider>();
                if(collider != null)
                    collider.terrainData = pooledData;
            }
            else Debug.LogError("TerrainDataPool.Instance is null!");
            
            
            // Store the terrain instance
            terrainInstances[chunkCoord] = newTerrain;
            
            return newTerrain;
        }
        
        /// <summary>
        /// Unload chunks that are too far from the player.
        /// </summary>
        private void UnloadDistantChunks()
        {
            List<Vector2Int> chunksToUnload = new List<Vector2Int>();
            
            // Find chunks beyond unload radius
            foreach (var chunkCoord in loadedChunks)
            {
                float distance = Vector2.Distance(
                    new Vector2(chunkCoord.x, chunkCoord.y),
                    new Vector2(currentPlayerChunk.x, currentPlayerChunk.y)
                );
                
                if (distance > unloadRadius)
                {
                    chunksToUnload.Add(chunkCoord);
                }
            }
            
            // Unload chunks
            foreach (var chunkCoord in chunksToUnload)
            {
                UnloadChunk(chunkCoord);
            }
        }
        
        /// <summary>
        /// Unload a single chunk.
        /// </summary>
        private void UnloadChunk(Vector2Int chunkCoord)
        {
            if (logChunkOperations)
            {
                Debug.Log($"Unloading chunk {chunkCoord}");
            }
            
            // Clear prefabs
            asyncPrefabPlacer.ClearChunk(chunkCoord);
            
            // Destroy terrain instance and release TerrainData to pool
            if (terrainInstances.TryGetValue(chunkCoord, out Terrain terrainInstance))
            {
                if (terrainInstance != null)
                {
                    // Release TerrainData back to pool if pool exists
                    if (Goodgulf.TerrainUtils.TerrainDataPool.Instance != null && terrainInstance.terrainData != null)
                    {
                        Goodgulf.TerrainUtils.TerrainDataPool.Instance.Release(terrainInstance.terrainData);
                    }
                    
                    // Destroy the terrain GameObject
                    Destroy(terrainInstance.gameObject);
                }
                
                terrainInstances.Remove(chunkCoord);
            }
            
            loadedChunks.Remove(chunkCoord);
            totalChunksUnloaded++;
            
            // Fire unload event
            onChunkUnload?.Invoke(chunkCoord);
        }
        
        /// <summary>
        /// Update load time statistics.
        /// </summary>
        private void UpdateLoadTimeStats(float loadTime)
        {
            recentLoadTimes.Add(loadTime);
            
            // Keep only last 100 load times
            if (recentLoadTimes.Count > 100)
            {
                recentLoadTimes.RemoveAt(0);
            }
            
            // Calculate average
            averageLoadTime = recentLoadTimes.Average();
        }
        
        /// <summary>
        /// Get streaming statistics as a string.
        /// </summary>
        public string GetStreamingStats()
        {
            return $"Loaded: {loadedChunks.Count} | " +
                   $"Loading: {loadingChunks.Count} | " +
                   $"Pending: {pendingLoadRequests.Count} | " +
                   $"Total Loaded: {totalChunksLoaded} | " +
                   $"Avg Load Time: {averageLoadTime:F2}ms";
        }
        
        /// <summary>
        /// Force load a specific chunk (useful for preloading or debugging).
        /// </summary>
        public async Task ForceLoadChunk(int chunkX, int chunkZ)
        {
            Vector2Int chunkCoord = new Vector2Int(chunkX, chunkZ);
            
            if (!loadedChunks.Contains(chunkCoord) && !loadingChunks.Contains(chunkCoord))
            {
                await LoadChunk(chunkCoord);
            }
        }
        
        /// <summary>
        /// Force unload a specific chunk.
        /// </summary>
        public void ForceUnloadChunk(int chunkX, int chunkZ)
        {
            Vector2Int chunkCoord = new Vector2Int(chunkX, chunkZ);
            
            if (loadedChunks.Contains(chunkCoord))
            {
                UnloadChunk(chunkCoord);
            }
        }
        
        /// <summary>
        /// Unload all chunks.
        /// </summary>
        public void UnloadAllChunks()
        {
            List<Vector2Int> allChunks = new List<Vector2Int>(loadedChunks);
            
            foreach (var chunk in allChunks)
            {
                UnloadChunk(chunk);
            }
            
            pendingLoadRequests.Clear();
        }
        
        /// <summary>
        /// Check if a chunk is loaded.
        /// </summary>
        public bool IsChunkLoaded(int chunkX, int chunkZ)
        {
            return loadedChunks.Contains(new Vector2Int(chunkX, chunkZ));
        }
        
        /// <summary>
        /// Get list of currently loaded chunk coordinates.
        /// </summary>
        public List<Vector2Int> GetLoadedChunks()
        {
            return new List<Vector2Int>(loadedChunks);
        }
        
        private void OnDestroy()
        {
            // Clean up
            UnloadAllChunks();
        }
        
        private void OnDrawGizmos()
        {
            if (!showDebugGizmos || player == null) return;
            
            Vector3 playerPos = player.position;
            Vector2Int playerChunk = new Vector2Int(
                Mathf.FloorToInt(playerPos.x / chunkSize),
                Mathf.FloorToInt(playerPos.z / chunkSize)
            );
            
            // Draw load radius
            Gizmos.color = Color.green;
            DrawCircleGizmo(
                new Vector3(playerChunk.x * chunkSize, 0, playerChunk.y * chunkSize),
                loadRadius * chunkSize
            );
            
            // Draw unload radius
            Gizmos.color = Color.red;
            DrawCircleGizmo(
                new Vector3(playerChunk.x * chunkSize, 0, playerChunk.y * chunkSize),
                unloadRadius * chunkSize
            );
            
            // Draw loaded chunks
            if (Application.isPlaying)
            {
                foreach (var chunk in loadedChunks)
                {
                    Gizmos.color = new Color(0, 1, 0, 0.2f);
                    Vector3 chunkCenter = new Vector3(
                        chunk.x * chunkSize + chunkSize * 0.5f,
                        0,
                        chunk.y * chunkSize + chunkSize * 0.5f
                    );
                    Gizmos.DrawCube(chunkCenter, new Vector3(chunkSize * 0.9f, 1, chunkSize * 0.9f));
                }
                
                // Draw loading chunks in yellow
                foreach (var chunk in loadingChunks)
                {
                    Gizmos.color = new Color(1, 1, 0, 0.2f);
                    Vector3 chunkCenter = new Vector3(
                        chunk.x * chunkSize + chunkSize * 0.5f,
                        0,
                        chunk.y * chunkSize + chunkSize * 0.5f
                    );
                    Gizmos.DrawCube(chunkCenter, new Vector3(chunkSize * 0.9f, 2, chunkSize * 0.9f));
                }
            }
        }
        
        private void DrawCircleGizmo(Vector3 center, float radius)
        {
            int segments = 32;
            float angleStep = 360f / segments;
            
            for (int i = 0; i < segments; i++)
            {
                float angle1 = i * angleStep * Mathf.Deg2Rad;
                float angle2 = (i + 1) * angleStep * Mathf.Deg2Rad;
                
                Vector3 point1 = center + new Vector3(
                    Mathf.Cos(angle1) * radius,
                    0,
                    Mathf.Sin(angle1) * radius
                );
                
                Vector3 point2 = center + new Vector3(
                    Mathf.Cos(angle2) * radius,
                    0,
                    Mathf.Sin(angle2) * radius
                );
                
                Gizmos.DrawLine(point1, point2);
            }
        }
        
        private void OnGUI()
        {
            if (!Application.isPlaying) return;
            
            // Display streaming stats
            GUI.Box(new Rect(10, 220, 500, 80), "");
            GUI.Label(new Rect(20, 230, 480, 20), $"Streaming: {GetStreamingStats()}");
            GUI.Label(new Rect(20, 250, 480, 20), $"Async Cache: {asyncPrefabPlacer.GetAsyncCacheStats()}");
            GUI.Label(new Rect(20, 270, 480, 20), $"Player Chunk: {currentPlayerChunk}");
        }
        
        
        public GameObject TerrainParent()
        {
            return gameObject;
        }

        public int ViewDistance()
        {
            return loadRadius;
        }

        public Transform Player()
        {
            return player;
        }

        public Terrain TerrainPrefab()
        {
            return terrainPrefab;
        }
        
        public float ChunkSize()
        {
            return chunkSize;
        }

        public int UnloadRadius()
        {
            return unloadRadius;
        }        


    }    
}
