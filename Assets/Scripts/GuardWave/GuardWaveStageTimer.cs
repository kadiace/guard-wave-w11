using System;
using UnityEngine;
using UnityEngine.UI;

namespace GuardWave
{
    [Serializable]
    public sealed class GuardWavePaletteEntry
    {
        public string materialId;
        // -1 is unlimited. Zero or greater is the maximum number of active
        // player-placed objects of this material.
        public int placementLimit;
        // True when this palette entry is required before the stage can begin.
        public bool target;
    }

    [Serializable]
    public sealed class GuardWaveStageInfo
    {
        public string sceneName;
        public string disasterName;
        public bool limitPreparationTime;
        public int countdownSeconds;
        // The protected objective is not a palette entry. Its displayed name
        // comes from the material catalog, not hard-coded scene UI text.
        public string targetMaterialId;
        public GuardWavePaletteEntry[] palette;
        public string TargetName { get; private set; }

        /// <summary>자재의 배치 제한을 조회하고 항목이 없으면 무제한을 반환한다.</summary>
        public int GetPlacementLimit(string materialId)
        {
            foreach (GuardWavePaletteEntry entry in palette)
                if (entry.materialId == materialId) return entry.placementLimit;
            return -1;
        }

        /// <summary>해당 항목이 스테이지의 필수 배치 목표인지 반환한다.</summary>
        public bool IsTargetMaterial(string materialId)
        {
            foreach (GuardWavePaletteEntry entry in palette)
                if (entry.target && entry.materialId == materialId) return true;
            return false;
        }

        /// <summary>필수 목표 항목의 남은 배치 수를 조회하여 배치 완료 여부를 반환한다.</summary>
        public bool AreTargetsPlaced(Func<string, int> getRemainingPlacements)
        {
            foreach (GuardWavePaletteEntry entry in palette)
                if (entry.target && getRemainingPlacements(entry.materialId) > 0) return false;
            return true;
        }
    }

    /// <summary>스테이지별 설정에 따라 준비시간을 초기화하고 경과시킨다.</summary>
    public sealed class StagePreparationClock
    {
        private readonly float _duration;
        public bool Enabled { get; }
        public float Remaining { get; private set; }
        public bool Expired => Enabled && Remaining <= 0f;

        /// <summary>공통 설정에서 제한 여부와 준비 시간을 읽는다.</summary>
        public StagePreparationClock(GuardWaveStageInfo stage)
        {
            Enabled = stage.limitPreparationTime;
            _duration = stage.countdownSeconds;
            Reset();
        }

        /// <summary>준비 시간을 설정된 값으로 되돌린다.</summary>
        public void Reset() => Remaining = _duration;

        /// <summary>제한이 활성화된 경우 경과 시간만큼 남은 시간을 줄인다.</summary>
        public void Advance(float deltaTime)
        {
            if (Enabled) Remaining = Mathf.Max(0f, Remaining - deltaTime);
        }
    }

    [Serializable]
    public sealed class GuardWaveStageInfoFile
    {
        public GuardWaveStageInfo[] stages;

        /// <summary>Both the stage timer and the palette use this single stage definition.</summary>
        public static GuardWaveStageInfo LoadForScene(string sceneName)
        {
            TextAsset asset = Resources.Load<TextAsset>("Datas/StageInfo");
            if (asset == null)
                throw new InvalidOperationException("Missing Resources/Datas/StageInfo.json");

            GuardWaveStageInfoFile file = JsonUtility.FromJson<GuardWaveStageInfoFile>(asset.text);
            GuardWaveStageInfo stage = file?.stages == null ? null
                : Array.Find(file.stages, entry => entry != null && entry.sceneName == sceneName);
            if (stage == null || string.IsNullOrWhiteSpace(stage.disasterName) ||
                stage.countdownSeconds <= 0 || stage.palette == null ||
                stage.palette.Length == 0 || stage.palette.Length > 4)
                throw new InvalidOperationException("Invalid stage settings: " + sceneName);

            if (string.IsNullOrWhiteSpace(stage.targetMaterialId))
                throw new InvalidOperationException(
                    "Missing targetMaterialId in StageInfo.json: " + sceneName);
            GuardWaveMaterial targetMaterial = new GuardWaveCatalog().Get(stage.targetMaterialId);
            if (targetMaterial == null)
                throw new InvalidOperationException(
                    "Unknown targetMaterialId in StageInfo.json: " + stage.targetMaterialId);
            stage.TargetName = targetMaterial.name;

            for (int i = 0; i < stage.palette.Length; i++)
            {
                GuardWavePaletteEntry entry = stage.palette[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.materialId) ||
                    entry.placementLimit < -1)
                    throw new InvalidOperationException(
                        "Invalid palette item in StageInfo.json: " + sceneName);
                for (int j = 0; j < i; j++)
                    if (stage.palette[j].materialId == entry.materialId)
                        throw new InvalidOperationException(
                            "Duplicate palette item in StageInfo.json: " + entry.materialId);
            }

            return stage;
        }
    }

    /// <summary>공통 HUD의 재해 이름과 초:센티초 카운트다운을 관리한다.</summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveStageTimer : MonoBehaviour
    {
        private const int WARNING_SECONDS = 10;
        // Small markers every ten seconds until the final ten, then stronger second ticks and three impact beats.
        private const int TenSecondInterval = 10;
        private const float TenSecondPulseDuration = 0.35f;
        private const float WarningPulseDuration = 0.38f;
        private const float ImpactPulseCycleDuration = 0.45f;
        private const int ImpactPulseRepeats = 3;

        private enum PulseKind { None, TenSecond, Warning, Impact }
        private static readonly Color ImpactColor = new Color(1f, 0.08f, 0.08f, 1f);
        private static readonly Color FlashColor = new Color(1f, 0.95f, 0.86f, 1f);
        private static readonly Color WarningColor = new Color(0.95f, 0.17f, 0.17f, 1f);
        private static readonly Color TenSecondHighlightColor = new Color(1f, 0.65f, 0.1f, 1f);

        private GuardWaveGame _game;
        private IStageHudContext _context;
        private bool _wasContextPreparing;
        private bool _wasContextDisasterStarted;
        private int _shownFloodCrestPhase;
        private FogSceneController _fogMode;
        private Text _disasterName;
        private Text _countdown;
        private string _preparationDisasterName;
        private StagePreparationClock _preparationClock;
        private float _remainingSeconds;
        private int _displayedHundredths = -1;
        private bool _disasterStarted;
        private bool _wasAwaitingFailure;
        private bool _wasFinished;
        private bool _initialized;
        private int _defaultFontSize;
        private Vector3 _defaultCountdownScale;
        private float _pulseElapsed;
        private float _pulseDuration;
        private PulseKind _pulseKind;

        /// <summary>씬 이름으로 StageInfo.json을 조회하여 재해 이름과 제한 시간을 초기화한다.</summary>
        public void Initialize(GuardWaveGame game, FogSceneController fogMode)
        {
            _game = game;
            _fogMode = fogMode;
            GuardWaveStageInfo stage = game.StageInfo;

            _disasterName = transform.Find("DisasterName").GetComponent<Text>();
            _countdown = transform.Find("Countdown").GetComponent<Text>();
            // Enlarged digits must not be truncated by the prefab text rectangle.
            _countdown.verticalOverflow = VerticalWrapMode.Overflow;
            _defaultFontSize = _countdown.fontSize;
            _defaultCountdownScale = _countdown.rectTransform.localScale;
            _preparationDisasterName = stage.disasterName + "가 오기까지";
            _disasterName.text = _preparationDisasterName;
            _preparationClock = new StagePreparationClock(stage);
            ResetCountdown();
            _initialized = true;
        }

        /// <summary>씬이 직접 준비시간을 관리하는 스테이지에서도 동일한 카운트다운 UI를 사용한다.</summary>
        public void Initialize(IStageHudContext context)
        {
            _context = context;
            _disasterName = transform.Find("DisasterName").GetComponent<Text>();
            _countdown = transform.Find("Countdown").GetComponent<Text>();
            _countdown.gameObject.SetActive(true);
            _countdown.verticalOverflow = VerticalWrapMode.Overflow;
            _defaultFontSize = _countdown.fontSize;
            _defaultCountdownScale = _countdown.rectTransform.localScale;
            _preparationDisasterName = context.StageInfo.disasterName + "가 오기까지";
            _preparationClock = new StagePreparationClock(context.StageInfo);
            ResetCountdown();
            _wasContextPreparing = context.IsPreparing;
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized) return;
            if (_context != null)
            {
                RefreshContextCountdown();
                return;
            }

            bool awaitingFailure = _game.AwaitingFailure;
            bool finished = _game.IsFinished;
            if ((_wasAwaitingFailure && !awaitingFailure) || (_wasFinished && !finished))
            {
                ResetCountdown();
                _wasAwaitingFailure = awaitingFailure;
                _wasFinished = finished;
                return;
            }
            _wasAwaitingFailure = awaitingFailure;
            _wasFinished = finished;

            if (finished && _fogMode != null && _disasterStarted)
                CompleteFogCountdown();
            if (awaitingFailure || finished || Time.timeScale <= 0f) return;

            // After a successful disaster pass, the next preparation gets a full timer.
            if (_disasterStarted)
            {
                if (_game.IsFloodStage)
                {
                    int phase = _game.FloodCrestPhase;
                    if (phase != _shownFloodCrestPhase)
                    {
                        if (_shownFloodCrestPhase > 0)
                            StartPulse(PulseKind.Impact);
                        _shownFloodCrestPhase = phase;
                        // Do not compare a fresh 30s countdown with the old
                        // 00:00 of the preceding crest.
                        _displayedHundredths = -1;
                    }
                    _disasterName.text = _game.FloodCrestCountdownLabel;
                    _remainingSeconds = _game.FloodCrestSecondsRemaining;
                    RefreshCountdown();
                    AnimateCountdown();
                    return;
                }
                if (_game.IsLavaStage)
                {
                    _remainingSeconds = _game.LavaFlowSecondsRemaining;
                    RefreshCountdown();
                    AnimateCountdown();
                    return;
                }
                if (CanReady) ResetCountdown();
                else
                {
                    // This uses the fog solver's simulation clock, so the
                    // countdown advances twice as fast after simulation t=30.
                    // AwaitingFailure is handled above: the HUD freezes even
                    // while FogSceneController continues simulating.
                    if (_fogMode != null && _fogMode.IsComing)
                    {
                        _remainingSeconds = _fogMode.RemainingSimulationSeconds;
                        RefreshCountdown();
                    }
                    AnimateCountdown();
                }
                return;
            }

            if (!_preparationClock.Enabled) return;
            _preparationClock.Advance(Time.deltaTime);
            _remainingSeconds = _preparationClock.Remaining;
            RefreshCountdown();
            // A rules modal can keep Ready unavailable; trigger once it closes.
            if (_preparationClock.Expired && CanReady) Ready();
            AnimateCountdown();
        }

        /// <summary>
        /// Scene-owned preparation, warning, active disaster and grace intervals
        /// are displayed through the same shared Countdown text and animations.
        /// </summary>
        private void RefreshContextCountdown()
        {
            bool preparing = _context.IsPreparing;
            bool disasterStarted = _context.DisasterStarted;
            if (preparing && !_wasContextPreparing)
                ResetCountdown();

            if (preparing)
            {
                _remainingSeconds = _context.PreparationSecondsRemaining;
                _disasterName.text = _preparationDisasterName;
                RefreshCountdown();
            }
            else
            {
                _disasterName.text = _context.DisasterCountdownLabel;
                _remainingSeconds = Mathf.Max(0f, _context.DisasterCountdownSecondsRemaining);
                RefreshCountdown();

                // Start the same arrival pulse when the actual snow release
                // begins, rather than when Ready starts the five-second warning.
                if (disasterStarted && !_wasContextDisasterStarted &&
                    _pulseKind != PulseKind.Impact)
                    StartPulse(PulseKind.Impact);
            }

            _wasContextPreparing = preparing;
            _wasContextDisasterStarted = disasterStarted;
            if (Time.timeScale > 0f && !_context.Failed && !_context.Finished)
                AnimateCountdown();
        }

        private bool CanReady => _fogMode == null ? _game.CanReady : _fogMode.CanReady;

        /// <summary>준비 완료 버튼과 자연 만료에서 동일한 파도/독안개 시작 경로를 호출한다.</summary>
        public void Ready()
        {
            if (!_initialized || _disasterStarted || !CanReady) return;
            if (_fogMode == null)
            {
                if (_game.IsFloodStage)
                {
                    _game.Ready();
                    if (!_game.WaveHasStarted) return;
                    _disasterStarted = true;
                    _shownFloodCrestPhase = _game.FloodCrestPhase;
                    _disasterName.text = _game.FloodCrestCountdownLabel;
                    _remainingSeconds = _game.FloodCrestSecondsRemaining;
                    _displayedHundredths = -1;
                    _countdown.gameObject.SetActive(true);
                    RefreshCountdown();
                    return;
                }
                _remainingSeconds = 0f;
                if (!_preparationClock.Enabled) _displayedHundredths = -1;
                RefreshCountdown();
                _game.Ready();
                _disasterStarted = true;
                if (_game.IsLavaStage)
                {
                    _disasterName.text = "용암류가 지나가기까지";
                    _remainingSeconds = _game.LavaFlowSecondsRemaining;
                    RefreshCountdown();
                }
                else if (!_preparationClock.Enabled)
                {
                    _disasterName.text = _game.StageInfo.disasterName + " 진행 중";
                    _countdown.gameObject.SetActive(false);
                }
            }
            else
            {
                // Automatic expiry already starts the impact pulse at 00:00.
                // Manual Ready skips that transition: explicitly play the
                // SAME pulse, without restarting one that is already running.
                if (_pulseKind != PulseKind.Impact)
                    StartPulse(PulseKind.Impact);
                _disasterStarted = true;
                _disasterName.text = "독안개 사라지기까지";
                _remainingSeconds = _fogMode.MaximumSimulationDurationSeconds;
                RefreshCountdown();
                _fogMode.Ready();
            }
        }

        /// <summary>
        /// Fog has a 60-second maximum, not a guaranteed 60-second run.
        /// If the density clears early, show 00:00 before the victory popup,
        /// never a misleading countdown such as 00:12.
        /// </summary>
        public void CompleteFogCountdown()
        {
            if (_fogMode == null || !_disasterStarted || _game.AwaitingFailure)
                return;
            _remainingSeconds = 0f;
            // A normal victory is not a second impact. Keep the red label
            // but stop pulsing once the remaining time snaps to zero.
            _pulseKind = PulseKind.None;
            _pulseElapsed = 0f;
            RefreshCountdown();
            ApplyCountdownStyle();
        }

        /// <summary>재해 종료, 실패 복원 또는 게임 재시작 시 해당 스테이지의 준비 시간을 재설정한다.</summary>
        private void ResetCountdown()
        {
            _preparationClock.Reset();
            _remainingSeconds = _preparationClock.Remaining;
            _countdown.gameObject.SetActive(true);
            if (_disasterName != null)
                _disasterName.text = _preparationDisasterName;
            _displayedHundredths = -1;
            _disasterStarted = false;
            _shownFloodCrestPhase = 0;
            _pulseKind = PulseKind.None;
            _pulseElapsed = 0f;
            if (!_preparationClock.Enabled)
            {
                _disasterName.text = "준비 중";
                _countdown.text = "배치를 마치면\n준비 완료를 누르세요";
                _countdown.fontSize = 22;
                _countdown.rectTransform.localScale = _defaultCountdownScale;
                _countdown.color = Color.white;
                return;
            }
            RefreshCountdown();
            // Include the initial boundary: 120s/40s/30s are ten-second marks.
            // A countdown starting at 10s already belongs to the stronger warning.
            if (_displayedHundredths > 0)
            {
                if (_displayedHundredths <= WARNING_SECONDS * 100)
                    StartPulse(PulseKind.Warning);
                else if (_displayedHundredths % (TenSecondInterval * 100) == 0)
                    StartPulse(PulseKind.TenSecond);
            }
            ApplyCountdownStyle();
        }

        /// <summary>10초 전까지는 10초마다 주황색, 10초부터 1초마다 빨간색 펄스를 준다.</summary>
        private void RefreshCountdown()
        {
            int hundredths = Mathf.CeilToInt(_remainingSeconds * 100f);
            if (hundredths == _displayedHundredths) return;

            int previousHundredths = _displayedHundredths;
            _displayedHundredths = hundredths;
            _countdown.text = $"{hundredths / 100:00}:{hundredths % 100:00}";

            // Once the fog arrives, keep only the arrival impact animation.
            // Countdown ticks must not replace it with ten-second pulses.
            if (previousHundredths >= 0 && !(_fogMode != null && _disasterStarted))
            {
                if (hundredths == 0 && previousHundredths > 0)
                {
                    StartPulse(PulseKind.Impact);
                }
                else if (hundredths > 0)
                {
                    // Use crossed boundaries so lower framerates do not skip the pulse
                    // when a frame jumps past a value ending in :00.
                    int interval = TenSecondInterval * 100;
                    bool crossedTenSecondMark =
                        (hundredths + interval - 1) / interval <
                        (previousHundredths + interval - 1) / interval;
                    bool crossedWholeSecond =
                        (hundredths + 99) / 100 != (previousHundredths + 99) / 100;
                    bool finalTenSeconds = hundredths <= WARNING_SECONDS * 100;

                    // The 10:00 boundary is a red warning, not an orange marker.
                    if (finalTenSeconds && crossedWholeSecond)
                        StartPulse(PulseKind.Warning);
                    else if (!finalTenSeconds && crossedTenSecondMark)
                        StartPulse(PulseKind.TenSecond);
                }
            }
            ApplyCountdownStyle();
        }

        private void StartPulse(PulseKind kind)
        {
            _pulseKind = kind;
            _pulseElapsed = 0f;
            _pulseDuration = kind == PulseKind.Impact
                ? ImpactPulseCycleDuration * ImpactPulseRepeats
                : kind == PulseKind.Warning ? WarningPulseDuration
                : TenSecondPulseDuration;
        }

        /// <summary>게임 시간으로 연출을 진행하여 가이드/ESC 일시정지 중에는 정지한다.</summary>
        private void AnimateCountdown()
        {
            if (_pulseKind == PulseKind.None) return;
            _pulseElapsed = Mathf.Min(_pulseDuration, _pulseElapsed + Time.deltaTime);
            if (_pulseElapsed >= _pulseDuration)
                _pulseKind = PulseKind.None;
            ApplyCountdownStyle();
        }

        private void ApplyCountdownStyle()
        {
            bool fogInProgress = _fogMode != null && _disasterStarted;
            bool expired = _remainingSeconds <= 0f;
            bool finalTenSeconds = _displayedHundredths > 0 &&
                                   _displayedHundredths <= WARNING_SECONDS * 100;
            _countdown.fontSize = _defaultFontSize +
                (expired || fogInProgress ? 8 : finalTenSeconds ? 5 : 0);

            // Arrival is a persistent red warning in Fog, regardless of the
            // 60:00 countdown replacing the preparation's 00:00.
            Color color = expired || fogInProgress ? ImpactColor :
                _remainingSeconds <= WARNING_SECONDS ? WarningColor : Color.white;
            float scale = 1f;
            if (_pulseKind != PulseKind.None)
            {
                // Impact plays three complete expansion/flash/recovery cycles.
                float t = _pulseKind == PulseKind.Impact
                    ? (_pulseElapsed % ImpactPulseCycleDuration) / ImpactPulseCycleDuration
                    : Mathf.Clamp01(_pulseElapsed / _pulseDuration);
                float blink = Mathf.Sin(Mathf.PI * t);
                float amplitude = _pulseKind == PulseKind.Impact ? 0.30f
                    : _pulseKind == PulseKind.Warning ? 0.16f : 0.12f;
                float flashStrength = _pulseKind == PulseKind.Impact ? 0.90f
                    : _pulseKind == PulseKind.Warning ? 0.75f : 1f;
                Color flashColor = _pulseKind == PulseKind.TenSecond
                    ? TenSecondHighlightColor : FlashColor;
                scale += amplitude * blink;
                color = Color.Lerp(color, flashColor, blink * blink * flashStrength);
                if (_pulseKind != PulseKind.TenSecond)
                    color.a = 1f - blink * blink *
                        (_pulseKind == PulseKind.Impact ? 0.25f : 0.35f);
            }
            _countdown.rectTransform.localScale = _defaultCountdownScale * scale;
            _countdown.color = color;
        }
    }
}
