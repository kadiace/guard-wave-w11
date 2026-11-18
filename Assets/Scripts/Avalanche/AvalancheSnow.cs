using System;
using System.Collections.Generic;

using UnityEngine;

namespace Avalanche
{
    /// <summary>Conserves moving and deposited snow on a height field and projects physical barriers onto cell faces.</summary>
    public sealed class AvalancheSnow : IDisposable
    {
        private const int W = AvalancheTerrain.WIDTH;
        private const int L = AvalancheTerrain.LENGTH;
        private const float DX = AvalancheTerrain.CELL;
        private const float G = 9.81f;
        private const float EPSILON = 0.000001f;
        private readonly float[] _moving = new float[W * L];
        private readonly float[] _ground = new float[W * L];
        private readonly float[] _slope = new float[W * L];
        private readonly float[] _settled = new float[W * L];
        private readonly float[] _depositClock = new float[W * L];
        private readonly Vector2[] _velocity = new Vector2[W * L];
        private readonly float[] _outgoing = new float[W * L];
        private readonly float[] _change = new float[W * L];
        private readonly Vector2[] _momentumChange = new Vector2[W * L];
        private readonly float[] _fluxX = new float[(W + 1) * L];
        private readonly float[] _fluxZ = new float[W * (L + 1)];
        private readonly float[] _barrierX = new float[(W + 1) * L];
        private readonly float[] _barrierZ = new float[W * (L + 1)];
        private readonly Vector3[] _vertices = new Vector3[W * L * 4];
        private readonly Color[] _colors = new Color[W * L * 4];
        private readonly Vector2[] _uv = new Vector2[W * L * 4];
        private readonly Dictionary<int, Matrix4x4> _poses = new Dictionary<int, Matrix4x4>();
        private readonly Settings _settings;
        private readonly Mesh _mesh;
        private float _renderClock;
        public Mesh Surface => _mesh;
        public bool IsValid { get; private set; } = true;
        public string Error { get; private set; }
        public float InjectedVolume { get; private set; }
        public float OutflowVolume { get; private set; }
        public float ActiveVolume { get; private set; }
        public float DepositedVolume { get; private set; }
        public float VolumeError { get; private set; }

        [Serializable]
        public sealed class Settings
        {
            [Header("Snow")]
            [SerializeField, Min(1f)] private float _density = 300f;
            [SerializeField, Range(0f, 1f)] private float _movingFriction = 0.18f;
            [SerializeField, Range(0f, 1f)] private float _restingFriction = 0.30f;
            [SerializeField, Min(0.01f)] private float _settleSpeed = 0.25f;
            [SerializeField, Min(0f)] private float _settleDelay = 0.5f;
            [SerializeField, Min(0.01f)] private float _depositHalfLife = 1f;
            [SerializeField, Min(0f)] private float _pressureCoefficient = 0.4f;
            [SerializeField, Min(0f)] private float _maximumLoad = 12000f;
            [Header("Solver")]
            [SerializeField, Range(0.05f, 0.45f)] private float _cfl = 0.4f;
            [SerializeField, Min(1)] private int _maxSubsteps = 32;
            [SerializeField, Min(0.01f)] private float _meshInterval = 0.05f;
            public float Density => _density;
            public float MovingFriction => _movingFriction;
            public float RestingFriction => _restingFriction;
            public float SettleSpeed => _settleSpeed;
            public float SettleDelay => _settleDelay;
            public float DepositHalfLife => _depositHalfLife;
            public float PressureCoefficient => _pressureCoefficient;
            public float MaximumLoad => _maximumLoad;
            public float Cfl => _cfl;
            public int MaxSubsteps => _maxSubsteps;
            public float MeshInterval => _meshInterval;
        }

        /// <summary>Allocates the conservative grid and its single reusable surface mesh.</summary>
        public AvalancheSnow(Settings settings)
        {
            _settings = settings;
            int[] triangles = new int[W * L * 6];
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                Vector3 center = Center(x, z);
                _ground[i] = AvalancheTerrain.Height(center.x, center.z);
                _slope[i] = new Vector2((AvalancheTerrain.Height(center.x + DX, center.z) - AvalancheTerrain.Height(center.x - DX, center.z)) / (2f * DX),
                    (AvalancheTerrain.Height(center.x, center.z + DX) - AvalancheTerrain.Height(center.x, center.z - DX)) / (2f * DX)).magnitude;
                for (int j = 0; j < 4; j++)
                    _vertices[i * 4 + j] = center + new Vector3((j >= 2 ? 0.5f : -0.5f) * DX, 0f, (j % 2 == 1 ? 0.5f : -0.5f) * DX);
                int v = i * 4, t = i * 6;
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 2; triangles[t + 4] = v + 1; triangles[t + 5] = v + 3;
            }
            _mesh = new Mesh { name = "Avalanche moving and deposited snow", vertices = _vertices, triangles = triangles };
            _mesh.MarkDynamic();
            _mesh.bounds = new Bounds(new Vector3(0f, 8f, 30f), new Vector3(36f, 40f, 60f));
            Reset();
        }

        /// <summary>Returns the world centre of a snow cell.</summary>
        private static Vector3 Center(int x, int z) => new Vector3(AvalancheTerrain.MIN_X + (x + 0.5f) * DX, 0f, (z + 0.5f) * DX);

        /// <summary>Returns terrain plus stationary snow without treating obstacles as volume that can erase snow.</summary>
        private float Bed(int x, int z) => _ground[z * W + x] + _settled[z * W + x];

        /// <summary>Clears both releases, deposition history and diagnostics for a fresh attempt.</summary>
        public void Reset()
        {
            Array.Clear(_moving, 0, _moving.Length);
            Array.Clear(_settled, 0, _settled.Length);
            Array.Clear(_velocity, 0, _velocity.Length);
            Array.Clear(_depositClock, 0, _depositClock.Length);
            _poses.Clear();
            Array.Fill(_barrierX, float.NegativeInfinity);
            Array.Fill(_barrierZ, float.NegativeInfinity);
            InjectedVolume = OutflowVolume = ActiveVolume = DepositedVolume = VolumeError = 0f;
            IsValid = true;
            Error = "";
            RefreshMesh();
        }

        /// <summary>Distributes exactly the requested release volume using a normalized elliptical mound.</summary>
        public void Release(Vector2 center, Vector2 radius, float volume, Vector2 initialVelocity)
        {
            float sum = 0f;
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++) sum += ReleaseWeight(Center(x, z), center, radius);
            if (sum <= EPSILON) { Fail("발원 영역이 격자 밖에 있습니다."); return; }
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                float added = volume * ReleaseWeight(Center(x, z), center, radius) / (sum * DX * DX);
                float total = _moving[i] + added;
                if (total > EPSILON) _velocity[i] = (_velocity[i] * _moving[i] + initialVelocity * added) / total;
                _moving[i] = total;
            }
            InjectedVolume += volume;
            RefreshMesh();
        }

        /// <summary>Returns a tapered elliptical release weight for a cell centre.</summary>
        private static float ReleaseWeight(Vector3 p, Vector2 center, Vector2 radius)
        {
            float x = (p.x - center.x) / Mathf.Max(0.1f, radius.x);
            float z = (p.z - center.y) / Mathf.Max(0.1f, radius.y);
            return Mathf.Max(0f, 1f - x * x - z * z);
        }

        /// <summary>Advances adaptive conservative substeps, validates volume and applies snow loads to physical pieces.</summary>
        public void Step(float dt, float totalVolume, IReadOnlyList<AvalanchePiece> pieces)
        {
            if (!IsValid) return;
            UpdateBarriers(pieces);
            float remaining = dt;
            int steps = 0;
            while (remaining > EPSILON)
            {
                float speed = 0.1f;
                for (int i = 0; i < _moving.Length; i++)
                    speed = Mathf.Max(speed, _velocity[i].magnitude + Mathf.Sqrt(G * _moving[i]));
                float step = Mathf.Min(remaining, _settings.Cfl * DX / speed);
                if (++steps > _settings.MaxSubsteps) { Fail("눈 계산의 시간 분할 상한을 초과했습니다."); return; }
                Advance(step);
                remaining -= step;
            }
            double volume = OutflowVolume;
            ActiveVolume = DepositedVolume = 0f;
            for (int i = 0; i < _moving.Length; i++)
            {
                if (!float.IsFinite(_moving[i]) || !float.IsFinite(_velocity[i].x) || !float.IsFinite(_velocity[i].y) || _moving[i] < -EPSILON)
                { Fail("눈의 두께 또는 속도가 안정 범위를 벗어났습니다."); return; }
                volume += (_moving[i] + _settled[i]) * DX * DX;
                DepositedVolume += _settled[i] * DX * DX;
                if (_velocity[i].magnitude >= _settings.SettleSpeed) ActiveVolume += _moving[i] * DX * DX;
            }
            VolumeError = Mathf.Abs((float)volume - InjectedVolume);
            if (VolumeError > totalVolume * 0.01f) { Fail("눈 부피 보존 오차가 방출량의 1%를 넘었습니다."); return; }
            foreach (AvalanchePiece piece in pieces) piece.ApplySnow(this, _settings);
            _renderClock += dt;
            if (_renderClock >= _settings.MeshInterval) { _renderClock = 0f; RefreshMesh(); }
        }

        /// <summary>Projects changed colliders onto shared cell boundaries without replacing the snow bed.</summary>
        private void UpdateBarriers(IReadOnlyList<AvalanchePiece> pieces)
        {
            bool changed = _poses.Count != pieces.Count;
            foreach (AvalanchePiece piece in pieces)
                if (!_poses.TryGetValue(piece.Id, out Matrix4x4 pose) || pose != piece.transform.localToWorldMatrix) changed = true;
            if (!changed) return;
            _poses.Clear();
            Array.Fill(_barrierX, float.NegativeInfinity);
            Array.Fill(_barrierZ, float.NegativeInfinity);
            foreach (AvalanchePiece piece in pieces)
            {
                _poses[piece.Id] = piece.transform.localToWorldMatrix;
                if (!piece.gameObject.activeSelf) continue;
                Bounds bounds = piece.Shape.bounds;
                int x0 = Mathf.Clamp(Mathf.FloorToInt((bounds.min.x + 18f) / DX), 0, W - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((bounds.max.x + 18f) / DX), 0, W);
                int z0 = Mathf.Clamp(Mathf.FloorToInt(bounds.min.z / DX), 0, L - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt(bounds.max.z / DX), 0, L);
                for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    if (z < L && x > 0 && x < W)
                        ProjectFace(piece, Center(x - 1, z), Vector3.right, ref _barrierX[z * (W + 1) + x]);
                    if (x < W && z > 0 && z < L)
                        ProjectFace(piece, Center(x, z - 1), Vector3.forward, ref _barrierZ[z * W + x]);
                }
            }
        }

        /// <summary>Finds collider crossings between neighbouring cell centres, including sub-cell thin walls.</summary>
        private static void ProjectFace(AvalanchePiece piece, Vector3 start, Vector3 direction, ref float height)
        {
            Bounds bounds = piece.Shape.bounds;
            float ground = Mathf.Min(AvalancheTerrain.Height(start.x, start.z), AvalancheTerrain.Height(start.x + direction.x * DX, start.z + direction.z * DX));
            if (bounds.min.y > ground + 0.4f) return;
            start.y = Mathf.Max(bounds.min.y + 0.02f, ground + 0.05f);
            bool intersects = piece.Shape.Raycast(new Ray(start, direction), out _, DX) ||
                piece.Shape.Raycast(new Ray(start + direction * DX, -direction), out _, DX) ||
                (piece.Shape.ClosestPoint(start) - start).sqrMagnitude < 0.0001f;
            if (intersects) height = Mathf.Max(height, bounds.max.y);
        }

        /// <summary>Updates cell velocities, transports donor volume and momentum and deposits slow snow.</summary>
        private void Advance(float dt)
        {
            Array.Clear(_outgoing, 0, _outgoing.Length);
            Array.Clear(_change, 0, _change.Length);
            Array.Clear(_momentumChange, 0, _momentumChange.Length);
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                if (_moving[i] <= EPSILON) { _velocity[i] = Vector2.zero; continue; }
                float surface = Bed(x, z) + _moving[i];
                float left = x > 0 ? Bed(x - 1, z) + _moving[i - 1] : surface;
                float right = x + 1 < W ? Bed(x + 1, z) + _moving[i + 1] : surface;
                float down = z > 0 ? Bed(x, z - 1) + _moving[i - W] : surface;
                float up = z + 1 < L ? Bed(x, z + 1) + _moving[i + W] : surface;
                Vector2 slope = new Vector2(right - left, up - down) / (2f * DX);
                Vector2 velocity = _velocity[i] - G * slope * dt;
                float deceleration = _settings.MovingFriction * G * dt;
                _velocity[i] = velocity.normalized * Mathf.Max(0f, velocity.magnitude - deceleration);
            }
            for (int z = 0; z < L; z++)
            for (int x = 1; x < W; x++)
            {
                int a = z * W + x - 1, b = a + 1, f = z * (W + 1) + x;
                _fluxX[f] = Flux(a, b, Bed(x - 1, z), Bed(x, z), _barrierX[f], true) * dt / DX;
                RegisterOutgoing(a, b, _fluxX[f]);
            }
            for (int z = 1; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int a = (z - 1) * W + x, b = a + W, f = z * W + x;
                _fluxZ[f] = Flux(a, b, Bed(x, z - 1), Bed(x, z), _barrierZ[f], false) * dt / DX;
                RegisterOutgoing(a, b, _fluxZ[f]);
            }
            for (int x = 0; x < W; x++)
            {
                _fluxZ[x] = Mathf.Max(0f, -_velocity[x].y) * _moving[x] * dt / DX;
                _outgoing[x] += _fluxZ[x];
            }
            for (int z = 0; z < L; z++)
            for (int x = 1; x < W; x++)
            {
                int a = z * W + x - 1;
                Transfer(a, a + 1, _fluxX[z * (W + 1) + x]);
            }
            for (int z = 1; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int a = (z - 1) * W + x;
                Transfer(a, a + W, _fluxZ[z * W + x]);
            }
            for (int x = 0; x < W; x++)
            {
                float amount = _fluxZ[x] * Mathf.Min(1f, _moving[x] / Mathf.Max(EPSILON, _outgoing[x]));
                _change[x] -= amount;
                _momentumChange[x] -= _velocity[x] * amount;
                OutflowVolume += amount * DX * DX;
            }
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                float depth = Mathf.Max(0f, _moving[i] + _change[i]);
                _velocity[i] = depth > EPSILON ? (_velocity[i] * _moving[i] + _momentumChange[i]) / depth : Vector2.zero;
                _moving[i] = depth;
                bool resting = _velocity[i].magnitude < _settings.SettleSpeed && _slope[i] <= _settings.RestingFriction;
                _depositClock[i] = resting ? _depositClock[i] + dt : 0f;
                if (_depositClock[i] < _settings.SettleDelay) continue;
                float deposited = depth < 0.005f ? depth : depth * (1f - Mathf.Exp(-0.693147f * dt / _settings.DepositHalfLife));
                _moving[i] -= deposited;
                _settled[i] += deposited;
            }
        }

        /// <summary>Computes a hydrostatically reconstructed face flux, allowing overflow above a collider.</summary>
        private float Flux(int a, int b, float bedA, float bedB, float barrier, bool xAxis)
        {
            float crest = Mathf.Max(bedA, Mathf.Max(bedB, barrier));
            float hA = Mathf.Max(0f, bedA + _moving[a] - crest);
            float hB = Mathf.Max(0f, bedB + _moving[b] - crest);
            float uA = xAxis ? _velocity[a].x : _velocity[a].y;
            float uB = xAxis ? _velocity[b].x : _velocity[b].y;
            float c = Mathf.Max(Mathf.Abs(uA) + Mathf.Sqrt(G * hA), Mathf.Abs(uB) + Mathf.Sqrt(G * hB));
            return 0.5f * (hA * uA + hB * uB - c * (hB - hA));
        }

        /// <summary>Accumulates requested donor outflow before limiting it to available cell volume.</summary>
        private void RegisterOutgoing(int a, int b, float amount)
        {
            if (amount >= 0f) _outgoing[a] += amount;
            else _outgoing[b] -= amount;
        }

        /// <summary>Moves the same donor volume and momentum into an adjacent cell without losing mass.</summary>
        private void Transfer(int a, int b, float signedAmount)
        {
            int donor = signedAmount >= 0f ? a : b;
            float amount = signedAmount * Mathf.Min(1f, _moving[donor] / Mathf.Max(EPSILON, _outgoing[donor]));
            _change[a] -= amount; _change[b] += amount;
            Vector2 momentum = _velocity[donor] * amount;
            _momentumChange[a] -= momentum; _momentumChange[b] += momentum;
        }

        /// <summary>Samples moving thickness, speed and deposited thickness at a world position.</summary>
        public bool Sample(Vector3 point, out float moving, out float deposited, out Vector2 velocity)
        {
            int x = Mathf.FloorToInt((point.x + 18f) / DX), z = Mathf.FloorToInt(point.z / DX);
            moving = deposited = 0f; velocity = Vector2.zero;
            if (x < 0 || x >= W || z < 0 || z >= L) return false;
            int i = z * W + x;
            moving = _moving[i]; deposited = _settled[i]; velocity = _velocity[i];
            return true;
        }

        /// <summary>Uploads a white moving layer and a muted stationary layer using reusable mesh arrays.</summary>
        private void RefreshMesh()
        {
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                float total = _moving[i] + _settled[i];
                Color color = Color.Lerp(new Color(0.66f, 0.76f, 0.85f), Color.white, Mathf.Clamp01(_velocity[i].magnitude / 3f));
                color.a = total > 0.002f ? 1f : 0f;
                for (int j = 0; j < 4; j++)
                {
                    int v = i * 4 + j;
                    _vertices[v].y = AvalancheTerrain.Height(_vertices[v].x, _vertices[v].z) + total + 0.015f;
                    _colors[v] = color;
                    _uv[v] = _velocity[i];
                }
            }
            _mesh.vertices = _vertices;
            _mesh.colors = _colors;
            _mesh.uv = _uv;
            _mesh.RecalculateNormals();
        }

        /// <summary>Stops numerical progression and exposes the reason for a recoverable technical result.</summary>
        private void Fail(string reason) { IsValid = false; Error = reason; }

        /// <summary>Releases the scene-owned surface mesh.</summary>
        public void Dispose() => UnityEngine.Object.Destroy(_mesh);
    }
}
