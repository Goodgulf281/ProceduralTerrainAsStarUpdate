using System;
using UnityEngine;

namespace Goodgulf.Building
{
    /// <summary>
    /// Shows the outline of a BuildingArea on a terrain.
    /// Useful for debugging physics interactions and placement of building blocks.
    /// </summary>


    // Ensures that a LineRenderer component is always present on this GameObject
    [RequireComponent(typeof(LineRenderer))]
    public class BuildingAreaOutline : MonoBehaviour
    {
        [Header("Performance")]
        [SerializeField] private float _updateInterval = 2f;
        private float _updateTimer = 0f;

        
        [Header("Layer")]
        [SerializeField] private LayerMask _layerMask;
        
        private Terrain _terrain;
        private BuildingArea _buildingArea;

        public Terrain Terrain
        {
            get => _terrain;
            set => _terrain = value;
        }

        public BuildingArea BuildingArea
        {
            get => _buildingArea;
            set => _buildingArea = value;
        }

        // Size of the square building area (world units)
        public float areaSize = 100f;

        // Number of sampled points per edge of the square
        // Higher values = smoother outline but more expensive
        public int samplesPerEdge = 25;

        // Small vertical offset so the line doesn't clip into the terrain
        public float yOffset = 0.1f;

        // Line color when placement is valid
        public Color validColor = Color.green;

        // Line color when placement is invalid
        public Color invalidColor = Color.red;

        private bool _isDirty = true;
        // Raycast height above terrain to ensure we fire downward from above any geometry
        private const float RaycastOriginHeight = 500f;
        private const float RaycastDistance = 1000f;
        
        // Cached reference to the LineRenderer
        private LineRenderer _line;

        void Awake()
        {
            // Get the LineRenderer attached to this GameObject
            if (!TryGetComponent<LineRenderer>(out LineRenderer lineRenderer))
            {
                throw new Exception("BuildingAreaOutline.Awake(): No line renderer found");
            }
            _line = lineRenderer;
            
            // Close the line into a loop (square outline)
            _line.loop = true;

            // Use world-space coordinates so the outline follows terrain properly
            _line.useWorldSpace = true;

            // Total number of points needed for all four edges
            _line.positionCount = samplesPerEdge * 4;
            
            SetValid(true);
        }

        void Update()
        {
            if (!_isDirty)
                return;

            UpdateOutline();
            _isDirty = false;
        }

        public void SetDirty()
        {
            _isDirty = true;
        }

        /// <summary>
        /// Samples the height at a world XZ position by raycasting downward onto the terrain layer.
        /// Returns the hit point Y, or 0 if no hit is found.
        /// </summary>
        private bool TryGetHeightAtPoint(Vector3 xzPoint, out float worldY)
        {
            Vector3 rayOrigin = new Vector3(xzPoint.x, RaycastOriginHeight, xzPoint.z);
            Ray ray = new Ray(rayOrigin, Vector3.down);

            if (Physics.Raycast(ray, out RaycastHit hit, RaycastDistance, _layerMask))
            {
                worldY = hit.point.y + yOffset;
                return true;
            }

            worldY = 0f;
            return false;
        }

        private void UpdateOutline()
        {
            if (_terrain == null || _buildingArea == null)
                throw new Exception("BuildingAreaOutline.UpdateOutline(): _buildingArea == null or _terrain == null");

            Vector3 terrainPos = _terrain.transform.position;
            Vector3 terrainSize = _terrain.terrainData.size;
            Vector3 center = _buildingArea.Position;

            float maxLeft   = center.x - terrainPos.x;
            float maxRight  = (terrainPos.x + terrainSize.x) - center.x;
            float maxBottom = center.z - terrainPos.z;
            float maxTop    = (terrainPos.z + terrainSize.z) - center.z;

            float halfX = Mathf.Min(areaSize * 0.5f, maxLeft, maxRight);
            float halfZ = Mathf.Min(areaSize * 0.5f, maxBottom, maxTop);

            int index = 0;

            // ---- Bottom edge (left to right) ----
            for (int i = 0; i < samplesPerEdge; i++)
            {
                float t = i / (float)(samplesPerEdge - 1);
                Vector3 p = center + new Vector3(Mathf.Lerp(-halfX, halfX, t), 0, -halfZ);
                p.y = TryGetHeightAtPoint(p, out float y) ? y : _terrain.SampleHeight(p) + terrainPos.y + yOffset;
                _line.SetPosition(index++, p);
            }

            // ---- Right edge (bottom to top) ----
            for (int i = 0; i < samplesPerEdge; i++)
            {
                float t = i / (float)(samplesPerEdge - 1);
                Vector3 p = center + new Vector3(halfX, 0, Mathf.Lerp(-halfZ, halfZ, t));
                p.y = TryGetHeightAtPoint(p, out float y) ? y : _terrain.SampleHeight(p) + terrainPos.y + yOffset;
                _line.SetPosition(index++, p);
            }

            // ---- Top edge (right to left) ----
            for (int i = 0; i < samplesPerEdge; i++)
            {
                float t = i / (float)(samplesPerEdge - 1);
                Vector3 p = center + new Vector3(Mathf.Lerp(halfX, -halfX, t), 0, halfZ);
                p.y = TryGetHeightAtPoint(p, out float y) ? y : _terrain.SampleHeight(p) + terrainPos.y + yOffset;
                _line.SetPosition(index++, p);
            }

            // ---- Left edge (top to bottom) ----
            for (int i = 0; i < samplesPerEdge; i++)
            {
                float t = i / (float)(samplesPerEdge - 1);
                Vector3 p = center + new Vector3(-halfX, 0, Mathf.Lerp(halfZ, -halfZ, t));
                p.y = TryGetHeightAtPoint(p, out float y) ? y : _terrain.SampleHeight(p) + terrainPos.y + yOffset;
                _line.SetPosition(index++, p);
            }
        }
        
        // Sets the outline color based on whether the area is valid for building
        public void SetValid(bool valid)
        {
            // Apply the same color to both ends of the line
            _line.startColor = valid ? validColor : invalidColor;
            _line.endColor   = valid ? validColor : invalidColor;
            
            _line.material.color = valid ? validColor : invalidColor;
        }
    }
}
