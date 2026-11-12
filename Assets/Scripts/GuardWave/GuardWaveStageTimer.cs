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
        private static readonly Color WarningColor = new Color(0.95f, 0.17f, 0.17f, 1f);

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
                return;
            }

            _remainingSeconds = Mathf.Max(0f, _remainingSeconds - Time.deltaTime);
            RefreshCountdown();
            // A rules modal can keep Ready unavailable; trigger once it closes.
            if (_remainingSeconds <= 0f && CanReady) Ready();
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

        /// <summary>재해 종료, 실패 복원 또는 게임 재시작 시 30초 준비 시간을 재설정한다.</summary>
        private void ResetCountdown()
        {
            _remainingSeconds = _durationSeconds;
            _displayedHundredths = -1;
            _disasterStarted = false;
            RefreshCountdown();
        }

        /// <summary>남은 시간을 SS:CC로 표시하고 10초 이하이면 빨간색으로 바꾼다.</summary>
        private void RefreshCountdown()
        {
            int hundredths = Mathf.CeilToInt(_remainingSeconds * 100f);
            if (hundredths == _displayedHundredths) return;
            _displayedHundredths = hundredths;
            _countdown.text = $"{hundredths / 100:00}:{hundredths % 100:00}";
            _countdown.color = _remainingSeconds <= WARNING_SECONDS ? WarningColor : Color.white;
        }
    }
}
