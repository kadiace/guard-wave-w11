using System.Collections.Generic;
using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Diagnostic-only observer attached to the Forest Defense Flag.
    /// Reports the REAL inputs to floating-body lift without changing physics.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(200)]
    public sealed class GuardWaveBuoyancyProbe : MonoBehaviour
    {
        [SerializeField, Min(0.25f)] private float reportIntervalSeconds = 1f;
        [SerializeField] private bool includeNearbyFloatingPieces = true;
        [SerializeField, Min(0f)] private float nearbyRadius = 25f;
        [SerializeField, Range(0, 20)] private int maxNearbyPieces = 8;


        [Header("Flood load diagnostics (no physics changes)")]
        [SerializeField] private bool reportAllFloodLogLoads = true;

        // Stable per-Rigidbody windows avoid confusing multiple palette logs named "통나무".
        private sealed class LogLoadWindow
        {
            public float StartTime;
            public Joint[] Joints;
            public Vector3 CpuFlowImpulse;
            public float CpuFlowImpulseAbs;
            public float PeakCpuFlowForce;
            public Vector3 CpuLiftImpulse;
            public Vector3 CpuDampingImpulse;
            public Vector3 GpuLinearImpulse;
            public Vector3 GpuAngularImpulse;
            public Vector3 PressureLinearImpulse;
            public float PeakPressureForce;
            public float PeakPressureTorque;
            public float PeakGpuLinearForce;
            public float PeakGpuTorque;
            public float PeakJointForce;
            public float PeakJointTorque;
            public int CpuSamples;
            public int GpuSamples;

            public void Reset(float startTime)
            {
                StartTime = startTime;
                CpuFlowImpulse = CpuLiftImpulse = CpuDampingImpulse = Vector3.zero;
                GpuLinearImpulse = GpuAngularImpulse = Vector3.zero;
                PressureLinearImpulse = Vector3.zero;
                PeakPressureForce = PeakPressureTorque = 0f;
                CpuFlowImpulseAbs = PeakCpuFlowForce = 0f;
                PeakGpuLinearForce = PeakGpuTorque = 0f;
                PeakJointForce = PeakJointTorque = 0f;
                CpuSamples = GpuSamples = 0;
            }
        }

        private static GuardWaveBuoyancyProbe _active;
        private readonly Dictionary<Rigidbody, LogLoadWindow> _logLoads =
            new Dictionary<Rigidbody, LogLoadWindow>();
        private readonly List<Rigidbody> _staleLogBodies = new List<Rigidbody>();
        private readonly List<GuardWavePiece> _nearby = new List<GuardWavePiece>();
        private GuardWaveGame _game;
        private GuardWavePiece _flag;
        private float _nextReportTime;

        private void Awake()
        {
            _flag = GetComponent<GuardWavePiece>();
        }


        private void OnEnable() => _active = this;

        private void OnDisable()
        {
            if (_active == this) _active = null;
            _logLoads.Clear();
        }

        // Called ONLY with the impulses actually applied by GuardWaveFlipFluid.
        internal static void RecordCpuLogImpulses(GuardWavePiece piece,
            Vector3 lift, Vector3 damping, Vector3 flow)
        {
            if (_active == null || _active._game == null ||
                !_active._game.IsFloodStage || !_active.reportAllFloodLogLoads ||
                piece == null || piece.MaterialId != "log") return;
            LogLoadWindow window = _active.GetLogLoadWindow(piece.Body);
            float dt = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            window.CpuLiftImpulse += lift;
            window.CpuDampingImpulse += damping;
            window.CpuFlowImpulse += flow;
            window.CpuFlowImpulseAbs += flow.magnitude;
            window.PeakCpuFlowForce = Mathf.Max(window.PeakCpuFlowForce,
                flow.magnitude / dt);
            window.CpuSamples++;
        }

        internal static void RecordGpuLogImpulses(GuardWavePiece piece,
            Vector3 linear, Vector3 angular)
        {
            if (_active == null || _active._game == null ||
                !_active._game.IsFloodStage || !_active.reportAllFloodLogLoads ||
                piece == null || piece.MaterialId != "log") return;
            LogLoadWindow window = _active.GetLogLoadWindow(piece.Body);
            float dt = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            window.GpuLinearImpulse += linear;
            window.GpuAngularImpulse += angular;
            window.PeakGpuLinearForce = Mathf.Max(window.PeakGpuLinearForce,
                linear.magnitude / dt);
            window.PeakGpuTorque = Mathf.Max(window.PeakGpuTorque,
                angular.magnitude / dt);
            window.GpuSamples++;
        }

        internal static void RecordGpuLogPressureImpulses(GuardWavePiece piece,
            Vector3 linear, Vector3 angular)
        {
            if (_active == null || _active._game == null ||
                !_active._game.IsFloodStage || !_active.reportAllFloodLogLoads ||
                piece == null || piece.MaterialId != "log") return;
            LogLoadWindow window = _active.GetLogLoadWindow(piece.Body);
            float dt = Mathf.Max(Time.fixedDeltaTime, 0.0001f);
            window.PressureLinearImpulse += linear;
            window.PeakPressureForce = Mathf.Max(window.PeakPressureForce,
                linear.magnitude / dt);
            window.PeakPressureTorque = Mathf.Max(window.PeakPressureTorque,
                angular.magnitude / dt);
        }

        private LogLoadWindow GetLogLoadWindow(Rigidbody body)
        {
            if (!_logLoads.TryGetValue(body, out LogLoadWindow window))
            {
                window = new LogLoadWindow();
                window.Reset(Time.fixedTime);
                _logLoads.Add(body, window);
            }
            return window;
        }

        // Sample once per physics step; a one-second-only joint sample misses brief failures.
        private void SampleLogJointForces()
        {
            foreach (KeyValuePair<Rigidbody, LogLoadWindow> entry in _logLoads)
            {
                if (entry.Key == null || entry.Value.Joints == null) continue;
                foreach (Joint joint in entry.Value.Joints)
                {
                    if (joint == null || !joint.gameObject.activeInHierarchy) continue;
                    entry.Value.PeakJointForce = Mathf.Max(
                        entry.Value.PeakJointForce, joint.currentForce.magnitude);
                    entry.Value.PeakJointTorque = Mathf.Max(
                        entry.Value.PeakJointTorque, joint.currentTorque.magnitude);
                }
            }
        }

        private void ReportAllFloodLogLoads()
        {
            foreach (GuardWavePiece piece in FindObjectsByType<GuardWavePiece>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (piece == null || !piece.enabled || piece.MaterialId != "log")
                    continue;
                Rigidbody body = piece.Body;
                if (body == null) continue;
                LogLoadWindow window = GetLogLoadWindow(body);
                float duration = Mathf.Max(Time.fixedTime - window.StartTime, 0.0001f);
                // Auto-glue can add joints after spawn, so refresh them on each report.
                Joint[] joints = body.GetComponents<Joint>();
                int jointCount = 0;
                float jointForceNow = 0f, jointTorqueNow = 0f;
                foreach (Joint joint in joints)
                {
                    if (joint == null || !joint.gameObject.activeInHierarchy) continue;
                    jointCount++;
                    jointForceNow = Mathf.Max(jointForceNow,
                        joint.currentForce.magnitude);
                    jointTorqueNow = Mathf.Max(jointTorqueNow,
                        joint.currentTorque.magnitude);
                }
                window.PeakJointForce = Mathf.Max(window.PeakJointForce, jointForceNow);
                window.PeakJointTorque = Mathf.Max(window.PeakJointTorque, jointTorqueNow);

                Vector3 position = body.worldCenterOfMass;
                Vector3 cpuFlowAverage = window.CpuFlowImpulse / duration;
                float cpuFlowAbsAverage = window.CpuFlowImpulseAbs / duration;
                float gpuLinearAverage = window.GpuLinearImpulse.magnitude / duration;
                Vector2 flow = Vector2.zero;
                float coverage = 0f, buoyancyForce = 0f;
                string waterState = "not-registered";
                if (_game.TryGetBuoyancyProbeSample(body, out var sample))
                {
                    flow = sample.FlowXZ;
                    coverage = sample.FluidCoverage;
                    buoyancyForce = sample.BuoyancyForceN;
                    waterState = sample.State;
                }
                Vector2 relativeFlow = flow -
                    new Vector2(body.linearVelocity.x, body.linearVelocity.z);
                Debug.Log(
                    $"[Flood LogLoad] t={Time.fixedTime:F2}s phase={_game.FloodCrestPhase} " +
                    $"id={body.GetInstanceID()} " +
                    $"name=\"{piece.name}\" pos={position.ToString("F2")} " +
                    $"rotation={body.rotation.eulerAngles.ToString("F1")} " +
                    $"axisRight={body.rotation * Vector3.right} " +
                    $"water={waterState} coverage={coverage:F3} " +
                    $"flowXZ={flow.ToString("F2")} relativeFlowXZ={relativeFlow.ToString("F2")}m/s " +
                    $"cpuFlowMean={cpuFlowAverage.ToString("F2")}N " +
                    $"cpuFlowAbsMean={cpuFlowAbsAverage:F2}N cpuFlowPeak={window.PeakCpuFlowForce:F2}N " +
                    $"cpuLiftMeanY={window.CpuLiftImpulse.y / duration:F2}N " +
                    $"cpuDampingMeanY={window.CpuDampingImpulse.y / duration:F2}N " +
                    $"gpuLinearAbsMean={gpuLinearAverage:F2}N " +
                    $"gpuLinearPeak={window.PeakGpuLinearForce:F2}N " +
                    $"gpuTorquePeak={window.PeakGpuTorque:F2}Nm " +
                    $"pressureMean={window.PressureLinearImpulse.magnitude / duration:F2}N " +
                    $"pressurePeak={window.PeakPressureForce:F2}N " +
                    $"pressureTorquePeak={window.PeakPressureTorque:F2}Nm " +
                    $"jointCount={jointCount} jointPeakForce={window.PeakJointForce:F2}N " +
                    $"jointPeakTorque={window.PeakJointTorque:F2}Nm " +
                    $"cpuSamples={window.CpuSamples} gpuSamples={window.GpuSamples} " +
                    $"window={duration:F2}s buoyancyNow={buoyancyForce:F2}N " +
                    "(GPU force/torque peaks are applied impulse per fixed step; " +
                    "joint peaks are physics-step sampled, owned joints only)",
                    piece);
                window.Reset(Time.fixedTime);
                window.Joints = joints;
            }

            _staleLogBodies.Clear();
            foreach (Rigidbody body in _logLoads.Keys)
                if (body == null || !body.gameObject.activeInHierarchy)
                    _staleLogBodies.Add(body);
            foreach (Rigidbody body in _staleLogBodies)
                _logLoads.Remove(body);
        }

        private void Start()
        {
            _game = FindFirstObjectByType<GuardWaveGame>();
            if (_game == null)
                Debug.LogWarning("[Forest Buoyancy] GuardWaveGame not found; diagnostics disabled.", this);
        }


        private void FixedUpdate()
        {
            if (_game == null) return;
            if (reportAllFloodLogLoads && _game.IsFloodStage)
                SampleLogJointForces();
            if (Time.fixedTime < _nextReportTime) return;
            _nextReportTime = Time.fixedTime + Mathf.Max(0.25f, reportIntervalSeconds);


            Report(_flag);
            if (reportAllFloodLogLoads && _game.IsFloodStage)
                ReportAllFloodLogLoads();
            if (!includeNearbyFloatingPieces || maxNearbyPieces <= 0) return;

            _nearby.Clear();
            float radiusSq = nearbyRadius * nearbyRadius;
            foreach (GuardWavePiece candidate in FindObjectsByType<GuardWavePiece>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (candidate == null || candidate == _flag || !candidate.enabled ||
                    candidate.RelativeFluidDensity >= 1f) continue;
                Vector3 distance = candidate.transform.position - transform.position;
                distance.y = 0f;
                if (distance.sqrMagnitude <= radiusSq) _nearby.Add(candidate);
            }

            _nearby.Sort((a, b) =>
            {
                Vector3 da = a.transform.position - transform.position;
                Vector3 db = b.transform.position - transform.position;
                da.y = 0f;
                db.y = 0f;
                return da.sqrMagnitude.CompareTo(db.sqrMagnitude);
            });

            for (int i = 0; i < Mathf.Min(maxNearbyPieces, _nearby.Count); ++i)
                Report(_nearby[i]);
        }

        private void Report(GuardWavePiece piece)
        {
            if (piece == null || !piece.gameObject.activeInHierarchy) return;

            if (!_game.TryGetBuoyancyProbeSample(piece.Body, out var sample))
            {
                Debug.Log(
                    $"[Forest Buoyancy] t={Time.fixedTime:F2}s object={piece.name} " +
                    "status=NOT_REGISTERED_IN_FLIP (no solver/body index)", piece);
                return;
            }

            // This is a render-mesh AABB estimate against the *physics* surface.
            // It is NOT the SSFR visual water surface and NOT submerged volume.
            float visibleHeightFraction = -1f;
            Bounds visualBounds = default;
            bool hasRenderer = false;
            foreach (Renderer renderer in piece.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled) continue;
                if (!hasRenderer) { visualBounds = renderer.bounds; hasRenderer = true; }
                else visualBounds.Encapsulate(renderer.bounds);
            }
            if (hasRenderer && sample.WaterlineSampleAgeSeconds >= 0f)
                visibleHeightFraction = Mathf.Clamp01(
                    (sample.PhysicsWaterlineY - visualBounds.min.y) /
                    Mathf.Max(0.0001f, visualBounds.size.y));

            Debug.Log(
                $"[Forest Buoyancy] t={Time.fixedTime:F2}s object={piece.name} " +
                $"material={piece.MaterialId} state={sample.State} " +
                $"density={sample.RelativeDensity:F3} mass={sample.MassKg:F3}kg " +
                $"waterlineRawY={sample.RawWaterlineY:F3} " +
                $"waterlinePhysicsY={sample.PhysicsWaterlineY:F3} " +
                $"readbackAge={sample.WaterlineSampleAgeSeconds:F3}s " +
                $"coverage={sample.FluidCoverage:F3} " +
                $"shapeSubmerged={sample.ShapeSubmergedFraction:F3} " +
                $"effectiveSubmerged={sample.EffectiveSubmergedFraction:F3} " +
                $"renderAABBHeightSubmerged={visibleHeightFraction:F3} (NOT SSFR) " +
                $"colliders={sample.SampledColliders} " +
                $"buoyancyOverWeight={sample.BuoyancyRatio:F3} " +
                $"buoyancy={sample.BuoyancyForceN:F2}N " +
                $"gravity={sample.GravityForceN:F2}N " +
                $"verticalDamping={sample.VerticalDampingForceN:F2}N " +
                $"netYWithoutContacts={sample.NetVerticalForceN:F2}N " +
                $"velocityY={sample.VerticalVelocity:F3}m/s " +
                $"flowXZ=({sample.FlowXZ.x:F2},{sample.FlowXZ.y:F2})m/s",
                piece);
        }
    }
}
