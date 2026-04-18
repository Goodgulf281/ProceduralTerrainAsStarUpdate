using UnityEngine;

namespace Goodgulf.Building
{
    public class PhysicsContactEvent : MonoBehaviour
    {
        int _index;

        private void OnEnable()
        {
            Debug.Log("Enabling Contact Event");
            
            Physics.ContactEvent += Physics_ContactEvent;
        }

        void OnDisable()
        {
            Debug.Log("Disabling Contact Event");
            
            Physics.ContactEvent -= Physics_ContactEvent;
        }

        void Physics_ContactEvent(PhysicsScene scene,
            Unity.Collections.NativeArray<ContactPairHeader>.ReadOnly contactPairHeader)
        {
            // Debug.Log("Contact Event");
            
            _index = contactPairHeader.Length - 1;
            for (int j = 0; j < contactPairHeader[_index].pairCount; j++)
            {
                ref readonly ContactPair pair = ref contactPairHeader[_index].GetContactPair(j);

                if (pair.isCollisionEnter) IsCollisionEnter(pair);
                if (pair.isCollisionStay) IsCollisionStay(pair);
                if (pair.isCollisionExit) IsCollisionExit(pair);
            }
        }

        static void IsCollisionEnter(ContactPair pair)
        {
            print("IsCollisionEnter: " + pair.GetContactPoint(0).normal.ToString());

            Debug.Log("PhysicsContactEvent.IsCollisionEnter(): "+pair.collider.gameObject.name+ " and "+pair.otherCollider.gameObject.name);

        }

        static void IsCollisionStay(ContactPair pair)
        {
            // print("IsCollisionStay");
        }

        static void IsCollisionExit(ContactPair pair)
        {
            print("IsCollisionExit");
            
            Debug.Log("Exit "+pair.collider.gameObject.name+ " and "+pair.otherCollider.gameObject.name);

        }
    }
}