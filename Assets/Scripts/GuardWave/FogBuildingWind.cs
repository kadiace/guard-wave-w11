using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace GuardWave
{
    /// <summary>
    /// A directed fan outlet affects the GPU gas solver; Shuriken particles
    /// visualize the airflow without applying a second physical force.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FogBuildingWind : MonoBehaviour
    {
        [Header("Material catalog")]
        [SerializeField] private string materialId = "building";

        [Header("Fan outlet")]
        [SerializeField] private Vector3 outletLocalPosition = new Vector3(0f, 2.05f, -0.7f);
        [SerializeField] private Vector3 outletLocalDirection = Vector3.forward;
        [SerializeField] private float[] additionalOutletHeights = { };
        [SerializeField, Min(0f)]
        [Tooltip("Strength of the fan outlet on the GPU fog simulation.")]
        private float windAcceleration = 14f;
        [SerializeField, Min(0.1f)] private float reach = 4f;
        [SerializeField, Min(0.1f)] private float outletRadius = 0.72f;

        [Header("Toxic gas durability")]
        [Tooltip("Point sampled for gas exposure, placed just outside the fan pole.")]
        [SerializeField] private Vector3 _gasExposureLocalPosition = new Vector3(0.18f, 0.9f, -0.035f);
        [Tooltip("Cumulative gameplay seconds above the density threshold before the fan stops.")]
        [SerializeField, Min(0.1f)] private float _gasDurabilitySeconds = 20f;
        [Tooltip("Gas density at which the fan starts consuming durability.")]
        [SerializeField, Range(0f, 1f)] private float _gasDamageDensityThreshold = 0.01f;

        [Header("Durability billboard")]
        [SerializeField] private bool _showDurabilityBillboard;
        [SerializeField] private Vector3 _durabilityBillboardLocalOffset = new Vector3(0f, 2.85f, 0f);
        [SerializeField, Min(0.001f)] private float _durabilityBillboardScale = 0.01f;

        [Header("Visible airflow (cosmetic only)")]
        [Tooltip("Seconds each particle lives. Particle speed is computed as GPU wind reach / lifetime.")]
        [SerializeField, Min(0.05f)] private float particleLifetime = 1.15f;
        [SerializeField, Min(0f)] private float particlesPerSecond = 32f;
        [SerializeField] private Color particleTint = new Color(0.77f, 0.92f, 1f, 0.85f);

        private GameObject _particleRoot;
        private Material _particleMaterial;
        private Texture2D _particleTexture;
        private float _remainingGasDurability;
        private Canvas _durabilityCanvas;
        private Image _durabilityFill;
        private Texture2D _durabilityBarTexture;
        private Sprite _durabilityBarSprite;
        private Camera _billboardCamera;
        private float _lastDisplayedDurability = -1f;
        private Color _lastDurabilityColor;

        public string MaterialId => materialId;
        public int OutletCount => 1 + (additionalOutletHeights != null ? additionalOutletHeights.Length : 0);
        public Vector3 OutletPosition => GetOutletPosition(0);
        public Vector3 OutletDirection => transform.TransformDirection(outletLocalDirection).normalized;
        public Vector3 GasExposurePosition => transform.TransformPoint(_gasExposureLocalPosition);
        public float GasDurabilityRemaining => Mathf.Max(0f, _remainingGasDurability);
        public bool IsOperational => _remainingGasDurability > 0f;
        public float WindAcceleration => IsOperational ? Mathf.Max(0f, windAcceleration) : 0f;
        public float Reach => Mathf.Max(0.1f, reach);
        public float OutletRadius => Mathf.Max(0.1f, outletRadius);

        private void Awake()
        {
            _remainingGasDurability = Mathf.Max(0f, _gasDurabilitySeconds);
            _billboardCamera = Camera.main;
        }

        public Vector3 GetOutletPosition(int index)
        {
            Vector3 local = outletLocalPosition;
            if (index > 0 && additionalOutletHeights != null && index <= additionalOutletHeights.Length)
                local.y = additionalOutletHeights[index - 1];
            return transform.TransformPoint(local);
        }

        private Vector3 GetLocalOutletPosition(int index)
        {
            Vector3 local = outletLocalPosition;
            if (index > 0 && additionalOutletHeights != null && index <= additionalOutletHeights.Length)
                local.y = additionalOutletHeights[index - 1];
            return local;
        }

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            if (_showDurabilityBillboard && _durabilityCanvas == null)
                CreateDurabilityBillboard();
            if (_particleRoot == null)
                CreateWindParticles();
            else if (IsOperational)
                StartWindParticles();
        }

        private void LateUpdate()
        {
            if (_durabilityCanvas == null) return;
            float normalizedDurability = _gasDurabilitySeconds > 0f
                ? Mathf.Clamp01(GasDurabilityRemaining / _gasDurabilitySeconds)
                : 0f;
            Color durabilityColor = normalizedDurability > 0.5f
                ? new Color(0.20f, 0.88f, 0.40f, 1f)
                : normalizedDurability > 0.25f
                    ? new Color(1f, 0.68f, 0.16f, 1f)
                    : new Color(0.95f, 0.22f, 0.18f, 1f);
            if (!Mathf.Approximately(normalizedDurability, _lastDisplayedDurability))
            {
                _durabilityFill.fillAmount = normalizedDurability;
                _lastDisplayedDurability = normalizedDurability;
            }
            if (durabilityColor != _lastDurabilityColor)
            {
                _durabilityFill.color = durabilityColor;
                _lastDurabilityColor = durabilityColor;
            }

            if (_billboardCamera == null) _billboardCamera = Camera.main;
            if (_billboardCamera == null) return;
            if (_durabilityCanvas.worldCamera != _billboardCamera)
                _durabilityCanvas.worldCamera = _billboardCamera;

            Vector3 toCamera = _billboardCamera.transform.position - _durabilityCanvas.transform.position;
            if (toCamera.sqrMagnitude > 0.0001f)
                _durabilityCanvas.transform.rotation = Quaternion.LookRotation(toCamera, Vector3.up);
        }

        /// <summary>Creates a camera-facing world-space health bar above this fan.</summary>
        private void CreateDurabilityBillboard()
        {
            _durabilityBarTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "Fan Durability Bar Texture"
            };
            _durabilityBarTexture.SetPixel(0, 0, Color.white);
            _durabilityBarTexture.Apply(false, true);
            _durabilityBarSprite = Sprite.Create(
                _durabilityBarTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
            _durabilityBarSprite.name = "Fan Durability Bar Sprite";

            GameObject root = new GameObject("Fan Durability Billboard", typeof(RectTransform), typeof(Canvas));
            root.transform.SetParent(transform, false);
            root.transform.localPosition = _durabilityBillboardLocalOffset;
            root.transform.localScale = Vector3.one * _durabilityBillboardScale;

            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(120f, 16f);

            _durabilityCanvas = root.GetComponent<Canvas>();
            _durabilityCanvas.renderMode = RenderMode.WorldSpace;
            _durabilityCanvas.overrideSorting = true;
            _durabilityCanvas.sortingOrder = 20;
            _durabilityCanvas.worldCamera = _billboardCamera;

            GameObject backgroundObject = new GameObject(
                "Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            backgroundObject.transform.SetParent(root.transform, false);
            RectTransform backgroundRect = backgroundObject.GetComponent<RectTransform>();
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;

            Image background = backgroundObject.GetComponent<Image>();
            background.sprite = _durabilityBarSprite;
            background.color = new Color(0.035f, 0.055f, 0.07f, 0.92f);
            background.raycastTarget = false;

            GameObject fillObject = new GameObject(
                "Durability", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            fillObject.transform.SetParent(backgroundObject.transform, false);
            RectTransform fillRect = fillObject.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(2f, 2f);
            fillRect.offsetMax = new Vector2(-2f, -2f);

            _durabilityFill = fillObject.GetComponent<Image>();
            _durabilityFill.sprite = _durabilityBarSprite;
            _durabilityFill.type = Image.Type.Filled;
            _durabilityFill.fillMethod = Image.FillMethod.Horizontal;
            _durabilityFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _durabilityFill.raycastTarget = false;
        }

        /// <summary>Consumes durability while sampled gas density reaches the damage threshold.</summary>
        public void ApplyGasExposure(float density, float elapsedSeconds)
        {
            if (!IsOperational || elapsedSeconds <= 0f || density < _gasDamageDensityThreshold)
                return;

            _remainingGasDurability = Mathf.Max(0f, _remainingGasDurability - elapsedSeconds);
            if (_remainingGasDurability <= 0f)
                StopWindParticles();
        }

        /// <summary>Restores the remaining durability saved before a Fog wave.</summary>
        public void RestoreGasDurability(float remainingSeconds)
        {
            _remainingGasDurability = Mathf.Clamp(
                remainingSeconds, 0f, Mathf.Max(0f, _gasDurabilitySeconds));
            if (!IsOperational)
                StopWindParticles();
            else if (isActiveAndEnabled)
                StartWindParticles();
        }

        /// <summary>Resumes airflow particles while the fan has remaining durability.</summary>
        private void StartWindParticles()
        {
            if (_particleRoot == null) return;
            foreach (ParticleSystem system in _particleRoot.GetComponentsInChildren<ParticleSystem>(true))
                system.Play(true);
        }

        /// <summary>Stops emitted airflow particles after gas exposure exhausts the fan's durability.</summary>
        private void StopWindParticles()
        {
            if (_particleRoot == null) return;
            foreach (ParticleSystem system in _particleRoot.GetComponentsInChildren<ParticleSystem>(true))
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        private void CreateWindParticles()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return;

            // Soft radial alpha, so the air forms moving wisps rather than solid squares.
            const int size = 32;
            _particleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _particleTexture.name = "Fan Wind Soft Particle";
            _particleTexture.filterMode = FilterMode.Bilinear;
            _particleTexture.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - (size - 1) * 0.5f) / (size * 0.5f);
                    float dy = (y - (size - 1) * 0.5f) / (size * 0.5f);
                    float softness = Mathf.Pow(Mathf.Clamp01(1f - dx * dx - dy * dy), 2.5f);
                    _particleTexture.SetPixel(x, y, new Color(1f, 1f, 1f, softness));
                }
            }
            _particleTexture.Apply(false, true);

            _particleMaterial = new Material(shader) { name = "Fan Wind Particle (Runtime)" };
            if (_particleMaterial.HasProperty("_BaseMap"))
                _particleMaterial.SetTexture("_BaseMap", _particleTexture);
            if (_particleMaterial.HasProperty("_MainTex"))
                _particleMaterial.SetTexture("_MainTex", _particleTexture);
            if (_particleMaterial.HasProperty("_BaseColor"))
                _particleMaterial.SetColor("_BaseColor", Color.white);
            if (_particleMaterial.HasProperty("_Color"))
                _particleMaterial.SetColor("_Color", Color.white);
            if (_particleMaterial.HasProperty("_Surface"))
                _particleMaterial.SetFloat("_Surface", 1f);
            if (_particleMaterial.HasProperty("_Blend"))
                _particleMaterial.SetFloat("_Blend", 0f);
            if (_particleMaterial.HasProperty("_ZWrite"))
                _particleMaterial.SetFloat("_ZWrite", 0f);
            if (_particleMaterial.HasProperty("_SrcBlend"))
                _particleMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (_particleMaterial.HasProperty("_DstBlend"))
                _particleMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            _particleMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _particleMaterial.SetOverrideTag("RenderType", "Transparent");
            _particleMaterial.renderQueue = (int)RenderQueue.Transparent;

            _particleRoot = new GameObject("Fan Wind Particles");
            _particleRoot.transform.SetParent(transform, false);
            for (int i = 0; i < OutletCount; i++)
            {
                GameObject go = new GameObject("Fan Outlet " + (i + 1) + " Airflow");
                go.transform.SetParent(_particleRoot.transform, false);
                go.transform.localPosition = GetLocalOutletPosition(i);
                go.transform.localRotation =
                    Quaternion.FromToRotation(Vector3.forward,
                        outletLocalDirection.sqrMagnitude > 0f
                            ? outletLocalDirection.normalized : Vector3.forward);

                ParticleSystem system = go.AddComponent<ParticleSystem>();
                system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ParticleSystem.MainModule main = system.main;
                main.duration = 2f;
                main.loop = true;
                main.playOnAwake = false;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                // The GPU applies each fan up to Reach metres along its axis.
                // With a fixed lifetime, speed * lifetime = that same distance.
                // WindAcceleration controls GPU force intensity, not GPU reach.
                float lifetime = Mathf.Max(0.05f, particleLifetime);
                main.startLifetime = lifetime;
                main.startSpeed = WindAcceleration > 0f ? Reach / lifetime : 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.11f, 0.25f);
                main.startColor = particleTint;
                main.maxParticles = 128;

                ParticleSystem.EmissionModule emission = system.emission;
                emission.enabled = true;
                emission.rateOverTime = WindAcceleration > 0f ? particlesPerSecond : 0f;

                ParticleSystem.ShapeModule shape = system.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 13f;
                shape.radius = OutletRadius;
                shape.length = 0.08f;

                Gradient fade = new Gradient();
                fade.SetKeys(
                    new[]
                    {
                        new GradientColorKey(Color.white, 0f),
                        new GradientColorKey(new Color(0.74f, 0.91f, 1f), 1f)
                    },
                    new[]
                    {
                        new GradientAlphaKey(0f, 0f),
                        new GradientAlphaKey(0.65f, 0.16f),
                        new GradientAlphaKey(0.40f, 0.65f),
                        new GradientAlphaKey(0f, 1f)
                    });
                ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
                color.enabled = true;
                color.color = new ParticleSystem.MinMaxGradient(fade);

                ParticleSystem.NoiseModule noise = system.noise;
                noise.enabled = true;
                noise.strength = 0.22f;
                noise.frequency = 0.6f;
                noise.scrollSpeed = 0.3f;

                ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.sharedMaterial = _particleMaterial;
                system.Play();
            }
        }

        private void OnDestroy()
        {
            if (_particleMaterial != null) Destroy(_particleMaterial);
            if (_particleTexture != null) Destroy(_particleTexture);
            if (_durabilityBarSprite != null) Destroy(_durabilityBarSprite);
            if (_durabilityBarTexture != null) Destroy(_durabilityBarTexture);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.25f, 0.88f, 1f, 0.85f);
            for (int i = 0; i < OutletCount; i++)
            {
                Vector3 start = GetOutletPosition(i);
                Vector3 direction = OutletDirection;
                Gizmos.DrawLine(start, start + direction * Reach);
                Gizmos.DrawWireSphere(start, OutletRadius);
            }
        }
    }
}
