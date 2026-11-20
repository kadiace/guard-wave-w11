using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GuardWave
{
    /// <summary>
    /// Forest-derived Fog scene only. Connects the existing ready HUD to the
    /// independent gas solver. Never invokes or modifies Forest's FLIP/APIC wave.
    /// </summary>
    [DefaultExecutionOrder(50)]
    [RequireComponent(typeof(GuardWaveGame))]
    public sealed class FogSceneController : MonoBehaviour
    {
        private const int AcceleratedSimulationSpeed = 2;
        private const int MAX_FAN_EXPOSURE_PROBES =
            FogFluidSolver.MAX_PROBE_COUNT - FogFlagDensityProbes.MaxSamples;

        [Header("Scene-authored volume mesh (no runtime creation)")]
        [SerializeField] private MeshRenderer _fogVolumeRenderer;

        [Header("Player-placed fog barrier")]
        [Tooltip("Root scale applied to fog barriers and their placement previews.")]
        [SerializeField] private Vector3 _barrierRootScale = Vector3.one;

        [Header("Three-dimensional gas simulation")]
        [SerializeField, Range(0.45f, 1.5f)] private float _cellSize = 0.75f;
        [SerializeField, Range(8, 64)] private int _pressureIterations = 24;
        [SerializeField, Range(0.5f, 12f)] private float _windSpeed = 5.5f;
        [SerializeField, Range(1f, 16f)] private float _inletDuration = 7f;
        [Header("Billowing smoke motion")]
        [SerializeField, Range(0f, 5f)] private float _vorticityStrength = 2.2f;
        [SerializeField, Range(0f, 4f)] private float _turbulenceStrength = 1.15f;
        [SerializeField, Range(0f, 8f)] private float _expansionStrength = 4.2f;
        [SerializeField, Range(0f, 4f)] private float _buoyancy = 1.05f;
        [SerializeField, Range(0f, 4f)] private float _settling = 0.54f;

        [Header("Volumetric smoke shading")]
        [SerializeField, Range(0.1f, 3f)] private float _extinction = 1.3f;
        [SerializeField, Range(0f, 6f)] private float _shadowStrength = 2.1f;
        [SerializeField, Range(0f, 1f)] private float _detailStrength = 0.65f;
        [SerializeField] private Color _fogTint = new Color(0.69f, 0.81f, 0.60f, 1f);

        private GuardWaveGame _game;
        private FogFluidSolver _fluid;
        private FogFlagDensityProbes _flagProbes;
        private readonly Vector3[] _probePositions = new Vector3[FogFluidSolver.MAX_PROBE_COUNT];
        private readonly Vector3[] _sampledProbePositions = new Vector3[FogFluidSolver.MAX_PROBE_COUNT];
        private readonly string[] _sampledProbeSources = new string[FogFlagDensityProbes.MaxSamples];
        private readonly FogBuildingWind[] _fanExposureTargets =
            new FogBuildingWind[MAX_FAN_EXPOSURE_PROBES];
        private readonly FogBuildingWind[] _fanRequestCandidates =
            new FogBuildingWind[MAX_FAN_EXPOSURE_PROBES];
        private readonly FogBuildingWind[] _sampledFanProbes =
            new FogBuildingWind[MAX_FAN_EXPOSURE_PROBES];
        private int _fanExposureTargetCount;
        private int _sampledFanProbeCount;
        private int _sampledProbeCount;
        private int _sampledOutOfBoundsCount;
        private float _lastFanExposureSampleTime;
        private float _nextProbeLogTime;
        private MaterialPropertyBlock _block;

        [Header("Defense flag gas exposure")]
        [SerializeField, Range(0f, 1f)] private float _contactDensity = 0.01f;
        [SerializeField, Range(0f, 1f)] private float _releaseDensity = 0.001f;
        [SerializeField, Min(0.1f)] private float _failureExposureSeconds = 5f;

        [Header("Fog simulation playback (simulation seconds)")]
        [SerializeField, Min(0f)] private float _doubleSpeedAfterSeconds = 30f;

        [Header("Fog simulation end time (simulation seconds)")]
        [SerializeField, Min(0.1f)] private float _residualFogExtensionSeconds = 5f;
        private const float MinimumSimulationSeconds = 30f;
        private const float MaximumSimulationSeconds = 60f;
        private const float EarlyClearDensity = 0.0001f;

        [Header("Fog density debug log (Unity Console)")]
        [SerializeField] private bool _logEachFlagProbeDensity = true;
        [SerializeField, Range(0.1f, 5f)] private float _probeLogIntervalSeconds = 0.5f;
        private const float MaxReadingAge = 0.25f;
        private float _exposureSeconds;
        private float _lastDensityAt = float.NegativeInfinity;
        private float _maxFlagDensity;
        private bool _flagTouching;
        private bool _running;
        private string _status;
        private float _opacity;
        private int _simulationSpeed = 1;
        // The stage ends at 30s by default. Each nonzero residual density
        // detection renews its deadline to (sample time + 5 simulated seconds),
        // with an unconditional 60-second upper bound.
        private float _scheduledSimulationEnd;
        private float _requestedProbeSimulationElapsed;
        private float _lastCompletedProbeSimulationElapsed;
        private bool _residualFogAtFlag;
        private float _simulationElapsedSeconds;

        public int SimulationSpeed => _simulationSpeed;
        public Vector3 BarrierRootScale => _barrierRootScale;
        public float MaximumSimulationDurationSeconds => MaximumSimulationSeconds;
        public float RemainingSimulationSeconds =>
            Mathf.Max(0f, MaximumSimulationSeconds - _simulationElapsedSeconds);
        public float RemainingScheduledWavePlaybackSeconds =>
            GetPlaybackSecondsUntil(_scheduledSimulationEnd);
        public bool IsComing => _running;
        public float FlagExposureSeconds => _exposureSeconds;
        public float FlagExposureLimitSeconds => _failureExposureSeconds;
        public float FlagMaximumDensity => _maxFlagDensity;
        // Keep the saturated bar visible while the toxic-exposure failure popup
        // is waiting for confirmation. FogPass stops on failure, but the exposure
        // counter stays at its final value until the next Ready.
        public bool IsFlagExposed =>
            (_running && _flagTouching &&
                Time.time - _lastDensityAt <= MaxReadingAge &&
                _game != null && !_game.AwaitingFailure) ||
            (_game != null && _game.AwaitingFailure &&
                _exposureSeconds >= _failureExposureSeconds);
        public bool CanReady => !_running && _game != null && _game.CanReady;
        public string StatusMessage => !string.IsNullOrEmpty(_status)
            ? _status : _game != null ? _game.StatusMessage : "";

        /// <summary>Converts a simulation deadline into remaining gameplay seconds using the configured playback speed.</summary>
        private float GetPlaybackSecondsUntil(float simulationDeadline)
        {
            float elapsed = Mathf.Clamp(_simulationElapsedSeconds, 0f, MaximumSimulationSeconds);
            float deadline = Mathf.Clamp(simulationDeadline, 0f, MaximumSimulationSeconds);
            float acceleratedStart = Mathf.Clamp(
                _doubleSpeedAfterSeconds, 0f, MaximumSimulationSeconds);
            float normalSpeedRemaining = Mathf.Max(
                0f, Mathf.Min(deadline, acceleratedStart) - elapsed);
            float acceleratedSpeedRemaining = Mathf.Max(
                0f, deadline - Mathf.Max(elapsed, acceleratedStart)) /
                AcceleratedSimulationSpeed;
            return normalSpeedRemaining + acceleratedSpeedRemaining;
        }

        /// <summary>Caches active fan targets in this scene for GPU density sampling during the wave.</summary>
        private int CacheFanExposureTargets()
        {
            Array.Clear(_fanExposureTargets, 0, _fanExposureTargets.Length);
            int count = 0;
            foreach (FogBuildingWind fan in UnityEngine.Object.FindObjectsByType<FogBuildingWind>(
                FindObjectsSortMode.None))
            {
                if (fan == null || !fan.isActiveAndEnabled || !fan.IsOperational ||
                    fan.gameObject.scene.handle != gameObject.scene.handle)
                    continue;
                if (count >= _fanExposureTargets.Length) break;
                _fanExposureTargets[count++] = fan;
            }
            return count;
        }

        private void Awake()
        {
            _game = GetComponent<GuardWaveGame>();
            _block = new MaterialPropertyBlock();
            if (_fogVolumeRenderer != null) _fogVolumeRenderer.enabled = false;
        }

        private void Start()
        {
            Camera camera = Camera.main;
            if (camera != null)
            {
                // Depth-based termination of each ray is required even for the ISO camera.
                UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (data == null) data = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                data.requiresDepthTexture = true;
            }
        }

        private void Update()
        {
            // The common failure popup restores GuardWaveGame's edit checkpoint.
            // Drop the old Fog status once that restoration has been confirmed.
            if (!_running && _game != null && !_game.AwaitingFailure && _game.CanReady)
                _status = null;
        }

        public void Ready()
        {
            if (!CanReady || _fogVolumeRenderer == null) return;
            StartCoroutine(FogPass());
        }

        private IEnumerator FogPass()
        {
            _running = true;
            _game.SetRulesOpen(true); // Existing placement UI and input become read-only.
            _flagProbes = _game.DefenseFlag != null
                ? _game.DefenseFlag.GetComponent<FogFlagDensityProbes>() : null;
            _fanExposureTargetCount = CacheFanExposureTargets();
            _exposureSeconds = 0f;
            _lastDensityAt = float.NegativeInfinity;
            _maxFlagDensity = 0f;
            _flagTouching = false;
            _sampledProbeCount = 0;
            _sampledFanProbeCount = 0;
            _sampledOutOfBoundsCount = 0;
            _lastFanExposureSampleTime = Time.time;
            _nextProbeLogTime = float.NegativeInfinity;
            _simulationSpeed = 1;
            _simulationElapsedSeconds = 0f;
            _scheduledSimulationEnd = MinimumSimulationSeconds;
            _lastCompletedProbeSimulationElapsed = float.NegativeInfinity;
            _residualFogAtFlag = false;
            _requestedProbeSimulationElapsed = 0f;
            if (_flagProbes == null)
            {
                _status = $"{_game.TargetName}의 FogFlagDensityProbes 구성을 찾을 수 없습니다.";
                Debug.LogError(_status);
                _running = false;
                _game.SetRulesOpen(false);
                yield break;
            }
            _status = "도시 도로로 밀려오는 안개에 배치한 선풍기의 바람이 맞서고 있습니다.";
            float ground = -2.25f;
            GameObject bed = GameObject.Find("Fog Asphalt Ground");
            if (bed != null)
            {
                Collider c = bed.GetComponent<Collider>();
                if (c != null) ground = c.bounds.max.y;
            }

            // Urban highway fog spans a 100 m wide simulation domain centered at world X=0.
            // Background towers have no fans; player-placed mid-height vents remain in the gas domain.
            Vector3 origin = new Vector3(-50f, ground - 0.5f, -70f);
            Vector3 extent = new Vector3(100f, 20f, 140f);
            // Align X/Y/Z cell counts with the 100:20:140 metre dimensions.
            // The 5:1:7 ratio keeps the real volume within the requested bounds.
            int xCellCount = Mathf.CeilToInt(extent.x / (_cellSize * 5f)) * 5;
            float voxelSize = extent.x / xCellCount;
            try
            {
                _fluid = new FogFluidSolver(gameObject.scene, origin, extent,
                    voxelSize, _pressureIterations);
                _fluid.ConfigureMotion(_vorticityStrength, _turbulenceStrength,
                    _expansionStrength, _buoyancy, _settling);
                _fluid.ConfigureDensityFade(MinimumSimulationSeconds, MaximumSimulationSeconds);
                Bounds bounds = _fluid.WorldBounds;
                _fogVolumeRenderer.transform.position = bounds.center;
                _fogVolumeRenderer.transform.localScale = bounds.size;
                _fogVolumeRenderer.enabled = true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                DisposeFog();
                _status = "안개 시뮬레이션을 시작하지 못했습니다. Compute Shader 지원을 확인하세요.";
                _running = false;
                _game.SetRulesOpen(false);
                yield break;
            }

            // The original inlet/travel heuristic (~26s) is replaced with a
            // fixed 30s minimum independent of wind speed.
            float elapsed = 0f, accumulator = 0f, realElapsed = 0f;
            const float SimulationStepSeconds = 0.05f;
            bool failed = false;
            bool exposureFailure = false;
            // A density below 0.0001 on a post-30s GPU reading ends the
            // healthy stage immediately; the previous rolling 5s deadline
            // remains a secondary exit for readings below 0.001.
            // A failed stage keeps simulating until t=60 while its HUD freezes.
            // Gas density itself fades linearly during t=30..60.
            while (true)
            {
                yield return new WaitForFixedUpdate();
                if (_game.AwaitingFailure)
                    failed = true; // Keep the plume moving behind the failure popup.
                float dt = Time.fixedDeltaTime; // Exposure and HUD use unaccelerated game seconds.
                realElapsed += dt;
                int nextSpeed = elapsed >= _doubleSpeedAfterSeconds
                    ? AcceleratedSimulationSpeed : 1;
                if (nextSpeed != _simulationSpeed)
                {
                    _simulationSpeed = nextSpeed;
                    Debug.Log($"[FogSimulation] playback={_simulationSpeed}x " +
                        $"realElapsed={realElapsed:F2}s simulationElapsed={elapsed:F2}s");
                }

                // Advance the gas domain (and its inlet/tail timing), NOT the
                // entire Unity game. Several stable 0.05s solver steps may be
                // dispatched in one FixedUpdate at 2x/3x; never enlarge _Dt.
                float simulatedDt = Mathf.Min(dt * _simulationSpeed,
                    Mathf.Max(0f, MaximumSimulationSeconds - elapsed));
                elapsed += simulatedDt;
                _simulationElapsedSeconds = elapsed;
                accumulator += simulatedDt;
                bool stepped = false;
                while (accumulator + 0.00001f >= SimulationStepSeconds ||
                       (elapsed >= MaximumSimulationSeconds && accumulator >= 0.001f))
                {
                    // Drain the final partial solver step at t=60 so the
                    // shader's density fade actually reaches zero.
                    float step = Mathf.Min(SimulationStepSeconds, accumulator);
                    accumulator = Mathf.Max(0f, accumulator - step);
                    float stepElapsed = elapsed - accumulator;
                    // Fast source ignition followed by a brief, tapered smoke tail.
                    float ramp = Mathf.SmoothStep(0f, 1f,
                        Mathf.Clamp01(stepElapsed / 0.75f));
                    float tail = Mathf.SmoothStep(0f, 1f,
                        Mathf.Clamp01((_inletDuration - stepElapsed) / 1.2f));
                    float inlet = stepElapsed < _inletDuration ? ramp * tail : 0f;
                    _fluid.Step(step, inlet, _windSpeed, ground);
                    stepped = true;
                }
                // Read one set of detection dots after the final step of this
                // physics frame; more solver steps do not change exposure time.
                if (stepped && !_game.AwaitingFailure)
                {
                    int flagProbeCount = _flagProbes.Collect(_probePositions, _fluid.WorldBounds);
                    int totalProbeCount = flagProbeCount;
                    int fanProbeCount = 0;
                    for (int i = 0; i < _fanExposureTargetCount &&
                        totalProbeCount < _probePositions.Length; i++)
                    {
                        FogBuildingWind fan = _fanExposureTargets[i];
                        if (fan == null || !fan.isActiveAndEnabled || !fan.IsOperational)
                            continue;
                        Vector3 position = fan.GasExposurePosition;
                        if (!_fluid.WorldBounds.Contains(position)) continue;
                        _probePositions[totalProbeCount++] = position;
                        _fanRequestCandidates[fanProbeCount++] = fan;
                    }

                    if (flagProbeCount == 0)
                    {
                        // No target samples are in the volume, so treat the goal as clear.
                        _flagTouching = false;
                        _residualFogAtFlag = false;
                        _maxFlagDensity = 0f;
                        _exposureSeconds = 0f;
                        _lastCompletedProbeSimulationElapsed = elapsed;
                        if (_logEachFlagProbeDensity && Time.unscaledTime >= _nextProbeLogTime)
                        {
                            Debug.Log($"[FogFlagDensity] no valid dots (excluded=" +
                                $"{_flagProbes.LastOutOfBoundsCount}, bounds={_fluid.WorldBounds})");
                            _nextProbeLogTime = Time.unscaledTime + _probeLogIntervalSeconds;
                        }
                    }

                    // Share the asynchronous GPU readback between the objective and its fans.
                    if (totalProbeCount > 0 && _fluid.RequestDensitySamples(
                        _probePositions, totalProbeCount, ReceiveDensitySamples))
                    {
                        _requestedProbeSimulationElapsed = elapsed;
                        _sampledProbeCount = flagProbeCount;
                        _sampledFanProbeCount = fanProbeCount;
                        _sampledOutOfBoundsCount = _flagProbes.LastOutOfBoundsCount;
                        for (int i = 0; i < flagProbeCount; i++)
                        {
                            _sampledProbePositions[i] = _probePositions[i];
                            _sampledProbeSources[i] = _flagProbes.GetSampleSource(i);
                        }
                        for (int i = 0; i < fanProbeCount; i++)
                            _sampledFanProbes[i] = _fanRequestCandidates[i];
                    }
                }

                // Readbacks are asynchronous. Only count physics time while the
                // last GPU density reading is fresh; never stall for GPU data.
                // A stale reading is not uninterrupted exposure.
                if (!_game.AwaitingFailure)
                {
                    if (_flagTouching && Time.time - _lastDensityAt > MaxReadingAge)
                    {
                        _flagTouching = false;
                        _exposureSeconds = 0f;
                    }
                    if (IsFlagExposed)
                        _exposureSeconds += dt;

                    if (_exposureSeconds >= _failureExposureSeconds)
                    {
                        _game.FailFromFogExposure();
                        exposureFailure = _game.AwaitingFailure;
                        failed = exposureFailure;
                        // Do not reset _exposureSeconds: the failed progress bar
                        // remains full until ConfirmFailure restores the stage.
                    }
                }
                // Do not fade the visual material independently. The GPU
                // density itself fades 30..60s, matching gameplay probes.
                _opacity = 1f;
                RenderVolume();

                // After 30s, a fresh reading below 0.0001 ends immediately,
                // regardless of the 5s extension. A later 0.001 release
                // also permits the original rolling-deadline exit. Failure
                // popups suppress these early exits, not the gas simulation.
                bool hasPostThirtyReading =
                    _lastCompletedProbeSimulationElapsed >= MinimumSimulationSeconds;
                bool clearEnoughToFinish = elapsed >= MinimumSimulationSeconds &&
                    hasPostThirtyReading && _maxFlagDensity < EarlyClearDensity;
                bool pastDeadline = elapsed >= _scheduledSimulationEnd;
                bool latestReadbackIsAfterDeadline =
                    _lastCompletedProbeSimulationElapsed >= _scheduledSimulationEnd;
                bool deadlineCleared = pastDeadline && latestReadbackIsAfterDeadline &&
                    !_residualFogAtFlag;
                if (elapsed >= MaximumSimulationSeconds ||
                    (!_game.AwaitingFailure && (clearEnoughToFinish || deadlineCleared)))
                {
                    Debug.Log($"[FogSimulation] complete sim={elapsed:F2}s " +
                        $"real={realElapsed:F2}s deadline={_scheduledSimulationEnd:F2}s " +
                        $"density={_maxFlagDensity:F6} hardStop=" +
                        (elapsed >= MaximumSimulationSeconds));
                    break;
                }
            }

            _running = false;
            _game.SetRulesOpen(false);
            if (!failed && !_game.AwaitingFailure)
            {
                _game.CompleteStageRound();
                _status = _game.IsFinished
                    ? $"안개가 지나갔습니다. {_game.TargetName}을 지켜냈습니다."
                    : _game.StatusMessage;
                if (_game.IsFinished)
                    yield return ContinueFogAfterSuccess(ground);
            }
            else
            {
                _status = exposureFailure
                    ? $"{_game.TargetName}이 독안개에 5초 이상 노출되었습니다. 확인을 누르면 배치 상태로 돌아갑니다."
                    : "방어에 실패했습니다. 배치 상태를 복원하고 다시 시도하세요.";
            }
            DisposeFog();
        }

        /// <summary>
        /// Keeps fog moving behind the victory popup without repeating the
        /// exposure or success checks, until leaving the Fog scene.
        /// </summary>
        private IEnumerator ContinueFogAfterSuccess(float ground)
        {
            // Gameplay clears gas by 60s; reinstate a fresh inlet for the
            // victory-only display so the fog does not disappear behind the UI.
            _fluid.ConfigureDensityFade(1000000f, 1000001f);
            while (_game.IsFinished && _fluid != null)
            {
                yield return new WaitForFixedUpdate();
                _fluid.Step(Time.fixedDeltaTime, 1f, _windSpeed, ground);
                RenderVolume();
            }
        }

        /// <summary>Splits one GPU density readback between the objective and active fan exposure sensors.</summary>
        private void ReceiveDensitySamples(float peakDensity, float[] samples)
        {
            if (!_running || _game == null || _game.AwaitingFailure) return;
            if (_sampledProbeCount > 0)
            {
                float flagPeakDensity = 0f;
                for (int i = 0; i < _sampledProbeCount; i++)
                    flagPeakDensity = Mathf.Max(flagPeakDensity, samples[i]);
                ReceiveFlagDensity(flagPeakDensity, samples);
            }

            float now = Time.time;
            float elapsedSeconds = Mathf.Max(0f, now - _lastFanExposureSampleTime);
            _lastFanExposureSampleTime = now;
            int fanSampleOffset = _sampledProbeCount;
            for (int i = 0; i < _sampledFanProbeCount; i++)
            {
                FogBuildingWind fan = _sampledFanProbes[i];
                if (fan == null || !fan.isActiveAndEnabled) continue;
                fan.ApplyGasExposure(samples[fanSampleOffset + i], elapsedSeconds);
            }
        }

        /// <summary>Updates objective exposure and the fog wave's residual-density deadline.</summary>
        private void ReceiveFlagDensity(float peakDensity, float[] samples)
        {
            if (!_running || _game == null || _game.AwaitingFailure) return;
            _lastDensityAt = Time.time;
            _maxFlagDensity = peakDensity;
            _lastCompletedProbeSimulationElapsed = _requestedProbeSimulationElapsed;
            _residualFogAtFlag = peakDensity >= Mathf.Min(_releaseDensity, _contactDensity);
            if (_residualFogAtFlag)
            {
                // Do not add five seconds cumulatively: renew the deadline
                // based on the exact sampled simulation time.
                float renewedEnd = _requestedProbeSimulationElapsed +
                    _residualFogExtensionSeconds;
                _scheduledSimulationEnd = Mathf.Min(MaximumSimulationSeconds,
                    Mathf.Max(_scheduledSimulationEnd, renewedEnd));
            }
            bool wasTouching = _flagTouching;
            // Hysteresis: the first contact requires 0.01, but once touched,
            // any detectable fog >=0.001 keeps the five-second timer active.
            // Density below 0.001 (or no valid samples) resets exposure.
            float releaseThreshold = Mathf.Min(_releaseDensity, _contactDensity);
            _flagTouching = peakDensity >= (wasTouching
                ? releaseThreshold : _contactDensity);
            if (!_flagTouching) _exposureSeconds = 0f;

            // Include the very first crossing in addition to timed snapshots.
            if (_logEachFlagProbeDensity &&
                (Time.unscaledTime >= _nextProbeLogTime || wasTouching != _flagTouching))
            {
                LogFlagProbeDensities(samples);
                _nextProbeLogTime = Time.unscaledTime + _probeLogIntervalSeconds;
            }
        }

        private void LogFlagProbeDensities(float[] samples)
        {
            int count = Mathf.Min(samples.Length, _sampledProbeCount);
            int startHits = 0, holdHits = 0;
            float releaseThreshold = Mathf.Min(_releaseDensity, _contactDensity);
            float minDensity = float.PositiveInfinity;
            float maxDensity = 0f;
            StringBuilder message = new StringBuilder(1536);
            message.Append("[FogFlagDensity] t=")
                .Append(Time.time.ToString("F2"))
                .Append("s startThreshold=").Append(_contactDensity.ToString("F4"))
                .Append(" releaseThreshold=").Append(Mathf.Min(_releaseDensity,
                    _contactDensity).ToString("F4"))
                .Append(" simSpeed=").Append(_simulationSpeed).Append('x')
                .Append(" simElapsed=").Append(_requestedProbeSimulationElapsed.ToString("F2"))
                .Append("s earlyClearDensity=").Append(EarlyClearDensity.ToString("F4"))
                .Append(" deadline=").Append(_scheduledSimulationEnd.ToString("F2"))
                .Append("s hardStop=").Append(MaximumSimulationSeconds.ToString("F2"))
                .Append("s residualFog=").Append(_residualFogAtFlag ? "YES" : "NO")
                .Append(" sampled=").Append(count)
                .Append(" excludedOutsideVolume=").Append(_sampledOutOfBoundsCount)
                .Append(" exposure=").Append(_exposureSeconds.ToString("F2"))
                .Append('/').Append(_failureExposureSeconds.ToString("F2")).Append("s");
            for (int i = 0; i < count; i++)
            {
                float density = samples[i];
                bool startHit = density >= _contactDensity;
                bool holdHit = density >= releaseThreshold;
                if (startHit) startHits++;
                if (holdHit) holdHits++;
                minDensity = Mathf.Min(minDensity, density);
                maxDensity = Mathf.Max(maxDensity, density);
                Vector3 p = _sampledProbePositions[i];
                message.Append("\n  #").Append(i.ToString("D2"))
                    .Append(' ').Append(_sampledProbeSources[i])
                    .Append(" world=(").Append(p.x.ToString("F2"))
                    .Append(", ").Append(p.y.ToString("F2"))
                    .Append(", ").Append(p.z.ToString("F2"))
                    .Append(") density=").Append(density.ToString("F5"))
                    .Append(startHit ? " START" : holdHit ? " HOLD" : " miss");
            }
            message.Append("\n  summary: min=")
                .Append(count > 0 ? minDensity.ToString("F4") : "n/a")
                .Append(" max=").Append(maxDensity.ToString("F5"))
                .Append(" startHits=").Append(startHits).Append('/').Append(count)
                .Append(" holdHits=").Append(holdHits).Append('/').Append(count)
                .Append(" touching=").Append(_flagTouching ? "YES" : "NO");
            Debug.Log(message.ToString());
        }

        private void RenderVolume()
        {
            if (_fluid == null || _fogVolumeRenderer == null) return;
            Bounds bounds = _fluid.WorldBounds;
            _fogVolumeRenderer.GetPropertyBlock(_block);
            _block.SetTexture("_FogDensityTex", _fluid.Density);
            _block.SetVector("_FogVolumeMin", bounds.min);
            _block.SetVector("_FogVolumeMax", bounds.max);
            _block.SetColor("_FogTint", _fogTint);
            _block.SetFloat("_FogExtinction", _extinction);
            _block.SetFloat("_FogShadowStrength", _shadowStrength);
            _block.SetFloat("_FogDetailStrength", _detailStrength);
            _block.SetFloat("_FogOpacity", _opacity);
            _fogVolumeRenderer.SetPropertyBlock(_block);
        }

        private void DisposeFog()
        {
            if (_fogVolumeRenderer != null)
            {
                _fogVolumeRenderer.enabled = false;
                _fogVolumeRenderer.SetPropertyBlock(null);
            }
            _fluid?.Dispose();
            _fluid = null;
        }

        /// <summary>
        /// Confirmation ends the retained failure fog, including its active GPU
        /// steps and readbacks. Neither the failure popup nor reaching t=60
        /// implicitly restores placement state.
        /// </summary>
        public void ResetAfterFailureConfirmation()
        {
            StopAllCoroutines();
            DisposeFog();
            if (_running && _game != null) _game.SetRulesOpen(false);
            _running = false;
            _flagTouching = false;
            _exposureSeconds = 0f;
            _lastDensityAt = float.NegativeInfinity;
            _maxFlagDensity = 0f;
            _simulationElapsedSeconds = 0f;
            _simulationSpeed = 1;
            _status = null;
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            DisposeFog();
            if (_running && _game != null) _game.SetRulesOpen(false);
            _running = false;
            _flagTouching = false;
            _exposureSeconds = 0f;
        }
    }
}
