using UnityEngine;

using UnityEngine.InputSystem;

namespace Avalanche
{
    /// <summary>Unscaled orthographic inspection camera with pan, orbit, zoom and citizen focus.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class AvalancheCamera : MonoBehaviour
    {
        [Header("Overview pose")]
        [SerializeField] private Vector3 _focus = new Vector3(0f, 0f, 29f);
        [SerializeField] private float _yaw = -12f;
        [SerializeField, Range(35f, 85f)] private float _pitch = 58f;
        [SerializeField, Range(8f, 40f)] private float _zoom = 36f;

        [Header("Camera controls")]
        [SerializeField, Range(8f, 40f)] private float _citizenFocusZoom = 13f;
        [SerializeField, Min(0f)] private float _scrollSensitivity = 0.015f;
        [SerializeField, Min(0f)] private float _orbitYawSensitivity = 0.22f;
        [SerializeField, Min(0f)] private float _orbitPitchSensitivity = 0.18f;
        [SerializeField, Min(0f)] private float _keyboardPanSpeed = 0.7f;
        private Vector3 _currentFocus;
        private float _currentYaw;
        private float _currentPitch;
        private float _currentZoom;
        private Camera _camera;
        private AvalancheStage _stage;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _stage = FindFirstObjectByType<AvalancheStage>();
            Overview();
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
            if (mouse != null && !_stage.Hud.BlocksPointer(mouse.position.ReadValue()))
            {
                _currentZoom = Mathf.Clamp(_currentZoom - mouse.scroll.ReadValue().y * _scrollSensitivity, 8f, 40f);
                Vector2 delta = mouse.delta.ReadValue();
                if (mouse.rightButton.isPressed)
                {
                    _currentYaw += delta.x * _orbitYawSensitivity;
                    _currentPitch = Mathf.Clamp(_currentPitch - delta.y * _orbitPitchSensitivity, 35f, 85f);
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
            _currentFocus.x = Mathf.Clamp(_currentFocus.x, -22f, 22f);
            _currentFocus.z = Mathf.Clamp(_currentFocus.z, -2f, 62f);
            ApplyPose();
        }

        /// <summary>Applies the orbit parameters to the orthographic camera.</summary>
        private void ApplyPose()
        {
            transform.rotation = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
            transform.position = _currentFocus - transform.forward * 90f;
            _camera.orthographicSize = _currentZoom;
        }
    }
}
