using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace GuardWave
{
    /// <summary>원본 2D GuardWave UI 프리팹을 3D 물리 게임의 상태와 연결한다.</summary>
    public sealed class GuardWaveHud : MonoBehaviour
    {
        private readonly Button[] _cards = new Button[3];
        private readonly Text[] _names = new Text[3];
        private readonly Image[] _silhouettes = new Image[3];
        private readonly Image[] _backgrounds = new Image[3];
        private readonly string[] _shownIds = new string[3];
        private readonly Dictionary<string, Sprite> _generatedIcons = new Dictionary<string, Sprite>();

        private GuardWaveGame _game;
        private Text _stage;
        private Text _durability;
        private Text _target;
        private Text _rerollAction;
        private Text _detail;
        private Text _stageTitle;
        private Text _stageBody;
        private Text _completeBody;
        private Text _rulesBody;
        private Text _tooltipText;
        private GameObject _tooltip;
        private GameObject _stagePopup;
        private GameObject _completePopup;
        private GameObject _rulesPopup;
        private Button _rerollButton;
        private Button _readyButton;
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
        private GuardWaveDropGuideGraphic _dropGuide;

        /// <summary>게임 인스턴스 및 원본 HUD 프리팹의 버튼과 상태 텍스트를 연결한다.</summary>
        public void Initialize(GuardWaveGame game)
        {
            _game = game;
            _stage = FindText("TopBar/StageStat/Value");
            transform.Find("TopBar/RoundStat").gameObject.SetActive(false);
            _durability = FindText("TopBar/DurabilityStat/Value");
            _target = FindText("TopBar/TargetStat/Value");
            _detail = FindText("PlacementDetailPanel/DetailText");
            _stagePopup = transform.Find("StageResultPopup").gameObject;
            _stageTitle = FindText("StageResultPopup/Heading/TitleText");
            _stageBody = FindText("StageResultPopup/Contents/BodyText");
            _completePopup = transform.Find("RunCompletePopup").gameObject;
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
            _candidatePanelShown = _candidatePanel.anchoredPosition;

            _dropGuide = transform.Find("Drop Prediction Dashed Line")
                .GetComponent<GuardWaveDropGuideGraphic>();
            _dropGuide.gameObject.SetActive(false);

            Transform candidates = transform.Find("CandidateSelectionRow/CandidateRow");
            for (int i = 0; i < _cards.Length; i++)
            {
                int index = i;
                Transform candidate = candidates.GetChild(i);
                _cards[i] = candidate.GetComponent<Button>();
                _names[i] = candidate.Find("MaterialName").GetComponent<Text>();
                _silhouettes[i] = candidate.Find("Silhouette").GetComponent<Image>();
                _backgrounds[i] = candidate.GetComponent<Image>();
                _cards[i].onClick.AddListener(() => _game.SelectOffer(index));
                GuardWaveCandidateHover hover = candidate.GetComponent<GuardWaveCandidateHover>();
                hover.Initialize(this, index);
            }

            _rerollButton = transform.Find("CandidateSelectionRow/CandidateActions/RerollActionButton").GetComponent<Button>();
            _rerollAction = _rerollButton.GetComponentInChildren<Text>();
            transform.Find("CandidateSelectionRow/CandidateActions").gameObject.SetActive(false);
            transform.Find("TopBar/RerollStat").gameObject.SetActive(false);
            transform.Find("ActiveEffectsPanel").gameObject.SetActive(false);

            // The 2D manual removal action was replaced by the barrel explosion in 3D.
            transform.Find("TopBar/BombStat").gameObject.SetActive(false);
            transform.Find("CandidateSelectionRow/CandidateActions/BombActionButton").gameObject.SetActive(false);
            Button rulesButton = transform.Find("TopBar/RulesButton").GetComponent<Button>();
            rulesButton.onClick.AddListener(ShowRules);
            _readyButton = transform.Find("TopBar/ReadyButton").GetComponent<Button>();
            _readyButton.onClick.AddListener(_game.Ready);
            transform.Find("RulesPopup/CloseButton").GetComponent<Button>().onClick.AddListener(HideRules);
            Button confirmButton = transform.Find("StageResultPopup/CloseButton").GetComponent<Button>();
            confirmButton.onClick.AddListener(_game.ConfirmFailure);
            confirmButton.GetComponentInChildren<Text>().text = "확인";
            transform.Find("RunCompletePopup/CloseButton").GetComponent<Button>().onClick.AddListener(_game.Restart);

            _stagePopup.SetActive(false);
            _completePopup.SetActive(false);
            _rulesPopup.SetActive(false);
            _tooltip.SetActive(false);
            _rulesBody.text = @"고무오리, 쓰레기 봉투, 큰 돌 중 원하는 기물을 선택해 무제한 배치하세요.
선택한 기물은 우클릭 또는 Esc로 해제하기 전까지 계속 배치할 수 있습니다.
우측 상단의 준비완료를 누르면 홍수가 밀려옵니다.
깃발을 지키세요! 파도에 깃발이 밀려 절벽 뒤 파괴 경계를 넘으면 방어에 실패합니다.
실패 팝업의 확인을 누르면 준비완료를 누르기 직전의 배치로 돌아갑니다.

우클릭 드래그: 카메라 이동
스크롤: 확대
우클릭 중 스크롤: 카메라 회전
Q/E: 기물 회전 / 우클릭 또는 Esc: 선택 취소

홍수에 밀려 절벽 뒤로 넘어간 기물은 파괴됩니다.
";
            Refresh();
        }

        private void Update()
        {
            if (_game == null) return;
            AnimateCandidatePanel();
            Refresh();
        }

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

        /// <summary>실제 낙하 시작점과 현재 미리보기의 화면 좌표를 실시간 점선으로 연결한다.</summary>
        public void ShowDropGuide(Vector2 launchScreen, Vector2 landingScreen)
        {
            if (_dropGuide == null) return;
            if (!_dropGuide.gameObject.activeSelf) _dropGuide.gameObject.SetActive(true);
            _dropGuide.SetScreenEndpoints(launchScreen, landingScreen);
        }

        public void HideDropGuide()
        {
            if (_dropGuide != null && _dropGuide.gameObject.activeSelf)
                _dropGuide.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
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

        /// <summary>상단 스테이지 정보, 하단 카드, 버튼, 팝업을 현재 게임 상태로 갱신한다.</summary>
        private void Refresh()
        {
            _stage.text = "STAGE " + _game.StageNumber + " / " + _game.StageCount;
            _durability.transform.parent.gameObject.SetActive(false);
            _target.transform.parent.gameObject.SetActive(false);
            _rerollAction.text = "리롤\n" + _game.RerollCount;
            if (_game.IsPaletteHidden && _hoveredCard >= 0)
            {
                _hoveredCard = -1;
                _tooltip.SetActive(false);
            }
            _rerollButton.interactable = _game.CanReroll && _game.RerollCount > 0;
            _readyButton.interactable = _game.CanReady;
            _detail.text = _game.StatusMessage ?? "";
            if (_hoveredPiece != null && !_game.IsPlacedPiece(_hoveredPiece))
            {
                _hoveredPiece = null;
                if (_hoveredCard < 0) _tooltip.SetActive(false);
            }
            if (_hoveredPiece != null && _hoveredCard < 0 && _tooltip.activeSelf)
            {
                string description = AbilityTooltip(_hoveredPiece.MaterialId);
                if (_tooltipText.text != description)
                {
                    Mouse mouse = Mouse.current;
                    ShowDescription(description, mouse == null ? Vector2.zero : mouse.position.ReadValue());
                }
            }

            for (int i = 0; i < _cards.Length; i++)
            {
                GuardWaveMaterial info = _game.CurrentOffers[i];
                if (info == null) continue;
                if (_shownIds[i] != info.id)
                {
                    _shownIds[i] = info.id;
                    _names[i].text = info.name;
                    _silhouettes[i].sprite = Icon(info);
                    _silhouettes[i].color = Color.white;
                    _silhouettes[i].preserveAspect = true;
                }
                _cards[i].interactable = _game.CanSelect;
                _backgrounds[i].color = _game.SelectedOffer == i
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
            _completePopup.SetActive(_game.IsFinished);
            if (_game.AwaitingFailure)
            {
                _stageTitle.text = "방어 실패";
                _stageBody.text = _game.StatusMessage ?? "";
            }
            if (_game.IsFinished)
                _completeBody.text = "자재 " + _game.PieceCount + "개가 남았습니다.\n"
                    + "닫기 버튼을 누르면 다시 시작합니다.";
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
            Color color = material == null ? Color.gray : material.color;
            Color shadow = new Color(color.r * 0.53f, color.g * 0.53f, color.b * 0.53f, 1f);
            int shape = Shape(info.id, info.category);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x - 15.5f) / 14f;
                    float v = (y - 15.5f) / 14f;
                    bool filled = shape == 0 ? Mathf.Abs(u) < 0.28f && Mathf.Abs(v) < 0.85f
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
            if (id == "tire" || id == "life_ring") return 2;
            if (id == "sunflower" || id == "living_tree" || id == "growing_vine") return 3;
            if (id == "branch" || id == "leafy_branch" || id == "vine") return 0;
            if (id == "shoe" || id == "log" || id == "umbrella") return 1;
            if (id == "pebble" || id == "large_stone" || id == "fossil") return 5;
            if (category == "폐기물") return 6;
            return 4;
        }

        /// <summary>원본 규칙 팝업을 표시한다.</summary>
        private void ShowRules()
        {
            _hoveredPiece = null;
            _hoveredCard = -1;
            _tooltip.SetActive(false);
            _game.SetRulesOpen(true);
            _rulesPopup.SetActive(true);
        }

        /// <summary>원본 규칙 팝업을 닫는다.</summary>
        private void HideRules()
        {
            _rulesPopup.SetActive(false);
            _hoveredPiece = null;
            _hoveredCard = -1;
            _tooltip.SetActive(false);
            _game.SetRulesOpen(false);
        }

        /// <summary>원본의 후보 자재 설명 형식을 사용해 커서 옆 툴팁을 표시한다.</summary>
        public void ShowTooltip(int index, Vector2 screen)
        {
            if (!_game.CanShowItemInfo) return;
            GuardWaveMaterial material = _game.CurrentOffers[index];
            if (material == null) return;
            string description = AbilityTooltip(material.id);
            if (description == null) return;
            _hoveredCard = index;
            _hoveredPiece = null;
            ShowDescription(description, screen);
        }

        /// <summary>기물에 물리 레이캐스트가 적중하면 접촉 정보를 표시한다.</summary>
        public void SetHoveredPiece(GuardWavePiece piece, Vector2 screen)
        {
            if (!_game.CanShowItemInfo || !_game.IsPlacedPiece(piece) ||
                AbilityTooltip(piece.MaterialId) == null)
                piece = null;

            if (piece != _hoveredPiece)
            {
                _hoveredPiece = piece;
                if (_hoveredCard < 0)
                {
                    if (piece == null) _tooltip.SetActive(false);
                    else ShowDescription(AbilityTooltip(piece.MaterialId), screen);
                }
            }
            if (piece != null && _hoveredCard < 0)
            {
                // The UI card exit can hide the shared tooltip after this piece is selected.
                if (!_tooltip.activeSelf) ShowDescription(AbilityTooltip(piece.MaterialId), screen);
                else MoveTooltip(screen);
            }
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
