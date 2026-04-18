using UnityEngine;
using UnityEngine.InputSystem;

namespace Goodgulf.Input
{

    public class InputHandler : MonoBehaviour
    {
        [SerializeField]
        private GameObject debugPrefab;

        [SerializeField]
        private float range;
        
        private Camera _mainCamera;

        private void Awake()
        {
            _mainCamera = Camera.main;
        }

        public void OnClick(InputAction.CallbackContext context)
        {
            if (!context.started) return;
            
            Vector3 screenPosition = Mouse.current.position.ReadValue();
            // screenPosition.z = _mainCamera.nearClipPlane;
            
            Vector3 worldPosition = _mainCamera.ScreenToWorldPoint(screenPosition);

            RaycastHit hitData;
            
            Ray ray = _mainCamera.ScreenPointToRay(screenPosition);
            
            if(Physics.Raycast(ray, out hitData, range))
            {
                // https://gamedevbeginner.com/how-to-convert-the-mouse-position-to-world-space-in-unity-2d-3d/
                
                if (hitData.collider.gameObject.layer == LayerMask.NameToLayer("Player"))
                    return;
                
                worldPosition = hitData.point;
                
                Instantiate(debugPrefab, worldPosition, Quaternion.identity);
            }
            
            // Debug.Log(screenPosition);
        }

        public void OnSelect(InputAction.CallbackContext context)
        {
            if(!context.performed) return;
            
            //Debug.Log("key pressed "+context.ReadValue<float>());
            Debug.Log("key pressed "+context.control.name);
        }
        
        
    }


}