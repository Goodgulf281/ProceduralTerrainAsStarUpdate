using System;
using System.Collections.Generic;
using UnityEngine;

namespace Goodgulf.Building
{
    /*
     * Attach this component to a Terrain to enable building on it.
     *
     * Responsibilities:
     * - Holds BuildingAreas used by the builder system to store constructed items
     * - Ensures a trigger collider exists that matches the terrain bounds
     * - Provides a reliable way to detect player movement across multiple terrains
     *
     * The collider is intentionally a trigger and mirrors the terrain size.
     */

    // Ensure a BoxCollider is always present on this GameObject
    [RequireComponent(typeof(BoxCollider))]
    public class BuildableTerrain : MonoBehaviour
    {
        // Reference to the Terrain this component belongs to
        // Serialized so it can be assigned or inspected in the Editor
        [SerializeField] 
        private Terrain _terrain;

        // Cached reference to the terrain collider (if needed elsewhere later)
        private Collider _terrainCollider;

        /// <summary>
        /// Public access to the Terrain.
        /// When set, also caches the terrain's collider reference.
        /// </summary>
        public Terrain terrain
        {
            get => _terrain;
            set
            {
                _terrain = value;
                _terrainCollider = _terrain != null 
                    ? _terrain.GetComponent<Collider>() 
                    : null;
            }
        }

        // Stores building areas indexed by grid coordinate
        // Vector2Int typically represents chunk or tile indices
        private Dictionary<Vector2Int, BuildingArea> buildingAreas;

        /// <summary>
        /// Read-only access to the building areas dictionary.
        /// </summary>
        public Dictionary<Vector2Int, BuildingArea> BuildingAreas
        {
            get => buildingAreas;
        }

        /// <summary>
        /// Unity lifecycle method.
        /// Initializes internal data structures.
        /// </summary>
        void Awake()
        {
            buildingAreas = new Dictionary<Vector2Int, BuildingArea>();
        }

        /// <summary>
        /// Draws debug gizmos in the Scene view.
        /// Useful for visually identifying the terrain origin.
        /// </summary>
        void OnDrawGizmos()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(transform.position, 5.0f);
        }

        /// <summary>
        /// Called when the component is first added or reset in the Editor.
        /// Automatically assigns the Terrain and updates the collider.
        /// </summary>
        void Reset()
        {
            terrain = GetComponent<Terrain>();
            UpdateCollider();
        }

        /// <summary>
        /// Called in the Editor whenever a serialized field changes.
        /// Ensures the collider always matches the terrain size.
        /// </summary>
        void OnValidate()
        {
            UpdateCollider();
        }

        /// <summary>
        /// Ensures the BoxCollider matches the Terrain dimensions.
        /// The collider is set as a trigger and centered correctly.
        /// </summary>
        void UpdateCollider()
        {
            // Auto-assign terrain if missing
            if (!terrain)
                terrain = GetComponent<Terrain>();

            // Abort if no terrain exists on this GameObject
            if (!terrain)
                return;

            // Fetch the required BoxCollider
            var collider = GetComponent<BoxCollider>();

            // Use trigger collider for overlap-based detection
            collider.isTrigger = true;

            // Match collider size to terrain dimensions
            Vector3 size = terrain.terrainData.size;
            collider.size = new Vector3(size.x, size.y, size.z);

            // Center collider so it aligns with terrain origin
            collider.center = size / 2f;
        }
    }
}
