using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Protects scene-authored Initial Wall logs from the first creek impact,
    /// then enables wave-driven FixedJoint breakage once that impact settles.
    /// Does not affect palette logs or mud bonds.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FixedJoint))]
    [DefaultExecutionOrder(150)]
    public sealed class GuardWaveInitialLogJoint : MonoBehaviour
    {
        [Header("FixedJoint break force")]
        [SerializeField, Min(0f)] private float initialBreakForce = 80f;
        [SerializeField, Min(0f)] private float settledBreakForce = 35f;

        [Header("Initial creek impact")]
        [Tooltip("Minimum applied fluid force that marks this log's first water contact (N).")]
        [SerializeField, Min(0f)] private float firstContactForce = 1f;
        [Tooltip("Wait at least this long after first water contact before weakening the joint.")]
        [SerializeField, Min(0f)] private float minimumProtectionSeconds = 0.8f;
        [Tooltip("Joint load must be below this value before the first impact is considered settled (N).")]
        [SerializeField, Min(0f)] private float settledForceCeiling = 32.5f;
        [Tooltip("Required continuous low-load duration after the initial protection period.")]
        [SerializeField, Min(0f)] private float settledDuration = 0.2f;

        private FixedJoint _joint;
        private float _firstContactTime = -1f;
        private float _stableTime;
        private bool _settled;

        private void OnEnable()
        {
            _joint = GetComponent<FixedJoint>();
            _firstContactTime = -1f;
            _stableTime = 0f;
            _settled = false;
            if (_joint != null)
                _joint.breakForce = initialBreakForce;
        }

        /// <summary>Called on the physics tick that applies the GPU fluid impulse.</summary>
        internal void ObserveFluidImpulse(Vector3 impulse)
        {
            if (_joint == null)
                _joint = GetComponent<FixedJoint>();
            if (_settled || _firstContactTime >= 0f || _joint == null)
                return;
            float force = impulse.magnitude / Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            if (force >= firstContactForce)
                _firstContactTime = Time.fixedTime;
        }

        private void FixedUpdate()
        {
            if (_joint == null)
                _joint = GetComponent<FixedJoint>();
            if (_settled || _firstContactTime < 0f || _joint == null)
                return;
            if (Time.fixedTime - _firstContactTime < minimumProtectionSeconds)
                return;

            // currentForce is the previous solved physics step's joint reaction.
            // Require continuous calm rather than dropping to 35 N amid the
            // first high-force creek impact.
            if (_joint.currentForce.magnitude <= settledForceCeiling)
                _stableTime += Time.fixedDeltaTime;
            else
                _stableTime = 0f;

            if (_stableTime < settledDuration)
                return;

            _joint.breakForce = initialBreakForce > 0f
                ? _joint.breakForce * settledBreakForce / initialBreakForce
                : settledBreakForce;
            _settled = true;
        }
    }
}
