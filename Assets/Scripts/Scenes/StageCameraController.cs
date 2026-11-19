using System;

using UnityEngine;

using UnityEngine.InputSystem;

namespace GuardWave
{
    /// <summary>스테이지별 직교 카메라의 전체 보기, 이동, 회전과 확대를 처리한다.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class StageCameraController : MonoBehaviour
    {
        private const float WINDOWS_SCROLL_UNITS_PER_TICK = 120f;
        [Header("Overview pose")]
        [SerializeField] private Vector3 _focus = new Vector3(0f, 0f, 29f);
        [SerializeField] private float _yaw = -12f;
        [SerializeField, Range(35f, 85f)] private float _pitch = 58f;
        [SerializeField, Range(8f, 40f)] private float _zoom = 36f;
        [SerializeField, Min(0.1f)] private float _overviewDistance = 90f;

        [Header("Camera controls")]
        [SerializeField, Range(8f, 40f)] private float _citizenFocusZoom = 13f;
        [SerializeField, Min(0f)] private float _scrollSensitivity = 0.015f;
        [SerializeField, Min(0f)] private float _orbitYawSensitivity = 0.22f;
        [SerializeField, Min(0f)] private float _orbitPitchSensitivity = 0.18f;
        [SerializeField, Min(0f)] private float _keyboardPanSpeed = 0.7f;
        [SerializeField] private Vector2 _focusMin = new Vector2(-22f, -2f);
        [SerializeField] private Vector2 _focusMax = new Vector2(22f, 62f);
        [SerializeField] private Vector2 _pitchRange = new Vector2(35f, 85f);
        [SerializeField] private Vector2 _zoomRange = new Vector2(8f, 40f);
        private Vector3 _currentFocus;
        private float _currentYaw;
        private float _currentPitch;
        private float _currentZoom;
        private Camera _camera;
        private Func<Vector2, bool> _pointerBlocked;
        private bool _commonBuildControls;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        /// <summary>전체 보기 조작을 활성화하고 UI의 포인터 차단 함수를 연결한다.</summary>
        public void InitializeOverview(Func<Vector2, bool> pointerBlocked, bool commonBuildControls = false)
        {
            _camera = GetComponent<Camera>();
            _pointerBlocked = pointerBlocked;
            _commonBuildControls = commonBuildControls;
            enabled = true;
            Overview();
        }

        /// <summary>스테이지가 입력 시점을 직접 제어하도록 자동 전체 보기 입력을 끈다.</summary>
        public void InitializeManual()
        {
            _camera = GetComponent<Camera>();
            _pointerBlocked = null;
            enabled = false;
        }

        /// <summary>Frames a selected citizen zone without changing any simulation state.</summary>
        public void Focus(Vector3 point)
        {
            _currentFocus = point;
            _currentZoom = _citizenFocusZoom;
            ApplyPose();
        }

        /// <summary>Returns to the whole-village overview.</summary>
        public void Overview()
        {
            _currentFocus = _focus;
            _currentYaw = _yaw;
            _currentPitch = _pitch;
            _currentZoom = _zoom;
            ApplyPose();
        }

        private void Update()
        {
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (_pointerBlocked == null) return;
            if (mouse != null && !_pointerBlocked(mouse.position.ReadValue()))
            {
                float scroll = NormalizeScrollSteps(mouse.scroll.ReadValue().y);
                if (!_commonBuildControls || !mouse.rightButton.isPressed)
                    _currentZoom = Mathf.Clamp(_currentZoom - scroll * _scrollSensitivity, _zoomRange.x, _zoomRange.y);
                Vector2 delta = mouse.delta.ReadValue();
                if (mouse.rightButton.isPressed)
                {
                    if (_commonBuildControls)
                    {
                        _currentFocus -= Quaternion.Euler(0f, _currentYaw, 0f) * new Vector3(delta.x, 0f, delta.y) * (_currentZoom / 450f);
                        if (Mathf.Abs(scroll) > 0.01f) _currentYaw += scroll * 15f;
                    }
                    else
                    {
                        _currentYaw += delta.x * _orbitYawSensitivity;
                        _currentPitch = Mathf.Clamp(_currentPitch - delta.y * _orbitPitchSensitivity, _pitchRange.x, _pitchRange.y);
                    }
                }
                if (mouse.middleButton.isPressed)
                    _currentFocus -= Quaternion.Euler(0f, _currentYaw, 0f) * new Vector3(delta.x, 0f, delta.y) * (_currentZoom / 450f);
            }
            if (keyboard != null)
            {
                Vector3 move = new Vector3((keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0), 0f,
                    (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0));
                _currentFocus += Quaternion.Euler(0f, _currentYaw, 0f) * move * (dt * _currentZoom * _keyboardPanSpeed);
                if (keyboard.homeKey.wasPressedThisFrame) Overview();
            }
            _currentFocus.x = Mathf.Clamp(_currentFocus.x, _focusMin.x, _focusMax.x);
            _currentFocus.z = Mathf.Clamp(_currentFocus.z, _focusMin.y, _focusMax.y);
            ApplyPose();
        }

        /// <summary>Applies the orbit parameters to the orthographic camera.</summary>
        private void ApplyPose()
        {
            transform.rotation = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
            PositionAtFocus(_currentFocus, _overviewDistance);
            _camera.orthographicSize = _currentZoom;
        }

        /// <summary>현재 회전을 유지하고 카메라를 주시점에서 지정 거리로 배치한다.</summary>
        public void PositionAtFocus(Vector3 focus, float distance)
        {
            transform.position = focus - transform.forward * distance;
        }

        /// <summary>화면 드래그를 지면의 Z축 이동량으로 환산한다.</summary>
        public float GetDragAlongZ(Vector2 delta)
        {
            float unit = 2f * _camera.orthographicSize / Mathf.Max(Screen.height, 1);
            Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized;
            Vector3 up = Vector3.ProjectOnPlane(transform.up, Vector3.up).normalized;
            return unit * (right.z * delta.x + up.z * delta.y);
        }

        /// <summary>휠 델타에 따라 직교 카메라의 크기를 지수적으로 조정한다.</summary>
        public void ZoomByScroll(float scroll, float sensitivity, float minimum, float maximum)
        {
            _camera.orthographicSize = Mathf.Clamp(_camera.orthographicSize *
                Mathf.Exp(-scroll * sensitivity), minimum, maximum);
        }

        /// <summary>플랫폼별 휠 델타를 휠 칸으로 환산해 카메라를 회전한다.</summary>
        public void RotateByScroll(float scroll, float degreesPerStep)
        {
            transform.Rotate(Vector3.up, NormalizeScrollSteps(scroll) * degreesPerStep, Space.World);
        }

        /// <summary>Converts Windows platform wheel units to logical ticks for overview controls.</summary>
        private static float NormalizeScrollSteps(float scroll)
        {
            if (InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange &&
                (Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer))
                return scroll / WINDOWS_SCROLL_UNITS_PER_TICK;
            return scroll;
        }
    }
}
