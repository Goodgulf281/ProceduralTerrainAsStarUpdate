using UnityEngine;
using System.Collections.Generic;

namespace Goodgulf.TerrainUtils
{
    [CreateAssetMenu(fileName = "PrefabPlacementConfig", menuName = "Terrain/Prefab Placement Config")]
    public class PrefabPlacementConfig : ScriptableObject
    {
        [System.Serializable]
        public class PrefabPlacementRule
        {
            [Header("Prefab Settings")]
            public GameObject prefab;
            
            [Header("Placement Density")]
            [Tooltip("Number of placement attempts per square unit")]
            [Range(0.001f, 10f)]
            public float density = 0.1f;
            
            [Header("Rotation Settings")]
            public bool randomRotationX = false;
            public bool randomRotationY = true;
            public bool randomRotationZ = false;
            
            [Tooltip("If axis rotation is disabled, use this fixed rotation value (in degrees)")]
            public Vector3 fixedRotation = Vector3.zero;
            
            [Header("Scale Settings")]
            public bool randomScale = false;
            [Range(0.1f, 5f)]
            public float minScale = 0.8f;
            [Range(0.1f, 5f)]
            public float maxScale = 1.2f;
            
            [Header("Terrain Constraints")]
            [Tooltip("Minimum terrain slope (in degrees) where this prefab can spawn")]
            [Range(0f, 90f)]
            public float minSlope = 0f;
            
            [Tooltip("Maximum terrain slope (in degrees) where this prefab can spawn")]
            [Range(0f, 90f)]
            public float maxSlope = 45f;
            
            [Tooltip("Minimum height (world Y position) where this prefab can spawn")]
            public float minHeight = float.MinValue;
            
            [Tooltip("Maximum height (world Y position) where this prefab can spawn")]
            public float maxHeight = float.MaxValue;
            
            [Header("Spacing")]
            [Tooltip("Minimum distance between instances of this prefab")]
            public float minSpacing = 1f;
            
            [Header("Alignment")]
            [Tooltip("Align the prefab to terrain normal")]
            public bool alignToTerrainNormal = false;
            
            [Tooltip("Offset from terrain surface")]
            public float surfaceOffset = 0f;
            
            [Header("Terrain Modification")]
            [Tooltip("Modify terrain height around this prefab (useful for buildings)")]
            public bool modifyTerrain = false;
            
            [Tooltip("Radius around prefab to modify terrain")]
            [Range(0.1f, 50f)]
            public float terrainModificationRadius = 5f;
            
            [Tooltip("How to modify the terrain height")]
            public TerrainModificationType modificationType = TerrainModificationType.FlattenToSpawnPoint;
            
            [Tooltip("Smoothness of terrain modification transition (0 = sharp edge, 1 = very smooth)")]
            [Range(0f, 1f)]
            public float modificationSmoothness = 0.5f;
            
            [Tooltip("Additional height offset to add when raising terrain")]
            public float terrainHeightOffset = 0f;
        }
        
        public enum TerrainModificationType
        {
            FlattenToSpawnPoint,    // Flatten terrain to the height where prefab spawned
            RaiseToSpawnPoint,      // Only raise terrain up to spawn point (don't lower)
            LowerToSpawnPoint,      // Only lower terrain down to spawn point (don't raise)
            RaiseByOffset,          // Raise terrain by terrainHeightOffset amount
            FlattenToOffset         // Flatten to spawn point + terrainHeightOffset
        }
        
        [Header("Placement Rules")]
        public List<PrefabPlacementRule> placementRules = new List<PrefabPlacementRule>();
        
        [Header("Global Settings")]
        [Tooltip("Use this to offset the placement seed from the terrain generation seed")]
        public int seedOffset = 1000;
        
        [Tooltip("Maximum placement attempts per rule to avoid infinite loops")]
        public int maxPlacementAttempts = 1000;
        
        [Header("Terrain Modification Safety")]
        [Tooltip("Prevent terrain modification within this distance from chunk edges (in meters). Prevents seams between chunks.")]
        [Range(0f, 50f)]
        public float edgeBufferDistance = 5f;
        
        [Tooltip("If true, prefabs that would modify terrain near edges will not be placed at all. If false, they'll be placed but terrain modification will be skipped.")]
        public bool preventPlacementNearEdges = true;
        
        [Header("Performance & Caching")]
        [Tooltip("Enable persistent caching of placed prefabs. Chunks are only generated once and cached to disk.")]
        public bool enablePersistentCache = true;
        
        [Tooltip("If true, cache will be invalidated when this config is modified (recommended).")]
        public bool invalidateCacheOnConfigChange = true;
        
        /// <summary>
        /// Calculate a hash of this configuration for cache validation.
        /// </summary>
        public int GetConfigHash()
        {
            int hash = seedOffset;
            hash = hash * 31 + maxPlacementAttempts;
            hash = hash * 31 + edgeBufferDistance.GetHashCode();
            hash = hash * 31 + (preventPlacementNearEdges ? 1 : 0);
            
            foreach (var rule in placementRules)
            {
                if (rule.prefab != null)
                {
                    hash = hash * 31 + rule.prefab.name.GetHashCode();
                    hash = hash * 31 + rule.density.GetHashCode();
                    hash = hash * 31 + rule.terrainModificationRadius.GetHashCode();
                    hash = hash * 31 + (int)rule.modificationType;
                }
            }
            
            return hash;
        }
    }
}
