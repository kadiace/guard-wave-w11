using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RestoreCastle3D
{
    /// <summary>
    /// 후보 프리팹의 시각 메시만 남긴 반투명 복제본을 -Y Raycast 충돌 예상 높이에 표시한다.
    /// 실제 중력·회전·충돌로 인한 최종 정착은 재현하지 않는 정적 낙하 가이드이다.
    /// </summary>
    internal sealed class CastleDropPreview3D
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

        /// <summary>활성 고스트의 실제 표시 중심 위치. 화면 점선의 도착점으로 사용한다.</summary>
        public bool IsVisible => _instance != null && _instance.activeSelf;
        public Vector3 LandingPosition => _instance != null ? _instance.transform.position : Vector3.zero;

        /// <summary>배치 가능 여부에 맞춰 색을 바꾸고 예상 접촉면 위에 자재 미리보기를 표시한다.</summary>
        public void Show(CastleMaterial3D info, Vector3 placement, Quaternion rotation,
            float dropHeight, bool canPlace, float fallbackSurfaceY)
        {
            if (info == null) { Hide(); return; }
            if (_instance == null || _materialId != info.id)
                Rebuild(info);
            if (_instance == null) return;
            SetPlacementValidity(canPlace);

            // Use the same fixed XZ as the real drop; trace vertically from above
            // the launch plane onto the first physical surface at this point.
            Vector3 origin = new Vector3(placement.x, dropHeight + 5f, placement.z);
            bool hasHit = Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                MaximumRayDistance, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            if (!hasHit && canPlace)
            {
                Hide();
                return;
            }

            float surfaceY = hasHit ? hit.point.y : fallbackSurfaceY;
            _instance.transform.SetPositionAndRotation(
                new Vector3(placement.x,
                    surfaceY - _colliderBottomY + VisualOffset,
                    placement.z),
                rotation);
            if (!_instance.activeSelf) _instance.SetActive(true);
        }

        /// <summary>커서가 화면이나 허용된 입력 영역을 벗어났을 때 미리보기를 숨긴다.</summary>
        public void Hide()
        {
            if (_instance != null && _instance.activeSelf) _instance.SetActive(false);
        }

        /// <summary>이전 프리팹과 생성한 투명 머티리얼을 해제한다.</summary>
        public void Dispose()
        {
            if (_instance != null) Object.Destroy(_instance);
            _instance = null;
            _materialId = null;
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

        /// <summary>그리기용 프리팹만 복제하고 물리·게임 로직은 비활성화한다.</summary>
        private void Rebuild(CastleMaterial3D info)
        {
            Dispose();
            GameObject prefab = Resources.Load<GameObject>("Prefabs/" + info.id);
            if (prefab == null)
            {
                Debug.LogWarning("[CastleDropPreview3D] Missing prefab: " + info.id);
                return;
            }

            _instance = Object.Instantiate(prefab);
            _instance.name = "Drop Preview - " + info.name;
            _instance.SetActive(false);
            _materialId = info.id;

            BoxCollider sourceCollider = _instance.GetComponent<BoxCollider>();
            _colliderBottomY = sourceCollider == null
                ? 0f
                : (sourceCollider.center.y - sourceCollider.size.y * 0.5f)
                    * _instance.transform.localScale.y;

            // A preview must never push pieces, fall, start a barrel fuse,
            // participate in contact scoring, or block the prediction Raycast.
            foreach (Collider collider in _instance.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            foreach (Rigidbody body in _instance.GetComponentsInChildren<Rigidbody>(true))
            {
                body.useGravity = false;
                body.isKinematic = true;
                body.detectCollisions = false;
            }
            foreach (CastlePiece3D piece in _instance.GetComponentsInChildren<CastlePiece3D>(true))
                piece.enabled = false;

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
