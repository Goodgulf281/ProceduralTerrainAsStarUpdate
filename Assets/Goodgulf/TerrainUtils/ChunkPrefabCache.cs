using UnityEngine;
using System.Collections.Generic;
using System;

namespace Goodgulf.TerrainUtils
{
    /// <summary>
    /// Serializable cache data for a single chunk's placed prefabs.
    /// Designed for efficient serialization and deserialization.
    /// </summary>
    [Serializable]
    public class ChunkPrefabCache
    {
        [Serializable]
        public struct PlacedPrefabData
        {
            public int prefabId;           // Hash of prefab name for lookup
            public Vector3 position;       // World position
            public Quaternion rotation;    // Rotation
            public Vector3 scale;          // Scale
            
            // Optional: Store if terrain was modified (for debugging/validation)
            public bool terrainModified;
        }
        
        // Chunk identification
        public int chunkX;
        public int chunkZ;
        
        // Cache metadata
        public int seed;                   // Seed used for generation
        public int configHash;             // Hash of the config used
        public long timestamp;             // When cache was created (for versioning)
        
        // Placed prefab data
        public List<PlacedPrefabData> placedPrefabs = new List<PlacedPrefabData>();
        
        // Version for cache format changes
        public int cacheVersion = 1;
        
        /// <summary>
        /// Check if this cache is valid for the given seed and config.
        /// </summary>
        public bool IsValid(int currentSeed, int currentConfigHash)
        {
            return seed == currentSeed && configHash == currentConfigHash;
        }
    }
    
    /// <summary>
    /// Manages persistent caching of chunk prefab data using binary serialization.
    /// </summary>
    public static class ChunkCacheManager
    {
        private static readonly string CACHE_FOLDER_NAME = "TerrainPrefabCache";
        private static string _cachePath;
        
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
        /// Save chunk cache to disk using binary serialization.
        /// </summary>
        public static bool SaveCache(ChunkPrefabCache cache)
        {
            try
            {
                string filePath = GetCacheFilePath(cache.chunkX, cache.chunkZ);
                string json = JsonUtility.ToJson(cache, false);
                
                // Use binary serialization for smaller file size
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                
                // Optional: Compress the data
                byte[] compressed = CompressBytes(bytes);
                
                System.IO.File.WriteAllBytes(filePath, compressed);
                
                Debug.Log($"Saved Chunk Prefab Cache to {filePath} with size {compressed.Length}");
                
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save chunk cache ({cache.chunkX}, {cache.chunkZ}): {e.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Load chunk cache from disk.
        /// </summary>
        public static ChunkPrefabCache LoadCache(int chunkX, int chunkZ)
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
                
                return cache;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to load chunk cache ({chunkX}, {chunkZ}): {e.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Check if cache exists for a chunk.
        /// </summary>
        public static bool HasCache(int chunkX, int chunkZ)
        {
            return System.IO.File.Exists(GetCacheFilePath(chunkX, chunkZ));
        }
        
        /// <summary>
        /// Delete cache file for a chunk.
        /// </summary>
        public static bool DeleteCache(int chunkX, int chunkZ)
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
                Debug.LogError($"Failed to delete chunk cache ({chunkX}, {chunkZ}): {e.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Clear all cached data.
        /// </summary>
        public static void ClearAllCaches()
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
                Debug.LogError($"Failed to clear all caches: {e.Message}");
            }
        }
        
        /// <summary>
        /// Get total cache size in bytes.
        /// </summary>
        public static long GetTotalCacheSize()
        {
            try
            {
                string cachePath = GetCachePath();
                if (!System.IO.Directory.Exists(cachePath))
                {
                    return 0;
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
                return 0;
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
