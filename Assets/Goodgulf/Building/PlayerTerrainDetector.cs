using UnityEngine;

namespace Goodgulf.Building
{
    /// <summary>
    /// Detects which Terrain the player is currently standing on.
    /// 
    /// This component should be attached to a GameObject with a Trigger Collider
    /// (typically a player or a child trigger object). When the trigger enters a
    /// BuildableTerrain volume, the active terrain for the Builder system is updated.
    /// </summary>
    public class PlayerTerrainDetector : MonoBehaviour
    {
        /// <summary>
        /// The Terrain the player is currently inside.
        /// Read-only from outside to prevent accidental state changes.
        /// </summary>
        public Terrain CurrentTerrain { get; private set; }

        /// <summary>
        /// Unity callback invoked when this trigger collider enters another collider.
        /// Used here to detect when the player moves onto a new buildable terrain.
        /// </summary>
        /// <param name="other">The collider that was entered.</param>
        void OnTriggerEnter(Collider other)
        {
            // Optional safety check for editor-time execution.
            // Uncomment if this script should only run during Play Mode.
            // if (!Application.isPlaying)
            //     return;

            // Check whether the entered collider belongs to a BuildableTerrain zone
            if (other.TryGetComponent<BuildableTerrain>(out var zone))
            {
                // Only react if the player actually entered a different terrain
                if (zone.terrain != CurrentTerrain)
                {
                    // Update cached terrain reference
                    CurrentTerrain = zone.terrain;

                    // Notify systems that depend on the active terrain
                    ChangeTerrain(zone.terrain);
                }
            }
        }

        /// <summary>
        /// Handles logic that should occur when the player enters a new terrain.
        /// Currently updates the Builder system and logs the change for debugging.
        /// </summary>
        /// <param name="newTerrain">The newly entered terrain.</param>
        private void ChangeTerrain(Terrain newTerrain)
        {
            // Log terrain change with color formatting for easy identification in the console
            Debug.Log(
                $"PlayerTerrainDetector.OnTerrainChanged(): " +
                $"<color=green>Entered terrain: {newTerrain.name}</color>"
            );

            Builder.Instance.CacheTerrains();
            
            // Inform the Builder singleton which terrain is now active
            Builder.Instance.ActiveTerrain = newTerrain;
        }
    }
}
