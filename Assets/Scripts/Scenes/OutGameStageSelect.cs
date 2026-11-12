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

        for (int i = 0; i < _posterButtons.Length; i++)
        {
            int index = i;
            _posterButtons[i].onClick.AddListener(() => OnPosterClicked(index));
        }

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
