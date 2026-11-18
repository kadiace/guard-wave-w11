using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// Fades West/East background towers according to signed camera yaw relative
    /// to the camera's initial direction, preserving colliders and render order.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FogBackgroundTowerTransparency : MonoBehaviour
    {
        private enum TowerSide { West, East }

        [Header("Camera yaw fade (relative to initial view)")]
        [SerializeField] private TowerSide _towerSide = TowerSide.West;
        [SerializeField, Range(0f, 180f)] private float _yawThresholdDegrees = 5f;
        [SerializeField, Range(0f, 1f)] private float _transparentOpacity = 0.05f;
        [SerializeField, Min(0.01f)] private float _outlineWidth = 0.18f;
        [SerializeField] private Color _outlineColor = new Color(0.12f, 0.19f, 0.24f, 1f);
        [SerializeField] private Shader _fadeShader;

        private Camera _camera;
        private float _initialCameraYaw;
        private bool _hasInitialCameraYaw;
        private Collider _buildingCollider;
        private Renderer[] _renderers;
        private Material[][] _originalMaterials;
        private ShadowCastingMode[] _originalShadowModes;
        private int[] _originalSortingOrders;
        private Dictionary<Material, Material> _fadeMaterials;
        private MaterialPropertyBlock _propertyBlock;
        private bool _usingFadeMaterials;
        private float _currentOpacity = 1f;

        private void Awake()
        {
            _camera = Camera.main;
            CaptureInitialCameraYaw();
            _buildingCollider = GetComponent<Collider>();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _originalMaterials = new Material[_renderers.Length][];
            _originalShadowModes = new ShadowCastingMode[_renderers.Length];
            _originalSortingOrders = new int[_renderers.Length];
            _fadeMaterials = new Dictionary<Material, Material>();
            _propertyBlock = new MaterialPropertyBlock();

            for (int i = 0; i < _renderers.Length; i++)
            {
                _originalMaterials[i] = _renderers[i].sharedMaterials;
                _originalShadowModes[i] = _renderers[i].shadowCastingMode;
                _originalSortingOrders[i] = _renderers[i].sortingOrder;
            }
        }

        private void LateUpdate()
        {
            if (_camera == null) _camera = Camera.main;
            if (_camera == null || _buildingCollider == null || _fadeShader == null) return;
            if (!_hasInitialCameraYaw) CaptureInitialCameraYaw();

            // Fog starts with a 45-degree world Y rotation. Measure the user's
            // +/-5-degree scroll rotation from that initial pose, not world zero.
            float yawDelta = Mathf.DeltaAngle(_initialCameraYaw,
                _camera.transform.eulerAngles.y);
            bool shouldFade = _towerSide == TowerSide.West
                ? yawDelta >= _yawThresholdDegrees
                : yawDelta <= -_yawThresholdDegrees;
            if (!shouldFade)
            {
                if (_usingFadeMaterials) RestoreOriginalMaterials();
                return;
            }

            bool opacityChanged = !Mathf.Approximately(_currentOpacity, _transparentOpacity);
            _currentOpacity = _transparentOpacity;
            if (!_usingFadeMaterials) UseFadeMaterials();
            else if (opacityChanged) UpdateOpacity();
        }

        private void CaptureInitialCameraYaw()
        {
            if (_camera == null) return;
            _initialCameraYaw = _camera.transform.eulerAngles.y;
            _hasInitialCameraYaw = true;
        }

        private void OnDisable()
        {
            if (_usingFadeMaterials) RestoreOriginalMaterials();
        }

        private void OnDestroy()
        {
            if (_usingFadeMaterials) RestoreOriginalMaterials();
            if (_fadeMaterials == null) return;

            foreach (Material material in _fadeMaterials.Values)
                Destroy(material);
            _fadeMaterials.Clear();
        }

        /// <summary>
        /// Applies fade materials and an explicit draw order: tower body,
        /// decorative trims, then windows. This keeps lower-floor windows from
        /// blending behind the tall body due to renderer-center depth sorting.
        /// Physics colliders and the original renderers remain intact.
        /// </summary>
        private void UseFadeMaterials()
        {
            Bounds bounds = _buildingCollider.bounds;
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                Material[] original = _originalMaterials[i];
                Material[] replacements = new Material[original.Length];
                for (int slot = 0; slot < original.Length; slot++)
                {
                    Material source = original[slot];
                    if (source == null) continue;
                    if (!_fadeMaterials.TryGetValue(source, out Material replacement))
                    {
                        replacement = new Material(_fadeShader)
                        {
                            name = source.name + " (Background Fade)"
                        };
                        if (source.HasProperty("_BaseColor"))
                            replacement.SetColor("_BaseColor", source.GetColor("_BaseColor"));
                        else if (source.HasProperty("_Color"))
                            replacement.SetColor("_BaseColor", source.GetColor("_Color"));
                        if (source.HasProperty("_BaseMap"))
                            replacement.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
                        _fadeMaterials.Add(source, replacement);
                    }
                    replacements[slot] = replacement;
                }

                // Apply the angle-triggered alpha before swapping materials
                // to avoid a one-frame flash at the threshold.
                renderer.GetPropertyBlock(_propertyBlock);
                _propertyBlock.SetFloat("_Opacity", _currentOpacity);
                _propertyBlock.SetVector("_WorldBoundsMin", bounds.min);
                _propertyBlock.SetVector("_WorldBoundsMax", bounds.max);
                _propertyBlock.SetFloat("_EdgeWidth", _outlineWidth);
                _propertyBlock.SetColor("_EdgeColor", _outlineColor);
                _propertyBlock.SetFloat("_OutlineEnabled",
                    renderer.gameObject.name == "Concrete Tower" ? 1f : 0f);
                renderer.SetPropertyBlock(_propertyBlock);
                _propertyBlock.Clear();
                renderer.sharedMaterials = replacements;
                renderer.sortingOrder = _originalSortingOrders[i] +
                    GetFadeSortingOffset(renderer.gameObject.name);
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
            _usingFadeMaterials = true;
        }

        /// <summary>
        /// Returns the transparency sorting group for this tower's renderer.
        /// The body is composited first, the trims second, and windows last,
        /// regardless of each floor's renderer bounds center.
        /// </summary>
        private static int GetFadeSortingOffset(string objectName)
        {
            if (objectName == "Concrete Tower") return 0;
            if (objectName.StartsWith("Window_") || objectName == "Ground Floor Entry")
                return 2;
            return 1;
        }

        /// <summary>
        /// Updates wall, window and edge opacity consistently across the tower.
        /// </summary>
        private void UpdateOpacity()
        {
            foreach (Renderer renderer in _renderers)
            {
                renderer.GetPropertyBlock(_propertyBlock);
                _propertyBlock.SetFloat("_Opacity", _currentOpacity);
                renderer.SetPropertyBlock(_propertyBlock);
                _propertyBlock.Clear();
            }
        }

        /// <summary>
        /// Restores opaque materials and original shadows below the yaw threshold
        /// or when the component is deactivated.
        /// </summary>
        private void RestoreOriginalMaterials()
        {
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                renderer.sharedMaterials = _originalMaterials[i];
                renderer.SetPropertyBlock(null);
                renderer.sortingOrder = _originalSortingOrders[i];
                renderer.shadowCastingMode = _originalShadowModes[i];
            }
            _usingFadeMaterials = false;
            _currentOpacity = 1f;
        }
    }
}
