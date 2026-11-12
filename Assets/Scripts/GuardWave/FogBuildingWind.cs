using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// Four independent +Z wall air-conditioners. Their world-space vents are
    /// consumed by the GPU gas solver; small Shuriken particles only visualize
    /// the real wind, without applying a second physical force.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FogBuildingWind : MonoBehaviour
    {
        [Header("Material catalog")]
        [SerializeField] private string materialId = "building";

        [Header("Outdoor air conditioners: local +Z")]
        [SerializeField] private Vector3 outletLocalPosition = new Vector3(0f, 2.05f, 3.49f);
        [SerializeField] private Vector3 outletLocalDirection = Vector3.forward;
        [SerializeField] private float[] additionalOutletHeights = { 7.05f, 12.05f, 17.05f };
        [SerializeField, Min(0f)]
        [Tooltip("Strength of each AC vent on the GPU fog simulation. Original: 28. Default reduced to 5.6 (20%).")]
        private float windAcceleration = 5.6f;
        [SerializeField, Min(0.1f)] private float reach = 14f;
        [SerializeField, Min(0.1f)] private float outletRadius = 16f;

        [Header("Visible airflow (cosmetic only)")]
        [Tooltip("Seconds each particle lives. Particle speed is computed as GPU wind reach / lifetime.")]
        [SerializeField, Min(0.05f)] private float particleLifetime = 1.15f;
        [SerializeField, Min(0f)] private float particlesPerSecond = 32f;
        [SerializeField] private Color particleTint = new Color(0.77f, 0.92f, 1f, 0.85f);

        private GameObject _particleRoot;
        private Material _particleMaterial;
        private Texture2D _particleTexture;

        public string MaterialId => materialId;
        public int OutletCount => 1 + (additionalOutletHeights != null ? additionalOutletHeights.Length : 0);
        public Vector3 OutletPosition => GetOutletPosition(0);
        public Vector3 OutletDirection => transform.TransformDirection(outletLocalDirection).normalized;
        public float WindAcceleration => Mathf.Max(0f, windAcceleration);
        public float Reach => Mathf.Max(0.1f, reach);
        public float OutletRadius => Mathf.Max(0.1f, outletRadius);

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
            if (Application.isPlaying && _particleRoot == null)
                CreateWindParticles();
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
            _particleTexture.name = "AC Wind Soft Particle";
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

            _particleMaterial = new Material(shader) { name = "AC Wind Particle (Runtime)" };
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

            _particleRoot = new GameObject("Air Conditioner Wind Particles");
            _particleRoot.transform.SetParent(transform, false);
            for (int i = 0; i < OutletCount; i++)
            {
                GameObject go = new GameObject("Vent " + (i + 1) + " Airflow");
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
                shape.radius = 0.27f;
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
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.25f, 0.88f, 1f, 0.85f);
            for (int i = 0; i < OutletCount; i++)
            {
                Vector3 start = GetOutletPosition(i);
                Vector3 direction = OutletDirection;
                Gizmos.DrawLine(start, start + direction * Reach);
                Gizmos.DrawWireSphere(start, 0.35f);
            }
        }
    }
}
