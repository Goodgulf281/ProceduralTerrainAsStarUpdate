using UnityEngine;

/// <summary>
/// Marks an area where building is not allowed.
/// Visualizes the non-buildable range in the Scene view using Gizmos.
/// </summary>
public class NonBuildableArea : MonoBehaviour
{
    /// <summary>
    /// Radius of the non-buildable area, measured from this GameObject's position.
    /// </summary>
    public float Range;

    /// <summary>
    /// When enabled, the non-buildable area will be visualized
    /// as a red sphere in the Scene view.
    /// </summary>
    [SerializeField] 
    private bool _showArea = true;

    /// <summary>
    /// Called by Unity in the editor to allow drawing Gizmos.
    /// This method does not run in builds and is only for visualization.
    /// </summary>
    void OnDrawGizmos()
    {
        // Only draw the gizmo when visualization is enabled
        if (_showArea)
        {
            // Set gizmo color to red to indicate a restricted area
            Gizmos.color = Color.red;

            // Draw a sphere representing the non-buildable range
            Gizmos.DrawSphere(transform.position, Range);
        }
    }
}
