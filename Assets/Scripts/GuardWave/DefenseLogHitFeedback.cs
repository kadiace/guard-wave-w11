using UnityEngine;

namespace GuardWave
{
    /// <summary>Flashes and compresses a render-only copy of a struck Defense log.</summary>
    public sealed class DefenseLogHitFeedback : MonoBehaviour
    {
        private const float FLASH_SECONDS = 0.065f;
        private const float RECOVERY_SECONDS = 0.16f;
        private const float HIT_SCALE = 0.82f;

        private MeshRenderer _source;
        private MeshRenderer _visual;
        private MaterialPropertyBlock _flash;
        private float _hitTime;
        private bool _isWhite;

        private void Awake()
        {
            _source = GetComponent<MeshRenderer>();
            GameObject visual = new GameObject("Hit Visual");
            visual.layer = gameObject.layer;
            visual.transform.SetParent(transform, false);
            visual.AddComponent<MeshFilter>().sharedMesh = GetComponent<MeshFilter>().sharedMesh;
            _visual = visual.AddComponent<MeshRenderer>();
            _visual.sharedMaterials = _source.sharedMaterials;
            _visual.renderingLayerMask = _source.renderingLayerMask;
            _visual.shadowCastingMode = _source.shadowCastingMode;
            _visual.receiveShadows = _source.receiveShadows;
            _visual.enabled = false;
            _flash = new MaterialPropertyBlock();
            _flash.SetColor("_BaseColor", Color.white);
            _flash.SetTexture("_BaseMap", Texture2D.whiteTexture);
        }

        /// <summary>Restarts the hit pulse immediately, including during recovery from a previous hit.</summary>
        public void Play()
        {
            _hitTime = Time.time;
            _isWhite = true;
            _visual.transform.localScale = Vector3.one * HIT_SCALE;
            _visual.SetPropertyBlock(_flash);
            _source.enabled = false;
            _visual.enabled = true;
            enabled = true;
        }

        private void LateUpdate()
        {
            float elapsed = Time.time - _hitTime;
            if (elapsed >= RECOVERY_SECONDS)
            {
                enabled = false;
                return;
            }
            if (_isWhite && elapsed >= FLASH_SECONDS)
            {
                _visual.SetPropertyBlock(null);
                _isWhite = false;
            }
            float t = elapsed / RECOVERY_SECONDS;
            _visual.transform.localScale = Vector3.one * Mathf.Lerp(HIT_SCALE, 1f, t * (2f - t));
        }

        private void OnDisable()
        {
            if (_visual != null) _visual.enabled = false;
            if (_source != null) _source.enabled = true;
        }
    }
}
