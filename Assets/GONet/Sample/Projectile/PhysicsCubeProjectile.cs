/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 */

using GONet;
using UnityEngine;

namespace GONet.Sample
{
    [RequireComponent(typeof(GONetParticipant))]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class PhysicsCubeProjectile : MonoBehaviour, IGONetPoolResettable
    {
        [SerializeField]
        private float launchSpeed = 12f;

        private Rigidbody rb;
        private GONetParticipant gonetParticipant;

        private void Awake()
        {
            gonetParticipant = GetComponent<GONetParticipant>();
            rb = GetComponent<Rigidbody>();
        }

        private void Start()
        {
            if (gonetParticipant != null && !gonetParticipant.IsPooled)
            {
                Launch();
            }
        }

        public void ResetForPoolBorrow()
        {
            Launch();
        }

        public void ResetForPoolReturn()
        {
            if (rb == null)
            {
                return;
            }

            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.Sleep();
        }

        private void Launch()
        {
            if (rb == null || !GONetMain.IsServer)
            {
                return;
            }

            rb.isKinematic = false;
            rb.useGravity = true;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.AddForce(transform.forward * launchSpeed, ForceMode.VelocityChange);
        }
    }
}
