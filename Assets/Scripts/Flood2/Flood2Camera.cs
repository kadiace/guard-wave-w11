using UnityEngine;

using UnityEngine.InputSystem;

namespace Flood2
{
    /// <summary>Unscaled orthographic inspection camera with pan, orbit, zoom and citizen focus.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class Flood2Camera : MonoBehaviour
    {
        [Header("Inspection camera")]
        [SerializeField] private Vector3 _focus = new Vector3(0f, 0f, 29f);
        [SerializeField] private float _yaw = -12f;
        [SerializeField, Range(35f, 85f)] private float _pitch = 58f;
        [SerializeField, Range(8f, 40f)] private float _zoom = 36f;

        [Header("Camera Controls")]
        [SerializeField, Min(0f)] private float _scrollSensitivity = 0.015f;
        private Camera _camera;
        private Flood2Stage _stage;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _stage = FindFirstObjectByType<Flood2Stage>();
            ApplyPose();
        }

        /// <summary>Frames a selected citizen zone without changing any simulation state.</summary>
        public void Focus(Vector3 point)
        {
            _focus = point;
            _zoom = 13f;
            ApplyPose();
        }

        /// <summary>Returns to the whole-village overview.</summary>
        public void Overview()
        {
            _focus = new Vector3(0f, 0f, 29f); _yaw = -12f; _pitch = 58f; _zoom = 36f;
            ApplyPose();
        }

        private void Update()
        {
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (mouse != null && !_stage.Hud.BlocksPointer(mouse.position.ReadValue()))
            {
                _zoom = Mathf.Clamp(_zoom - mouse.scroll.ReadValue().y * _scrollSensitivity, 8f, 40f);
                Vector2 delta = mouse.delta.ReadValue();
                if (mouse.rightButton.isPressed) { _yaw += delta.x * 0.22f; _pitch = Mathf.Clamp(_pitch - delta.y * 0.18f, 35f, 85f); }
                if (mouse.middleButton.isPressed)
                    _focus -= Quaternion.Euler(0f, _yaw, 0f) * new Vector3(delta.x, 0f, delta.y) * (_zoom / 450f);
            }
            if (keyboard != null)
            {
                Vector3 move = new Vector3((keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0), 0f,
                    (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0));
                _focus += Quaternion.Euler(0f, _yaw, 0f) * move * (dt * _zoom * 0.7f);
                if (keyboard.homeKey.wasPressedThisFrame) Overview();
            }
            _focus.x = Mathf.Clamp(_focus.x, -22f, 22f);
            _focus.z = Mathf.Clamp(_focus.z, -2f, 62f);
            ApplyPose();
        }

        /// <summary>Applies the orbit parameters to the orthographic camera.</summary>
        private void ApplyPose()
        {
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.position = _focus - transform.forward * 90f;
            _camera.orthographicSize = _zoom;
        }
    }
}
