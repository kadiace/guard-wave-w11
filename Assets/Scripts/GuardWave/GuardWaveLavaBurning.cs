using UnityEngine;

namespace GuardWave
{
    /// <summary>
    /// Logs a body's first GPU/SDF lava contact, its short visible ignition,
    /// and the final destruction decision. Each contacted Rigidbody gets one.
    /// </summary>
    public sealed class GuardWaveLavaBurning : MonoBehaviour
    {
        private GuardWaveGame _game;
        private Rigidbody _body;
        private GuardWavePiece _piece;
        private ParticleSystem _fire;
        private Material _fireMaterial;
        private float _seconds;
        private float _duration;
        private float _nextLog;
        private bool _isFlag;
        private bool _completed;
        private string _label;

        public void Ignite(GuardWaveGame game, Rigidbody body,
            GuardWavePiece piece, bool isFlag, float skin, float duration)
        {
            _game = game;
            _body = body;
            _piece = piece;
            _isFlag = isFlag;
            _duration = Mathf.Max(0.05f, duration);
            _nextLog = 0.25f;
            _label = $"{gameObject.name}#{gameObject.GetInstanceID()}";
            string material = piece != null ? piece.MaterialId : (isFlag ? "default_flag" : "unknown");
            float density = piece != null ? piece.RelativeFluidDensity : 0f;
            Debug.Log($"[LavaDestroy][CONTACT] object={_label} material={material} " +
                $"isFlag={isFlag} mass={body.mass:F2} relativeDensity={density:F2} " +
                $"position={body.position.ToString("F2")} simulationTime={Time.fixedTime:F3}s " +
                $"GPU_contact=TRUE criterion=SDF<CollisionSkin skin={skin:F3}m " +
                $"requiresRepeatedHits=FALSE requiresDamage=FALSE " +
                $"burnThreshold={_duration:F3}s", this);
            CreateFire();
        }

        private void FixedUpdate()
        {
            if (_completed || _body == null || _game == null) return;
            _seconds += Time.fixedDeltaTime;
            if (_seconds >= _duration)
            {
                _completed = true;
                Debug.Log($"[LavaDestroy][DESTROY] object={_label} " +
                    $"burnElapsed={_seconds:F3}s burnThreshold={_duration:F3}s " +
                    $"thresholdReached=TRUE reason=GPU_LAVA_CONTACT " +
                    $"position={_body.position.ToString("F2")} ", this);
                _game.FinishLavaBurn(this, _body, _piece, _isFlag);
                return;
            }
            if (_seconds >= _nextLog)
            {
                Debug.Log($"[LavaDestroy][BURNING] object={_label} " +
                    $"elapsed={_seconds:F3}s duration={_duration:F3}s " +
                    $"remaining={Mathf.Max(0f, _duration-_seconds):F3}s " +
                    $"decision=DESTROY_PENDING flameActive={_fire != null}", this);
                _nextLog += 0.25f;
            }
        }

        public void Cancel()
        {
            _completed = true;
            if (_fire != null) Destroy(_fire.gameObject);
            Destroy(this);
        }

        private void CreateFire()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(false);
            Bounds bounds = new Bounds(transform.position, Vector3.one);
            bool hasBounds = false;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            if (!hasBounds)
            {
                Collider collider = GetComponent<Collider>();
                if (collider != null) bounds = collider.bounds;
            }

            GameObject root = new GameObject("LavaFire");
            root.transform.SetParent(transform, true);
            root.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);
            Vector3 scale = root.transform.lossyScale;
            root.transform.localScale = new Vector3(
                root.transform.localScale.x / Mathf.Max(.0001f, Mathf.Abs(scale.x)),
                root.transform.localScale.y / Mathf.Max(.0001f, Mathf.Abs(scale.y)),
                root.transform.localScale.z / Mathf.Max(.0001f, Mathf.Abs(scale.z)));

            _fire = root.AddComponent<ParticleSystem>();
            var main = _fire.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(.28f, .60f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.9f, 2.5f);
            float size = Mathf.Clamp(.2f + bounds.size.magnitude * .065f, .27f, .8f);
            main.startSize = new ParticleSystem.MinMaxCurve(size * .5f, size);
            main.startColor = new Color(1f, .5f, .1f, 1f);
            main.maxParticles = 320;
            main.gravityModifier = -.035f;

            var emission = _fire.emission;
            emission.rateOverTime = Mathf.Clamp(bounds.size.magnitude * 14f, 26f, 105f);
            var shape = _fire.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(
                Mathf.Max(.25f, bounds.size.x * .85f),
                Mathf.Max(.15f, bounds.size.y * .85f),
                Mathf.Max(.25f, bounds.size.z * .85f));

            var col = _fire.colorOverLifetime;
            col.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(new[]
            {
                new GradientColorKey(new Color(1f, .94f, .27f), 0f),
                new GradientColorKey(new Color(1f, .45f, .05f), .4f),
                new GradientColorKey(new Color(.9f, .08f, .02f), 1f)
            }, new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, .18f),
                new GradientAlphaKey(.7f, .7f),
                new GradientAlphaKey(0f, 1f)
            });
            col.color = new ParticleSystem.MinMaxGradient(gradient);

            var sizes = _fire.sizeOverLifetime;
            sizes.enabled = true;
            sizes.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, .6f), new Keyframe(.4f, 1f),
                    new Keyframe(1f, .1f)));

            ParticleSystemRenderer particleRenderer = root.GetComponent<ParticleSystemRenderer>();
            particleRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            particleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            particleRenderer.receiveShadows = false;
            Shader shader = Resources.Load<Shader>("Textures/GuardWaveLavaFlame");
            if (shader != null)
            {
                _fireMaterial = new Material(shader) { name = "Lava fire (runtime)" };
                particleRenderer.sharedMaterial = _fireMaterial;
            }
            else
                Debug.LogWarning("[LavaDestroy] Lava flame shader was not found.", this);
            _fire.Play();
        }

        private void OnDestroy()
        {
            if (_fireMaterial != null) Destroy(_fireMaterial);
        }
    }
}
