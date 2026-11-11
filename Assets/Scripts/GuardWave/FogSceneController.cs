using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GuardWave
{
    /// <summary>
    /// Forest-derived Fog scene only. Connects the existing ready HUD to the
    /// independent gas solver. Never invokes or modifies Forest's FLIP/APIC wave.
    /// </summary>
    [DefaultExecutionOrder(50)]
    [RequireComponent(typeof(GuardWaveGame))]
    public sealed class FogSceneController : MonoBehaviour
    {
        [Header("Scene-authored volume mesh (no runtime creation)")]
        [SerializeField] private MeshRenderer _fogVolumeRenderer;

        [Header("Three-dimensional gas simulation")]
        [SerializeField, Range(0.45f, 1.5f)] private float _cellSize = 0.75f;
        [SerializeField, Range(8, 64)] private int _pressureIterations = 24;
        [SerializeField, Range(0.5f, 12f)] private float _windSpeed = 5.5f;
        [SerializeField, Range(1f, 16f)] private float _inletDuration = 7f;
        [Header("Billowing smoke motion")]
        [SerializeField, Range(0f, 5f)] private float _vorticityStrength = 2.2f;
        [SerializeField, Range(0f, 4f)] private float _turbulenceStrength = 1.15f;
        [SerializeField, Range(0f, 8f)] private float _expansionStrength = 4.2f;
        [SerializeField, Range(0f, 4f)] private float _buoyancy = 1.05f;
        [SerializeField, Range(0f, 4f)] private float _settling = 0.54f;

        [Header("Volumetric smoke shading")]
        [SerializeField, Range(0.1f, 3f)] private float _extinction = 1.3f;
        [SerializeField, Range(0f, 6f)] private float _shadowStrength = 2.1f;
        [SerializeField, Range(0f, 1f)] private float _detailStrength = 0.65f;
        [SerializeField] private Color _fogTint = new Color(0.69f, 0.81f, 0.60f, 1f);

        private GuardWaveGame _game;
        private FogFluidSolver _fluid;
        private MaterialPropertyBlock _block;
        private bool _running;
        private string _status;
        private float _opacity;

        public bool IsComing => _running;
        public bool CanReady => !_running && _game != null && _game.CanReady;
        public string StatusMessage => !string.IsNullOrEmpty(_status)
            ? _status : _game != null ? _game.StatusMessage : "";

        private void Awake()
        {
            _game = GetComponent<GuardWaveGame>();
            _block = new MaterialPropertyBlock();
            if (_fogVolumeRenderer != null) _fogVolumeRenderer.enabled = false;
        }

        private void Start()
        {
            Camera camera = Camera.main;
            if (camera != null)
            {
                // Depth-based termination of each ray is required even for the ISO camera.
                UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (data == null) data = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                data.requiresDepthTexture = true;
            }
        }

        public void Ready()
        {
            if (!CanReady || _fogVolumeRenderer == null) return;
            StartCoroutine(FogPass());
        }

        private IEnumerator FogPass()
        {
            _running = true;
            _game.SetRulesOpen(true); // Existing placement UI and input become read-only.
            _status = "짙은 연막이 상류에서 부풀어 오르고 있습니다.";
            float ground = -2.25f;
            GameObject bed = GameObject.Find("Flat Muddy Creek Bed");
            if (bed != null)
            {
                Collider c = bed.GetComponent<Collider>();
                if (c != null) ground = c.bounds.max.y;
            }

            // 24 m inlet [-12,12], but a 40 m wide domain allows natural sideward spread.
            // Longitudinal interval covers the Forest creek and downstream cliff.
            Vector3 origin = new Vector3(-20f, ground - 0.5f, -29f);
            Vector3 extent = new Vector3(40f, 9f, 83f);
            try
            {
                _fluid = new FogFluidSolver(gameObject.scene, origin, extent,
                    _cellSize, _pressureIterations);
                _fluid.ConfigureMotion(_vorticityStrength, _turbulenceStrength,
                    _expansionStrength, _buoyancy, _settling);
                Bounds bounds = _fluid.WorldBounds;
                _fogVolumeRenderer.transform.position = bounds.center;
                _fogVolumeRenderer.transform.localScale = bounds.size;
                _fogVolumeRenderer.enabled = true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                DisposeFog();
                _status = "안개 시뮬레이션을 시작하지 못했습니다. Compute Shader 지원을 확인하세요.";
                _running = false;
                _game.SetRulesOpen(false);
                yield break;
            }

            float travel = _fluid.WorldBounds.size.z / Mathf.Max(0.5f, _windSpeed);
            float total = _inletDuration + travel + 4f;
            float elapsed = 0f, accumulator = 0f;
            while (elapsed < total)
            {
                yield return new WaitForFixedUpdate();
                float dt = Time.fixedDeltaTime;
                elapsed += dt;
                accumulator += dt;
                if (accumulator >= 0.05f)
                {
                    // Bounded 20 Hz simulation decoupled from renderer frame rate.
                    float step = Mathf.Min(accumulator, 0.07f);
                    accumulator -= step;
                    // Fast source ignition followed by a brief, tapered smoke tail.
                    float ramp = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / 0.75f));
                    float tail = Mathf.SmoothStep(0f, 1f,
                        Mathf.Clamp01((_inletDuration - elapsed) / 1.2f));
                    float inlet = elapsed < _inletDuration ? ramp * tail : 0f;
                    _fluid.Step(step, inlet, _windSpeed, ground);
                }
                _opacity = Mathf.Clamp01((total - elapsed) / 2.5f);
                RenderVolume();
            }

            DisposeFog();
            _running = false;
            _game.SetRulesOpen(false);
            _status = "안개가 지나갔습니다. 다시 준비 완료를 누를 수 있습니다.";
        }

        private void RenderVolume()
        {
            if (_fluid == null || _fogVolumeRenderer == null) return;
            Bounds bounds = _fluid.WorldBounds;
            _fogVolumeRenderer.GetPropertyBlock(_block);
            _block.SetTexture("_FogDensityTex", _fluid.Density);
            _block.SetVector("_FogVolumeMin", bounds.min);
            _block.SetVector("_FogVolumeMax", bounds.max);
            _block.SetColor("_FogTint", _fogTint);
            _block.SetFloat("_FogExtinction", _extinction);
            _block.SetFloat("_FogShadowStrength", _shadowStrength);
            _block.SetFloat("_FogDetailStrength", _detailStrength);
            _block.SetFloat("_FogOpacity", _opacity);
            _fogVolumeRenderer.SetPropertyBlock(_block);
        }

        private void DisposeFog()
        {
            if (_fogVolumeRenderer != null)
            {
                _fogVolumeRenderer.enabled = false;
                _fogVolumeRenderer.SetPropertyBlock(null);
            }
            _fluid?.Dispose();
            _fluid = null;
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            DisposeFog();
            if (_running && _game != null) _game.SetRulesOpen(false);
            _running = false;
        }
    }
}
