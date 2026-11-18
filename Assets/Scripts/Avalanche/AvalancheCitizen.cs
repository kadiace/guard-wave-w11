using System;

using UnityEngine;

namespace Avalanche
{
    public enum AvalancheCitizenState { Safe, Warning, Critical, RescueRequired, Protected }

    /// <summary>Player-placed citizen objective with water-based danger tracking.</summary>
    public sealed class AvalancheCitizen : MonoBehaviour
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
        public int PlacementId { get; private set; }
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
        public AvalancheCitizenState State { get; private set; }

        [Serializable]
        internal sealed class RiskSettings
        {
            [Header("Danger thresholds")]
            [SerializeField, Min(0f)] private float _probeRadius = 0.4f;
            [SerializeField, Range(0f, 1f)] private float _minimumWetCoverage = 0.25f;
            [SerializeField, Min(0f)] private float _dangerDepth = 0.45f;
            [SerializeField, Min(0f)] private float _fastWaterDepth = 0.2f;
            [SerializeField, Min(0f)] private float _dangerSpeed = 2f;
            [SerializeField, Min(0f)] private float _severeDepth = 1.1f;

            [Header("Exposure (points out of 100)")]
            [SerializeField, Min(0f)] private float _dangerDelaySeconds = 0.2f;
            [SerializeField, Min(0f)] private float _exposurePerSecond = 12.5f;
            [SerializeField, Min(0f)] private float _severeExposurePerSecond = 25f;
            [SerializeField, Min(0f)] private float _recoveryDelaySeconds = 0.75f;
            [SerializeField, Min(0f)] private float _recoveryPerSecond = 10f;

            [Header("Residual water")]
            [SerializeField, Min(0f)] private float _unsettledDepth = 0.1f;
            [SerializeField, Min(0f)] private float _unsettledSpeed = 0.5f;
            [SerializeField, Min(0f)] private float _unsettledRisePerSecond = 0.1f;
            public float ProbeRadius => _probeRadius;
            public float MinimumWetCoverage => _minimumWetCoverage;
            public float DangerDepth => _dangerDepth;
            public float FastWaterDepth => _fastWaterDepth;
            public float DangerSpeed => _dangerSpeed;
            public float SevereDepth => _severeDepth;
            public float DangerDelaySeconds => _dangerDelaySeconds;
            public float ExposurePerSecond => _exposurePerSecond;
            public float SevereExposurePerSecond => _severeExposurePerSecond;
            public float RecoveryDelaySeconds => _recoveryDelaySeconds;
            public float RecoveryPerSecond => _recoveryPerSecond;
            public float UnsettledDepth => _unsettledDepth;
            public float UnsettledSpeedSquared => _unsettledSpeed * _unsettledSpeed;
            public float UnsettledRisePerSecond => _unsettledRisePerSecond;
        }

        /// <summary>Assigns a stable identity and visual references when authoring the new scene.</summary>
        public void Configure(string id, string zone, Transform visual, Renderer marker)
        {
            _citizenId = id; _zone = zone; _visual = visual; _marker = marker;
        }

        /// <summary>배치 위치에 충돌체가 없는 시민 외형과 목표 컴포넌트를 생성한다.</summary>
        public static AvalancheCitizen Create(int id, Vector3 position, Transform parent, Material coat, Material skin, Material marker)
        {
            GameObject root = new GameObject($"Citizen {id + 1:00}");
            root.transform.SetParent(parent, false);
            root.transform.position = position;
            Transform visual = new GameObject("Visual").transform;
            visual.SetParent(root.transform, false);
            CreateVisual("Coat", PrimitiveType.Capsule, visual, Vector3.up * 0.85f, new Vector3(0.55f, 0.5f, 0.42f), coat);
            CreateVisual("Head", PrimitiveType.Sphere, visual, Vector3.up * 1.48f, Vector3.one * 0.36f, skin);
            for (int i = -1; i <= 1; i += 2)
            {
                CreateVisual("Boot", PrimitiveType.Cube, visual, new Vector3(i * 0.15f, 0.2f, 0f), new Vector3(0.18f, 0.4f, 0.25f), marker);
                GameObject arm = CreateVisual("Arm", PrimitiveType.Capsule, visual, new Vector3(i * 0.34f, 0.9f, 0f), new Vector3(0.15f, 0.27f, 0.15f), coat);
                arm.transform.localRotation = Quaternion.Euler(0f, 0f, i * 16f);
            }
            Renderer beacon = CreateVisual("Status beacon", PrimitiveType.Sphere, root.transform, Vector3.up * 2.05f, Vector3.one * 0.23f, marker).GetComponent<Renderer>();
            AvalancheCitizen citizen = root.AddComponent<AvalancheCitizen>();
            citizen.PlacementId = id;
            citizen.Configure($"시민 {id + 1:00}", "보호 대상", visual, beacon);
            citizen.ResetState();
            return citizen;
        }

        /// <summary>시민의 장식용 형상을 생성하고 배치 및 유체 계산에 충돌하지 않게 한다.</summary>
        private static GameObject CreateVisual(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.layer = 2;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.GetComponent<Collider>().enabled = false;
            Destroy(part.GetComponent<Collider>());
            part.GetComponent<Renderer>().sharedMaterial = material;
            return part;
        }

        /// <summary>Resets all danger history for a new or restored attempt.</summary>
        public void ResetState()
        {
            Exposure = Depth = Speed = Coverage = MaxDepth = _dangerTime = _safeTime = _exposedSeconds = 0f;
            _previousPeakDepth = 0f;
            InDanger = HasUnsettledWater = false;
            State = AvalancheCitizenState.Safe;
        }

        /// <summary>Uses five water probes to update exposure and detect rising or fast water at the citizen.</summary>
        internal void Observe(AvalancheWater water, float dt, RiskSettings settings)
        {
            if (State == AvalancheCitizenState.RescueRequired || State == AvalancheCitizenState.Protected) return;
            float depth = 0f;
            float speed = 0f;
            float peakDepth = 0f;
            bool movingWater = false;
            int wet = 0;
            for (int i = 0; i < 5; i++)
            {
                Vector3 offset = i == 0 ? Vector3.zero : new Vector3(Mathf.Cos((i - 1) * Mathf.PI * 0.5f), 0f, Mathf.Sin((i - 1) * Mathf.PI * 0.5f)) * settings.ProbeRadius;
                Vector3 p = transform.position + offset;
                if (!water.Sample(p + Vector3.up * 0.08f, out float surface, out Vector2 flow) || surface <= p.y + 0.02f) continue;
                float probeDepth = surface - p.y;
                wet++;
                depth += probeDepth;
                speed += flow.magnitude;
                peakDepth = Mathf.Max(peakDepth, probeDepth);
                movingWater |= probeDepth >= settings.UnsettledDepth && flow.sqrMagnitude > settings.UnsettledSpeedSquared;
            }
            HasUnsettledWater = movingWater || peakDepth >= settings.UnsettledDepth && peakDepth - _previousPeakDepth > settings.UnsettledRisePerSecond * dt;
            _previousPeakDepth = peakDepth;
            Coverage = wet / 5f;
            Depth = wet > 0 ? depth / wet : 0f;
            Speed = wet > 0 ? speed / wet : 0f;
            MaxDepth = Mathf.Max(MaxDepth, Depth);
            InDanger = wet > 0 && Coverage >= settings.MinimumWetCoverage &&
                (Depth >= settings.DangerDepth || Depth >= settings.FastWaterDepth && Speed >= settings.DangerSpeed);
            if (InDanger)
            {
                _safeTime = 0f;
                _dangerTime += dt;
                _exposedSeconds += dt;
                if (_dangerTime > settings.DangerDelaySeconds + 0.0001f)
                    Exposure = Mathf.Min(100f, Exposure + (Depth >= settings.SevereDepth ? settings.SevereExposurePerSecond : settings.ExposurePerSecond) * dt);
            }
            else
            {
                _dangerTime = 0f;
                _safeTime += dt;
                if (_safeTime > settings.RecoveryDelaySeconds) Exposure = Mathf.Max(0f, Exposure - settings.RecoveryPerSecond * dt);
            }
            if (Exposure >= 100f) State = AvalancheCitizenState.RescueRequired;
            else if (Exposure >= 70f || wet > 0 && Depth >= settings.SevereDepth || State == AvalancheCitizenState.Critical && Exposure >= 60f)
                State = AvalancheCitizenState.Critical;
            else State = Exposure >= 30f || InDanger ? AvalancheCitizenState.Warning : AvalancheCitizenState.Safe;
        }

        /// <summary>Marks a citizen protected only after the stage's full flood completion rules pass.</summary>
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
