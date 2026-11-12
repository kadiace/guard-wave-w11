using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Diagnostic probe for Forest/Initial Wall's first child Rigidbody (Stick).
    /// Logs the actual impulses applied by GuardWaveFlipFluid and the joint's
    /// observed reaction loads. It never changes any physics settings.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveStickForceProbe : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float logInterval = 0.5f;

        private static GuardWaveStickForceProbe _active;
        private Rigidbody _stick;
        private Joint _joint;
        private float _elapsed;
        private float _sumImpulseMagnitudes;
        private float _peakImpulse;
        private float _peakAngularImpulse;
        private Vector3 _peakImpulseDirection;
        private float _peakJointForce;
        private float _peakJointTorque;
        private int _readbacks;

        private void Awake()
        {
            _stick = GetComponentInChildren<Rigidbody>();
            if (_stick == null)
            {
                Debug.LogWarning("[GuardWave StickForce] Initial Wall has no child Rigidbody.", this);
                enabled = false;
                return;
            }

            _joint = _stick.GetComponent<Joint>();
        }

        private void OnEnable()
        {
            _active = this;
        }

        private void OnDisable()
        {
            if (_active == this)
                _active = null;
        }

        /// <summary>Called with the CLAMPED impulse actually passed to AddForce/AddTorque.</summary>
        internal static void RecordImpulse(Rigidbody body, Vector3 impulse, Vector3 angularImpulse)
        {
            GuardWaveStickForceProbe probe = _active;
            if (probe == null || probe._stick != body)
                return;

            probe._readbacks++;

            float magnitude = impulse.magnitude;
            probe._sumImpulseMagnitudes += magnitude;
            if (magnitude > probe._peakImpulse)
            {
                probe._peakImpulse = magnitude;
                probe._peakImpulseDirection = impulse.normalized;
            }

            probe._peakAngularImpulse = Mathf.Max(probe._peakAngularImpulse, angularImpulse.magnitude);
        }

        private void FixedUpdate()
        {
            // Unity's Joint.currentForce/currentTorque are physics-solver reaction loads.
            // They are NOT the same as the external fluid force on the Rigidbody.
            // A failed attempt may recreate the joint on snapshot rollback.
            if (_joint == null && _stick != null)
                _joint = _stick.GetComponent<Joint>();
            if (_joint != null)
            {
                _peakJointForce = Mathf.Max(_peakJointForce, _joint.currentForce.magnitude);
                _peakJointTorque = Mathf.Max(_peakJointTorque, _joint.currentTorque.magnitude);
            }

            _elapsed += Time.fixedDeltaTime;
            if (_elapsed < logInterval)
                return;

            float step = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            float averageFluidForce = _sumImpulseMagnitudes / _elapsed;
            float peakFluidForceEstimate = _peakImpulse / step;
            float peakFluidTorqueEstimate = _peakAngularImpulse / step;
            string breakForce = _joint != null ? _joint.breakForce.ToString("F2") : "no joint";
            string breakTorque = _joint != null ? _joint.breakTorque.ToString("F2") : "no joint";

            Debug.Log(
                $"[GuardWave StickForce] Temp Wall/Stick over {_elapsed:F2}s, samples={_readbacks} | " +
                $"fluid avg~{averageFluidForce:F2} N, peak~{peakFluidForceEstimate:F2} N " +
                $"(peak impulse={_peakImpulse:F3} N*s, direction={_peakImpulseDirection}) | " +
                $"fluid angular peak~{peakFluidTorqueEstimate:F2} N*m | " +
                $"joint peak force={_peakJointForce:F2} N / breakForce={breakForce} N, " +
                $"peak torque={_peakJointTorque:F2} N*m / breakTorque={breakTorque} N*m. " +
                "Fluid peaks are impulse/fixedDeltaTime estimates (async GPU readback); " +
                "use joint peaks to tune break thresholds.",
                _stick);

            _elapsed = 0f;
            _readbacks = 0;
            _sumImpulseMagnitudes = 0f;
            _peakImpulse = 0f;
            _peakAngularImpulse = 0f;
            _peakImpulseDirection = Vector3.zero;
            _peakJointForce = 0f;
            _peakJointTorque = 0f;

        }
    }
}
