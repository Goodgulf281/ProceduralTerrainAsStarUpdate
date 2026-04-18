using System.Collections.Generic;
using UnityEngine;

namespace Goodgulf.Building
{
    // Allows this ScriptableObject to be created from the Unity Asset menu
    // under: Builder / Build Template
    [CreateAssetMenu(menuName = "Builder/Build Template")]
    public class BuildTemplate : ScriptableObject
    {
        // Unique identifier for this build template
        public int id;

        // Vertical offset applied when placing the build object
        // Useful for correcting pivot or ground alignment
        public float buildYOffset;

        // Radius used for placement checks (e.g., collision or proximity validation)
        public float radius;

        // Strength value, used for combined durability/distace to ground calculations
        public float strength;

        // Physical dimensions of the build object
        public float width;
        public float height;
        public float depth;

        // Local-space snap points used to align this object with others
        public List<Vector3> snapPoints;

        // Prefab used for the actual placed build object
        public GameObject buildPrefab;

        // Transparent or preview prefab shown during placement
        public GameObject transparentPrefab;
    }
}
