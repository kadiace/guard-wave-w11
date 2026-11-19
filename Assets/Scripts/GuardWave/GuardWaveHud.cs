using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace GuardWave
{
    /// <summary>Connects scene-owned rules to the shared stage UI without depending on a particular disaster solver.</summary>
    public interface IStageHudContext
    {
        GuardWaveStageInfo StageInfo { get; }
        GuardWaveMaterial[] Offers { get; }
        int SelectedIndex { get; }
        bool CanEdit { get; }
        bool CanReady { get; }
        bool Failed { get; }
        bool Finished { get; }
        string Status { get; }
        string Result { get; }
        string ResultTitle { get; }
        string Rules { get; }
        string DisasterLabel { get; }
        /// <summary>Returns the live inventory for a palette entry.</summary>
        int Remaining(string id);
        /// <summary>Selects one palette item.</summary>
        void Select(int index);
        /// <summary>Commits construction and begins the disaster warning.</summary>
        void Ready();
        /// <summary>Restores construction after a failed attempt.</summary>
        void Retry();
        /// <summary>Returns to stage selection.</summary>
        void ReturnToMenu();
        /// <summary>Pauses scene interaction while the rule modal is visible.</summary>
        void SetRulesOpen(bool open);
    }

    /// <summary>원본 2D GuardWave UI 프리팹을 3D 물리 게임의 상태와 연결한다.</summary>
    public sealed class GuardWaveHud : MonoBehaviour
    {
        private readonly Button[] _cards = new Button[5];
        private readonly Text[] _names = new Text[5];
        private readonly Text[] _remainingLabels = new Text[5];
        private readonly Image[] _silhouettes = new Image[5];
        private readonly Image[] _backgrounds = new Image[5];
        private readonly string[] _shownIds = new string[5];
        private readonly Dictionary<string, Sprite> _generatedIcons = new Dictionary<string, Sprite>();

        private GuardWaveGame _game;
        private IStageHudContext _context;
        private Text _contextDisaster;
        private FogSceneController _fogMode;
        private GuardWaveStageTimer _stageTimer;
        private GameObject _fogExposurePanel;
        private RectTransform _fogExposureFill;
        private Image _fogExposureFillImage;
        private Text _fogExposureLabel;
        private Text _detail;
        private Text _stageTitle;
        private Text _stageBody;
        private Text _completeTitle;
        private Text _completeBody;
        private Text _rulesBody;
        private Text _tooltipText;
        private GameObject _tooltip;
        private GameObject _stagePopup;
        private GameObject _completePopup;
        private GameObject _rulesPopup;
        private Button _readyButton;
        private Text _readyLabel;
        private GuardWavePiece _hoveredPiece;
        private int _hoveredCard = -1;
        private const float TooltipMargin = 14f;
        private const float TooltipGap = 16f;

        [Header("Candidate panel slide")]
        [SerializeField, Min(0.05f)] private float candidateSlideDuration = 0.36f;
        [SerializeField]
        private AnimationCurve candidateSlideCurve =
            AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        private RectTransform _candidatePanel;
        private Vector2 _candidatePanelShown;
        private float _candidateSlide;

        /// <summary>게임 인스턴스 및 원본 HUD 프리팹의 버튼과 상태 텍스트를 연결한다.</summary>
        public void Initialize(GuardWaveGame game)
        {
            _game = game;
            _fogMode = game.GetComponent<FogSceneController>();
            _stageTimer = transform.Find("DisasterCountdown").GetComponent<GuardWaveStageTimer>();
            _stageTimer.Initialize(game, _fogMode);
            Transform fogExposure = transform.Find("FogExposurePanel");
            if (fogExposure != null)
            {
                _fogExposurePanel = fogExposure.gameObject;
                _fogExposureFill = fogExposure.Find("ExposureTrack/ExposureFill").GetComponent<RectTransform>();
                _fogExposureFillImage = _fogExposureFill.GetComponent<Image>();
                _fogExposureLabel = fogExposure.Find("ExposureLabel").GetComponent<Text>();
                _fogExposurePanel.SetActive(false);
            }
            _detail = FindText("PlacementDetailPanel/DetailText");
            _stagePopup = transform.Find("StageResultPopup").gameObject;
            _stageTitle = FindText("StageResultPopup/Heading/TitleText");
            _stageBody = FindText("StageResultPopup/Contents/BodyText");
            _completePopup = transform.Find("RunCompletePopup").gameObject;
            _completeTitle = FindText("RunCompletePopup/Heading/TitleText");
            _completeBody = FindText("RunCompletePopup/Contents/BodyText");
            _rulesPopup = transform.Find("RulesPopup").gameObject;
            _rulesBody = FindText("RulesPopup/Contents/BodyText");
            _tooltip = transform.Find("CandidateMaterialTooltip").gameObject;
            _tooltipText = FindText("CandidateMaterialTooltip/CandidateMaterialTooltipText");

            // The original 2D HUD is instantiated into a different game scene.
            // Use an explicit top-left anchor and pivot for predictable placement.
            RectTransform tooltipRect = (RectTransform)_tooltip.transform;
            tooltipRect.anchorMin = new Vector2(0f, 1f);
            tooltipRect.anchorMax = new Vector2(0f, 1f);
            tooltipRect.pivot = new Vector2(0f, 1f);
            tooltipRect.localRotation = Quaternion.identity;
            tooltipRect.localScale = Vector3.one;
            tooltipRect.SetAsLastSibling();

            // Limit text rendering to the panel so lengthy descriptions cannot escape.
            _tooltipText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _tooltipText.verticalOverflow = VerticalWrapMode.Truncate;
            _tooltipText.resizeTextForBestFit = true;
            _tooltipText.resizeTextMinSize = 12;
            _tooltipText.resizeTextMaxSize = 19;

            _candidatePanel = transform.Find("CandidateSelectionRow").GetComponent<RectTransform>();
            if (_game.StageInfo.palette.Length > 4)
                _candidatePanel.sizeDelta += new Vector2(180f, 0f);
            _candidatePanelShown = _candidatePanel.anchoredPosition;

            Transform candidates = transform.Find("CandidateSelectionRow/CandidateRow");
            for (int i = 0; i < _cards.Length; i++)
            {
                int index = i;
                Transform candidate = i < candidates.childCount ? candidates.GetChild(i)
                    : Instantiate(candidates.GetChild(0), candidates);
                if (i >= 4) candidate.name = "Candidate Target";
                _cards[i] = candidate.GetComponent<Button>();
                _names[i] = candidate.Find("MaterialName").GetComponent<Text>();
                _remainingLabels[i] = CreateRemainingLabel(candidate, _names[i].font);
                _silhouettes[i] = candidate.Find("Silhouette").GetComponent<Image>();
                _backgrounds[i] = candidate.GetComponent<Image>();
                _cards[i].onClick.AddListener(() => _game.SelectOffer(index));
                GuardWaveCandidateHover hover = candidate.GetComponent<GuardWaveCandidateHover>();
                if (hover == null)
                    hover = candidate.gameObject.AddComponent<GuardWaveCandidateHover>();
                hover.Initialize(this, index);
            }

            transform.Find("ActiveEffectsPanel").gameObject.SetActive(false);
            Button rulesButton = transform.Find("CandidateSelectionRow/CandidateActions/RulesButton").GetComponent<Button>();
            rulesButton.onClick.AddListener(ShowRules);
            _readyButton = transform.Find("CandidateSelectionRow/CandidateActions/ReadyButton").GetComponent<Button>();
            _readyLabel = _readyButton.transform.Find("Label").GetComponent<Text>();
            _readyButton.onClick.AddListener(_stageTimer.Ready);
            transform.Find("RulesPopup/CloseButton").GetComponent<Button>().onClick.AddListener(HideRules);
            Button confirmButton = transform.Find("StageResultPopup/CloseButton").GetComponent<Button>();
            confirmButton.onClick.AddListener(_game.ConfirmFailure);
            confirmButton.GetComponentInChildren<Text>().text = "확인";
            _completeTitle.text = "스테이지 클리어";
            Button returnButton = transform.Find("RunCompletePopup/CloseButton").GetComponent<Button>();
            returnButton.onClick.AddListener(ReturnToStageSelection);
            returnButton.GetComponentInChildren<Text>().text = "돌아가기";

            _stagePopup.SetActive(false);
            _completePopup.SetActive(false);
            _rulesPopup.SetActive(false);
            _tooltip.SetActive(false);
            _rulesBody.text = $@"진흙을 선택하고 왼쪽 버튼으로 드래그하면 커서 아래 가장 가까운 물체 표면에 풀이 발립니다.
바른 자국에 통나무나 조약돌이 닿으면 부착됩니다.
통나무는 {_game.GetPlacementLimit("log")}개, 조약돌은 {_game.GetPlacementLimit("forest_pebble")}개까지 배치할 수 있으며, 남은 개수는 팔레트에 표시됩니다.
팔레트에서 재료를 선택해 마우스를 누른 채 드래그하면 겹치지 않는 지점마다 연속 배치됩니다.
선택한 재료는 우클릭으로 해제하기 전까지 계속 사용할 수 있습니다.
하단의 준비완료를 누르면 홍수가 밀려옵니다.
깃발은 팔레트 마지막의 금색 목표물입니다. 깃발을 직접 배치해야 준비 완료할 수 있습니다.
깃발을 지키세요! 파도에 깃발이 밀려 절벽 뒤 파괴 경계를 넘으면 방어에 실패합니다.
실패 팝업에서 확인을 누르면 마지막으로 저장된 배치 상태로 돌아갑니다. 배치한 적이 없다면 씬이 재시작됩니다.

우클릭 드래그: 카메라 이동
스크롤: 확대
우클릭 중 스크롤: 카메라 회전
Q/E: 기물 회전 / 우클릭 또는 Esc: 선택 취소
Ctrl+Z: 최근 기물 배치, 깃발 배치 또는 수동 제거 되돌리기

홍수에 밀려 절벽 뒤로 넘어간 기물은 파괴됩니다.
";
            if (_game.IsLavaStage)
                _rulesBody.text = "목표 깃발을 직접 배치해야 준비 완료할 수 있습니다.\n"
                    + "나무집 10개, 돌집 10개, 나무 판자 30개를 사용할 수 있습니다.\n"
                    + "사전 배치된 집도 재고에 포함됩니다. 우클릭으로 회수하면 수량이 반환됩니다.\n"
                    + "준비 완료를 누르면 용암이 -Z 방향으로 60초 동안 흐릅니다.\n"
                    + "용암 입자가 가까워지거나 표면이 용암으로 뒤덮이면 불이 붙고 3초 뒤 파괴됩니다. 깃발이 살아남으면 성공합니다.";
            if (_fogMode != null)
                _rulesBody.text = $"깃발을 직접 배치해야 준비 완료할 수 있습니다.\n건물을 최대 {_game.GetPlacementLimit("building")}개 배치할 수 있으며, 남은 개수는 팔레트에 표시됩니다.\n"
                    + "마우스를 누른 채 드래그하면 건물이 겹치지 않는 지점마다 연속 배치됩니다.\n"
                    + "기물을 배치한 후 준비 완료를 누르면 안개가 상류에서 몰려옵니다.\n"
                    + "안개는 건물과 기물을 돌아 -Z 방향으로 흐르며 옆으로 확산됩니다.\n"
                    + "깃발 주변 독안개 밀도 0.01 이상에서 노출이 시작되며, 시작 후에는 0.001 이상이면 유지됩니다.\n"
                    + "노출이 5초 연속되면 방어에 실패하며, 0.001 미만이면 게이지가 초기화됩니다.\n"
                    + "안개 시뮬레이션은 기본 30초, 밀도 0.001 이상 감지 시 현재 시뮬레이션 시간 +5초로 연장됩니다.\n"
                    + "30초 이후 깃발 밀도가 0.0001 미만이면 조기 종료하고, 60초에는 반드시 종료됩니다.\n"
                    + "30~60초에는 독안개 밀도가 서서히 0으로 감소합니다.\n"
                    + "시뮬레이션 시간 0~30초는 1배속, 30~60초는 2배속입니다.\n"
                    + "실패 팝업에서는 상단 타이머만 멈추고 안개 시뮬레이션은 계속됩니다.\n"
                    + "안개가 지나간 뒤 깃발이 살아 있다면 스테이지를 클리어합니다.\n\n"
                    + "우클릭 드래그: 카메라 이동 / 스크롤: 확대 / Q/E: 기물 회전";
            Refresh();
        }

        private void Update()
        {
            if (_context != null) { RefreshContext(); return; }
            if (_game == null) return;
            AnimateCandidatePanel();
            Refresh();
        }

        /// <summary>Binds scene-provided cards and actions to the same prefab used by the other stages.</summary>
        public void Initialize(IStageHudContext context)
        {
            _context = context;
            _detail = FindText("PlacementDetailPanel/DetailText");
            _stagePopup = transform.Find("StageResultPopup").gameObject;
            _stageTitle = FindText("StageResultPopup/Heading/TitleText");
            _stageBody = FindText("StageResultPopup/Contents/BodyText");
            _completePopup = transform.Find("RunCompletePopup").gameObject;
            _completeTitle = FindText("RunCompletePopup/Heading/TitleText");
            _completeBody = FindText("RunCompletePopup/Contents/BodyText");
            _rulesPopup = transform.Find("RulesPopup").gameObject;
            _rulesBody = FindText("RulesPopup/Contents/BodyText");
            _tooltip = transform.Find("CandidateMaterialTooltip").gameObject;
            _tooltipText = FindText("CandidateMaterialTooltip/CandidateMaterialTooltipText");
            _tooltip.SetActive(false);
            RectTransform tooltipRect = (RectTransform)_tooltip.transform;
            tooltipRect.anchorMin = tooltipRect.anchorMax = tooltipRect.pivot = new Vector2(0f, 1f);
            _tooltipText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _tooltipText.verticalOverflow = VerticalWrapMode.Truncate;
            _tooltipText.resizeTextForBestFit = true;
            _tooltipText.resizeTextMinSize = 12;
            _tooltipText.resizeTextMaxSize = 19;
            transform.Find("ActiveEffectsPanel").gameObject.SetActive(false);
            transform.Find("FogExposurePanel").gameObject.SetActive(false);
            Transform disaster = transform.Find("DisasterCountdown");
            disaster.GetComponent<GuardWaveStageTimer>().enabled = false;
            _contextDisaster = disaster.GetComponentInChildren<Text>();
            _contextDisaster.resizeTextForBestFit = true;
            _contextDisaster.resizeTextMinSize = 12;
            _contextDisaster.resizeTextMaxSize = 24;
            foreach (Text label in disaster.GetComponentsInChildren<Text>()) if (label != _contextDisaster) label.gameObject.SetActive(false);
            Transform candidates = transform.Find("CandidateSelectionRow/CandidateRow");
            for (int i = 0; i < _cards.Length; i++)
            {
                int index = i;
                Transform card = i < candidates.childCount ? candidates.GetChild(i) : Instantiate(candidates.GetChild(0), candidates);
                card.gameObject.SetActive(i < context.Offers.Length);
                _cards[i] = card.GetComponent<Button>();
                _names[i] = card.Find("MaterialName").GetComponent<Text>();
                _remainingLabels[i] = CreateRemainingLabel(card, _names[i].font);
                _remainingLabels[i].gameObject.SetActive(i < context.Offers.Length);
                _silhouettes[i] = card.Find("Silhouette").GetComponent<Image>();
                _backgrounds[i] = card.GetComponent<Image>();
                _cards[i].onClick.RemoveAllListeners();
                _cards[i].onClick.AddListener(() => context.Select(index));
                if (i >= context.Offers.Length) continue;
                _names[i].text = context.Offers[i].name;
                _silhouettes[i].sprite = Icon(context.Offers[i]);
                _silhouettes[i].preserveAspect = true;
                _silhouettes[i].color = Color.white;
                GuardWaveCandidateHover hover = card.GetComponent<GuardWaveCandidateHover>();
                if (hover == null) hover = card.gameObject.AddComponent<GuardWaveCandidateHover>();
                hover.Initialize(this, i);
            }
            _readyButton = transform.Find("CandidateSelectionRow/CandidateActions/ReadyButton").GetComponent<Button>();
            _readyLabel = _readyButton.GetComponentInChildren<Text>();
            _readyButton.onClick.AddListener(() => context.Ready());
            transform.Find("CandidateSelectionRow/CandidateActions/RulesButton").GetComponent<Button>().onClick.AddListener(() => SetContextRules(true));
            transform.Find("RulesPopup/CloseButton").GetComponent<Button>().onClick.AddListener(() => SetContextRules(false));
            _stagePopup.transform.Find("CloseButton").GetComponent<Button>().onClick.AddListener(() => context.Retry());
            Text retryLabel = _stagePopup.transform.Find("CloseButton").GetComponentInChildren<Text>();
            retryLabel.text = "배치 복원 · 다시 시도";
            retryLabel.resizeTextForBestFit = true;
            retryLabel.resizeTextMinSize = 12;
            retryLabel.resizeTextMaxSize = 24;
            _completePopup.transform.Find("CloseButton").GetComponent<Button>().onClick.AddListener(() => context.ReturnToMenu());
            _completePopup.transform.Find("CloseButton").GetComponentInChildren<Text>().text = "돌아가기";
            _rulesBody.text = context.Rules;
            _rulesBody.resizeTextForBestFit = true;
            _rulesBody.resizeTextMinSize = 14;
            _rulesBody.resizeTextMaxSize = 24;
            _completeTitle.text = "스테이지 클리어";
            _stagePopup.SetActive(false); _completePopup.SetActive(false); _rulesPopup.SetActive(false);
            RefreshContext();
        }

        /// <summary>Refreshes shared inventory, selection, readiness and result views from scene-owned state.</summary>
        private void RefreshContext()
        {
            _detail.text = _context.Status;
            _contextDisaster.text = _context.DisasterLabel;
            _readyButton.interactable = _context.CanReady;
            _readyLabel.text = _context.CanReady ? "준비 완료" : _context.CanEdit ? "목표물 배치 필요" : "진행 중";
            for (int i = 0; i < _context.Offers.Length; i++)
            {
                string id = _context.Offers[i].id;
                int remaining = _context.Remaining(id);
                bool target = _context.StageInfo.IsTargetMaterial(id);
                _remainingLabels[i].text = remaining + "/" + _context.StageInfo.GetPlacementLimit(id);
                _names[i].color = target ? new Color(1f, 0.88f, 0.58f) : Color.white;
                _cards[i].interactable = _context.CanEdit && remaining > 0;
                _backgrounds[i].color = target ? new Color(0.54f, 0.35f, 0.14f)
                    : _context.SelectedIndex == i ? new Color(0.29f, 0.46f, 0.57f) : new Color(0.2f, 0.3f, 0.37f);
            }
            _stagePopup.SetActive(_context.Failed);
            _completePopup.SetActive(_context.Finished);
            if (!_context.CanEdit) _tooltip.SetActive(false);
            _stageTitle.text = _context.ResultTitle;
            _stageBody.text = _context.Result;
            _completeBody.text = _context.Result;
        }

        /// <summary>Controls the shared rules modal and lets the scene pause its own simulation.</summary>
        private void SetContextRules(bool open)
        {
            _rulesPopup.SetActive(open);
            _context.SetRulesOpen(open);
        }

        /// <summary>Opens the shared rule modal for a scene-backed HUD context.</summary>
        public void OpenSceneRules() => SetContextRules(true);

        /// <summary>후보가 닫혀 있거나 파도 진행 중에는 선택 패널을 아래로 숨긴다.</summary>
        private void AnimateCandidatePanel()
        {
            if (_candidatePanel == null) return;
            float target = _game.IsPaletteHidden ? 1f : 0f;
            _candidateSlide = Mathf.MoveTowards(_candidateSlide, target,
                Time.unscaledDeltaTime / Mathf.Max(0.05f, candidateSlideDuration));
            float eased = candidateSlideCurve == null
                ? Mathf.SmoothStep(0f, 1f, _candidateSlide)
                : candidateSlideCurve.Evaluate(_candidateSlide);
            float hideDistance = _candidatePanel.rect.height + _candidatePanelShown.y + 32f;
            _candidatePanel.anchoredPosition = _candidatePanelShown
                + Vector2.down * hideDistance * eased;
        }

        private void OnDestroy()
        {
            if (_rulesPopup != null && _rulesPopup.activeSelf) Time.timeScale = 1f;
            foreach (Sprite sprite in _generatedIcons.Values)
            {
                if (sprite == null) continue;
                Texture2D texture = sprite.texture;
                Destroy(sprite);
                if (texture != null) Destroy(texture);
            }
        }

        /// <summary>프리팹의 하위 경로에서 UGUI 텍스트 참조를 찾아 반환한다.</summary>
        private Text FindText(string path) => transform.Find(path).GetComponent<Text>();

        /// <summary>
        /// An overlay on the palette card shows remaining/total while leaving
        /// the existing vertical layout, silhouette and tooltip intact.
        /// </summary>
        private static Text CreateRemainingLabel(Transform candidate, Font font)
        {
            GameObject counter = new GameObject("RemainingCount",
                typeof(RectTransform), typeof(CanvasRenderer),
                typeof(Text), typeof(LayoutElement), typeof(Outline));
            counter.transform.SetParent(candidate, false);
            LayoutElement element = counter.GetComponent<LayoutElement>();
            element.ignoreLayout = true;
            RectTransform rect = counter.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-8f, 6f);
            rect.sizeDelta = new Vector2(86f, 24f);

            Text text = counter.GetComponent<Text>();
            text.font = font;
            text.fontSize = 17;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleRight;
            text.color = Color.white;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            Outline outline = counter.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1f, -1f);
            counter.transform.SetAsLastSibling();
            counter.SetActive(false);
            return text;
        }

        /// <summary>상단 스테이지 정보, 하단 카드, 버튼, 팝업을 현재 게임 상태로 갱신한다.</summary>
        /// <summary>ESC 메뉴가 가이드를 먼저 닫도록 현재 표시 상태를 공개한다.</summary>
        public bool IsRulesOpen => _rulesPopup != null && _rulesPopup.activeSelf;

        private void Refresh()
        {
            // Match the visible countdown to early fog completion before
            // activating the success popup in this same HUD refresh.
            if (_fogMode != null && _game.IsFinished)
                _stageTimer.CompleteFogCountdown();
            RefreshFogExposure();
            if (_game.IsPaletteHidden && _hoveredCard >= 0)
            {
                _hoveredCard = -1;
                _tooltip.SetActive(false);
            }
            bool canReady = _fogMode == null ? _game.CanReady : _fogMode.CanReady;
            _readyButton.interactable = canReady;
            bool incoming = _fogMode == null ? _game.WaveHasStarted : _fogMode.IsComing;
            string readyText = !canReady && incoming ? "오는 중..."
                : !_game.AllTargetsPlaced ? "목표물 배치 필요" : "준비 완료";
            if (_readyLabel.text != readyText)
                _readyLabel.text = readyText;
            _detail.text = (_fogMode == null ? _game.StatusMessage : _fogMode.StatusMessage) ?? "";
            if (_hoveredPiece != null && !_game.IsPlacedPiece(_hoveredPiece))
            {
                _hoveredPiece = null;
                if (_hoveredCard < 0) _tooltip.SetActive(false);
            }
            if (_hoveredPiece != null && _hoveredCard < 0 && _tooltip.activeSelf)
            {
                string description = PieceDescription(_hoveredPiece);
                if (_tooltipText.text != description)
                {
                    Mouse mouse = Mouse.current;
                    ShowDescription(description, mouse == null ? Vector2.zero : mouse.position.ReadValue());
                }
            }

            for (int i = 0; i < _cards.Length; i++)
            {
                GuardWaveMaterial info = _game.CurrentOffers[i];
                if (info == null)
                {
                    // The HUD is shared with Fog, where the fourth offer is absent.
                    if (_cards[i].gameObject.activeSelf)
                        _cards[i].gameObject.SetActive(false);
                    _shownIds[i] = null;
                    _names[i].text = "";
                    _remainingLabels[i].gameObject.SetActive(false);
                    _silhouettes[i].sprite = null;
                    _silhouettes[i].color = Color.clear;
                    _cards[i].interactable = false;
                    _backgrounds[i].color = new Color(0.12f, 0.17f, 0.21f, 1f);
                    continue;
                }
                if (!_cards[i].gameObject.activeSelf)
                    _cards[i].gameObject.SetActive(true);
                if (_shownIds[i] != info.id)
                {
                    _shownIds[i] = info.id;
                    _names[i].text = info.name;
                    _names[i].color = _game.IsTargetMaterial(info.id)
                        ? new Color(1f, 0.88f, 0.58f, 1f) : Color.white;
                    _silhouettes[i].sprite = Icon(info);
                    _silhouettes[i].color = Color.white;
                    _silhouettes[i].preserveAspect = true;
                }
                int limit = _game.GetPlacementLimit(info.id);
                int remaining = limit < 0 ? -1 : _game.GetRemainingPlacements(info.id);
                Text counter = _remainingLabels[i];
                if (limit >= 0)
                {
                    if (!counter.gameObject.activeSelf) counter.gameObject.SetActive(true);
                    string quantity = remaining + "/" + limit;
                    if (counter.text != quantity) counter.text = quantity;
                    counter.color = _game.IsTargetMaterial(info.id)
                        ? new Color(1f, 0.86f, 0.5f, 1f)
                        : remaining == 0
                            ? new Color(1f, 0.48f, 0.43f, 1f) : Color.white;
                }
                else if (counter.gameObject.activeSelf)
                    counter.gameObject.SetActive(false);

                bool exhausted = limit >= 0 && remaining == 0;
                _cards[i].interactable = _game.CanSelect && !exhausted;
                bool target = _game.IsTargetMaterial(info.id);
                _backgrounds[i].color = target
                    ? exhausted
                        ? new Color(0.34f, 0.25f, 0.12f, 1f)
                        : _game.SelectedOffer == i
                            ? new Color(0.85f, 0.55f, 0.18f, 1f)
                            : new Color(0.54f, 0.35f, 0.14f, 1f)
                    : exhausted
                        ? new Color(0.17f, 0.2f, 0.23f, 1f)
                        : _game.SelectedOffer == i
                            ? new Color(0.29f, 0.46f, 0.57f, 1f)
                            : new Color(0.2f, 0.3f, 0.37f, 1f);
            }

            if (!_game.CanShowItemInfo)
            {
                _hoveredPiece = null;
                _hoveredCard = -1;
                _tooltip.SetActive(false);
            }
            _stagePopup.SetActive(_game.AwaitingFailure && !_game.IsFinished);
            // Fog remains active behind victory UI; only Forest pauses physics.
            if (_game.IsFinished && !_completePopup.activeSelf && _fogMode == null)
                Time.timeScale = 0f;
            _completePopup.SetActive(_game.IsFinished);
            if (_game.AwaitingFailure)
            {
                _stageTitle.text = "방어 실패";
                _stageBody.text = _game.StatusMessage ?? "";
            }
            if (_game.IsFinished)
                _completeBody.text = "재해가 지나갔습니다.\n"
                    + "깃발을 지켜냈습니다!\n\n"
                    + "돌아가기를 누르면 스테이지 선택 화면으로 이동합니다.";
        }

        /// <summary>
        /// The prefab-authored bar appears with the first qualifying fog density
        /// sample, tracks continuous exposure, and disappears on contact loss,
        /// disaster completion, or failure.
        /// </summary>
        private void RefreshFogExposure()
        {
            if (_fogExposurePanel == null) return;
            // During a toxic-exposure failure, the full bar must remain above
            // the popup until the player confirms and restores the checkpoint.
            bool visible = _fogMode != null && _fogMode.IsFlagExposed &&
                !_game.IsFinished;
            if (_fogExposurePanel.activeSelf != visible)
                _fogExposurePanel.SetActive(visible);
            if (!visible) return;

            float duration = _fogMode.FlagExposureLimitSeconds;
            float elapsed = Mathf.Clamp(_fogMode.FlagExposureSeconds, 0f, duration);
            float progress = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, duration));
            Vector2 end = _fogExposureFill.anchorMax;
            if (!Mathf.Approximately(end.x, progress))
                _fogExposureFill.anchorMax = new Vector2(progress, end.y);

            // Yellow on first contact, red near failure.
            _fogExposureFillImage.color = Color.Lerp(
                new Color(0.98f, 0.72f, 0.26f, 1f),
                new Color(0.96f, 0.15f, 0.18f, 1f), progress);
            string label = $"독안개 노출  {elapsed:0.0} / {duration:0.0}초";
            if (_fogExposureLabel.text != label)
                _fogExposureLabel.text = label;
        }

        /// <summary>승리 팝업에서 세션 클리어 상태를 보존한 채 Game 씬으로 복귀한다.</summary>
        private void ReturnToStageSelection()
        {
            Time.timeScale = 1f;
            Managers.Clear();
            Managers.Scene.Load(Scene.Game);
        }

        /// <summary>원본의 후보 이미지 영역에 표시할 저해상도 픽셀 실루엣을 생성한다.</summary>
        private Sprite Icon(GuardWaveMaterial info)
        {
            if (_generatedIcons.TryGetValue(info.id, out Sprite sprite)) return sprite;
            const int size = 32;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            Material material = Resources.Load<Material>("Materials/" + info.id + "_Base");
            Color color = info.id == "mud" ? new Color(0.42f, 0.28f, 0.16f, 1f)
                : info.id == "citizen" ? new Color(1f, 0.8f, 0.38f)
                : info.id == "panel" ? new Color(0.8f, 0.57f, 0.29f)
                : info.id == "sandbag" ? new Color(0.68f, 0.61f, 0.40f)
                : info.id == "adhesive" ? new Color(0.98f, 0.72f, 0.22f)
                : info.id == "default_flag" ? new Color(0.99f, 0.76f, 0.29f, 1f)
                : material == null ? Color.gray : material.color;
            Color shadow = new Color(color.r * 0.53f, color.g * 0.53f, color.b * 0.53f, 1f);
            int shape = Shape(info.id, info.category);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x - 15.5f) / 14f;
                    float v = (y - 15.5f) / 14f;
                    bool filled = shape == 10
                        ? (u * u + (v - 0.52f) * (v - 0.52f) < 0.08f || Mathf.Abs(u) < 0.26f && v < 0.25f && v > -0.30f || Mathf.Abs(Mathf.Abs(u) - 0.17f) < 0.10f && v < -0.25f && v > -0.85f)
                        : shape == 11 ? u * u + v * v < 0.55f
                        : shape == 9
                        ? ((Mathf.Abs(u + 0.48f) < 0.12f && v > -0.85f && v < 0.85f) ||
                           (u > -0.4f && u < 0.72f && v > 0.08f && v < 0.7f))
                        : shape == 8
                        ? ((u > -0.48f && u < 0.55f && v > 0.12f && v < 0.53f) ||
                           (u > -0.88f && u <= -0.48f && v > 0.19f && v < 0.46f) ||
                           (u > 0.02f && u < 0.27f && v > -0.78f && v <= 0.12f))
                        : shape == 7
                        ? u * u + v * v < 0.74f + 0.08f * Mathf.Sin(u * 12f) * Mathf.Cos(v * 9f)
                        : shape == 0 ? Mathf.Abs(u) < 0.28f && Mathf.Abs(v) < 0.85f
                        : shape == 1 ? Mathf.Abs(u) < 0.8f && Mathf.Abs(v) < 0.36f
                        : shape == 2 ? (u * u + v * v < 0.78f && u * u + v * v > 0.22f)
                        : shape == 3 ? (u * u + v * v < 0.72f && v > 0.08f) || (Mathf.Abs(u) < 0.14f && v < 0.35f && v > -0.9f)
                        : shape == 4 ? Mathf.Abs(u) < 0.68f && Mathf.Abs(v) < 0.72f
                        : shape == 5 ? Mathf.Abs(u) + Mathf.Abs(v) < 1.15f
                        : Mathf.Abs(u) < 0.66f && v < 0.6f && v > -0.78f;
                    Color pixel = filled ? (x < 15 || y < 15 ? shadow : color) : Color.clear;
                    texture.SetPixel(x, y, pixel);
                }
            }
            texture.Apply(false);
            sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            _generatedIcons.Add(info.id, sprite);
            return sprite;
        }

        /// <summary>재료 ID와 종류에 따라 카드의 간략한 픽셀 실루엣 유형을 결정한다.</summary>
        private static int Shape(string id, string category)
        {
            if (id == "mud") return 7;
            if (id == "default_flag") return 9;
            if (id == "citizen") return 10;
            if (id == "adhesive") return 11;
            if (id == "sandbag") return 1;
            if (id == "tire" || id == "life_ring") return 2;
            if (id == "sunflower" || id == "living_tree" || id == "growing_vine") return 3;
            if (id == "branch" || id == "leafy_branch" || id == "vine") return 0;
            if (id == "shoe" || id == "log" || id == "umbrella") return 1;
            if (id == "pebble" || id == "forest_pebble" ||
                id == "large_stone" || id == "fossil") return 5;
            if (category == "폐기물") return 6;
            return 4;
        }

        /// <summary>원본 규칙 팝업을 표시한다.</summary>
        private void ShowRules()
        {
            if (_rulesPopup.activeSelf || _game.GetComponent<ForestSceneContext>().PauseMenu.IsOpen) return;
            _hoveredPiece = null;
            _hoveredCard = -1;
            _tooltip.SetActive(false);
            _game.SetRulesOpen(true);
            _rulesPopup.SetActive(true);
            Time.timeScale = 0f;
        }

        /// <summary>원본 규칙 팝업을 닫는다.</summary>
        public void HideRules()
        {
            if (_context != null) { SetContextRules(false); return; }
            if (!_rulesPopup.activeSelf) return;
            _rulesPopup.SetActive(false);
            _hoveredPiece = null;
            _hoveredCard = -1;
            _tooltip.SetActive(false);
            _game.SetRulesOpen(_fogMode != null && _fogMode.IsComing);
            Time.timeScale = 1f;
        }

        /// <summary>원본의 후보 자재 설명 형식을 사용해 커서 옆 툴팁을 표시한다.</summary>
        public void ShowTooltip(int index, Vector2 screen)
        {
            if (_context != null)
            {
                if (!_context.CanEdit || index < 0 || index >= _context.Offers.Length) return;
                GuardWaveMaterial offer = _context.Offers[index];
                _hoveredCard = index;
                ShowDescription(offer.name + "\n\n" + string.Join("\n", offer.abilities ?? Array.Empty<string>()), screen);
                return;
            }
            if (!_game.CanShowItemInfo) return;
            GuardWaveMaterial material = _game.CurrentOffers[index];
            if (material == null) return;
            string description = CandidateDescription(material);
            if (description == null) return;
            _hoveredCard = index;
            _hoveredPiece = null;
            ShowDescription(description, screen);
        }

        /// <summary>기물에 물리 레이캐스트가 적중하면 접촉 정보를 표시한다.</summary>
        public void SetHoveredPiece(GuardWavePiece piece, Vector2 screen)
        {
            string description = piece != null && _game.IsPlacedPiece(piece)
                ? PieceDescription(piece) : null;
            if (!_game.CanShowItemInfo || description == null)
                piece = null;

            if (piece != _hoveredPiece)
            {
                _hoveredPiece = piece;
                if (_hoveredCard < 0)
                {
                    if (piece == null) _tooltip.SetActive(false);
                    else ShowDescription(description, screen);
                }
            }
            if (piece != null && _hoveredCard < 0)
            {
                // The UI card exit can hide the shared tooltip after this piece is selected.
                if (!_tooltip.activeSelf) ShowDescription(description, screen);
                else MoveTooltip(screen);
            }
        }

        /// <summary>Shared candidate/piece text for the Forest palette and its objective.</summary>
        private string CandidateDescription(GuardWaveMaterial material)
        {
            if (material == null) return null;
            switch (material.id)
            {
                case "mud":
                case "log":
                case "forest_pebble":
                case "default_flag":
                case "building":
                    return BuildCandidateDetails(material);
                default:
                    return AbilityTooltip(material.id);
            }
        }

        private string PieceDescription(GuardWavePiece piece)
        {
            if (piece == null) return null;
            GuardWaveMaterial material = _game.GetPieceMaterial(piece);
            return material != null ? CandidateDescription(material) : AbilityTooltip(piece.MaterialId);
        }

        /// <summary>접착제와 성장 기물에만 표시할 짧은 툴팁 문구를 반환한다.</summary>
        private static string AbilityTooltip(string materialId)
        {
            if (materialId == "all_purpose_glue") return "접착제";
            if (materialId == "living_tree" || materialId == "bamboo") return "성장한다";
            return null;
        }

        /// <summary>후보 자재의 기존 상세 설명을 구성한다.</summary>
        private static string BuildCandidateDetails(GuardWaveMaterial material)
        {
            string[] abilities = material.abilities == null
                ? Array.Empty<string>()
                : material.abilities.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            string description = abilities.Length == 0 ? "고유 능력 없음" : string.Join("\n", abilities);
            return material.name + "\n\n"
                + description + "\n\n[" + material.category + "]";
        }

        /// <summary>텍스트에 맞게 크기를 제한한 뒤 전체 패널을 화면 안으로 배치한다.</summary>
        private void ShowDescription(string description, Vector2 screen)
        {
            _tooltipText.text = description;
            _tooltip.SetActive(true);
            RectTransform panel = (RectTransform)_tooltip.transform;
            RectTransform canvas = (RectTransform)transform;

            float maxWidth = Mathf.Max(1f, canvas.rect.width - TooltipMargin * 2f);
            float maxHeight = Mathf.Max(1f, canvas.rect.height - TooltipMargin * 2f);
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                Mathf.Min(360f, maxWidth));
            Canvas.ForceUpdateCanvases();
            float preferredHeight = _tooltipText.preferredHeight + 24f;
            panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Min(maxHeight, Mathf.Max(Mathf.Min(64f, maxHeight), preferredHeight)));

            // Layout can change when the tooltip becomes active.
            Canvas.ForceUpdateCanvases();
            MoveTooltip(screen);
        }

        /// <summary>화면 좌표를 Canvas의 좌상단 앵커 좌표로 변환하고 경계를 보장한다.</summary>
        public void MoveTooltip(Vector2 screen)
        {
            if (!_tooltip.activeSelf) return;
            RectTransform tooltip = (RectTransform)_tooltip.transform;
            RectTransform canvas = (RectTransform)transform;
            Canvas uiCanvas = GetComponent<Canvas>();
            Camera eventCamera = uiCanvas != null && uiCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? uiCanvas.worldCamera
                : null;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvas, screen, eventCamera, out Vector2 pointer)) return;

            Rect bounds = canvas.rect;
            float width = tooltip.rect.width;
            float height = tooltip.rect.height;

            // Prefer below-right; flip left or above at the edges.
            float left = pointer.x + TooltipGap;
            if (left + width > bounds.xMax - TooltipMargin)
                left = pointer.x - TooltipGap - width;
            float top = pointer.y - TooltipGap;
            if (top - height < bounds.yMin + TooltipMargin)
                top = pointer.y + TooltipGap + height;

            float minLeft = bounds.xMin + TooltipMargin;
            float maxLeft = Mathf.Max(minLeft, bounds.xMax - TooltipMargin - width);
            float minTop = bounds.yMin + TooltipMargin + height;
            float maxTop = Mathf.Max(minTop, bounds.yMax - TooltipMargin);
            left = Mathf.Clamp(left, minLeft, maxLeft);
            top = Mathf.Clamp(top, minTop, maxTop);

            // Anchors and pivot are (0,1), so convert from Canvas-local to
            // top-left-anchored coordinates before assigning anchoredPosition.
            tooltip.anchoredPosition = new Vector2(left - bounds.xMin, top - bounds.yMax);
        }

        /// <summary>후보에서 포인터가 벗어나면 툴팁 소유권을 해제한다.</summary>
        public void HideTooltip()
        {
            _hoveredCard = -1;
            _tooltip.SetActive(false);
        }
    }

}
