using UnityEngine;

using UnityEngine.InputSystem;

namespace Flood2
{
    public enum Flood2Phase { Introduction, Preparing, Warning, Flooding, Assessing, Cleared, Failed, TechnicalError }

    /// <summary>Scene-owned citizen rescue rules and the two-peak flood, isolated from Flood and Fog.</summary>
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(Flood2Construction), typeof(Flood2Hud))]
    public sealed class Flood2Stage : MonoBehaviour
    {
        [Header("Scene references")]
        [SerializeField] private Camera _viewCamera;
        [SerializeField] private MeshCollider _ground;
        [SerializeField] private MeshFilter _waterSurface;
        [SerializeField] private Flood2Citizen[] _citizens;
        [SerializeField] private Font _font;

        [Header("Construction catalog")]
        [SerializeField] private Material _panelMaterial;
        [SerializeField] private Material _stoneMaterial;
        [SerializeField] private Material _previewMaterial;
        [SerializeField] private Material _lineMaterial;
        [SerializeField] private PhysicsMaterial _friction;

        [Header("Prototype rules")]
        [SerializeField, Min(10f)] private float _preparationSeconds = 120f;
        [SerializeField, Min(1f)] private float _warningSeconds = 5f;
        [SerializeField, Min(0.1f)] private float _firstPeakDepth = 2.6f;
        [SerializeField, Min(0.1f)] private float _secondPeakDepth = 3.4f;
        [SerializeField, Min(20f)] private float _tailObservationSeconds = 25f;
        [SerializeField, Min(60f)] private float _attemptLimitSeconds = 150f;
        private Flood2Water _water;
        private float _warningClock;
        private float _sampleClock;
        private float _stableClock;
        private float _attemptClock;
        private float _previousTimeScale;
        public Camera ViewCamera => _viewCamera;
        public MeshCollider Ground => _ground;
        public Flood2Citizen[] Citizens => _citizens;
        public Font Font => _font;
        public Material PanelMaterial => _panelMaterial;
        public Material StoneMaterial => _stoneMaterial;
        public Material PreviewMaterial => _previewMaterial;
        public Material LineMaterial => _lineMaterial;
        public PhysicsMaterial Friction => _friction;
        public Flood2Construction Construction { get; private set; }
        public Flood2Hud Hud { get; private set; }
        public Flood2Water Water => _water;
        public Flood2Phase Phase { get; private set; } = Flood2Phase.Introduction;
        public bool Paused { get; private set; }
        public bool CanEdit => Phase == Flood2Phase.Preparing && !Paused;
        public float PreparationLeft { get; private set; }
        public float FloodElapsed { get; private set; }
        public float WarningLeft => Mathf.Max(0f, _warningSeconds - _warningClock);
        public string ResultMessage { get; private set; }
        public int Attempt { get; private set; } = 1;

        /// <summary>Assigns authored scene references and dedicated Flood2 assets.</summary>
        public void Configure(Camera view, MeshCollider ground, MeshFilter water, Flood2Citizen[] citizens, Font font,
            Material panel, Material stone, Material preview, Material line, PhysicsMaterial friction)
        {
            _viewCamera = view; _ground = ground; _waterSurface = water; _citizens = citizens; _font = font;
            _panelMaterial = panel; _stoneMaterial = stone; _previewMaterial = preview; _lineMaterial = line; _friction = friction;
        }

        private void Awake()
        {
            _previousTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            Construction = GetComponent<Flood2Construction>();
            Hud = GetComponent<Flood2Hud>();
            _water = new Flood2Water();
            _waterSurface.sharedMesh = _water.Surface;
            Construction.Initialize(this);
            Hud.Initialize(this);
            PreparationLeft = _preparationSeconds;
            foreach (var citizen in _citizens) citizen.ResetState();
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.escapeKey.wasPressedThisFrame && Phase != Flood2Phase.Introduction) TogglePause();
            if (keyboard.enterKey.wasPressedThisFrame)
            {
                if (Phase == Flood2Phase.Introduction) BeginPreparation();
                else if (CanEdit) Ready();
            }
        }

        private void FixedUpdate()
        {
            if (Paused || Phase == Flood2Phase.Introduction || IsFinished()) return;
            float dt = Time.fixedDeltaTime;
            Construction.TickContacts(dt);
            if (Phase == Flood2Phase.Preparing)
            {
                PreparationLeft = Mathf.Max(0f, PreparationLeft - dt);
                if (PreparationLeft <= 0f) Ready();
                return;
            }
            _attemptClock += dt;
            if (Phase == Flood2Phase.Warning)
            {
                _warningClock += dt;
                if (_warningClock >= _warningSeconds) Phase = Flood2Phase.Flooding;
                return;
            }
            FloodElapsed += dt;
            float inlet = InletDepth(FloodElapsed);
            _water.Step(dt, inlet, Construction.Pieces);
            if (!_water.IsValid)
            {
                End(Flood2Phase.TechnicalError, "물 계산이 안정 범위를 벗어났습니다. 시민 실패로 처리하지 않습니다. 배치를 복원해 다시 시도하세요.");
                return;
            }
            if (FloodElapsed >= 32f) Phase = Flood2Phase.Assessing;
            _sampleClock += dt;
            if (_sampleClock >= 0.1f - 0.0001f)
            {
                foreach (var citizen in _citizens) citizen.Observe(_water, _sampleClock);
                bool settled = true;
                foreach (var citizen in _citizens)
                {
                    settled &= !citizen.HasUnsettledWater;
                    if (citizen.State != Flood2CitizenState.RescueRequired) continue;
                    End(Flood2Phase.Failed, $"{citizen.Zone} · {citizen.CitizenId}에게 구조가 필요합니다.\n홍수 {FloodElapsed:0.0}초 / 최대 수심 {citizen.MaxDepth:0.00}m / 현재 유속 {citizen.Speed:0.0}m/s\n위험 노출 {citizen.ExposedSeconds:0.0}초. 물길과 접합 상태를 살펴보세요.");
                    return;
                }
                _stableClock = Phase == Flood2Phase.Assessing && settled ? _stableClock + _sampleClock : 0f;
                _sampleClock = 0f;
            }
            bool allSafe = true;
            foreach (var citizen in _citizens) allSafe &= citizen.SafeSeconds >= 5f && !citizen.InDanger;
            if (Phase == Flood2Phase.Assessing && FloodElapsed >= 32f + _tailObservationSeconds && _stableClock >= 3f && allSafe)
            {
                foreach (var citizen in _citizens) citizen.Protect();
                End(Flood2Phase.Cleared, "두 차례의 홍수가 지나갔습니다.\n여섯 시민 모두 안전합니다. 여러분이 만든 물길이 마을을 지켰습니다.");
            }
            else if (_attemptClock >= _attemptLimitSeconds)
                End(Flood2Phase.Failed, "시민 주변에 잔류 홍수 위험이 남아 있습니다.\n물길이 시민을 지나지 않도록 배치를 수정하세요.");
        }

        /// <summary>Returns the deterministic upstream input, measured from the end of the warning.</summary>
        private float InletDepth(float time)
        {
            if (time < 4f) return Mathf.Lerp(0.2f, _firstPeakDepth, time / 4f);
            if (time < 11f) return _firstPeakDepth;
            if (time < 16f) return Mathf.Lerp(_firstPeakDepth, 0.65f, (time - 11f) / 5f);
            if (time < 18f) return Mathf.Lerp(0.65f, _secondPeakDepth, (time - 16f) / 2f);
            if (time < 24f) return _secondPeakDepth;
            if (time < 32f) return Mathf.Lerp(_secondPeakDepth, 0f, (time - 24f) / 8f);
            return 0f;
        }

        /// <summary>Closes the instructions and starts the finite construction period.</summary>
        public void BeginPreparation()
        {
            if (Phase != Flood2Phase.Introduction) return;
            Phase = Flood2Phase.Preparing;
            Time.timeScale = 1f;
        }

        /// <summary>Finalizes edits and saves the layout before locking tools for the warning.</summary>
        public void Ready()
        {
            if (!CanEdit) return;
            Construction.SaveCheckpoint();
            _warningClock = _attemptClock = 0f;
            Phase = Flood2Phase.Warning;
        }

        /// <summary>Pauses simulation and rule clocks together while leaving the camera and menus usable.</summary>
        public void TogglePause()
        {
            if (IsFinished() || Phase == Flood2Phase.Introduction) return;
            Construction.FinishInput();
            Paused = !Paused;
            Time.timeScale = Paused ? 0f : 1f;
        }

        /// <summary>Reports whether the current attempt has reached a terminal result.</summary>
        public bool IsFinished() => Phase == Flood2Phase.Cleared || Phase == Flood2Phase.Failed || Phase == Flood2Phase.TechnicalError;

        /// <summary>Emits one terminal result and freezes both bodies and danger accumulation.</summary>
        private void End(Flood2Phase phase, string message)
        {
            if (IsFinished()) return;
            Phase = phase;
            ResultMessage = message;
            Time.timeScale = 0f;
        }

        /// <summary>Restores the pre-flood layout or clears it, with fresh water, citizen state and timers.</summary>
        public void Retry(bool empty)
        {
            Construction.ResetLayout(empty);
            _water.Reset();
            foreach (var citizen in _citizens) citizen.ResetState();
            _warningClock = _sampleClock = _stableClock = _attemptClock = FloodElapsed = 0f;
            PreparationLeft = _preparationSeconds;
            ResultMessage = "";
            Attempt++;
            Paused = false;
            Phase = Flood2Phase.Preparing;
            Time.timeScale = 1f;
        }

        /// <summary>Returns to the existing stage-selection scene without altering its stage catalog.</summary>
        public void ReturnToMenu()
        {
            Time.timeScale = 1f;
            Managers.Scene.Load(Scene.Game);
        }

        private void OnDestroy()
        {
            _water?.Dispose();
            Time.timeScale = _previousTimeScale > 0f ? _previousTimeScale : 1f;
        }
    }
}
