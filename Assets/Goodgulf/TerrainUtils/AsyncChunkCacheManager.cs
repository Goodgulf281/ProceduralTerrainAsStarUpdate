using UnityEngine;
using System.Collections.Generic;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Goodgulf.TerrainUtils
{
    /// <summary>
    /// Async cache manager that performs file I/O on background threads.
    /// Significantly improves performance by not blocking the main thread.
    /// </summary>
    public static class AsyncChunkCacheManager
    {
        private static readonly string CACHE_FOLDER_NAME = "TerrainPrefabCache";
        private static string _cachePath;
        
        // Thread-safe queue for save operations
        private static readonly Queue<ChunkPrefabCache> _saveQueue = new Queue<ChunkPrefabCache>();
        private static readonly object _saveQueueLock = new object();
        
        // Background save thread
        private static Thread _saveThread;
        private static bool _saveThreadRunning = false;
        private static readonly AutoResetEvent _saveSignal = new AutoResetEvent(false);
        
        // Statistics
        private static int _pendingSaves = 0;
        private static int _totalSavesCompleted = 0;
        private static int _totalLoadsCompleted = 0;
        
        /// <summary>
        /// Initialize the async cache system (call once at startup).
        /// </summary>
        public static void Initialize()
        {
            if (_saveThreadRunning)
            {
                return;
            }
            
            _saveThreadRunning = true;
            _saveThread = new Thread(SaveWorker)
            {
                Name = "ChunkCacheSaveWorker",
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            _saveThread.Start();
            
            Debug.Log("AsyncChunkCacheManager initialized");
        }
        
        /// <summary>
        /// Shutdown the async cache system (call on application quit).
        /// </summary>
        public static void Shutdown()
        {
            if (!_saveThreadRunning)
            {
                return;
            }
            
            _saveThreadRunning = false;
            _saveSignal.Set(); // Wake up thread to exit
            
            if (_saveThread != null && _saveThread.IsAlive)
            {
                _saveThread.Join(1000); // Wait up to 1 second
            }
            
            Debug.Log($"AsyncChunkCacheManager shutdown. Completed {_totalSavesCompleted} saves, {_totalLoadsCompleted} loads");
        }
        
        /// <summary>
        /// Background worker thread for saving caches.
        /// </summary>
        private static void SaveWorker()
        {
            while (_saveThreadRunning)
            {
                ChunkPrefabCache cacheToSave = null;
                
                // Get next cache from queue
                lock (_saveQueueLock)
                {
                    if (_saveQueue.Count > 0)
                    {
                        cacheToSave = _saveQueue.Dequeue();
                        _pendingSaves = _saveQueue.Count;
                    }
                }
                
                // Save the cache (outside lock)
                if (cacheToSave != null)
                {
                    try
                    {
                        SaveCacheSync(cacheToSave);
                        Interlocked.Increment(ref _totalSavesCompleted);
                    }
                    catch (Exception e)
                    {
                        // Log on main thread
                        UnityMainThreadDispatcher.Enqueue(() =>
                        {
                            Debug.LogError($"Failed to save chunk cache ({cacheToSave.chunkX}, {cacheToSave.chunkZ}): {e.Message}");
                        });
                    }
                }
                else
                {
                    // No work, wait for signal
                    _saveSignal.WaitOne(100); // Timeout prevents hanging on shutdown
                }
            }
        }
        
        /// <summary>
        /// Get the cache directory path, creating it if needed.
        /// </summary>
        public static string GetCachePath()
        {
            if (string.IsNullOrEmpty(_cachePath))
            {
                _cachePath = System.IO.Path.Combine(
                    Application.persistentDataPath,
                    CACHE_FOLDER_NAME
                );
                
                if (!System.IO.Directory.Exists(_cachePath))
                {
                    System.IO.Directory.CreateDirectory(_cachePath);
                }
            }
            
            return _cachePath;
        }
        
        /// <summary>
        /// Generate cache file name for a chunk.
        /// </summary>
        public static string GetCacheFileName(int chunkX, int chunkZ)
        {
            return $"chunk_{chunkX}_{chunkZ}.dat";
        }
        
        /// <summary>
        /// Get full path for a chunk's cache file.
        /// </summary>
        public static string GetCacheFilePath(int chunkX, int chunkZ)
        {
            return System.IO.Path.Combine(GetCachePath(), GetCacheFileName(chunkX, chunkZ));
        }
        
        /// <summary>
        /// Queue a cache to be saved asynchronously on a background thread.
        /// Non-blocking, returns immediately.
        /// </summary>
        public static void SaveCacheAsync(ChunkPrefabCache cache)
        {
            if (!_saveThreadRunning)
            {
                Initialize();
            }
            
            lock (_saveQueueLock)
            {
                _saveQueue.Enqueue(cache);
                _pendingSaves = _saveQueue.Count;
            }
            
            _saveSignal.Set(); // Wake up save thread
        }
        
        /// <summary>
        /// Synchronous save (used internally by background thread).
        /// </summary>
        private static void SaveCacheSync(ChunkPrefabCache cache)
        {
            string filePath = GetCacheFilePath(cache.chunkX, cache.chunkZ);
            string json = JsonUtility.ToJson(cache, false);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            byte[] compressed = CompressBytes(bytes);
            System.IO.File.WriteAllBytes(filePath, compressed);
        }
        
        /// <summary>
        /// Load cache asynchronously using Task-based approach.
        /// Returns a Task that can be awaited.
        /// </summary>
        public static async Task<ChunkPrefabCache> LoadCacheAsync(int chunkX, int chunkZ)
        {
            string filePath = GetCacheFilePath(chunkX, chunkZ);
            
            if (!System.IO.File.Exists(filePath))
            {
                return null;
            }
            
            // Run file I/O on thread pool
            return await Task.Run(() =>
            {
                try
                {
                    byte[] compressed = System.IO.File.ReadAllBytes(filePath);
                    byte[] bytes = DecompressBytes(compressed);
                    string json = System.Text.Encoding.UTF8.GetString(bytes);
                    
                    // JSON parsing must happen on main thread for Unity
                    ChunkPrefabCache cache = null;
                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        cache = JsonUtility.FromJson<ChunkPrefabCache>(json);
                    });
                    
                    // Wait for main thread to complete
                    while (cache == null)
                    {
                        Thread.Sleep(1);
                    }
                    
                    Interlocked.Increment(ref _totalLoadsCompleted);
                    return cache;
                }
                catch (Exception e)
                {
                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        Debug.LogWarning($"Failed to load chunk cache ({chunkX}, {chunkZ}): {e.Message}");
                    });
                    return null;
                }
            });
        }
        
        /// <summary>
        /// Synchronous load (for compatibility with existing code).
        /// </summary>
        public static ChunkPrefabCache LoadCacheSync(int chunkX, int chunkZ)
        {
            try
            {
                string filePath = GetCacheFilePath(chunkX, chunkZ);
                
                if (!System.IO.File.Exists(filePath))
                {
                    return null;
                }
                
                byte[] compressed = System.IO.File.ReadAllBytes(filePath);
                byte[] bytes = DecompressBytes(compressed);
                string json = System.Text.Encoding.UTF8.GetString(bytes);
                
                ChunkPrefabCache cache = JsonUtility.FromJson<ChunkPrefabCache>(json);
                
                Interlocked.Increment(ref _totalLoadsCompleted);
                return cache;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to load chunk cache ({chunkX}, {chunkZ}): {e.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Check if cache exists for a chunk (fast, just checks file system).
        /// </summary>
        public static bool HasCache(int chunkX, int chunkZ)
        {
            return System.IO.File.Exists(GetCacheFilePath(chunkX, chunkZ));
        }
        
        /// <summary>
        /// Delete cache file for a chunk asynchronously.
        /// </summary>
        public static async Task<bool> DeleteCacheAsync(int chunkX, int chunkZ)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string filePath = GetCacheFilePath(chunkX, chunkZ);
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                    return true;
                }
                catch (Exception e)
                {
                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        Debug.LogError($"Failed to delete chunk cache ({chunkX}, {chunkZ}): {e.Message}");
                    });
                    return false;
                }
            });
        }
        
        /// <summary>
        /// Clear all cached data asynchronously.
        /// </summary>
        public static async Task ClearAllCachesAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    string cachePath = GetCachePath();
                    if (System.IO.Directory.Exists(cachePath))
                    {
                        System.IO.Directory.Delete(cachePath, true);
                        System.IO.Directory.CreateDirectory(cachePath);
                    }
                }
                catch (Exception e)
                {
                    UnityMainThreadDispatcher.Enqueue(() =>
                    {
                        Debug.LogError($"Failed to clear all caches: {e.Message}");
                    });
                }
            });
        }
        
        /// <summary>
        /// Get total cache size in bytes asynchronously.
        /// </summary>
        public static async Task<long> GetTotalCacheSizeAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    string cachePath = GetCachePath();
                    if (!System.IO.Directory.Exists(cachePath))
                    {
                        return 0L;
                    }
                    
                    long totalSize = 0;
                    string[] files = System.IO.Directory.GetFiles(cachePath);
                    
                    foreach (string file in files)
                    {
                        System.IO.FileInfo fileInfo = new System.IO.FileInfo(file);
                        totalSize += fileInfo.Length;
                    }
                    
                    return totalSize;
                }
                catch
                {
                    return 0L;
                }
            });
        }
        
        /// <summary>
        /// Get statistics about async operations.
        /// </summary>
        public static string GetAsyncStats()
        {
            return $"Pending Saves: {_pendingSaves} | Total Saves: {_totalSavesCompleted} | Total Loads: {_totalLoadsCompleted}";
        }
        
        /// <summary>
        /// Wait for all pending save operations to complete.
        /// Useful before exiting the application.
        /// </summary>
        public static async Task WaitForPendingSaves(int timeoutMs = 5000)
        {
            int elapsed = 0;
            while (_pendingSaves > 0 && elapsed < timeoutMs)
            {
                await Task.Delay(100);
                elapsed += 100;
            }
            
            if (_pendingSaves > 0)
            {
                Debug.LogWarning($"Timeout waiting for pending saves. {_pendingSaves} saves may be lost.");
            }
        }
        
        /// <summary>
        /// Compress byte array using GZip.
        /// </summary>
        private static byte[] CompressBytes(byte[] data)
        {
            using (var compressedStream = new System.IO.MemoryStream())
            using (var zipStream = new System.IO.Compression.GZipStream(
                compressedStream, 
                System.IO.Compression.CompressionMode.Compress))
            {
                zipStream.Write(data, 0, data.Length);
                zipStream.Close();
                return compressedStream.ToArray();
            }
        }
        
        /// <summary>
        /// Decompress byte array using GZip.
        /// </summary>
        private static byte[] DecompressBytes(byte[] data)
        {
            using (var compressedStream = new System.IO.MemoryStream(data))
            using (var zipStream = new System.IO.Compression.GZipStream(
                compressedStream, 
                System.IO.Compression.CompressionMode.Decompress))
            using (var resultStream = new System.IO.MemoryStream())
            {
                zipStream.CopyTo(resultStream);
                return resultStream.ToArray();
            }
        }
    }
}
