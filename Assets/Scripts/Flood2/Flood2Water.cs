using System;
using System.Collections.Generic;

using UnityEngine;

namespace Flood2
{
    /// <summary>Conservative local-inertial shallow water on a height field, with open downstream drainage.</summary>
    public sealed class Flood2Water : IDisposable
    {
        private const int W = Flood2Terrain.WIDTH;
        private const int L = Flood2Terrain.LENGTH;
        private const float DX = Flood2Terrain.CELL;
        private const float GRAVITY = 9.81f;
        private const float DRY = 0.005f;
        private readonly float[] _ground = new float[W * L];
        private readonly float[] _bed = new float[W * L];
        private readonly float[] _depth = new float[W * L];
        private readonly float[] _previous = new float[W * L];
        private readonly float[] _outgoing = new float[W * L];
        private readonly float[] _scale = new float[W * L];
        private readonly float[] _displaced = new float[W * L];
        private readonly float[] _qx = new float[(W + 1) * L];
        private readonly float[] _qz = new float[W * (L + 1)];
        private readonly Vector3[] _vertices = new Vector3[W * L * 4];
        private readonly Color[] _colors = new Color[W * L * 4];
        private readonly Vector2[] _flowUv = new Vector2[W * L * 4];
        private readonly Mesh _mesh;
        private readonly FlowSettings _settings;
        private float _renderClock;
        public float Volume { get; private set; }
        public bool IsValid { get; private set; } = true;
        public Mesh Surface => _mesh;

        [Serializable]
        internal sealed class FlowSettings
        {
            [Header("Boundary flow")]
            [SerializeField] private float _initialSurfaceElevation = -0.65f;
            [SerializeField, Range(0f, 18f)] private float _inletHalfWidth = 5.5f;
            [SerializeField, Min(0f)] private float _inletVelocity = 3f;
            [SerializeField, Min(0f)] private float _inletDepthFeedback = 2f;
            [SerializeField, Min(0f)] private float _drainCoefficient = 0.65f;

            [Header("Channel flow")]
            [SerializeField, Min(0f)] private float _flowDamping = 0.3f;
            [SerializeField, Min(0f)] private float _bedFriction = 0.035f;
            [Tooltip("An obstacle this far above the bed or closer can block a water cell.")]
            [SerializeField, Min(0f)] private float _obstacleClearance = 0.35f;
            [SerializeField, Min(0.01f)] private float _meshRefreshSeconds = 0.05f;
            public float InitialSurfaceElevation => _initialSurfaceElevation;
            public float InletHalfWidth => _inletHalfWidth;
            public float InletVelocity => _inletVelocity;
            public float InletDepthFeedback => _inletDepthFeedback;
            public float DrainCoefficient => _drainCoefficient;
            public float FlowDamping => _flowDamping;
            public float BedFriction => _bedFriction;
            public float ObstacleClearance => _obstacleClearance;
            public float MeshRefreshSeconds => _meshRefreshSeconds;
        }

        /// <summary>Builds the scene editor's quiet water preview from the default flow settings.</summary>
        public Flood2Water() : this(new FlowSettings()) { }

        /// <summary>Allocates the solver and render mesh using the scene's flow settings.</summary>
        internal Flood2Water(FlowSettings settings)
        {
            _settings = settings;
            var indices = new int[W * L * 6];
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                Vector3 center = Center(x, z);
                _ground[i] = Flood2Terrain.Height(center.x, center.z);
                int v = i * 4;
                _vertices[v] = center + new Vector3(-DX / 2, 0, -DX / 2);
                _vertices[v + 1] = center + new Vector3(-DX / 2, 0, DX / 2);
                _vertices[v + 2] = center + new Vector3(DX / 2, 0, -DX / 2);
                _vertices[v + 3] = center + new Vector3(DX / 2, 0, DX / 2);
                int t = i * 6;
                indices[t] = v; indices[t + 1] = v + 1; indices[t + 2] = v + 2;
                indices[t + 3] = v + 2; indices[t + 4] = v + 1; indices[t + 5] = v + 3;
            }
            _mesh = new Mesh { name = "Flood2 live water", vertices = _vertices, triangles = indices };
            _mesh.MarkDynamic();
            _mesh.bounds = new Bounds(new Vector3(0, 3, 30), new Vector3(38, 18, 62));
            Reset();
        }

        /// <summary>Returns a cell's world centre with zero vertical coordinate.</summary>
        private static Vector3 Center(int x, int z) => new Vector3(Flood2Terrain.MIN_X + (x + 0.5f) * DX, 0, (z + 0.5f) * DX);

        /// <summary>Restores the quiet pre-flood channel and clears all momentum and diagnostics.</summary>
        public void Reset()
        {
            Array.Copy(_ground, _bed, _ground.Length);
            Array.Clear(_qx, 0, _qx.Length);
            Array.Clear(_qz, 0, _qz.Length);
            float total = 0f;
            for (int i = 0; i < _depth.Length; i++) total += _depth[i] = Mathf.Max(0f, _settings.InitialSurfaceElevation - _ground[i]);
            Volume = total * DX * DX;
            _renderClock = 0f;
            IsValid = true;
            RefreshMesh();
        }

        /// <summary>Rasterizes actual box collider silhouettes; submerged elevated objects do not seal the ground.</summary>
        private void UpdateObstacles(IReadOnlyList<Flood2Piece> pieces)
        {
            Array.Copy(_bed, _previous, _bed.Length);
            Array.Copy(_ground, _bed, _ground.Length);
            float clearance = _settings.ObstacleClearance;
            foreach (Flood2Piece piece in pieces)
            {
                if (!piece.gameObject.activeSelf) continue;
                Bounds bounds = piece.Shape.bounds;
                int minX = Mathf.Clamp(Mathf.FloorToInt((bounds.min.x - Flood2Terrain.MIN_X) / DX), 0, W - 1);
                int maxX = Mathf.Clamp(Mathf.FloorToInt((bounds.max.x - Flood2Terrain.MIN_X) / DX), 0, W - 1);
                int minZ = Mathf.Clamp(Mathf.FloorToInt(bounds.min.z / DX), 0, L - 1);
                int maxZ = Mathf.Clamp(Mathf.FloorToInt(bounds.max.z / DX), 0, L - 1);
                for (int z = minZ; z <= maxZ; z++)
                for (int x = minX; x <= maxX; x++)
                {
                    int i = z * W + x;
                    Vector3 p = Center(x, z);
                    if (bounds.min.y > _bed[i] + clearance) continue;
                    if (piece.Shape.Raycast(new Ray(p + Vector3.up * 20f, Vector3.down), out RaycastHit hit, 25f))
                        _bed[i] = Mathf.Max(_bed[i], hit.point.y);
                }
            }
            Array.Clear(_displaced, 0, _displaced.Length);
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                float removed = Mathf.Min(_depth[i], Mathf.Max(0f, _bed[i] - _previous[i]));
                if (removed <= 0f) continue;
                int neighbors = (x > 0 ? 1 : 0) + (x + 1 < W ? 1 : 0) + (z > 0 ? 1 : 0) + (z + 1 < L ? 1 : 0);
                _depth[i] -= removed;
                float share = removed / neighbors;
                if (x > 0) _displaced[i - 1] += share;
                if (x + 1 < W) _displaced[i + 1] += share;
                if (z > 0) _displaced[i - W] += share;
                if (z + 1 < L) _displaced[i + W] += share;
            }
            for (int i = 0; i < _depth.Length; i++) _depth[i] += _displaced[i];
        }

        /// <summary>Advances flux, water volume and body forces using bounded substeps and the requested inlet depth.</summary>
        public void Step(float dt, float inletDepth, IReadOnlyList<Flood2Piece> pieces)
        {
            UpdateObstacles(pieces);
            float maxSignal = 1f;
            for (int i = 0; i < _depth.Length; i++)
                maxSignal = Mathf.Max(maxSignal, Velocity(i).magnitude + Mathf.Sqrt(GRAVITY * _depth[i]));
            int steps = Mathf.Max(2, Mathf.CeilToInt(dt * maxSignal / (DX * 0.35f)));
            if (steps > 24) { IsValid = false; return; }
            for (int s = 0; s < steps; s++) Substep(dt / steps, inletDepth);
            float total = 0f;
            for (int i = 0; i < _depth.Length; i++)
            {
                float h = _depth[i];
                if (float.IsNaN(h) || float.IsInfinity(h) || h > 12f) { IsValid = false; return; }
                total += h;
            }
            Volume = total * DX * DX;
            foreach (Flood2Piece piece in pieces) piece.ApplyWater(this);
            _renderClock += dt;
            if (_renderClock >= _settings.MeshRefreshSeconds) { _renderClock = 0f; RefreshMesh(); }
        }

        /// <summary>Updates local inertial discharge; high beds block flow until the water overtops them.</summary>
        private float Discharge(float oldFlux, int a, int b, float dt)
        {
            float etaA = _bed[a] + _depth[a];
            float etaB = _bed[b] + _depth[b];
            float flowDepth = Mathf.Max(0f, Mathf.Max(etaA, etaB) - Mathf.Max(_bed[a], _bed[b]));
            if (flowDepth < DRY) return 0f;
            float flux = (oldFlux - GRAVITY * flowDepth * dt * (etaB - etaA) / DX) /
                (1f + dt * (_settings.FlowDamping + _settings.BedFriction * Mathf.Abs(oldFlux) / Mathf.Max(0.01f, flowDepth * flowDepth)));
            return Mathf.Clamp(flux, -flowDepth * 8f, flowDepth * 8f);
        }

        /// <summary>Transfers each face's discharge once and scales outgoing flux to preserve nonnegative volume.</summary>
        private void Substep(float dt, float inletDepth)
        {
            Array.Clear(_outgoing, 0, _outgoing.Length);
            for (int z = 0; z < L; z++)
            for (int x = 1; x < W; x++)
            {
                int b = z * W + x;
                int edge = z * (W + 1) + x;
                float q = _qx[edge] = Discharge(_qx[edge], b - 1, b, dt);
                _outgoing[q > 0f ? b - 1 : b] += Mathf.Abs(q);
            }
            for (int z = 1; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int b = z * W + x;
                float q = _qz[b] = Discharge(_qz[b], b - W, b, dt);
                _outgoing[q > 0f ? b - W : b] += Mathf.Abs(q);
            }
            for (int x = 0; x < W; x++)
            {
                _qz[x] = -Mathf.Max(0f, Mathf.Sqrt(GRAVITY * _depth[x]) * _depth[x] * _settings.DrainCoefficient);
                _outgoing[x] += -_qz[x];
                int i = (L - 1) * W + x;
                float inletX = Center(x, L - 1).x;
                _qz[L * W + x] = Mathf.Abs(inletX) < _settings.InletHalfWidth && inletDepth > 0f
                    ? -Mathf.Max(0f, _settings.InletVelocity * inletDepth + _settings.InletDepthFeedback * (inletDepth - _depth[i])) : 0f;
            }
            for (int i = 0; i < _depth.Length; i++)
                _scale[i] = _outgoing[i] > 0f ? Mathf.Min(1f, _depth[i] * DX / (dt * _outgoing[i])) : 1f;
            float factor = dt / DX;
            for (int z = 0; z < L; z++)
            for (int x = 1; x < W; x++)
            {
                int b = z * W + x;
                int edge = z * (W + 1) + x;
                float q = _qx[edge] *= _scale[_qx[edge] > 0f ? b - 1 : b];
                _depth[b - 1] -= q * factor; _depth[b] += q * factor;
            }
            for (int z = 1; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int b = z * W + x;
                float q = _qz[b] *= _scale[_qz[b] > 0f ? b - W : b];
                _depth[b - W] -= q * factor; _depth[b] += q * factor;
            }
            for (int x = 0; x < W; x++)
            {
                _qz[x] *= _scale[x];
                _depth[x] += _qz[x] * factor;
                _depth[(L - 1) * W + x] -= _qz[L * W + x] * factor;
            }
            for (int i = 0; i < _depth.Length; i++) _depth[i] = Mathf.Max(0f, _depth[i]);
        }

        /// <summary>Returns depth-averaged horizontal velocity at a wet cell.</summary>
        private Vector2 Velocity(int i)
        {
            float h = _depth[i];
            if (h < 0.02f) return Vector2.zero;
            int z = i / W;
            int x = i - z * W;
            return Vector2.ClampMagnitude(new Vector2((_qx[z * (W + 1) + x] + _qx[z * (W + 1) + x + 1]) * 0.5f,
                (_qz[i] + _qz[i + W]) * 0.5f) / h, 8f);
        }

        /// <summary>Samples actual occupied water at a world point; dry or outside cells have no water surface.</summary>
        public bool Sample(Vector3 point, out float surface, out Vector2 flow)
        {
            int x = Mathf.FloorToInt((point.x - Flood2Terrain.MIN_X) / DX);
            int z = Mathf.FloorToInt(point.z / DX);
            surface = point.y;
            flow = Vector2.zero;
            if (x < 0 || x >= W || z < 0 || z >= L) return false;
            int i = z * W + x;
            if (_depth[i] < 0.02f || point.y < _bed[i] - 0.08f) return false;
            surface = _bed[i] + _depth[i];
            flow = Velocity(i);
            return true;
        }

        /// <summary>Uploads visible water cells and their flow direction without reallocating mesh arrays.</summary>
        private void RefreshMesh()
        {
            for (int i = 0; i < _depth.Length; i++)
            {
                float h = _depth[i];
                Vector2 flow = Velocity(i);
                Color color = Color.Lerp(new Color(0.16f, 0.64f, 0.65f), new Color(0.055f, 0.28f, 0.42f), Mathf.Clamp01(h / 2f));
                color.a = h < 0.02f ? 0f : Mathf.Lerp(0.5f, 0.92f, Mathf.Clamp01(h));
                for (int j = 0; j < 4; j++)
                {
                    int v = i * 4 + j;
                    _vertices[v].y = _bed[i] + h + 0.015f;
                    _colors[v] = color;
                    _flowUv[v] = flow;
                }
            }
            _mesh.vertices = _vertices;
            _mesh.colors = _colors;
            _mesh.uv = _flowUv;
        }

        /// <summary>Releases the runtime water mesh when the stage closes.</summary>
        public void Dispose() => UnityEngine.Object.Destroy(_mesh);
    }
}
