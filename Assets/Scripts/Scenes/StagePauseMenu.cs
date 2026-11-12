using GuardWave;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

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

    private void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame)
            return;

        if (IsOpen) Resume();
        else if (_hud != null && _hud.IsRulesOpen) _hud.HideRules();
        else Open();
    }

    private void Open()
    {
        if (IsOpen) return;

        IsOpen = true;
        _modalRoot.SetActive(true);
        Time.timeScale = 0f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void Resume()
    {
        if (!IsOpen) return;

        IsOpen = false;
        _modalRoot.SetActive(false);
        Time.timeScale = 1f;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void ReturnToStages()
    {
        if (!IsOpen) return;

        IsOpen = false;
        Time.timeScale = 1f;
        Managers.Clear();
        Managers.Scene.Load(Scene.Game);
    }

    private void OnDestroy()
    {
        // Pausing must never leak into the next scene or a stopped Play Mode.
        if (IsOpen) Time.timeScale = 1f;
    }
}
