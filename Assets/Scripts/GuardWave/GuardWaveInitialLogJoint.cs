using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// A scene-authored Initial Wall log starts each attempt at 80 N, then
    /// drops to 35 N after the first creek impact has settled. Mud drying is
    /// the same x2 strength multiplier used for ordinary mud bonds.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FixedJoint))]
    [DefaultExecutionOrder(150)]
    public sealed class GuardWaveInitialLogJoint : MonoBehaviour
    {
        [Header("FixedJoint break force")]
        [SerializeField, Min(0f)] private float initialBreakForce = 80f;
        [SerializeField, Min(0f)] private float settledBreakForce = 35f;

        [Header("First creek impact")]
        [SerializeField, Min(0f)] private float firstContactForce = 1f;
        [SerializeField, Min(0f)] private float minimumProtectionSeconds = 0.8f;
        [Tooltip("Continuous low-load time needed to consider the first impact settled.")]
        [SerializeField, Min(0f)] private float settledDuration = 0.2f;
        [Tooltip("Guarantee the initial 80 N protection ends even when ambient joint load never falls below 35 N.")]
        [SerializeField, Min(0f)] private float maximumProtectionSeconds = 2f;

        private FixedJoint _joint;
        private float _firstContactTime = -1f;
        private float _stableTime;
        private bool _settled;
        private bool _isDry;

        private void OnEnable()
        {
            // Scene load and reactivation; a rollback instead calls Restart
            // with its newly created FixedJoint explicitly.
            Restart(GetComponent<FixedJoint>(), false);
        }

        /// <summary>
        /// Rebind after an Undo/failure/reset, regardless of the previously
        /// captured joint force or whether the old joint has been destroyed.
        /// </summary>
        internal void Restart(FixedJoint joint, bool isDry)
        {
            _joint = joint;
            _firstContactTime = -1f;
            _stableTime = 0f;
            _settled = false;
            _isDry = isDry;
            ApplyBreakForce();
        }

        /// <summary>Only the normal mud curing multiplier changes.</summary>
        internal void SetDryness(bool dry)
        {
            _isDry = dry;
            ApplyBreakForce();
        }

        private void ApplyBreakForce()
        {
            if (_joint != null)
                _joint.breakForce = (_settled ? settledBreakForce : initialBreakForce)
                    * (_isDry ? 2f : 1f);
        }

        /// <summary>Called on the physics tick that applies a GPU fluid impulse.</summary>
        internal void ObserveFluidImpulse(Vector3 impulse)
        {
            if (_joint == null || _settled || _firstContactTime >= 0f)
                return;

            float force = impulse.magnitude / Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            if (force >= firstContactForce)
                _firstContactTime = Time.fixedTime;
        }

        private void FixedUpdate()
        {
            if (_joint == null || _settled || _firstContactTime < 0f)
                return;

            float elapsed = Time.fixedTime - _firstContactTime;
            if (elapsed < minimumProtectionSeconds)
                return;

            // Use the actual target break force rather than a hard-coded
            // 32.5 N ceiling which ambient flow may never reach. A deadline
            // prevents a permanently strong joint when loads keep fluctuating.
            float targetForce = settledBreakForce * (_isDry ? 2f : 1f);
            if (_joint.currentForce.magnitude <= targetForce)
                _stableTime += Time.fixedDeltaTime;
            else
                _stableTime = 0f;

            if (_stableTime < settledDuration &&
                elapsed < Mathf.Max(maximumProtectionSeconds, minimumProtectionSeconds))
                return;

            _settled = true;
            ApplyBreakForce();
        }
    }
}
