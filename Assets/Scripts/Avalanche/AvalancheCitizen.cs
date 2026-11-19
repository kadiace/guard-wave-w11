using System;

using UnityEngine;

namespace Avalanche
{
    public enum AvalancheCitizenState { Safe, Warning, Critical, RescueRequired, Protected }

    /// <summary>Scene-authored citizen objective with snow impact and burial tracking.</summary>
    public sealed class AvalancheCitizen : MonoBehaviour
    {
        [Header("Identity and appearance")]
        [SerializeField] private int _objectiveId;
        [SerializeField] private string _citizenId;
        [SerializeField] private string _zone;
        [SerializeField] private Transform _visual;
        [SerializeField] private Renderer _marker;
        private MaterialPropertyBlock _tint;
        private float _safeTime;
        public string CitizenId => _citizenId;
        public int ObjectiveId => _objectiveId;
        public string Zone => _zone;
        public float Exposure { get; private set; }
        public float Depth { get; private set; }
        public float Speed { get; private set; }
        public float MaxDepth { get; private set; }
        public bool InDanger { get; private set; }
        public AvalancheCitizenState State { get; private set; }
        public float ImpactSeconds { get; private set; }
        public float BurialSeconds { get; private set; }
        public bool IsSafe { get; private set; } = true;
        public string FailureReason { get; private set; }

        /// <summary>Assigns the authored identity and visual references without creating a runtime placement.</summary>
        public void Configure(int id, string zone, Transform visual, Renderer marker)
        {
            _objectiveId = id;
            _citizenId = $"시민 {id + 1:00}";
            _zone = zone;
            _visual = visual;
            _marker = marker;
            ResetState();
        }

        /// <summary>Resets both independent danger histories for an edited or restored attempt.</summary>
        public void ResetState()
        {
            ImpactSeconds = BurialSeconds = Exposure = Depth = Speed = MaxDepth = _safeTime = 0f;
            InDanger = false;
            IsSafe = true;
            FailureReason = "";
            State = AvalancheCitizenState.Safe;
        }

        [Serializable]
        internal sealed class RiskSettings
        {
            [SerializeField, Min(0f)] private float _probeRadius = 0.4f;
            [SerializeField, Min(0f)] private float _impactDepth = 0.35f;
            [SerializeField, Min(0f)] private float _impactSpeed = 2f;
            [SerializeField, Min(0.1f)] private float _impactLimit = 1.5f;
            [SerializeField, Min(0f)] private float _impactRecovery = 2f;
            [SerializeField, Min(0f)] private float _burialDepth = 0.35f;
            [SerializeField, Min(0.1f)] private float _burialLimit = 2f;
            public float ProbeRadius => _probeRadius;
            public float ImpactDepth => _impactDepth;
            public float ImpactSpeed => _impactSpeed;
            public float ImpactLimit => _impactLimit;
            public float ImpactRecovery => _impactRecovery;
            public float BurialDepth => _burialDepth;
            public float BurialLimit => _burialLimit;
        }

        /// <summary>Observes five snow probes, accumulating recoverable impact and continuous burial independently.</summary>
        internal void Observe(AvalancheSnow snow, float dt, RiskSettings settings)
        {
            if (State == AvalancheCitizenState.RescueRequired || State == AvalancheCitizenState.Protected) return;
            int impacts = 0, burials = 0;
            Depth = Speed = 0f;
            for (int i = 0; i < 5; i++)
            {
                Vector3 offset = i == 0 ? Vector3.zero : new Vector3(Mathf.Cos((i - 1) * Mathf.PI * 0.5f), 0f, Mathf.Sin((i - 1) * Mathf.PI * 0.5f)) * settings.ProbeRadius;
                snow.Sample(transform.position + offset, out float moving, out float deposited, out Vector2 velocity);
                if (moving >= settings.ImpactDepth && velocity.magnitude >= settings.ImpactSpeed) impacts++;
                if (moving + deposited >= settings.BurialDepth) burials++;
                Depth = Mathf.Max(Depth, moving + deposited);
                Speed = Mathf.Max(Speed, velocity.magnitude);
            }
            bool impact = impacts >= 2, burial = burials >= 2;
            if (impact) { ImpactSeconds += dt; _safeTime = 0f; }
            else
            {
                _safeTime += dt;
                if (_safeTime >= settings.ImpactRecovery) ImpactSeconds = 0f;
            }
            BurialSeconds = burial ? BurialSeconds + dt : 0f;
            InDanger = impact || burial;
            IsSafe = !InDanger && ImpactSeconds == 0f;
            MaxDepth = Mathf.Max(MaxDepth, Depth);
            Exposure = Mathf.Max(ImpactSeconds / settings.ImpactLimit, BurialSeconds / settings.BurialLimit) * 100f;
            if (BurialSeconds + 0.0001f >= settings.BurialLimit) FailureReason = "매몰";
            else if (ImpactSeconds + 0.0001f >= settings.ImpactLimit) FailureReason = "충돌";
            if (!string.IsNullOrEmpty(FailureReason)) State = AvalancheCitizenState.RescueRequired;
            else State = Exposure >= 75f ? AvalancheCitizenState.Critical : Exposure >= 50f || InDanger ? AvalancheCitizenState.Warning : AvalancheCitizenState.Safe;
        }

        /// <summary>Marks a citizen protected only after the stage's full snow completion rules pass.</summary>
        public void Protect() => State = AvalancheCitizenState.Protected;

        private void Update()
        {
            _tint ??= new MaterialPropertyBlock();
            Color color = State == AvalancheCitizenState.Critical || State == AvalancheCitizenState.RescueRequired ? new Color(1f, 0.24f, 0.22f)
                : State == AvalancheCitizenState.Warning ? new Color(1f, 0.73f, 0.23f) : new Color(0.2f, 0.85f, 0.7f);
            _tint.SetColor("_BaseColor", color);
            _marker.SetPropertyBlock(_tint);
            float sway = InDanger ? 5f : 1.5f;
            _visual.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 2f + transform.position.x) * sway);
        }
    }
}
