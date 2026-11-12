using System;
using UnityEngine;
using UnityEngine.UI;

namespace GuardWave
{
    [Serializable]
    public sealed class GuardWaveStageInfo
    {
        public string sceneName;
        public string disasterName;
        public int countdownSeconds;
    }

    [Serializable]
    public sealed class GuardWaveStageInfoFile
    {
        public GuardWaveStageInfo[] stages;
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
        private FogSceneController _fogMode;
        private Text _disasterName;
        private Text _countdown;
        private float _durationSeconds;
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
            TextAsset asset = Resources.Load<TextAsset>("Datas/StageInfo");
            if (asset == null)
                throw new InvalidOperationException("Missing Resources/Datas/StageInfo.json");

            GuardWaveStageInfoFile file = JsonUtility.FromJson<GuardWaveStageInfoFile>(asset.text);
            GuardWaveStageInfo stage = file?.stages == null ? null
                : Array.Find(file.stages, entry => entry.sceneName == game.gameObject.scene.name);
            if (stage == null || string.IsNullOrWhiteSpace(stage.disasterName) ||
                stage.countdownSeconds <= 0)
                throw new InvalidOperationException(
                    "Invalid stage timer settings: " + game.gameObject.scene.name);

            _disasterName = transform.Find("DisasterName").GetComponent<Text>();
            _countdown = transform.Find("Countdown").GetComponent<Text>();
            // Enlarged digits must not be truncated by the prefab text rectangle.
            _countdown.verticalOverflow = VerticalWrapMode.Overflow;
            _defaultFontSize = _countdown.fontSize;
            _defaultCountdownScale = _countdown.rectTransform.localScale;
            _disasterName.text = stage.disasterName + "가 오기까지";
            _durationSeconds = stage.countdownSeconds;
            ResetCountdown();
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized) return;

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

            if (awaitingFailure || finished || Time.timeScale <= 0f) return;

            // After a successful disaster pass, the next preparation gets a full timer.
            if (_disasterStarted)
            {
                if (CanReady) ResetCountdown();
                else AnimateCountdown();
                return;
            }

            _remainingSeconds = Mathf.Max(0f, _remainingSeconds - Time.deltaTime);
            RefreshCountdown();
            // A rules modal can keep Ready unavailable; trigger once it closes.
            if (_remainingSeconds <= 0f && CanReady) Ready();
            AnimateCountdown();
        }

        private bool CanReady => _fogMode == null ? _game.CanReady : _fogMode.CanReady;

        /// <summary>준비 완료 버튼과 자연 만료에서 동일한 파도/독안개 시작 경로를 호출한다.</summary>
        public void Ready()
        {
            if (!_initialized || _disasterStarted || !CanReady) return;
            _remainingSeconds = 0f;
            RefreshCountdown();
            if (_fogMode == null) _game.Ready();
            else _fogMode.Ready();
            _disasterStarted = true;
        }

        /// <summary>재해 종료, 실패 복원 또는 게임 재시작 시 해당 스테이지의 준비 시간을 재설정한다.</summary>
        private void ResetCountdown()
        {
            _remainingSeconds = _durationSeconds;
            _displayedHundredths = -1;
            _disasterStarted = false;
            _pulseKind = PulseKind.None;
            _pulseElapsed = 0f;
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

            if (previousHundredths >= 0)
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
            bool expired = _remainingSeconds <= 0f;
            bool finalTenSeconds = _displayedHundredths > 0 &&
                                   _displayedHundredths <= WARNING_SECONDS * 100;
            _countdown.fontSize = _defaultFontSize + (expired ? 8 : finalTenSeconds ? 5 : 0);

            Color color = expired ? ImpactColor :
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
