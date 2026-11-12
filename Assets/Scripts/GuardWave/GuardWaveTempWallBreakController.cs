using System.Collections.Generic;
using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Breaks only Forest/Temp Wall's authored Stick joints after a real wave impact.
    /// A static breakForce of 100 N would break these joints before any water arrives:
    /// the first stick already carries ~268 N of joint reaction at rest.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveTempWallBreakController : MonoBehaviour
    {
        [SerializeField, Min(1f)] private float waveImpactThresholdNewtons = 100f;
        [SerializeField, Min(1f)] private float impactBreakForceNewtons = 100f;

        private static GuardWaveTempWallBreakController _active;
        private readonly Dictionary<Rigidbody, StickJointState> _sticks =
            new Dictionary<Rigidbody, StickJointState>();
        private GuardWaveGame _game;

        private void Awake()
        {
            _game = FindFirstObjectByType<GuardWaveGame>();
            // Do not change the shared Stick prefab: only this Temp Wall's children.
            foreach (Transform child in transform)
            {
                Rigidbody body = child.GetComponent<Rigidbody>();
                ConfigurableJoint joint = child.GetComponent<ConfigurableJoint>();
                if (body != null && joint != null)
                    _sticks.Add(body, new StickJointState(body, joint));
            }
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

        /// <summary>
        /// The GPU solver applies this exact clamped impulse with ForceMode.Impulse.
        /// Compare impulse / fixedDeltaTime to the force estimates in StickForce logs.
        /// </summary>
        internal static void RecordFluidImpulse(Rigidbody body, Vector3 impulse)
        {
            GuardWaveTempWallBreakController controller = _active;
            if (controller == null || body == null ||
                !controller._sticks.TryGetValue(body, out StickJointState stick) ||
                stick.Triggered || stick.Joint == null)
                return;

            if (controller._game == null)
                controller._game = FindFirstObjectByType<GuardWaveGame>();
            if (controller._game == null || !controller._game.WaveAttemptActive)
                return;

            float impactForce = impulse.magnitude / Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            if (impactForce < controller.waveImpactThresholdNewtons)
                return;

            // Only the wave arms a breakable joint. At rest it remains at its
            // authored Infinity thresholds, unaffected by its ~268 N preload.
            stick.Triggered = true;
            stick.Joint.breakForce = controller.impactBreakForceNewtons;
            stick.Joint.breakTorque = Mathf.Infinity;
            Debug.Log(
                $"[GuardWave JointBreak] Temp Wall/{body.name}: wave impact ~{impactForce:F1} N " +
                $">= {controller.waveImpactThresholdNewtons:F1} N; " +
                $"breakForce set to {controller.impactBreakForceNewtons:F1} N.",
                body);
        }

        /// <summary>
        /// Joint breaks destroy the component. Restore it alongside the game's
        /// pre-wave Rigidbody snapshot so a failed defense can be retried.
        /// </summary>
        internal static void RestoreJointsAfterRollback()
        {
            GuardWaveTempWallBreakController controller = _active;
            if (controller == null) return;
            foreach (StickJointState stick in controller._sticks.Values)
                stick.Restore();
        }

        private sealed class StickJointState
        {
            public readonly Rigidbody Body;
            public ConfigurableJoint Joint;
            public bool Triggered;

            private readonly Rigidbody _connectedBody;
            private readonly Vector3 _anchor;
            private readonly Vector3 _connectedAnchor;
            private readonly Vector3 _axis;
            private readonly Vector3 _secondaryAxis;
            private readonly bool _autoAnchor;
            private readonly ConfigurableJointMotion _x, _y, _z, _angularX, _angularY, _angularZ;
            private readonly bool _enableCollision, _enablePreprocessing;
            private readonly bool _configuredInWorldSpace, _swapBodies;
            private readonly float _massScale, _connectedMassScale;
            private readonly float _breakForce, _breakTorque;

            public StickJointState(Rigidbody body, ConfigurableJoint joint)
            {
                Body = body;
                Joint = joint;
                _connectedBody = joint.connectedBody;
                _anchor = joint.anchor;
                _connectedAnchor = joint.connectedAnchor;
                _axis = joint.axis;
                _secondaryAxis = joint.secondaryAxis;
                _autoAnchor = joint.autoConfigureConnectedAnchor;
                _x = joint.xMotion;
                _y = joint.yMotion;
                _z = joint.zMotion;
                _angularX = joint.angularXMotion;
                _angularY = joint.angularYMotion;
                _angularZ = joint.angularZMotion;
                _enableCollision = joint.enableCollision;
                _enablePreprocessing = joint.enablePreprocessing;
                _configuredInWorldSpace = joint.configuredInWorldSpace;
                _swapBodies = joint.swapBodies;
                _massScale = joint.massScale;
                _connectedMassScale = joint.connectedMassScale;
                _breakForce = joint.breakForce;
                _breakTorque = joint.breakTorque;
            }

            public void Restore()
            {
                if (Body == null) return;

                if (Joint == null)
                {
                    // All authored Temp Wall joints have six locked motions,
                    // zero drives and limits, and world-connected anchors.
                    // Restore their captured instance-specific anchor/axis settings.
                    Joint = Body.gameObject.AddComponent<ConfigurableJoint>();
                    Joint.connectedBody = _connectedBody;
                    Joint.autoConfigureConnectedAnchor = _autoAnchor;
                    Joint.anchor = _anchor;
                    Joint.connectedAnchor = _connectedAnchor;
                    Joint.axis = _axis;
                    Joint.secondaryAxis = _secondaryAxis;
                    Joint.xMotion = _x;
                    Joint.yMotion = _y;
                    Joint.zMotion = _z;
                    Joint.angularXMotion = _angularX;
                    Joint.angularYMotion = _angularY;
                    Joint.angularZMotion = _angularZ;
                    Joint.enableCollision = _enableCollision;
                    Joint.enablePreprocessing = _enablePreprocessing;
                    Joint.configuredInWorldSpace = _configuredInWorldSpace;
                    Joint.swapBodies = _swapBodies;
                    Joint.massScale = _massScale;
                    Joint.connectedMassScale = _connectedMassScale;
                }

                Joint.breakForce = _breakForce;
                Joint.breakTorque = _breakTorque;
                Triggered = false;
            }
        }
    }
}
