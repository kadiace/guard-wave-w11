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
        private GuardWaveGame _game;
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
        private const float TooltipMargin = 14f;
        private const float TooltipGap = 16f;

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

            // Only Ready and Rules remain interactive; remove all candidate cards.
            transform.Find("CandidateSelectionRow/CandidateRow").gameObject.SetActive(false);

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
            _rulesBody.text =
                "씬의 기물과 목표 깃발은 시작부터 배치되어 있습니다.\n"
                + "준비 완료를 누르면 재해가 시작됩니다. 깃발을 지켜내세요.\n"
                + "실패 팝업의 확인을 누르면 사전 배치 상태로 복원합니다.\n\n"
                + "우클릭 드래그: 카메라 이동 / 스크롤: 확대\n"
                + "우클릭 중 스크롤: 카메라 회전 / R: 재시작";
            if (_game.IsLavaStage)
                _rulesBody.text += "\n용암에 닿은 기물은 불타다가 파괴됩니다.";
            if (_fogMode != null)
                _rulesBody.text += "\n독안개에 5초 이상 노출되면 방어에 실패합니다.";

            Refresh();
        }

        private void Update()
        {
            if (_game == null) return;
            Refresh();
        }

        /// <summary>후보가 닫혀 있거나 파도 진행 중에는 선택 패널을 아래로 숨긴다.</summary>


        /// <summary>실제 낙하 시작점과 현재 미리보기의 화면 좌표를 실시간 점선으로 연결한다.</summary>




        private void OnDestroy()
        {
            if (_rulesPopup != null && _rulesPopup.activeSelf) Time.timeScale = 1f;
        }

        /// <summary>프리팹의 하위 경로에서 UGUI 텍스트 참조를 찾아 반환한다.</summary>
        private Text FindText(string path) => transform.Find(path).GetComponent<Text>();

        /// <summary>
        /// An overlay on the palette card shows remaining/total while leaving
        /// the existing vertical layout, silhouette and tooltip intact.
        /// </summary>


        /// <summary>상단 스테이지 정보, 하단 카드, 버튼, 팝업을 현재 게임 상태로 갱신한다.</summary>
        /// <summary>ESC 메뉴가 가이드를 먼저 닫도록 현재 표시 상태를 공개한다.</summary>
        public bool IsRulesOpen => _rulesPopup != null && _rulesPopup.activeSelf;

        private void Refresh()
        {
            if (_fogMode != null && _game.IsFinished)
                _stageTimer.CompleteFogCountdown();
            RefreshFogExposure();
            bool canReady = _fogMode == null ? _game.CanReady : _fogMode.CanReady;
            _readyButton.interactable = canReady;
            bool incoming = _fogMode == null ? _game.WaveHasStarted : _fogMode.IsComing;
            string readyText = !canReady && incoming ? "오는 중..." : "준비 완료";
            if (_readyLabel.text != readyText) _readyLabel.text = readyText;
            _detail.text = (_fogMode == null ? _game.StatusMessage : _fogMode.StatusMessage) ?? "";
            if (_hoveredPiece != null && !_game.IsPlacedPiece(_hoveredPiece))
            {
                _hoveredPiece = null;
                _tooltip.SetActive(false);
            }
            if (_hoveredPiece != null && _tooltip.activeSelf)
            {
                string description = PieceDescription(_hoveredPiece);
                if (_tooltipText.text != description)
                {
                    Mouse mouse = Mouse.current;
                    ShowDescription(description,
                        mouse == null ? Vector2.zero : mouse.position.ReadValue());
                }
            }
            if (!_game.CanShowItemInfo)
            {
                _hoveredPiece = null;
                _tooltip.SetActive(false);
            }
            _stagePopup.SetActive(_game.AwaitingFailure && !_game.IsFinished);
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
            // Fog and Flood use the same authored progress track. A completed
            // exposure failure leaves its bar full until the player confirms.
            bool fogExposure = _fogMode != null && _fogMode.IsFlagExposed;
            bool floodDrowning = _fogMode == null && _game.IsFloodDrowning;
            bool visible = (fogExposure || floodDrowning) && !_game.IsFinished;
            if (_fogExposurePanel.activeSelf != visible)
                _fogExposurePanel.SetActive(visible);
            if (!visible) return;

            float duration = _fogMode != null ? _fogMode.FlagExposureLimitSeconds
                : _game.FloodDrowningLimitSeconds;
            float elapsed = Mathf.Clamp(_fogMode != null
                ? _fogMode.FlagExposureSeconds : _game.FloodDrowningSeconds, 0f, duration);
            float progress = Mathf.Clamp01(elapsed / Mathf.Max(0.01f, duration));
            Vector2 end = _fogExposureFill.anchorMax;
            if (!Mathf.Approximately(end.x, progress))
                _fogExposureFill.anchorMax = new Vector2(progress, end.y);

            // Yellow on first contact, red near failure.
            _fogExposureFillImage.color = Color.Lerp(
                new Color(0.98f, 0.72f, 0.26f, 1f),
                new Color(0.96f, 0.15f, 0.18f, 1f), progress);
            string label = _fogMode != null
                ? $"독안개 노출  {elapsed:0.0} / {duration:0.0}초"
                : $"깃발 익사  {elapsed:0.0} / {duration:0.0}초";
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


        /// <summary>재료 ID와 종류에 따라 카드의 간략한 픽셀 실루엣 유형을 결정한다.</summary>


        /// <summary>원본 규칙 팝업을 표시한다.</summary>
        private void ShowRules()
        {
            if (_rulesPopup.activeSelf || _game.GetComponent<ForestSceneContext>().PauseMenu.IsOpen) return;
            _hoveredPiece = null;
            _tooltip.SetActive(false);
            _game.SetRulesOpen(true);
            _rulesPopup.SetActive(true);
            Time.timeScale = 0f;
        }

        /// <summary>원본 규칙 팝업을 닫는다.</summary>
        public void HideRules()
        {
            if (!_rulesPopup.activeSelf) return;
            _rulesPopup.SetActive(false);
            _hoveredPiece = null;
            _tooltip.SetActive(false);
            _game.SetRulesOpen(_fogMode != null && _fogMode.IsComing);
            Time.timeScale = 1f;
        }

        /// <summary>원본의 후보 자재 설명 형식을 사용해 커서 옆 툴팁을 표시한다.</summary>


        /// <summary>기물에 물리 레이캐스트가 적중하면 접촉 정보를 표시한다.</summary>
        public void SetHoveredPiece(GuardWavePiece piece, Vector2 screen)
        {
            string description = piece != null && _game.IsPlacedPiece(piece)
                ? PieceDescription(piece) : null;
            if (!_game.CanShowItemInfo || description == null) piece = null;
            if (piece != _hoveredPiece)
            {
                _hoveredPiece = piece;
                if (piece == null) _tooltip.SetActive(false);
                else ShowDescription(description, screen);
            }
            if (piece != null)
            {
                if (!_tooltip.activeSelf) ShowDescription(description, screen);
                else MoveTooltip(screen);
            }
        }

        /// <summary>Shared candidate/piece text for the Forest palette and its objective.</summary>


        private string PieceDescription(GuardWavePiece piece)
        {
            if (piece == null) return null;
            GuardWaveMaterial material = _game.GetPieceMaterial(piece);
            return material != null ? BuildCandidateDetails(material) : AbilityTooltip(piece.MaterialId);
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

    }

}
