using System;

using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

using GuardWave;

/// <summary>
/// A shared in-game Escape menu for Forest, Fog, and future scene contexts.
/// The overlay is authored as a prefab, not rebuilt from UI code at runtime.
/// </summary>
[DefaultExecutionOrder(-150)]
[DisallowMultipleComponent]
public sealed class StagePauseMenu : MonoBehaviour
{
    [SerializeField] private GameObject _modalRoot;
    [SerializeField] private Button _resumeButton;
    [SerializeField] private Button _returnButton;
    private GuardWaveHud _hud;
    private Func<bool> _canOpen;
    private Action<bool> _onPauseChanged;
    private Func<bool> _consumeEscape;

    public bool IsOpen { get; private set; }

    private void Awake()
    {
        _modalRoot.SetActive(false);
        _resumeButton.onClick.AddListener(Resume);
        _returnButton.onClick.AddListener(ReturnToStages);
    }

    /// <summary>현재 스테이지의 HUD를 받아 ESC와 가이드 메뉴를 상호 배타적으로 제어한다.</summary>
    public void Initialize(GuardWaveHud hud)
    {
        _hud = hud;
    }

    /// <summary>스테이지의 일시정지 조건, 상태 변경과 Escape 처리를 연결한다.</summary>
    public void Initialize(Func<bool> canOpen, Action<bool> onPauseChanged, Func<bool> consumeEscape = null)
    {
        _canOpen = canOpen;
        _onPauseChanged = onPauseChanged;
        _consumeEscape = consumeEscape;
    }

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame)
            return;

        if (IsOpen) Resume();
        else if (_consumeEscape != null && _consumeEscape()) return;
        else if (_hud != null && _hud.IsRulesOpen) _hud.HideRules();
        else Open();
    }

    /// <summary>스테이지가 허용하면 공통 일시정지 화면을 연다.</summary>
    public void Open()
    {
        if (IsOpen || (_canOpen != null && !_canOpen())) return;

        IsOpen = true;
        _modalRoot.SetActive(true);
        Time.timeScale = 0f;
        _onPauseChanged?.Invoke(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    /// <summary>일시정지를 해제하고 스테이지의 입력과 시간 진행을 재개한다.</summary>
    public void Resume()
    {
        if (!IsOpen) return;

        IsOpen = false;
        _modalRoot.SetActive(false);
        Time.timeScale = 1f;
        _onPauseChanged?.Invoke(false);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    /// <summary>일시정지를 해제하고 스테이지 선택 화면으로 돌아간다.</summary>
    public void ReturnToStages()
    {
        if (!IsOpen) return;

        IsOpen = false;
        Time.timeScale = 1f;
        _onPauseChanged?.Invoke(false);
        Managers.Clear();
        Managers.Scene.Load(Scene.Game);
    }

    private void OnDestroy()
    {
        // Pausing must never leak into the next scene or a stopped Play Mode.
        if (IsOpen) Time.timeScale = 1f;
    }
}
