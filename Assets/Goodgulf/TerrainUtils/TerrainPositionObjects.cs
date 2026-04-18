using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Goodgulf.Controller;
using Goodgulf.Logging;

namespace Goodgulf.TerrainUtils
{


    public class TerrainPositionObjects : MonoBehaviour, IDebuggable
    {
        [Header("Debug")]
        [SerializeField] private bool _debugEnabled = false;
        // IDebuggable contract
        public bool DebugEnabled => _debugEnabled;


        [Header("Objects to be raised to terrain height")]
        public List<GameObject> objectsToBePlacedOnTerrain;
        
        [Header("Terrain Layer Mask")]
        public LayerMask layerMask;
        
        void Start()
        {
            // Invoke("RePosition", 0.2f);
        }

        public void RePosition()
        {
            Debug.Log("<color=yellow>TerrainPositionObjects.RePosition(): Invoke Coroutine</color>");
            
            StartCoroutine(SpawnObjectsWhenReady());
        }

        IEnumerator SpawnObjectsWhenReady()
        {
            
            // Wait for physics engine to update with colliders
            yield return new WaitForFixedUpdate();
            
            foreach (GameObject obj in objectsToBePlacedOnTerrain)
            {
#if TERRAIN_DEBUG                
                this.LogVerbose($"Placing object {obj.name} with position {obj.transform.position}");
#endif                
                Vector3 pos = obj.transform.position;
                pos.y += 5000.0f;

                RaycastHit hit;
                if (Physics.Raycast(pos, Vector3.down, out hit, Mathf.Infinity, layerMask, QueryTriggerInteraction.Ignore))
                {
#if TERRAIN_DEBUG                    
                    this.LogVerbose($"Place object {obj.name} at {hit.point}");
#endif
                    if (obj.TryGetComponent<ThirdPersonController>(out ThirdPersonController controller))
                    {
                        controller.TeleportCharacter(hit.point);
                    }
                    else obj.transform.position = hit.point;
                }
#if TERRAIN_DEBUG                
                else this.LogWarning($"Cannot place object {obj.name}");
#endif
            }

        }

    }


}
