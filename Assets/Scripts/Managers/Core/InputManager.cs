using UnityEngine;
using UnityEngine.InputSystem;

public class InputManager
{
    [Header("Input System")]
    private InputSystem_Actions _inputActions;
    public InputSystem_Actions.PlayerActions PlayerMap;
    public InputSystem_Actions.UIActions UIMap;
    private InputMode _inputMode = InputMode.Player;

    [Header("Player Mode")]
    public Vector2 MoveInput => _inputMode == InputMode.Player ? PlayerMap.Move.ReadValue<Vector2>() : Vector2.zero;

    [Header("UI Mode")]
    public bool ConfirmPressed => _inputMode == InputMode.UI && UIMap.Click.WasPressedThisFrame();

    public bool GamePadConnected { get; private set; }

    /// <summary>입력 액션을 한 번 생성하고 기본 Player 모드를 활성화한다.</summary>
    public void Init()
    {
        if (_inputActions != null)
            return;

        _inputActions = new InputSystem_Actions();

        PlayerMap = _inputActions.Player;
        UIMap = _inputActions.UI;

        InputSystem.onActionChange += CheckDeviceType;

        SetInputMode(InputMode.Player);
    }

    /// <summary>입력 액션과 전역 이벤트 구독을 해제하고 생성한 에셋을 정리한다.</summary>
    public void Shutdown()
    {
        if (_inputActions == null)
            return;

        PlayerMap.Disable();
        UIMap.Disable();
        InputSystem.onActionChange -= CheckDeviceType;
        _inputActions.Dispose();
        _inputActions = null;
        PlayerMap = default;
        UIMap = default;
        GamePadConnected = false;
    }

    /// <summary>지정한 입력 모드의 액션 맵만 활성화하고 커서와 시간 배율을 설정한다.</summary>
    public void SetInputMode(InputMode mode)
    {
        _inputMode = mode;

        if (_inputActions == null)
            return;

        PlayerMap.Disable();
        UIMap.Disable();

        if (mode == InputMode.Player)
        {
            PlayerMap.Enable();
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            Time.timeScale = 1f;
        }
        else
        {
            UIMap.Enable();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Time.timeScale = 0f;
        }
    }

    private void CheckDeviceType(object actionObject, InputActionChange change)
    {
        if (change != InputActionChange.ActionPerformed)
            return;

        if (actionObject is InputAction action && action.actionMap?.asset == _inputActions.asset)
            GamePadConnected = action.activeControl?.device is Gamepad;
    }
}
