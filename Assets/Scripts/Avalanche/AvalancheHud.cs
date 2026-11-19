using System.Collections.Generic;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using GuardWave;

namespace Avalanche
{
    /// <summary>Adapts Avalanche rules to the shared HUD and adds only citizen-specific observations.</summary>
    public sealed class AvalancheHud : MonoBehaviour, IStageHudContext
    {
        private AvalancheStage _stage;
        private GuardWaveHud _view;
        private Text _areas;
        private readonly List<Text> _citizenLabels = new List<Text>();
        private readonly List<RaycastResult> _hits = new List<RaycastResult>();
        private readonly GuardWaveMaterial[] _offers =
        {
            new GuardWaveMaterial { id = "panel", name = "유도 방벽", category = "panel", abilities = new[] { "눈을 막거나 사선으로 유도합니다.\n접합 블록과 받침으로 지지하세요." } },
            new GuardWaveMaterial { id = "sandbag", name = "하중 받침", category = "stone", abilities = new[] { "방벽을 받치고 낮은 틈을 보강합니다." } },
            new GuardWaveMaterial { id = "adhesive", name = "접합 블록", category = "adhesive", abilities = new[] { "지면이나 자재에 한 개씩 설치합니다.\n닿는 자재를 즉시 연결합니다." } }
        };
        public GuardWaveStageInfo StageInfo => _stage.StageInfo;
        public GuardWaveMaterial[] Offers => _offers;
        public int SelectedIndex => (int)_stage.Construction.Tool;
        public bool CanEdit => _stage.CanEdit;
        public bool IsPreparing => _stage.Phase == AvalanchePhase.Introduction ||
            _stage.Phase == AvalanchePhase.Preparing;
        public float PreparationSecondsRemaining => _stage.PreparationLeft;
        public bool DisasterStarted => _stage.Phase == AvalanchePhase.Sliding ||
            _stage.Phase == AvalanchePhase.Assessing ||
            _stage.Phase == AvalanchePhase.Cleared ||
            _stage.Phase == AvalanchePhase.Failed ||
            _stage.Phase == AvalanchePhase.TechnicalError;
        public string DisasterCountdownLabel => _stage.SettlementGraceActive
            ? "안정 확인까지"
            : _stage.Phase == AvalanchePhase.Warning
                ? "눈사태 시작까지" : "눈사태가 지나가기까지";
        public float DisasterCountdownSecondsRemaining => _stage.SettlementGraceActive
            ? _stage.SettlementGraceLeft
            : _stage.Phase == AvalanchePhase.Warning
                ? _stage.WarningLeft : _stage.DisasterSecondsRemaining;
        public bool CanReady => _stage.CanEdit && _stage.HasProtectedCitizens;
        public bool Failed => _stage.Phase == AvalanchePhase.Failed || _stage.Phase == AvalanchePhase.TechnicalError;
        public bool Finished => _stage.Phase == AvalanchePhase.Cleared;
        public bool RulesOpen { get; private set; }
        public string Status => _stage.Phase == AvalanchePhase.Preparing
            ? $"배치를 마치면 준비 완료를 눌러 주세요. 마을에 있는 {_stage.TargetName}들을 지켜 주세요.\n" + _stage.Construction.Hint
            : _stage.Phase == AvalanchePhase.Warning ? "눈사태 경보 · 편집이 잠겼습니다." : $"두 차례의 눈사태와 {_stage.TargetName}의 충돌·매몰 위험을 관찰하세요.";
        public string Result => _stage.ResultMessage;
        public string ResultTitle => _stage.Phase == AvalanchePhase.TechnicalError
            ? "계산 중단 · 배치 복원 가능"
            : _stage.StageInfo.FailureTitle;
        public string Rules => $"마을에 미리 배치된 {_stage.TargetName} {_stage.Citizens.Count}명을 두 차례의 눈사태에서 모두 지키세요.\n"
            + $"{_stage.TargetName}은 옮기거나 회수할 수 없습니다. 주변에 방어 구조물을 설치하세요.\n"
            + "유도 방벽과 하중 받침을 놓고, 접합 블록으로 닿는 자재를 연결하세요.\n"
            + "첫 눈은 경보 직후, 두 번째 눈은 12초에 내려옵니다. 첫 눈이 쌓인 지형은 유지됩니다.\n"
            + $"두 눈이 내린 뒤 {_stage.TargetName} 모두가 안전하고 빠른 눈 흐름이 잦아든 상태를 3초 유지하면 성공합니다. 62초에 안정 확인 중이면 최대 3초 더 관찰합니다.\n"
            + "충돌 노출 1.5초 누적 또는 매몰 2초 연속이면 실패합니다.\n\n"
            + "왼쪽 클릭: 배치 / 자재는 드래그로 연속 배치\nQ/E: 회전 / Ctrl+Z: 되돌리기\n"
            + "우클릭: 선택 취소·회수 / 우클릭 드래그: 카메라 이동\n휠: 확대 / 우클릭 중 휠: 카메라 회전\n"
            + $"{_stage.TargetName} 이름 클릭: 해당 {_stage.TargetName} 보기 / Home: 전체 보기\nEsc: 선택 취소 후 일시정지";

        /// <summary>Uses the shared stage prefab and creates a small scene-specific citizen panel.</summary>
        public void Initialize(AvalancheStage stage)
        {
            _stage = stage;
            _view = Instantiate(Resources.Load<GuardWaveHud>("Prefabs/UIs/GuardWaveHud"));
            _view.Initialize(this);
            RectTransform panel = new GameObject("AvalancheCitizenPanel", typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            panel.SetParent(_view.transform, false);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
            panel.anchoredPosition = new Vector2(18f, -110f);
            panel.sizeDelta = new Vector2(280f, 90f + stage.Citizens.Count * 56f);
            panel.GetComponent<Image>().color = new Color(0.10f, 0.16f, 0.21f, 0.94f);
            _areas = Label(panel, new Vector2(12f, -12f), new Vector2(256f, 70f), $"보호 대상 {_stage.TargetName}");
            for (int i = 0; i < stage.Citizens.Count; i++)
            {
                int row = i;
                Text label = Label(panel, new Vector2(12f, -90f - i * 56f), new Vector2(256f, 52f), "");
                Button button = label.gameObject.AddComponent<Button>();
                button.onClick.AddListener(() => FocusCitizen(row));
                _citizenLabels.Add(label);
            }
        }

        /// <summary>Creates a readable text label in the shared canvas font and coordinate system.</summary>
        private Text Label(Transform parent, Vector2 point, Vector2 size, string text)
        {
            Text label = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)).GetComponent<Text>();
            label.transform.SetParent(parent, false);
            RectTransform rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = point; rect.sizeDelta = size;
            label.font = _stage.Font; label.fontSize = 17; label.color = Color.white; label.text = text;
            return label;
        }

        private void Update()
        {
            if (_stage == null) return;
            _areas.text = $"보호 대상 {_stage.TargetName} {_stage.Citizens.Count}명\n{_stage.TargetName} 이름을 누르면 자세히 봅니다.";
            for (int i = 0; i < _citizenLabels.Count; i++)
            {
                AvalancheCitizen citizen = _stage.Citizens[i];
                _citizenLabels[i].text = $"{citizen.CitizenId} · {citizen.Zone}  {citizen.Depth:0.00}m\n충돌 {citizen.ImpactSeconds:0.0}s / 매몰 {citizen.BurialSeconds:0.0}s";
                _citizenLabels[i].color = citizen.Exposure >= 50f ? new Color(1f, 0.60f, 0.25f) : Color.white;
            }
        }

        /// <summary>Focuses the camera on a placed citizen without changing any risk state.</summary>
        private void FocusCitizen(int index)
        {
            if (index < _stage.Citizens.Count) _stage.ViewCamera.GetComponent<StageCameraController>().Focus(_stage.Citizens[index].transform.position);
        }

        /// <summary>Blocks world editing whenever any shared UI or modal owns the pointer.</summary>
        public bool BlocksPointer(Vector2 screen)
        {
            if (_stage.Phase == AvalanchePhase.Introduction || _stage.IsFinished() || _stage.Paused || RulesOpen) return true;
            if (EventSystem.current == null) return false;
            _hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = screen }, _hits);
            return _hits.Count > 0;
        }

        /// <summary>Returns the inventory of a shared palette entry.</summary>
        public int Remaining(string id) => _stage.Construction.GetRemainingPlacements(id);
        /// <summary>Selects a construction tool by shared card index.</summary>
        public void Select(int index) => _stage.Construction.SelectTool((AvalancheTool)index);
        /// <summary>Commits construction around the fixed scene objectives.</summary>
        public void Ready() => _stage.Ready();
        /// <summary>Restores the last pre-disaster layout.</summary>
        public void Retry() => _stage.Retry(false);
        /// <summary>Returns to the shared stage selection menu.</summary>
        public void ReturnToMenu() => _stage.ReturnToMenu();
        /// <summary>Reflects rule-modal visibility in scene editing and simulation time.</summary>
        public void SetRulesOpen(bool open)
        {
            RulesOpen = open;
            if (!open && _stage.Phase == AvalanchePhase.Introduction) _stage.BeginPreparation();
            else if (!_stage.IsFinished()) Time.timeScale = open ? 0f : 1f;
        }
        /// <summary>Closes the shared rules panel when Escape is consumed.</summary>
        public void CloseRules() => _view.HideRules();
    }
}
