using System.Collections.Generic;

using UnityEngine;

namespace GuardWave
{
    [DisallowMultipleComponent]
    public sealed class GuardWaveStickForceProbe : MonoBehaviour
    {
        private sealed class StickState
        {
            public Rigidbody Body;
            public Joint Joint;
            public float SumImpulseMagnitudes;
            public float PeakImpulse;
            public float PeakAngularImpulse;
            public Vector3 PeakImpulseDirection;
            public float PeakJointForce;
            public float PeakJointTorque;
            public int Readbacks;
        }

        private static GuardWaveStickForceProbe _active;
        private readonly Dictionary<Rigidbody, StickState> _states = new Dictionary<Rigidbody, StickState>();

        private void Awake()
        {
            foreach (Transform child in transform)
            {
                if (!child.name.StartsWith("Stick") && !child.name.StartsWith("Log")) continue;
                Rigidbody body = child.GetComponent<Rigidbody>();
                if (body == null) continue;
                _states.Add(body, new StickState { Body = body, Joint = body.GetComponent<Joint>() });
            }

            if (_states.Count == 0)
            {
                Debug.LogWarning("[GuardWave StickForce] Initial Wall has no child Stick or Log Rigidbody.", this);
                enabled = false;
            }
        }

        private void OnEnable()
        {
            _active = this;
        }

        private void OnDisable()
        {
            if (_active == this) _active = null;
        }

        /// <summary>Records the clamped impulse applied to a tracked Stick by the fluid simulation.</summary>
        internal static void RecordImpulse(Rigidbody body, Vector3 impulse, Vector3 angularImpulse)
        {
            if (_active == null || !_active._states.TryGetValue(body, out StickState state)) return;

            state.Readbacks++;
            float magnitude = impulse.magnitude;
            state.SumImpulseMagnitudes += magnitude;
            if (magnitude > state.PeakImpulse)
            {
                state.PeakImpulse = magnitude;
                state.PeakImpulseDirection = impulse.normalized;
            }

            state.PeakAngularImpulse = Mathf.Max(state.PeakAngularImpulse, angularImpulse.magnitude);
        }

        private void FixedUpdate()
        {
            foreach (StickState state in _states.Values)
            {
                if (state.Body == null) continue;

                // A failed attempt may recreate the joint on snapshot rollback.
                if (state.Joint == null) state.Joint = state.Body.GetComponent<Joint>();
                if (state.Joint != null)
                {
                    state.PeakJointForce = Mathf.Max(state.PeakJointForce, state.Joint.currentForce.magnitude);
                    state.PeakJointTorque = Mathf.Max(state.PeakJointTorque, state.Joint.currentTorque.magnitude);
                }

                float step = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
                float averageFluidForce = state.SumImpulseMagnitudes / step;
                float peakFluidForceEstimate = state.PeakImpulse / step;
                float peakFluidTorqueEstimate = state.PeakAngularImpulse / step;
                string breakForce = state.Joint != null ? state.Joint.breakForce.ToString("F2") : "no joint";
                string breakTorque = state.Joint != null ? state.Joint.breakTorque.ToString("F2") : "no joint";

                Debug.Log(
                    $"[GuardWave StickForce] {state.Body.name} over {Time.fixedDeltaTime:F2}s, samples={state.Readbacks} | " +
                    $"fluid avg~{averageFluidForce:F2} N, peak~{peakFluidForceEstimate:F2} N " +
                    $"(peak impulse={state.PeakImpulse:F3} N*s, direction={state.PeakImpulseDirection}) | " +
                    $"fluid angular peak~{peakFluidTorqueEstimate:F2} N*m | " +
                    $"joint peak force={state.PeakJointForce:F2} N / breakForce={breakForce} N, " +
                    $"peak torque={state.PeakJointTorque:F2} N*m / breakTorque={breakTorque} N*m. " +
                    "Fluid peaks are impulse/fixedDeltaTime estimates (async GPU readback); " +
                    "use joint peaks to tune break thresholds.",
                    state.Body);

                state.Readbacks = 0;
                state.SumImpulseMagnitudes = 0f;
                state.PeakImpulse = 0f;
                state.PeakAngularImpulse = 0f;
                state.PeakImpulseDirection = Vector3.zero;
                state.PeakJointForce = 0f;
                state.PeakJointTorque = 0f;
            }
        }
    }
}
