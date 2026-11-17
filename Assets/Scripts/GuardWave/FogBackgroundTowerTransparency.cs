using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// Fades background tower opacity linearly over the outer 0%-5% viewport margins,
    /// using the tower's projected bounds center. Its outline shares the same opacity.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FogBackgroundTowerTransparency : MonoBehaviour
    {
        [Header("Viewport fade")]
        [SerializeField, Range(0f, 0.5f)] private float _fadeThresholdFromEdgeFraction = 0f;
        [SerializeField, Range(0.01f, 0.5f)] private float _fullFadeFromEdgeFraction = 0.05f;
        [SerializeField, Range(0f, 1f)] private float _transparentOpacity = 0.05f;
        [SerializeField, Min(0.01f)] private float _outlineWidth = 0.18f;
        [SerializeField] private Color _outlineColor = new Color(0.12f, 0.19f, 0.24f, 1f);
        [SerializeField] private Shader _fadeShader;

        private Camera _camera;
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

            float fadeAmount = GetViewportFadeAmount(_buildingCollider.bounds);
            if (fadeAmount <= 0f)
            {
                if (_usingFadeMaterials) RestoreOriginalMaterials();
                return;
            }

            float opacity = Mathf.Lerp(1f, _transparentOpacity, fadeAmount);
            bool opacityChanged = !Mathf.Approximately(_currentOpacity, opacity);
            _currentOpacity = opacity;
            if (!_usingFadeMaterials) UseFadeMaterials();
            else if (opacityChanged) UpdateOpacity();
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
        /// Returns the screen-position-based linear fade amount of a visible
        /// tower: zero at the screen edge, one at the 5% boundary.
        /// The projected collider-bounds center is the reference point.
        /// </summary>
        private float GetViewportFadeAmount(Bounds worldBounds)
        {
            Vector3 min = worldBounds.min;
            Vector3 max = worldBounds.max;
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;

            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 position = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                Vector3 viewport = _camera.WorldToViewportPoint(position);
                if (viewport.z <= 0f) return 0f;

                minX = Mathf.Min(minX, viewport.x);
                maxX = Mathf.Max(maxX, viewport.x);
                minY = Mathf.Min(minY, viewport.y);
                maxY = Mathf.Max(maxY, viewport.y);
            }

            if (maxX <= 0f || minX >= 1f || maxY <= 0f || minY >= 1f)
                return 0f;

            // Fade each tower as a whole based on its projected center, so
            // upper and lower floors share the same alpha at every position.
            Vector3 centerViewport = _camera.WorldToViewportPoint(worldBounds.center);
            float distanceFromEdge = Mathf.Min(centerViewport.x, 1f - centerViewport.x);
            float fadeStart = _fadeThresholdFromEdgeFraction;
            float fadeEnd = Mathf.Max(fadeStart + 0.0001f, _fullFadeFromEdgeFraction);
            return Mathf.Clamp01((distanceFromEdge - fadeStart) / (fadeEnd - fadeStart));
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
                            name = source.name + " (West Background Fade)"
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

                // Set the current position-based alpha before the material swap
                // so entering the fade region does not flash an unrelated alpha.
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
        /// Updates wall, window and edge transparency from current projected
        /// position using the same alpha across all renderers in this tower.
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
        /// Restores opaque materials and original shadow settings when the tower
        /// leaves the fade region or the component is deactivated.
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
