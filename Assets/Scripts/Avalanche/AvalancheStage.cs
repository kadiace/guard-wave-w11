using System.Collections.Generic;

using UnityEngine;

using UnityEngine.InputSystem;
using UnityEngine.Serialization;

using GuardWave;

namespace Avalanche
{
    public enum AvalanchePhase { Introduction, Preparing, Warning, Sliding, Assessing, Cleared, Failed, TechnicalError }

    /// <summary>Owns the two snow releases, citizen rescue rules and scene-specific construction settings.</summary>
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(AvalancheConstruction), typeof(AvalancheHud))]
    public sealed class AvalancheStage : MonoBehaviour
    {
        [Header("Scene references")]
        [SerializeField] private Camera _viewCamera;
        [SerializeField] private MeshCollider _ground;
        [FormerlySerializedAs("_waterSurface")]
        [SerializeField] private MeshFilter _snowSurface;
        [SerializeField] private Font _font;
        [Header("Construction catalog")]
        [SerializeField] private Material _panelMaterial;
        [SerializeField] private Material _stoneMaterial;
        [SerializeField] private Material _previewMaterial;
        [SerializeField] private Material _lineMaterial;
        [SerializeField] private PhysicsMaterial _friction;
        [Header("Disaster timing")]
        [SerializeField, Min(0f)] private float _warningSeconds = 5f;
        [SerializeField, Min(1f)] private float _secondReleaseSeconds = 12f;
        [SerializeField, Min(1f)] private float _observationStartSeconds = 20f;
        [SerializeField, Min(1f)] private float _disasterLimitSeconds = 62f;
        [Tooltip("연속 안전 관찰 시간이며, 제한시간 종료 시 안정 확인 유예의 최대 시간으로도 사용합니다.")]
        [SerializeField, Min(0.1f)] private float _safeObservationSeconds = 3f;
        [Header("Snow releases")]
        [SerializeField] private Vector2 _firstSource = new Vector2(-2f, 52f);
        [SerializeField] private Vector2 _secondSource = new Vector2(2f, 52f);
        [SerializeField] private Vector2 _sourceRadius = new Vector2(3.6f, 4.8f);
        [SerializeField] private Vector2 _sourceVelocity = new Vector2(0f, -1f);
        [SerializeField, Min(1f)] private float _firstVolume = 150f;
        [SerializeField, Min(1f)] private float _secondVolume = 270f;
        [Header("Citizens")]
        [SerializeField] private AvalancheCitizen[] _citizens = new AvalancheCitizen[0];
        [SerializeField, Min(0.02f)] private float _citizenSampleInterval = 0.1f;
        [SerializeField, Range(0.001f, 0.1f)] private float _activeVolumeFraction = 0.01f;
        [SerializeField] private AvalancheCitizen.RiskSettings _citizenRisk = new AvalancheCitizen.RiskSettings();
        [Header("Terrain and snow")]
        [SerializeField] private AvalancheTerrain.Settings _terrainSettings = new AvalancheTerrain.Settings();
        [SerializeField] private AvalancheSnow.Settings _snowSettings = new AvalancheSnow.Settings();
        [Header("Diagnostics")]
        [SerializeField] private bool _logAttemptSummary = true;
        private AvalancheSnow _snow;
        private Mesh _terrainMesh;
        private Material _snowMaterial;
        private float _warningClock;
        private float _sampleClock;
        private float _stableClock;
        private float _settlementGraceClock;
        private float _previousTimeScale;
        private bool _firstReleased;
        private bool _secondReleased;
        private bool _settlementGraceActive;
        private AudioSource _avalancheAudio;
        private AudioClip _rumble;
        private GuardWaveStageInfo _stageInfo;
        private StagePreparationClock _preparationClock;
        private StagePauseMenu _pauseMenu;
        public Camera ViewCamera => _viewCamera;
        public MeshCollider Ground => _ground;
        public IReadOnlyList<AvalancheCitizen> Citizens => _citizens;
        public Font Font => _font;
        public Material PanelMaterial => _panelMaterial;
        public Material SandbagMaterial => _stoneMaterial;
        public Material PreviewMaterial => _previewMaterial;
        public Material LineMaterial => _lineMaterial;
        public PhysicsMaterial Friction => _friction;
        public AvalancheConstruction Construction { get; private set; }
        public AvalancheHud Hud { get; private set; }
        public AvalancheSnow Snow => _snow;
        public AvalanchePhase Phase { get; private set; } = AvalanchePhase.Introduction;
        public bool Paused { get; private set; }
        public bool CanEdit => Phase == AvalanchePhase.Preparing && !Paused && !Hud.RulesOpen;
        public bool HasProtectedCitizens => _citizens.Length > 0;
        public AvalancheTerrain.Settings TerrainSettings => _terrainSettings;
        public float PreparationLeft => _preparationClock.Remaining;
        internal bool LimitPreparationTime => _preparationClock.Enabled;
        internal GuardWaveStageInfo StageInfo => _stageInfo;
        public float Elapsed { get; private set; }
        public float DisasterSecondsRemaining => Mathf.Max(0f, _disasterLimitSeconds - Elapsed);
        public float SecondReleaseSeconds => _secondReleaseSeconds;
        public float WarningLeft => Mathf.Max(0f, _warningSeconds - _warningClock);
        public bool SettlementGraceActive => _settlementGraceActive && !IsFinished();
        public float SettlementGraceLeft => Mathf.Max(0f, _safeObservationSeconds - _settlementGraceClock);
        public string ResultMessage { get; private set; } = "";
        public int Attempt { get; private set; } = 1;

        /// <summary>Assigns authored scene references and materials for the snow stage.</summary>
        public void Configure(Camera view, MeshCollider ground, MeshFilter surface, Font font,
            Material panel, Material stone, Material preview, Material line, PhysicsMaterial friction)
        {
            _viewCamera = view; _ground = ground; _snowSurface = surface; _font = font;
            _panelMaterial = panel; _stoneMaterial = stone; _previewMaterial = preview; _lineMaterial = line; _friction = friction;
        }

        /// <summary>Registers the fixed citizens authored into this scene.</summary>
        public void ConfigureCitizens(AvalancheCitizen[] citizens) => _citizens = citizens;

        /// <summary>Clears risk histories while preserving every authored citizen and its scene position.</summary>
        private void ResetCitizens()
        {
            foreach (AvalancheCitizen citizen in _citizens) citizen.ResetState();
        }

        private void Awake()
        {
            _previousTimeScale = Time.timeScale;
            Managers.Init();
            Time.timeScale = 0f;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            _stageInfo = GuardWaveStageInfoFile.LoadForScene(gameObject.scene.name);
            _preparationClock = new StagePreparationClock(_stageInfo);
            AvalancheTerrain.Configure(_terrainSettings);
            _terrainMesh = AvalancheTerrain.CreateMesh();
            _ground.sharedMesh = _terrainMesh;
            _ground.GetComponent<MeshFilter>().sharedMesh = _terrainMesh;
            _snow = new AvalancheSnow(_snowSettings);
            _snowSurface.sharedMesh = _snow.Surface;
            _snowMaterial = new Material(Shader.Find("Avalanche/Snow surface"));
            _snowSurface.GetComponent<Renderer>().sharedMaterial = _snowMaterial;
            Construction = GetComponent<AvalancheConstruction>();
            Hud = GetComponent<AvalancheHud>();
            ResetCitizens();
            Construction.Initialize(this);
            Hud.Initialize(this);
            _viewCamera.GetComponent<StageCameraController>().InitializeOverview(Hud.BlocksPointer, true);
            _pauseMenu = Instantiate(Resources.Load<StagePauseMenu>("Prefabs/UIs/StagePauseMenu"), transform);
            _pauseMenu.Initialize(() => Phase != AvalanchePhase.Introduction && !IsFinished(),
                SetPaused, ConsumeEscape);
            CreateForecast(_firstSource, "First snow release");
            CreateForecast(_secondSource, "Second snow release");
            CreateAudio();
            BeginPreparation();
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || _pauseMenu.IsOpen) return;
            if (keyboard.rKey.wasPressedThisFrame)
            {
                Managers.Scene.ReloadCurrentScene();
                return;
            }
            if (!keyboard.enterKey.wasPressedThisFrame) return;
            if (Phase == AvalanchePhase.Introduction) BeginPreparation();
            else if (CanEdit) Ready();
        }

        private void FixedUpdate()
        {
            if (Paused || Hud.RulesOpen || Phase == AvalanchePhase.Introduction || IsFinished()) return;
            float dt = Time.fixedDeltaTime;
            Construction.TickContacts(dt);
            if (Phase == AvalanchePhase.Preparing)
            {
                _preparationClock.Advance(dt);
                if (_preparationClock.Expired) Ready();
                return;
            }
            if (Phase == AvalanchePhase.Warning)
            {
                _warningClock += dt;
                if (_warningClock >= _warningSeconds) Phase = AvalanchePhase.Sliding;
                return;
            }
            if (!_firstReleased) { _snow.Release(_firstSource, _sourceRadius, _firstVolume, _sourceVelocity); _firstReleased = true; _avalancheAudio.PlayOneShot(_rumble); }
            if (!_secondReleased && Elapsed + 0.0001f >= _secondReleaseSeconds)
            { _snow.Release(_secondSource, _sourceRadius, _secondVolume, _sourceVelocity); _secondReleased = true; _avalancheAudio.PlayOneShot(_rumble); }
            _snow.Step(dt, _firstVolume + _secondVolume, Construction.Pieces);
            Elapsed += dt;
            if (!_snow.IsValid) { End(AvalanchePhase.TechnicalError, _snow.Error + "\n배치를 복원해 다시 시도할 수 있습니다."); return; }
            _sampleClock += dt;
            if (_sampleClock + 0.0001f >= _citizenSampleInterval)
            {
                AvalancheCitizen failed = null;
                foreach (AvalancheCitizen citizen in Citizens)
                {
                    citizen.Observe(_snow, _sampleClock, _citizenRisk);
                    if (citizen.State == AvalancheCitizenState.RescueRequired && (failed == null || citizen.ObjectiveId < failed.ObjectiveId)) failed = citizen;
                }
                if (failed != null)
                {
                    End(AvalanchePhase.Failed, $"{failed.CitizenId} · {failed.FailureReason}\n{Elapsed:0.0}초 / 적설 {failed.Depth:0.00}m / 속도 {failed.Speed:0.0}m/s\n충돌 {failed.ImpactSeconds:0.0}초 · 매몰 {failed.BurialSeconds:0.0}초");
                    return;
                }
                bool settled = IsSettlementCandidate();
                _stableClock = settled ? _stableClock + _sampleClock : 0f;
                _sampleClock = 0f;
            }
            if (Elapsed >= _observationStartSeconds) Phase = AvalanchePhase.Assessing;
            if (_stableClock + 0.0001f >= _safeObservationSeconds && IsSettlementCandidate())
            {
                foreach (AvalancheCitizen citizen in Citizens) citizen.Protect();
                End(AvalanchePhase.Cleared, $"두 차례의 눈사태에서 시민 {Citizens.Count}명을 지켰습니다.");
            }
            else if (Elapsed >= _disasterLimitSeconds)
            {
                if (!_settlementGraceActive)
                {
                    if (!IsSettlementCandidate())
                    {
                        End(AvalanchePhase.Failed, "관찰 시간이 끝났습니다.\n시민 주변의 위험이나 움직이는 눈이 남아 있습니다.");
                        return;
                    }
                    _settlementGraceActive = true;
                    _settlementGraceClock = 0f;
                }
                _settlementGraceClock += dt;
                if (_settlementGraceClock + 0.0001f >= _safeObservationSeconds)
                    End(AvalanchePhase.Failed, "안정 확인 유예 시간이 끝났습니다.\n시민이 안전한 상태를 충분히 유지하지 못했습니다.");
            }
        }

        /// <summary>Checks the shared prerequisites for a continuous safe-observation interval.</summary>
        private bool IsSettlementCandidate()
        {
            if (!_secondReleased || Elapsed < Mathf.Max(_observationStartSeconds, _secondReleaseSeconds) ||
                _snow.ActiveVolume >= (_firstVolume + _secondVolume) * _activeVolumeFraction) return false;
            foreach (AvalancheCitizen citizen in Citizens)
                if (!citizen.IsSafe) return false;
            return true;
        }

        /// <summary>Starts unrestricted preparation after closing the stage introduction.</summary>
        public void BeginPreparation() { if (Phase != AvalanchePhase.Introduction) return; Phase = AvalanchePhase.Preparing; Time.timeScale = 1f; }

        /// <summary>Saves and locks construction around the scene's fixed protected citizens.</summary>
        public void Ready()
        {
            if (!CanEdit || !HasProtectedCitizens) return;
            Construction.SaveCheckpoint();
            Construction.CancelSelection();
            _warningClock = 0f;
            Phase = AvalanchePhase.Warning;
            _avalancheAudio.PlayOneShot(_rumble, 0.35f);
        }

        /// <summary>Cancels rules or the current tool before Escape can open the pause menu.</summary>
        private bool ConsumeEscape()
        {
            if (Hud.RulesOpen) { Hud.CloseRules(); return true; }
            return CanEdit && Construction.CancelSelection();
        }

        /// <summary>Reflects the shared pause menu state in construction and simulation.</summary>
        private void SetPaused(bool paused)
        {
            Paused = paused;
            if (paused) _avalancheAudio.Pause(); else _avalancheAudio.UnPause();
        }

        /// <summary>Marks a release ellipse and its direction without adding a physics obstacle.</summary>
        private void CreateForecast(Vector2 center, string name)
        {
            LineRenderer line = new GameObject(name).AddComponent<LineRenderer>();
            line.transform.SetParent(transform, false);
            line.sharedMaterial = _lineMaterial;
            line.startWidth = line.endWidth = 0.08f;
            line.startColor = line.endColor = new Color(0.95f, 0.65f, 0.25f);
            line.positionCount = 49;
            for (int i = 0; i < 49; i++)
            {
                float angle = i * Mathf.PI / 24f;
                float x = center.x + Mathf.Cos(angle) * _sourceRadius.x;
                float z = center.y + Mathf.Sin(angle) * _sourceRadius.y;
                line.SetPosition(i, new Vector3(x, AvalancheTerrain.Height(x, z) + 0.06f, z));
            }
        }

        /// <summary>Creates a quiet procedural low rumble to identify warnings and the two releases.</summary>
        private void CreateAudio()
        {
            _avalancheAudio = gameObject.AddComponent<AudioSource>();
            _avalancheAudio.playOnAwake = false;
            _avalancheAudio.volume = 0.2f;
            float[] samples = new float[44100 * 2];
            System.Random random = new System.Random(42);
            float low = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                low = low * 0.95f + ((float)random.NextDouble() * 2f - 1f) * 0.05f;
                samples[i] = low * Mathf.Sin(Mathf.PI * i / samples.Length);
            }
            _rumble = AudioClip.Create("Avalanche rumble", samples.Length, 1, 44100, false);
            _rumble.SetData(samples, 0);
        }

        /// <summary>Reports a terminal citizen, success or technical outcome.</summary>
        public bool IsFinished() => Phase == AvalanchePhase.Cleared || Phase == AvalanchePhase.Failed || Phase == AvalanchePhase.TechnicalError;

        /// <summary>Freezes the attempt and records a successful stage in the shared session.</summary>
        private void End(AvalanchePhase phase, string message)
        {
            if (IsFinished()) return;
            Phase = phase;
            ResultMessage = message;
            if (_logAttemptSummary)
            {
                Debug.Log($"[Avalanche] {phase} t={Elapsed:F2}s injected={_snow.InjectedVolume:F3}m3 outflow={_snow.OutflowVolume:F3}m3 active={_snow.ActiveVolume:F3}m3 deposited={_snow.DepositedVolume:F3}m3 error={_snow.VolumeError:F6}m3 pieces={Construction.Pieces.Count} bonds={Construction.BondCount}", this);
                if (phase == AvalanchePhase.Failed || phase == AvalanchePhase.TechnicalError) LogTerminalDiagnostics();
            }
            if (phase == AvalanchePhase.Cleared) StageClearSession.MarkCleared(Scene.Avalanche);
            Time.timeScale = 0f;
            _avalancheAudio.Stop();
        }

        /// <summary>Logs the last success-gate state and each citizen's risk counters for terminal diagnosis.</summary>
        private void LogTerminalDiagnostics()
        {
            bool allSafe = true;
            foreach (AvalancheCitizen citizen in Citizens) allSafe &= citizen.IsSafe;
            float activeLimit = (_firstVolume + _secondVolume) * _activeVolumeFraction;
            Debug.Log($"[Avalanche] CompletionGate elapsed={Elapsed:F2}s secondReleased={_secondReleased} active={_snow.ActiveVolume:F3}/{activeLimit:F3}m3 allSafe={allSafe} stable={_stableClock:F2}/{_safeObservationSeconds:F2}s grace={_settlementGraceActive}:{_settlementGraceClock:F2}/{_safeObservationSeconds:F2}s sampleClock={_sampleClock:F2}s", this);
            foreach (AvalancheCitizen citizen in Citizens)
                Debug.Log($"[Avalanche] Citizen {citizen.CitizenId} state={citizen.State} safe={citizen.IsSafe} danger={citizen.InDanger} exposure={citizen.Exposure:F1}% depth={citizen.Depth:F3}m maxDepth={citizen.MaxDepth:F3}m speed={citizen.Speed:F2}m/s impact={citizen.ImpactSeconds:F2}s burial={citizen.BurialSeconds:F2}s", citizen);
        }

        /// <summary>Restores the pre-disaster checkpoint or empties the stage and resets all snow and risk histories.</summary>
        public void Retry(bool empty)
        {
            Construction.ResetLayout(empty);
            ResetCitizens();
            _snow.Reset();
            _avalancheAudio.Stop();
            _firstReleased = _secondReleased = false;
            _warningClock = _sampleClock = _stableClock = _settlementGraceClock = Elapsed = 0f;
            _settlementGraceActive = false;
            _preparationClock.Reset();
            ResultMessage = "";
            Attempt++;
            Paused = false;
            Phase = AvalanchePhase.Preparing;
            Time.timeScale = 1f;
        }

        /// <summary>Returns to the shared stage carousel with cleared transient managers.</summary>
        public void ReturnToMenu() { Time.timeScale = 1f; Managers.Clear(); Managers.Scene.Load(Scene.Game); }

        private void OnDestroy()
        {
            _snow?.Dispose();
            if (_terrainMesh != null) Destroy(_terrainMesh);
            if (_snowMaterial != null) Destroy(_snowMaterial);
            if (_rumble != null) Destroy(_rumble);
            Time.timeScale = _previousTimeScale > 0f ? _previousTimeScale : 1f;
        }
    }
}
