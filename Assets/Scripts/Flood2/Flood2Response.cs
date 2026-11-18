using UnityEngine;

namespace Flood2
{
    /// <summary>Assigns the village crews and simulates travel along marked high-ground routes.</summary>
    public sealed class Flood2Response : MonoBehaviour
    {
        private const float WALKER_RADIUS = 0.28f;
        private static readonly Color ROUTE_OPEN = new Color(0.26f, 0.9f, 0.75f);
        private static readonly Color ROUTE_IDLE = new Color(0.50f, 0.67f, 0.63f, 0.75f);
        private static readonly Color ROUTE_BLOCKED = new Color(1f, 0.52f, 0.23f);
        private static readonly Color DRAIN_OPEN = new Color(0.23f, 0.72f, 1f);

        [Header("Evacuation")]
        [SerializeField, Min(0.1f)] private float _walkSpeed = 1.25f;
        [SerializeField, Min(0.01f)] private float _stopDepth = 0.18f;
        [SerializeField, Min(0.01f)] private float _stopFastWaterDepth = 0.08f;
        [SerializeField, Min(0.01f)] private float _stopWaterSpeed = 0.9f;

        [Header("Cleared storm drain")]
        [SerializeField, Range(0.3f, 4f)] private float _drainRadius = 1.8f;
        [SerializeField, Min(0f)] private float _drainCapacity = 1.15f;
        private readonly Zone[] _zones = new Zone[3];
        private Flood2Stage _stage;
        private MaterialPropertyBlock _tint;
        public int GuideZone { get; private set; } = -1;
        public int DrainZone { get; private set; } = -1;
        public float DrainRate { get; private set; }
        public float DrainedVolume { get; private set; }
        public bool DrainCovered { get; private set; }
        public int ShelteredCount => GuideZone < 0 ? 0 : _zones[GuideZone].ShelteredCount;
        public string RouteHoldReason => GuideZone < 0 ? "" : _zones[GuideZone].HoldReason;

        private sealed class Zone
        {
            public Vector3 Center { get; set; }
            public Vector3 Approach { get; set; }
            public Vector3 Landing { get; set; }
            public Vector3 Drain { get; set; }
            public Vector3[] Homes { get; } = new Vector3[2];
            public int[] Waypoints { get; } = new int[2];
            public int ShelteredCount { get; set; }
            public string HoldReason { get; set; } = "";
            public LineRenderer Route { get; set; }
            public Renderer Grate { get; set; }
        }

        /// <summary>Builds visible, non-solid markers for the three routes, landings and drain inlets.</summary>
        internal void Initialize(Flood2Stage stage)
        {
            _stage = stage;
            _tint = new MaterialPropertyBlock();
            CreateZone(0, new Vector2(-13.7f, 26f), new Vector2(-15.9f, 23.2f), new Vector2(-11.2f, 25.4f));
            CreateZone(1, new Vector2(0f, 12.9f), new Vector2(0f, 9.6f), new Vector2(0.15f, 14.7f));
            CreateZone(2, new Vector2(13.4f, 14f), new Vector2(15.9f, 17.4f), new Vector2(11.2f, 13.5f));
            RefreshMarkers();
        }

        /// <summary>Moves the single evacuation crew to the chosen zone, or cancels that assignment.</summary>
        public void AssignGuide(int zone)
        {
            if (!_stage.CanEdit || zone < 0 || zone >= _zones.Length) return;
            GuideZone = GuideZone == zone ? -1 : zone;
            RefreshMarkers();
        }

        /// <summary>Chooses the drain the maintenance crew clears before the flood.</summary>
        public void AssignDrain(int zone)
        {
            if (!_stage.CanEdit || zone < 0 || zone >= _zones.Length) return;
            DrainZone = DrainZone == zone ? -1 : zone;
            RefreshMarkers();
        }

        /// <summary>Restores citizens and clears counters; checkpoint retries keep the crew assignments.</summary>
        internal void ResetResponse(bool empty)
        {
            if (empty) GuideZone = DrainZone = -1;
            DrainRate = DrainedVolume = 0f;
            DrainCovered = false;
            for (int i = 0; i < _zones.Length; i++)
            {
                Zone zone = _zones[i];
                zone.HoldReason = "";
                zone.ShelteredCount = 0;
                for (int j = 0; j < 2; j++)
                {
                    _stage.Citizens[i * 2 + j].transform.position = zone.Homes[j];
                    zone.Waypoints[j] = 0;
                }
            }
            RefreshMarkers();
        }

        /// <summary>Checks for a structure over the selected grate and supplies the usable drain rate limit.</summary>
        internal float GetDrain(out Vector3 intake, out float radius)
        {
            intake = DrainZone < 0 ? Vector3.zero : _zones[DrainZone].Drain;
            radius = _drainRadius;
            DrainCovered = DrainZone >= 0 && Obstructed(intake + Vector3.up * 0.15f, 0.42f);
            return DrainZone >= 0 && !DrainCovered ? _drainCapacity : 0f;
        }

        /// <summary>Records the volume that the solver sent through the pipe to the downstream outlet.</summary>
        internal void RecordDrain(float volume, float dt)
        {
            DrainRate = volume / dt;
            DrainedVolume += volume;
        }

        /// <summary>Advances each assigned walker when the next step is clear and the route has no unsafe water.</summary>
        internal void Walk(float dt)
        {
            if (GuideZone < 0 || ShelteredCount == 2) return;
            Zone zone = _zones[GuideZone];
            string hold = "";
            for (int i = 0; i < 2; i++)
            {
                int leg = zone.Waypoints[i];
                if (leg >= 2) continue;
                Transform walker = _stage.Citizens[GuideZone * 2 + i].transform;
                Vector3 lane = Vector3.Cross(Vector3.up, (zone.Landing - zone.Approach).normalized) * (i == 0 ? -0.37f : 0.37f);
                Vector3 target = (leg == 0 ? zone.Approach : zone.Landing) + lane;
                Vector3 next = Vector3.MoveTowards(walker.position, target, _walkSpeed * dt);
                if (leg == 0) next.y = Flood2Terrain.Height(next.x, next.z) + 0.025f;
                if (Obstructed(next + Vector3.up * 0.36f, WALKER_RADIUS))
                {
                    hold = "구조물이 길을 막음";
                    continue;
                }
                if (UnsafeWater(walker.position) || UnsafeWater(next))
                {
                    hold = "대피로 침수";
                    continue;
                }
                walker.position = next;
                if (leg == 0 && new Vector2(next.x - target.x, next.z - target.z).sqrMagnitude <= 0.0025f)
                    zone.Waypoints[i] = 1;
                if (leg == 1 && next == target)
                {
                    zone.Waypoints[i] = 2;
                    zone.ShelteredCount++;
                }
            }
            if (hold == zone.HoldReason) return;
            zone.HoldReason = hold;
            RefreshMarkers();
        }

        /// <summary>Reports route or drain readiness, progress and restrictions for one district.</summary>
        internal string Status(int index)
        {
            string route = "현 위치 보호";
            if (index == GuideZone)
            {
                if (_stage.Phase == Flood2Phase.Preparing) route = "대피 안내 지정";
                else if (ShelteredCount == 2) route = "대피 완료 2/2";
                else if (RouteHoldReason.Length > 0) route = RouteHoldReason;
                else route = $"대피 중 {ShelteredCount}/2";
            }
            if (index != DrainZone) return route;
            if (_stage.Phase == Flood2Phase.Preparing || _stage.Phase == Flood2Phase.Warning) return route + " · 배수 준비";
            if (DrainCovered) return route + " · 배수구 막힘";
            return route + $" · {DrainRate:0.00}m³/s";
        }

        /// <summary>Checks if a dynamic flood defence overlaps the walking lane or drain inlet.</summary>
        private bool Obstructed(Vector3 point, float radius)
        {
            foreach (var piece in _stage.Construction.Pieces)
                if (piece.gameObject.activeSelf && (piece.Shape.ClosestPoint(point) - point).sqrMagnitude < radius * radius)
                    return true;
            return false;
        }

        /// <summary>Rejects deep water and shallow fast flow at foot level on the route.</summary>
        private bool UnsafeWater(Vector3 foot)
        {
            if (!_stage.Water.Sample(foot + Vector3.up * 0.08f, out float surface, out Vector2 flow)) return false;
            float depth = surface - foot.y;
            return depth >= _stopDepth || depth >= _stopFastWaterDepth && flow.sqrMagnitude >= _stopWaterSpeed * _stopWaterSpeed;
        }

        /// <summary>Creates one district's world landmarks and stores the two citizens' starting positions.</summary>
        private void CreateZone(int index, Vector2 approach, Vector2 landing, Vector2 inlet)
        {
            var zone = new Zone();
            _zones[index] = zone;
            for (int i = 0; i < 2; i++) zone.Homes[i] = _stage.Citizens[index * 2 + i].transform.position;
            zone.Center = (zone.Homes[0] + zone.Homes[1]) * 0.5f;
            zone.Approach = OnGround(approach);
            zone.Landing = new Vector3(landing.x, 2.5f, landing.y);
            zone.Drain = OnGround(inlet);
            var root = new GameObject($"Flood response {(char)('A' + index)}").transform;
            root.SetParent(transform, false);

            Vector3 ramp = zone.Landing - zone.Approach;
            Renderer walkway = Marker("Refuge ramp", root, (zone.Approach + zone.Landing) * 0.5f - Vector3.up * 0.12f,
                new Vector3(1.25f, 0.18f, ramp.magnitude), Quaternion.LookRotation(ramp));
            Tint(walkway, ROUTE_IDLE);
            Renderer deck = Marker("Raised refuge", root, zone.Landing - Vector3.up * 0.13f, new Vector3(2f, 0.25f, 1.8f), Quaternion.identity);
            Tint(deck, ROUTE_OPEN);
            Renderer post = Marker("Refuge support", root, zone.Landing - Vector3.up * 1.31f, new Vector3(0.28f, 2.4f, 0.28f), Quaternion.identity);
            Tint(post, ROUTE_IDLE);
            zone.Grate = Marker("Drain grate", root, zone.Drain + Vector3.up * 0.035f, new Vector3(0.78f, 0.09f, 0.64f), Quaternion.identity);
            for (int bar = -2; bar <= 2; bar++)
            {
                Renderer slot = Marker("Grate slot", root, zone.Drain + new Vector3(bar * 0.13f, 0.09f, 0f), new Vector3(0.045f, 0.025f, 0.49f), Quaternion.identity);
                Tint(slot, new Color(0.08f, 0.15f, 0.17f));
            }

            zone.Route = new GameObject("Marked evacuation route").AddComponent<LineRenderer>();
            zone.Route.transform.SetParent(root, false);
            zone.Route.sharedMaterial = _stage.LineMaterial;
            zone.Route.positionCount = 10;
            for (int i = 0; i <= 8; i++)
            {
                Vector3 point = Vector3.Lerp(zone.Center, zone.Approach, i / 8f);
                zone.Route.SetPosition(i, OnGround(new Vector2(point.x, point.z)) + Vector3.up * 0.04f);
            }
            zone.Route.SetPosition(9, zone.Landing + Vector3.up * 0.05f);
        }

        /// <summary>Projects an XZ point onto the same terrain used by the water solver.</summary>
        private static Vector3 OnGround(Vector2 point) => new Vector3(point.x, Flood2Terrain.Height(point.x, point.y), point.y);

        /// <summary>Creates an authored-looking marker without adding another obstacle to the simulation.</summary>
        private Renderer Marker(string label, Transform parent, Vector3 position, Vector3 size, Quaternion rotation)
        {
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visual.name = label;
            visual.transform.SetParent(parent, false);
            visual.transform.SetPositionAndRotation(position, rotation);
            visual.transform.localScale = size;
            Collider shape = visual.GetComponent<Collider>();
            shape.enabled = false;
            Destroy(shape);
            Renderer renderer = visual.GetComponent<Renderer>();
            renderer.sharedMaterial = _stage.PanelMaterial;
            return renderer;
        }

        /// <summary>Updates route and grate emphasis to match the plan and actual walking conditions.</summary>
        private void RefreshMarkers()
        {
            for (int i = 0; i < _zones.Length; i++)
            {
                Zone zone = _zones[i];
                bool guided = i == GuideZone;
                Color route = !guided ? ROUTE_IDLE : zone.HoldReason.Length > 0 ? ROUTE_BLOCKED : ROUTE_OPEN;
                zone.Route.startColor = zone.Route.endColor = route;
                zone.Route.startWidth = zone.Route.endWidth = guided ? 0.23f : 0.09f;
                Tint(zone.Grate, i == DrainZone ? DRAIN_OPEN : ROUTE_IDLE);
            }
        }

        /// <summary>Tints a landmark with a property block shared for setup.</summary>
        private void Tint(Renderer renderer, Color color)
        {
            _tint.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(_tint);
        }
    }
}
