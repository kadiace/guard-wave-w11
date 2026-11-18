using UnityEngine;

namespace Flood2
{
    public enum Flood2CitizenState { Safe, Warning, Critical, RescueRequired, Protected }

    /// <summary>Fixed citizen objective; danger is measured from occupied water, independently of rendering.</summary>
    public sealed class Flood2Citizen : MonoBehaviour
    {
        [Header("Identity and appearance")]
        [SerializeField] private string _citizenId;
        [SerializeField] private string _zone;
        [SerializeField] private Transform _visual;
        [SerializeField] private Renderer _marker;
        private MaterialPropertyBlock _tint;
        private float _dangerTime;
        private float _safeTime;
        private float _exposedSeconds;
        private float _previousPeakDepth;
        public string CitizenId => _citizenId;
        public string Zone => _zone;
        public float Exposure { get; private set; }
        public float Depth { get; private set; }
        public float Speed { get; private set; }
        public float Coverage { get; private set; }
        public float MaxDepth { get; private set; }
        public float ExposedSeconds => _exposedSeconds;
        public float SafeSeconds => _safeTime;
        public bool InDanger { get; private set; }
        public bool HasUnsettledWater { get; private set; }
        public Flood2CitizenState State { get; private set; }

        /// <summary>Assigns a stable identity and visual references when authoring the new scene.</summary>
        public void Configure(string id, string zone, Transform visual, Renderer marker)
        {
            _citizenId = id; _zone = zone; _visual = visual; _marker = marker;
        }

        /// <summary>Resets all danger history for a new or restored attempt.</summary>
        public void ResetState()
        {
            Exposure = Depth = Speed = Coverage = MaxDepth = _dangerTime = _safeTime = _exposedSeconds = 0f;
            _previousPeakDepth = 0f;
            InDanger = HasUnsettledWater = false;
            State = Flood2CitizenState.Safe;
        }

        /// <summary>Uses five water probes to update exposure and detect rising or fast water at the citizen.</summary>
        public void Observe(Flood2Water water, float dt)
        {
            if (State == Flood2CitizenState.RescueRequired || State == Flood2CitizenState.Protected) return;
            float depth = 0f;
            float speed = 0f;
            float peakDepth = 0f;
            bool movingWater = false;
            int wet = 0;
            for (int i = 0; i < 5; i++)
            {
                Vector3 offset = i == 0 ? Vector3.zero : new Vector3(Mathf.Cos((i - 1) * Mathf.PI * 0.5f), 0f, Mathf.Sin((i - 1) * Mathf.PI * 0.5f)) * 0.4f;
                Vector3 p = transform.position + offset;
                if (!water.Sample(p + Vector3.up * 0.08f, out float surface, out Vector2 flow) || surface <= p.y + 0.02f) continue;
                float probeDepth = surface - p.y;
                wet++;
                depth += probeDepth;
                speed += flow.magnitude;
                peakDepth = Mathf.Max(peakDepth, probeDepth);
                movingWater |= probeDepth >= 0.1f && flow.sqrMagnitude > 0.25f;
            }
            HasUnsettledWater = movingWater || peakDepth >= 0.1f && peakDepth - _previousPeakDepth > 0.1f * dt;
            _previousPeakDepth = peakDepth;
            Coverage = wet / 5f;
            Depth = wet > 0 ? depth / wet : 0f;
            Speed = wet > 0 ? speed / wet : 0f;
            MaxDepth = Mathf.Max(MaxDepth, Depth);
            InDanger = Coverage >= 0.25f && (Depth >= 0.45f || Depth >= 0.2f && Speed >= 2f);
            if (InDanger)
            {
                _safeTime = 0f;
                _dangerTime += dt;
                _exposedSeconds += dt;
                if (_dangerTime > 0.2f + 0.0001f) Exposure = Mathf.Min(100f, Exposure + (Depth >= 1.1f ? 25f : 12.5f) * dt);
            }
            else
            {
                _dangerTime = 0f;
                _safeTime += dt;
                if (_safeTime > 0.75f) Exposure = Mathf.Max(0f, Exposure - 10f * dt);
            }
            if (Exposure >= 100f) State = Flood2CitizenState.RescueRequired;
            else if (Exposure >= 70f || Depth >= 1.1f || State == Flood2CitizenState.Critical && Exposure >= 60f) State = Flood2CitizenState.Critical;
            else State = Exposure >= 30f || InDanger ? Flood2CitizenState.Warning : Flood2CitizenState.Safe;
        }

        /// <summary>Marks a citizen protected only after the stage's full flood completion rules pass.</summary>
        public void Protect() => State = Flood2CitizenState.Protected;

        private void Update()
        {
            _tint ??= new MaterialPropertyBlock();
            Color color = State == Flood2CitizenState.Critical || State == Flood2CitizenState.RescueRequired ? new Color(1f, 0.24f, 0.22f)
                : State == Flood2CitizenState.Warning ? new Color(1f, 0.73f, 0.23f) : new Color(0.2f, 0.85f, 0.7f);
            _tint.SetColor("_BaseColor", color);
            _marker.SetPropertyBlock(_tint);
            float sway = InDanger ? 5f : 1.5f;
            _visual.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 2f + transform.position.x) * sway);
        }
    }
}
