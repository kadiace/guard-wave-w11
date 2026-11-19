using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// 후보 프리팹의 시각 메시만 남긴 반투명 복제본을 배치할 표면 위에 표시한다.
    /// 생성 후에는 중력과 충돌에 따라 위치가 달라질 수 있다.
    /// </summary>
    internal sealed class GuardWaveDropPreview
    {
        private const float VisualOffset = 0.025f;
        private const float MaximumRayDistance = 500f;

        private readonly List<Material> _previewMaterials = new List<Material>();
        private readonly List<Color> _validTints = new List<Color>();
        private readonly List<Color> _invalidTints = new List<Color>();
        private GameObject _instance;
        private string _materialId;
        private float _colliderBottomY;
        private bool _canPlace = true;
        private bool _hasScaleOverride;
        private Vector3 _rootScaleOverride;

        /// <summary>활성 고스트의 실제 표시 중심 위치. 화면 점선의 도착점으로 사용한다.</summary>
        public bool IsVisible => _instance != null && _instance.activeSelf;
        public Vector3 LandingPosition => _instance != null ? _instance.transform.position : Vector3.zero;

        /// <summary>배치 가능 여부에 맞춰 색을 바꾸고 예상 접촉면 위에 자재 미리보기를 표시한다.</summary>
        public void Show(GuardWaveMaterial info, Vector3 placement, Quaternion rotation,
            float dropHeight, bool canPlace, float fallbackSurfaceY,
            Vector3? rootScaleOverride = null)
        {
            if (info == null) { Hide(); return; }
            bool hasScaleOverride = rootScaleOverride.HasValue;
            bool scaleChanged = hasScaleOverride != _hasScaleOverride ||
                (hasScaleOverride && rootScaleOverride.Value != _rootScaleOverride);
            if (_instance == null || _materialId != info.id || scaleChanged)
                Rebuild(info, rootScaleOverride);
            if (_instance == null) return;
            SetPlacementValidity(canPlace);

            // 같은 XZ에서 수직으로 표면을 찾아 미리보기와 생성 위치에 사용한다.
            Vector3 origin = new Vector3(placement.x, dropHeight + 5f, placement.z);
            bool hasHit = Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                MaximumRayDistance, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            // 표면을 찾지 못하면 기준 평면 위에 표시한다.
            float surfaceY = hasHit ? hit.point.y : fallbackSurfaceY;
            StagePlacement.ShowPreview(_instance,
                new Vector3(placement.x,
                    surfaceY - _colliderBottomY + VisualOffset,
                    placement.z),
                rotation);
        }

        /// <summary>커서가 화면이나 허용된 입력 영역을 벗어났을 때 미리보기를 숨긴다.</summary>
        public void Hide()
        {
            StagePlacement.HidePreview(_instance);
        }

        /// <summary>이전 프리팹과 생성한 투명 머티리얼을 해제한다.</summary>
        public void Dispose()
        {
            if (_instance != null)
            {
                GuardWaveGame.SafeDestroySelectedObject(_instance);
                Object.Destroy(_instance);
            }
            _instance = null;
            _materialId = null;
            _hasScaleOverride = false;
            _rootScaleOverride = Vector3.zero;
            foreach (Material material in _previewMaterials)
                if (material != null) Object.Destroy(material);
            _previewMaterials.Clear();
            _validTints.Clear();
            _invalidTints.Clear();
            _canPlace = true;
        }

        /// <summary>기존 미리보기 머티리얼의 색을 배치 가능 여부에 따라 전환한다.</summary>
        private void SetPlacementValidity(bool canPlace)
        {
            if (_canPlace == canPlace) return;
            _canPlace = canPlace;
            for (int i = 0; i < _previewMaterials.Count; i++)
            {
                Material material = _previewMaterials[i];
                Color tint = canPlace ? _validTints[i] : _invalidTints[i];
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
                if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
            }
        }

        /// <summary>
        /// Recreate only the visual hierarchy; no physics or gameplay
        /// components are ever instantiated by the drop guide.
        /// </summary>
        private static void CopyVisualHierarchy(
            Transform source, Transform destination, Mesh fallbackMesh)
        {
            MeshFilter sourceFilter = source.GetComponent<MeshFilter>();
            MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();
            if (sourceFilter != null && sourceRenderer != null)
            {
                Mesh mesh = sourceFilter.sharedMesh != null
                    ? sourceFilter.sharedMesh : fallbackMesh;
                if (mesh != null)
                {
                    MeshFilter filter = destination.gameObject.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;
                    MeshRenderer renderer = destination.gameObject.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                }
            }

            foreach (Transform child in source)
            {
                GameObject childVisual = new GameObject(child.name);
                childVisual.transform.SetParent(destination, false);
                childVisual.transform.localPosition = child.localPosition;
                childVisual.transform.localRotation = child.localRotation;
                childVisual.transform.localScale = child.localScale;
                CopyVisualHierarchy(child, childVisual.transform, fallbackMesh);
                childVisual.SetActive(child.gameObject.activeSelf);
            }
        }

        /// <summary>그리기용 프리팹만 복제하고 물리·게임 로직은 비활성화한다.</summary>
        private void Rebuild(GuardWaveMaterial info, Vector3? rootScaleOverride)
        {
            Dispose();
            GameObject prefab = Resources.Load<GameObject>("Prefabs/Objects/" + info.PrefabResourceId);
            if (prefab == null)
            {
                Debug.LogWarning("[GuardWaveDropPreview] Missing prefab: " + info.id);
                return;
            }

            // A preview is visual only: never instantiate a Rigidbody/
            // GuardWavePiece/Collider just to destroy or disable it. This also
            // avoids transient Rigidbody inspector targets in the Editor.
            _instance = new GameObject("Drop Preview - " + info.name);
            _instance.transform.localScale = rootScaleOverride ?? prefab.transform.localScale;
            _hasScaleOverride = rootScaleOverride.HasValue;
            _rootScaleOverride = rootScaleOverride.GetValueOrDefault();
            Mesh fallbackMesh = info.id == "forest_pebble"
                ? GuardWaveForestPebbleMesh.LoadImportedMesh()
                : null;
            CopyVisualHierarchy(prefab.transform, _instance.transform, fallbackMesh);
            _instance.hideFlags = HideFlags.HideInHierarchy;
            _materialId = info.id;

            // Measure the visual base after reconstructing mesh-only geometry.
            _colliderBottomY = 0f;
            bool found = false;
            foreach (Renderer sourceRenderer in _instance.GetComponentsInChildren<Renderer>(true))
            {
                if (sourceRenderer is MeshRenderer &&
                    sourceRenderer.GetComponent<MeshFilter>()?.sharedMesh == null)
                    continue;
                float bottom = sourceRenderer.bounds.min.y - _instance.transform.position.y;
                if (!found || bottom < _colliderBottomY)
                {
                    _colliderBottomY = bottom;
                    found = true;
                }
            }
            _instance.SetActive(false);

            foreach (Renderer renderer in _instance.GetComponentsInChildren<Renderer>(true))
            {
                Material[] originals = renderer.sharedMaterials;
                Material[] transparent = new Material[originals.Length];
                for (int i = 0; i < originals.Length; i++)
                {
                    Material source = originals[i];
                    if (source == null) continue;
                    Material clone = new Material(source);
                    clone.name = source.name + " (Drop Preview)";
                    Color color = source.HasProperty("_BaseColor")
                        ? source.GetColor("_BaseColor")
                        : source.color;
                    Color tint = Color.Lerp(color, new Color(0.2f, 0.93f, 0.92f, 1f), 0.7f);
                    tint.a = 0.43f;
                    Color invalidTint = Color.Lerp(color, new Color(1f, 0.12f, 0.12f, 1f), 0.85f);
                    invalidTint.a = 0.43f;
                    if (clone.HasProperty("_BaseColor")) clone.SetColor("_BaseColor", tint);
                    if (clone.HasProperty("_Color")) clone.SetColor("_Color", tint);
                    if (clone.HasProperty("_Surface")) clone.SetFloat("_Surface", 1f);
                    if (clone.HasProperty("_Blend")) clone.SetFloat("_Blend", 0f);
                    if (clone.HasProperty("_SrcBlend"))
                        clone.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    if (clone.HasProperty("_DstBlend"))
                        clone.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    if (clone.HasProperty("_ZWrite")) clone.SetFloat("_ZWrite", 0f);
                    clone.SetOverrideTag("RenderType", "Transparent");
                    clone.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    clone.DisableKeyword("_ALPHATEST_ON");
                    clone.renderQueue = (int)RenderQueue.Transparent;
                    _previewMaterials.Add(clone);
                    _validTints.Add(tint);
                    _invalidTints.Add(invalidTint);
                    transparent[i] = clone;
                }
                renderer.sharedMaterials = transparent;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }
    }
}
