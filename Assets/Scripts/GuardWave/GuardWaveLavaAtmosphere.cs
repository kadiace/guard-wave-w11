using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Smoke from GPU-confirmed lava XZ columns; independent, glowing ember
    /// dust throughout the map. Only instantiated in the Lava scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GuardWaveLavaAtmosphere : MonoBehaviour
    {
        private GuardWaveFluidSettings _settings;
        private GuardWaveFlipFluid _fluid;
        private ParticleSystem _smoke;
        private ParticleSystem _embers;
        private Material _smokeMaterial;
        private Material _emberMaterial;
        private float _smokeCredit;

        public void Initialize(GuardWaveFluidSettings settings, Bounds map)
        {
            _settings = settings;
            CreateSmoke();
            CreateEmbers(map);
        }

        internal void BindFluid(GuardWaveFlipFluid fluid)
        {
            _fluid = fluid;
            _smokeCredit = 0f;
        }

        private void Update()
        {
            if (_fluid == null || _smoke == null || _settings == null) return;
            var surface = _fluid.LavaSmokePositions;
            if (surface == null || surface.Count == 0) return;
            _smokeCredit = Mathf.Min(18f,
                _smokeCredit + Time.deltaTime * _settings.LavaSmokeParticlesPerSecond);
            int emitCount = Mathf.Min(18, Mathf.FloorToInt(_smokeCredit));
            _smokeCredit -= emitCount;
            for (int i = 0; i < emitCount; ++i)
            {
                Vector3 cell = surface[Random.Range(0, surface.Count)];
                ParticleSystem.EmitParams p = new ParticleSystem.EmitParams
                {
                    position = cell + new Vector3(Random.Range(-.22f, .22f),
                        Random.Range(.05f, .2f), Random.Range(-.22f, .22f)),
                    velocity = new Vector3(Random.Range(-.3f, .3f),
                        Random.Range(.8f, 1.7f), Random.Range(-.3f, .3f)),
                    startSize = Random.Range(.65f, 1.55f),
                    startLifetime = Random.Range(3f, 5.6f),
                    startColor = new Color(Random.Range(.15f, .29f),
                        Random.Range(.12f, .25f),
                        Random.Range(.11f, .23f), Random.Range(.32f, .48f))
                };
                _smoke.Emit(p, 1);
            }
        }

        private void CreateSmoke()
        {
            GameObject go = new GameObject("Lava Smoke - Physical FLIP Surface");
            go.transform.SetParent(transform, false);
            _smoke = go.AddComponent<ParticleSystem>();
            var main = _smoke.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startSpeed = 0f;
            main.startLifetime = 4.5f;
            main.startSize = 1f;
            main.maxParticles = 900;
            var shape = _smoke.shape;
            shape.enabled = false;
            var emission = _smoke.emission;
            emission.rateOverTime = 0f;
            var color = _smoke.colorOverLifetime;
            color.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(new[]
            {
                new GradientColorKey(new Color(.19f, .16f, .15f), 0f),
                new GradientColorKey(new Color(.34f, .31f, .29f), 1f)
            }, new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(.8f, .15f),
                new GradientAlphaKey(.6f, .55f),
                new GradientAlphaKey(0f, 1f)
            });
            color.color = new ParticleSystem.MinMaxGradient(gradient);
            var sizes = _smoke.sizeOverLifetime;
            sizes.enabled = true;
            sizes.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, .35f),
                    new Keyframe(.55f, 1.45f), new Keyframe(1f, 2.3f)));
            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Shader shader = Resources.Load<Shader>("Textures/GuardWaveLavaSmoke");
            if (shader != null)
            {
                _smokeMaterial = new Material(shader);
                _smokeMaterial.name = "Lava smoke runtime";
                renderer.sharedMaterial = _smokeMaterial;
            }
            else Debug.LogWarning("[LavaVFX] Lava smoke shader missing.", this);
            _smoke.Play();
        }

        private void CreateEmbers(Bounds map)
        {
            GameObject go = new GameObject("Lava Ember Dust - Map-wide Emission");
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(map.center.x, map.max.y + 3.2f, map.center.z);
            _embers = go.AddComponent<ParticleSystem>();
            var main = _embers.main;
            main.playOnAwake = false;
            main.loop = true;
            main.prewarm = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 8.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(.045f, .15f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.2f, .65f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, .24f, .025f, .65f),
                new Color(1f, .8f, .18f, 1f));
            main.maxParticles = 1200;
            main.gravityModifier = -.015f;
            var shape = _embers.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(Mathf.Max(24f, map.size.x + 12f), 8.5f,
                Mathf.Max(34f, map.size.z + 12f));
            var emission = _embers.emission;
            emission.enabled = true;
            emission.rateOverTime = _settings.LavaEmberParticlesPerSecond;
            var forces = _embers.forceOverLifetime;
            forces.enabled = true;
            forces.x = new ParticleSystem.MinMaxCurve(-.09f, .09f);
            forces.y = new ParticleSystem.MinMaxCurve(.04f, .18f);
            forces.z = new ParticleSystem.MinMaxCurve(-.09f, .09f);
            var color = _embers.colorOverLifetime;
            color.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(new[]
            {
                new GradientColorKey(new Color(1f, .35f, .08f), 0f),
                new GradientColorKey(new Color(1f, .92f, .40f), .6f),
                new GradientColorKey(new Color(.85f, .18f, .025f), 1f)
            }, new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, .25f),
                new GradientAlphaKey(.8f, .75f),
                new GradientAlphaKey(0f, 1f)
            });
            color.color = new ParticleSystem.MinMaxGradient(gradient);
            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Shader shader = Resources.Load<Shader>("Textures/GuardWaveLavaFlame");
            if (shader != null)
            {
                _emberMaterial = new Material(shader);
                _emberMaterial.name = "HDR Lava Ember Dust";
                renderer.sharedMaterial = _emberMaterial;
            }
            else Debug.LogWarning("[LavaVFX] Lava flame shader missing.", this);
            _embers.Play();
        }

        private void OnDestroy()
        {
            if (_smokeMaterial != null) Destroy(_smokeMaterial);
            if (_emberMaterial != null) Destroy(_emberMaterial);
        }
    }
}
