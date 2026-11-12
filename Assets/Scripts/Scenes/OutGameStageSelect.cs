using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Menu-only stage carousel, authored in Game.unity. The menu is intentionally independent
/// of Forest and Fog simulation systems; it changes scenes through SceneManagerEx.
/// </summary>
[DisallowMultipleComponent]
public sealed class OutGameStageSelect : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    [Header("Poster cards: Forest, Fog")]
    [SerializeField] private RectTransform[] _posters;
    [SerializeField] private CanvasGroup[] _posterOpacity;
    [SerializeField] private Image[] _posterFrames;
    [SerializeField] private Button[] _posterButtons;

    [Header("Navigation")]
    [SerializeField] private Button _previousButton;
    [SerializeField] private Button _nextButton;
    [SerializeField] private Button _playButton;

    [Header("Stage information")]
    [SerializeField] private Text _countLabel;
    [SerializeField] private Text _titleLabel;
    [SerializeField] private Text _descriptionLabel;
    [SerializeField] private Text _playLabel;
    [SerializeField] private Text _helpLabel;

    [Header("Motion")]
    [SerializeField, Min(200f)] private float _posterSpacing = 460f;
    [SerializeField, Range(0.5f, 1f)] private float _neighborScale = 0.74f;
    [SerializeField, Range(0.5f, 0.8f)] private float _neighborAlpha = 0.65f;
    [SerializeField, Min(0.1f)] private float _transitionSpeed = 10f;
    [SerializeField, Min(20f)] private float _swipeThreshold = 65f;

    private static readonly Scene[] Destinations = { Scene.Forest, Scene.Fog };
    private static readonly string[] Names = { "THE LAST DAM", "GREEN SILENCE" };
    private static readonly string[] Descriptions =
    {
        "FOREST  /  BUILD A DAM. HOLD THE FLOOD.",
        "FOG  /  SURVIVE THE ROLLING TOXIC CLOUD."
    };

    private readonly Color _activeFrame = new Color(0.95f, 0.78f, 0.50f, 1f);
    private readonly Color _inactiveFrame = new Color(0.38f, 0.46f, 0.43f, 1f);
    private readonly Color _nominatedColor = new Color(0.62f, 0.66f, 0.68f, 1f);
    private readonly Color _awardedColor = new Color(0.98f, 0.78f, 0.43f, 1f);
    private RectTransform[] _awardRoots;
    private Image[] _awardImages;
    private Text[] _awardLabels;
    private int _selected;
    private float _animatedIndex;
    private float _ignoreClickUntil;
    private float _scrollCooldownUntil;
    private bool _loading;
    private bool _dragging;
    private Vector2 _dragStartLocal;
    private Vector2 _dragCurrentLocal;

    private void Awake()
    {
        if (_posters == null || _posters.Length != Destinations.Length ||
            _posterButtons == null || _posterButtons.Length != Destinations.Length ||
            _posterOpacity == null || _posterOpacity.Length != Destinations.Length ||
            _posterFrames == null || _posterFrames.Length != Destinations.Length)
        {
            Debug.LogError("[OutGame] Missing poster scene references.", this);
            enabled = false;
            return;
        }

        Scene recentStage = StageClearSession.RecentlyCleared;
        for (int i = 0; i < Destinations.Length; i++)
        {
            if (Destinations[i] == recentStage) _selected = i;
        }
        _animatedIndex = _selected;
        CreateAwardBadges();

        for (int i = 0; i < _posterButtons.Length; i++)
        {
            int index = i;
            _posterButtons[i].onClick.AddListener(() => OnPosterClicked(index));
        }
        _posters[_selected].SetAsLastSibling();

        _previousButton.onClick.AddListener(SelectPrevious);
        _nextButton.onClick.AddListener(SelectNext);
        _playButton.onClick.AddListener(LaunchSelectedStage);
        ShowSelectedStage();
        ApplyPosterPoses();
    }

    private void Start()
    {
        // Managers owns the reusable EventSystem prefab and must remain a runtime system.
        Managers.Init();
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (StageClearSession.RecentlyCleared != Scene.Unknown)
            StartCoroutine(RevealRecentAward());
    }

    private void Update()
    {
        if (_loading) return;

        Keyboard keyboard = Keyboard.current;
        Gamepad gamepad = Gamepad.current;
        if ((keyboard != null && (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame)) ||
            (gamepad != null && gamepad.dpad.left.wasPressedThisFrame))
            Select(_selected - 1);
        if ((keyboard != null && (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame)) ||
            (gamepad != null && gamepad.dpad.right.wasPressedThisFrame))
            Select(_selected + 1);

        if ((keyboard != null && (keyboard.enterKey.wasPressedThisFrame ||
                                  keyboard.numpadEnterKey.wasPressedThisFrame ||
                                  keyboard.spaceKey.wasPressedThisFrame)) ||
            (gamepad != null && gamepad.buttonSouth.wasPressedThisFrame))
            LaunchSelectedStage();

        Mouse mouse = Mouse.current;
        if (mouse != null && Time.unscaledTime >= _scrollCooldownUntil)
        {
            float wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.1f)
            {
                Select(_selected + (wheel < 0f ? 1 : -1));
                _scrollCooldownUntil = Time.unscaledTime + 0.22f;
            }
        }

        // During a drag, the carousel follows the pointer instead of lerping to a stage.
        if (_dragging) return;

        // Unscaled time keeps the menu responsive if arriving from a paused game.
        float blend = 1f - Mathf.Exp(-_transitionSpeed * Time.unscaledDeltaTime);
        _animatedIndex = Mathf.Lerp(_animatedIndex, _selected, blend);
        if (Mathf.Abs(_animatedIndex - _selected) < 0.0005f) _animatedIndex = _selected;
        ApplyPosterPoses();
    }

    /// <summary>포스터 우하단에 회색 후보/금색 수상 배지를 배치한다.</summary>
    private void CreateAwardBadges()
    {
        Sprite sprite = Resources.Load<Sprite>("Badges/LaurelAward");
        if (sprite == null)
            throw new System.InvalidOperationException("Missing Resources/Badges/LaurelAward.png");

        _awardRoots = new RectTransform[_posters.Length];
        _awardImages = new Image[_posters.Length];
        _awardLabels = new Text[_posters.Length];

        for (int i = 0; i < _posters.Length; i++)
        {
            RectTransform root = CreateAwardRect("FestivalAwardBadge", _posters[i]);
            root.anchorMin = root.anchorMax = new Vector2(1f, 0f);
            root.pivot = new Vector2(1f, 0f);
            root.anchoredPosition = new Vector2(-17f, 17f);
            root.sizeDelta = new Vector2(148f, 152f);
            Image backdrop = root.gameObject.AddComponent<Image>();
            backdrop.color = new Color(0.04f, 0.06f, 0.07f, 0.72f);
            backdrop.raycastTarget = false;
            CanvasGroup group = root.gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;
            _awardRoots[i] = root;

            RectTransform art = CreateAwardRect("LaurelWomanStatuette", root);
            art.anchorMin = art.anchorMax = new Vector2(0.5f, 1f);
            art.pivot = new Vector2(0.5f, 1f);
            art.anchoredPosition = new Vector2(0f, -5f);
            art.sizeDelta = new Vector2(104f, 112f);
            Image icon = art.gameObject.AddComponent<Image>();
            icon.sprite = sprite;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            _awardImages[i] = icon;

            RectTransform textRect = CreateAwardRect("AwardStatus", root);
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = new Vector2(1f, 0f);
            textRect.pivot = new Vector2(0.5f, 0f);
            textRect.anchoredPosition = new Vector2(0f, 8f);
            textRect.sizeDelta = new Vector2(-6f, 28f);
            Text label = textRect.gameObject.AddComponent<Text>();
            label.font = _titleLabel.font;
            label.fontSize = 18;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            _awardLabels[i] = label;

            bool newlyCleared = StageClearSession.RecentlyCleared == Destinations[i];
            SetAwardState(i, StageClearSession.IsCleared(Destinations[i]) && !newlyCleared);
        }
    }

    /// <summary>uGUI 이미지·텍스트를 위한 자식 RectTransform을 생성한다.</summary>
    private static RectTransform CreateAwardRect(string name, Transform parent)
    {
        GameObject child = new GameObject(name, typeof(RectTransform));
        RectTransform rect = child.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    /// <summary>후보/수상 문구와 배지 색상을 세션 클리어 여부로 동기화한다.</summary>
    private void SetAwardState(int index, bool awarded)
    {
        Color color = awarded ? _awardedColor : _nominatedColor;
        _awardImages[index].color = color;
        _awardLabels[index].color = color;
        _awardLabels[index].text = awarded ? "AWARDED" : "NOMINATED";
    }

    /// <summary>클리어 직후 돌아온 포스터를 선택하고 배지를 회전시켜 수상을 드러낸다.</summary>
    private IEnumerator RevealRecentAward()
    {
        int index = System.Array.IndexOf(Destinations, StageClearSession.RecentlyCleared);
        if (index < 0) yield break;

        yield return new WaitForSecondsRealtime(0.35f);
        RectTransform badge = _awardRoots[index];
        float elapsed = 0f;
        bool revealed = false;
        const float duration = 0.76f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float width = Mathf.Max(0.025f, Mathf.Abs(Mathf.Cos(Mathf.PI * t)));
            float bounce = 1f + 0.13f * Mathf.Sin(Mathf.PI * t);
            badge.localScale = new Vector3(width, bounce, 1f);
            if (t >= 0.5f && !revealed)
            {
                SetAwardState(index, true);
                revealed = true;
            }
            yield return null;
        }
        badge.localScale = Vector3.one;
        SetAwardState(index, true);
        StageClearSession.ConsumeRecentClear(Destinations[index]);
    }

    private void ApplyPosterPoses()
    {
        for (int i = 0; i < _posters.Length; i++)
        {
            float distance = Mathf.Clamp01(Mathf.Abs(i - _animatedIndex));
            float easedDistance = distance * distance * (3f - 2f * distance);
            float scale = Mathf.Lerp(1f, _neighborScale, easedDistance);
            _posters[i].anchoredPosition = new Vector2(
                (i - _animatedIndex) * _posterSpacing, 0f);
            _posters[i].localScale = new Vector3(scale, scale, 1f);
            _posterOpacity[i].alpha = Mathf.Lerp(1f, _neighborAlpha, easedDistance);
            _posterFrames[i].color = Color.Lerp(_activeFrame, _inactiveFrame, easedDistance);
        }
    }

    private void SelectPrevious() => Select(_selected - 1);

    private void SelectNext() => Select(_selected + 1);

    private void Select(int index)
    {
        int next = Mathf.Clamp(index, 0, Destinations.Length - 1);
        if (_loading || next == _selected) return;
        _selected = next;
        // Bring the selected poster in front of the neighboring poster.
        _posters[_selected].SetAsLastSibling();
        ShowSelectedStage();
    }

    private void ShowSelectedStage()
    {
        _countLabel.text = $"{_selected + 1:00}  /  {Destinations.Length:00}";
        _titleLabel.text = Names[_selected];
        _descriptionLabel.text = Descriptions[_selected];
        _playLabel.text = "ENTER STAGE  \u2192";
        _helpLabel.text = "DRAG  /  SCROLL  /  \u2190  \u2192     TO BROWSE";
        _previousButton.interactable = _selected > 0;
        _nextButton.interactable = _selected < Destinations.Length - 1;
    }

    private void OnPosterClicked(int index)
    {
        if (_loading || Time.unscaledTime < _ignoreClickUntil) return;
        if (index == _selected) LaunchSelectedStage();
        else Select(index);
    }

    private RectTransform CarouselRoot => _posters[0].parent as RectTransform;

    private bool TryToCarouselLocal(Vector2 screenPosition, PointerEventData eventData,
        out Vector2 localPosition)
    {
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(
            CarouselRoot, screenPosition, eventData.pressEventCamera, out localPosition);
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (_loading || eventData.button != PointerEventData.InputButton.Left)
            return;

        RectTransform carousel = CarouselRoot;
        if (carousel == null ||
            !RectTransformUtility.RectangleContainsScreenPoint(
                carousel, eventData.pressPosition, eventData.pressEventCamera))
            return;

        // Buttons are ordinary clicks, not a draggable area.
        if (RectTransformUtility.RectangleContainsScreenPoint(
                (RectTransform)_previousButton.transform, eventData.pressPosition, eventData.pressEventCamera) ||
            RectTransformUtility.RectangleContainsScreenPoint(
                (RectTransform)_nextButton.transform, eventData.pressPosition, eventData.pressEventCamera) ||
            RectTransformUtility.RectangleContainsScreenPoint(
                (RectTransform)_playButton.transform, eventData.pressPosition, eventData.pressEventCamera))
            return;

        if (!TryToCarouselLocal(eventData.pressPosition, eventData, out _dragStartLocal))
            return;

        _dragging = true;
        _dragCurrentLocal = _dragStartLocal;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!_dragging || _loading) return;
        if (!TryToCarouselLocal(eventData.position, eventData, out _dragCurrentLocal))
            return;

        float delta = _dragCurrentLocal.x - _dragStartLocal.x;
        _animatedIndex = Mathf.Clamp(
            _selected - delta / _posterSpacing, 0f, Destinations.Length - 1);
        ApplyPosterPoses();
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (!_dragging) return;

        OnDrag(eventData);
        _dragging = false;

        float delta = _dragCurrentLocal.x - _dragStartLocal.x;
        if (Mathf.Abs(delta) < _swipeThreshold) return;

        _ignoreClickUntil = Time.unscaledTime + 0.2f;
        Select(_selected + (delta < 0f ? 1 : -1));
    }

    public void LaunchSelectedStage()
    {
        if (_loading) return;
        Scene target = Destinations[_selected];
        if (!Application.CanStreamedLevelBeLoaded(target.ToString()))
        {
            _helpLabel.text = $"MISSING SCENE IN BUILD SETTINGS: {target}";
            Debug.LogError($"[OutGame] Scene is not in build settings: {target}");
            return;
        }

        _loading = true;
        _playButton.interactable = false;
        _playLabel.text = "LOADING...";
        Managers.Clear();
        Managers.Scene.Load(target);
    }
}
