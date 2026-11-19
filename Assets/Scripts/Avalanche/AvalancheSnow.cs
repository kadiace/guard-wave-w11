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
        private readonly float[] _solidTop = new float[W * L];
        private readonly float[] _previousSolidTop = new float[W * L];
        private readonly float[] _pendingMoving = new float[W * L];
        private readonly float[] _pendingSettled = new float[W * L];
        private readonly Vector2[] _pendingMomentum = new Vector2[W * L];
        private readonly float[] _depositClock = new float[W * L];
        private readonly Vector2[] _velocity = new Vector2[W * L];
        private readonly float[] _outgoing = new float[W * L];
        private readonly float[] _change = new float[W * L];
        private readonly Vector2[] _flowVelocitySum = new Vector2[W * L];
        private readonly float[] _flowWeight = new float[W * L];
        private readonly float[] _fluxX = new float[(W + 1) * L];
        private readonly float[] _fluxZ = new float[W * (L + 1)];
        private readonly float[] _barrierX = new float[(W + 1) * L];
        private readonly float[] _barrierZ = new float[W * (L + 1)];
        private readonly Vector3[] _vertices = new Vector3[W * L * 4];
        private readonly Color[] _colors = new Color[W * L * 4];
        private readonly Vector2[] _uv = new Vector2[W * L * 4];
        private readonly Dictionary<int, Matrix4x4> _poses = new Dictionary<int, Matrix4x4>();
        private readonly List<AvalanchePiece> _activePieces = new List<AvalanchePiece>();
        private readonly Settings _settings;
        private readonly Mesh _mesh;
        private float _simulationClock;
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
            [Tooltip("눈 격자 계산 간격입니다. 간격이 길수록 CPU 사용량은 줄지만 흐름 반응이 늦어집니다.")]
            [SerializeField, Range(0.02f, 0.1f)] private float _simulationInterval = 0.05f;
            [Tooltip("셀 사이 표면 높이 차이에 반응하는 눈 이동량입니다.")]
            [SerializeField, Min(0.1f)] private float _flowResponse = 3f;
            [Tooltip("한 번의 격자 계산에서 셀의 이동층이 내보낼 수 있는 최대 비율입니다.")]
            [SerializeField, Range(0.05f, 0.75f)] private float _maximumCellTransferFraction = 0.45f;
            [Tooltip("시민 위험 및 구조물 하중 계산에 사용하는 최대 눈 흐름 속도입니다.")]
            [SerializeField, Min(1f)] private float _maximumFlowSpeed = 14f;
            [SerializeField, Min(0.01f)] private float _meshInterval = 0.05f;
            public float Density => _density;
            public float MovingFriction => _movingFriction;
            public float RestingFriction => _restingFriction;
            public float SettleSpeed => _settleSpeed;
            public float SettleDelay => _settleDelay;
            public float DepositHalfLife => _depositHalfLife;
            public float PressureCoefficient => _pressureCoefficient;
            public float MaximumLoad => _maximumLoad;
            public float SimulationInterval => _simulationInterval;
            public float FlowResponse => _flowResponse;
            public float MaximumCellTransferFraction => _maximumCellTransferFraction;
            public float MaximumFlowSpeed => _maximumFlowSpeed;
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

        /// <summary>Returns supported solid height plus deposited snow, keeping solid occupancy separate from snow mass.</summary>
        private float Bed(int x, int z) => _solidTop[z * W + x] + _settled[z * W + x];

        /// <summary>Clears both releases, deposition history and diagnostics for a fresh attempt.</summary>
        public void Reset()
        {
            Array.Clear(_moving, 0, _moving.Length);
            Array.Clear(_settled, 0, _settled.Length);
            Array.Clear(_velocity, 0, _velocity.Length);
            Array.Clear(_depositClock, 0, _depositClock.Length);
            Array.Clear(_pendingMoving, 0, _pendingMoving.Length);
            Array.Clear(_pendingSettled, 0, _pendingSettled.Length);
            Array.Clear(_pendingMomentum, 0, _pendingMomentum.Length);
            Array.Copy(_ground, _solidTop, _ground.Length);
            Array.Copy(_ground, _previousSolidTop, _ground.Length);
            _poses.Clear();
            Array.Fill(_barrierX, float.NegativeInfinity);
            Array.Fill(_barrierZ, float.NegativeInfinity);
            _simulationClock = 0f;
            _renderClock = 0f;
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
                if (_solidTop[i] > _ground[i] + EPSILON)
                {
                    _pendingMoving[i] += added;
                    _pendingMomentum[i] += initialVelocity * added;
                    continue;
                }
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

        /// <summary>Advances one bounded grid-flow tick when due, validates snow volume and applies loads to pieces.</summary>
        public void Step(float dt, float totalVolume, IReadOnlyList<AvalanchePiece> pieces)
        {
            if (!IsValid) return;
            _simulationClock += dt;
            if (_simulationClock + EPSILON >= _settings.SimulationInterval)
            {
                _simulationClock -= _settings.SimulationInterval;
                UpdateBarriers(pieces);
                RedistributeDisplacedSnow();
                Advance(_settings.SimulationInterval);
                double volume = OutflowVolume;
                ActiveVolume = DepositedVolume = 0f;
                for (int i = 0; i < _moving.Length; i++)
                {
                    if (!float.IsFinite(_moving[i]) || !float.IsFinite(_settled[i]) ||
                        !float.IsFinite(_pendingMoving[i]) || !float.IsFinite(_pendingSettled[i]) ||
                        !float.IsFinite(_velocity[i].x) || !float.IsFinite(_velocity[i].y) ||
                        _moving[i] < -EPSILON || _settled[i] < -EPSILON)
                    { Fail("눈의 두께 또는 속도가 안정 범위를 벗어났습니다."); return; }
                    volume += (_moving[i] + _settled[i] + _pendingMoving[i] + _pendingSettled[i]) * DX * DX;
                    DepositedVolume += (_settled[i] + _pendingSettled[i]) * DX * DX;
                    ActiveVolume += _pendingMoving[i] * DX * DX;
                    if (_velocity[i].magnitude >= _settings.SettleSpeed) ActiveVolume += _moving[i] * DX * DX;
                }
                VolumeError = Mathf.Abs((float)volume - InjectedVolume);
                if (VolumeError > totalVolume * 0.01f) { Fail("눈 부피 보존 오차가 방출량의 1%를 넘었습니다."); return; }
            }
            foreach (AvalanchePiece piece in pieces) piece.ApplySnow(this, _settings);
            _renderClock += dt;
            if (_renderClock >= _settings.MeshInterval) { _renderClock %= _settings.MeshInterval; RefreshMesh(); }
        }
        /// <summary>Projects changed colliders onto cell faces and supported columns, preserving displaced snow separately.</summary>
        private void UpdateBarriers(IReadOnlyList<AvalanchePiece> pieces)
        {
            int activeCount = 0;
            bool changed = false;
            foreach (AvalanchePiece piece in pieces)
            {
                if (!piece.gameObject.activeInHierarchy) continue;
                activeCount++;
                if (!_poses.TryGetValue(piece.Id, out Matrix4x4 pose) || pose != piece.transform.localToWorldMatrix) changed = true;
            }
            if (!changed && _poses.Count == activeCount) return;
            _poses.Clear();
            Array.Copy(_solidTop, _previousSolidTop, _solidTop.Length);
            Array.Copy(_ground, _solidTop, _ground.Length);
            Array.Fill(_barrierX, float.NegativeInfinity);
            Array.Fill(_barrierZ, float.NegativeInfinity);
            _activePieces.Clear();
            foreach (AvalanchePiece piece in pieces)
            {
                if (!piece.gameObject.activeInHierarchy) continue;
                _poses[piece.Id] = piece.transform.localToWorldMatrix;
                _activePieces.Add(piece);
            }
            _activePieces.Sort(CompareSupportHeight);
            foreach (AvalanchePiece piece in _activePieces)
            {
                Bounds bounds = piece.Shape.bounds;
                int x0 = Mathf.Clamp(Mathf.FloorToInt((bounds.min.x + 18f) / DX), 0, W - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((bounds.max.x + 18f) / DX), 0, W);
                int z0 = Mathf.Clamp(Mathf.FloorToInt(bounds.min.z / DX), 0, L - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt(bounds.max.z / DX), 0, L);
                for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    if (z < L && x < W) ProjectColumn(piece, x, z);
                    if (z < L && x > 0 && x < W)
                        ProjectFace(piece, Center(x - 1, z), Vector3.right, ref _barrierX[z * (W + 1) + x]);
                    if (x < W && z > 0 && z < L)
                        ProjectFace(piece, Center(x, z - 1), Vector3.forward, ref _barrierZ[z * W + x]);
                }
            }
            for (int i = 0; i < _moving.Length; i++)
            {
                float rise = Mathf.Max(0f, _solidTop[i] - _previousSolidTop[i]);
                float stationary = Mathf.Min(_settled[i], rise);
                float moving = Mathf.Min(_moving[i], Mathf.Max(0f, rise - stationary));
                _settled[i] -= stationary;
                _moving[i] -= moving;
                _pendingSettled[i] += stationary;
                _pendingMoving[i] += moving;
                _pendingMomentum[i] += _velocity[i] * moving;
            }
        }

        /// <summary>Projects lower supports before stacked pieces so their shared occupied height is continuous.</summary>
        private static int CompareSupportHeight(AvalanchePiece a, AvalanchePiece b) => a.Shape.bounds.min.y.CompareTo(b.Shape.bounds.min.y);

        /// <summary>Projects supported solid columns, keeping their occupied height separate from snow mass.</summary>
        private void ProjectColumn(AvalanchePiece piece, int x, int z)
        {
            int i = z * W + x;
            Vector3 center = Center(x, z);
            Bounds bounds = piece.Shape.bounds;
            if (bounds.min.y > _solidTop[i] + _settled[i] + 0.4f) return;
            center.y = bounds.max.y + 0.02f;
            if (piece.Shape.Raycast(new Ray(center, Vector3.down), out RaycastHit top, bounds.size.y + 0.04f))
                _solidTop[i] = Mathf.Max(_solidTop[i], top.point.y);
        }

        /// <summary>Moves displaced layers into adjacent unoccupied columns or keeps them until space becomes available.</summary>
        private void RedistributeDisplacedSnow()
        {
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                if (_pendingMoving[i] <= 0f && _pendingSettled[i] <= 0f) continue;
                if (_solidTop[i] <= _ground[i] + EPSILON)
                {
                    float depth = _moving[i] + _pendingMoving[i];
                    if (depth > EPSILON) _velocity[i] = (_velocity[i] * _moving[i] + _pendingMomentum[i]) / depth;
                    _moving[i] = depth;
                    _settled[i] += _pendingSettled[i];
                    _pendingMoving[i] = _pendingSettled[i] = 0f;
                    _pendingMomentum[i] = Vector2.zero;
                    continue;
                }
                int available = 0;
                for (int direction = 0; direction < 4; direction++)
                    if (OpenNeighbour(x, z, direction) >= 0) available++;
                if (available == 0) continue;
                for (int direction = 0; direction < 4; direction++)
                {
                    int target = OpenNeighbour(x, z, direction);
                    if (target < 0) continue;
                    float added = _pendingMoving[i] / available;
                    float depth = _moving[target] + added;
                    if (depth > EPSILON) _velocity[target] = (_velocity[target] * _moving[target] + _pendingMomentum[i] / available) / depth;
                    _moving[target] = depth;
                    _settled[target] += _pendingSettled[i] / available;
                }
                _pendingMoving[i] = _pendingSettled[i] = 0f;
                _pendingMomentum[i] = Vector2.zero;
            }
        }

        /// <summary>Returns an adjacent unoccupied cell or -1 at a boundary or another solid column.</summary>
        private int OpenNeighbour(int x, int z, int direction)
        {
            int nx = x + (direction == 0 ? -1 : direction == 1 ? 1 : 0);
            int nz = z + (direction == 2 ? -1 : direction == 3 ? 1 : 0);
            if (nx < 0 || nx >= W || nz < 0 || nz >= L) return -1;
            int index = nz * W + nx;
            return _solidTop[index] <= _ground[index] + EPSILON ? index : -1;
        }

        /// <summary>Finds collider crossings between neighbouring cell centres, including sub-cell thin walls.</summary>
        private static void ProjectFace(AvalanchePiece piece, Vector3 start, Vector3 direction, ref float height)
        {
            Bounds bounds = piece.Shape.bounds;
            float ground = Mathf.Min(AvalancheTerrain.Height(start.x, start.z), AvalancheTerrain.Height(start.x + direction.x * DX, start.z + direction.z * DX));
            float support = float.IsFinite(height) ? Mathf.Max(ground, height) : ground;
            if (bounds.min.y > support + 0.4f) return;
            start.y = Mathf.Max(bounds.min.y + 0.02f, ground + 0.05f);
            bool intersects = piece.Shape.Raycast(new Ray(start, direction), out _, DX) ||
                piece.Shape.Raycast(new Ray(start + direction * DX, -direction), out _, DX) ||
                (piece.Shape.ClosestPoint(start) - start).sqrMagnitude < 0.0001f;
            if (intersects) height = Mathf.Max(height, bounds.max.y);
        }

        /// <summary>Routes bounded snow transfers across lower neighbouring surfaces and deposits settled snow.</summary>
        private void Advance(float dt)
        {
            Array.Clear(_outgoing, 0, _outgoing.Length);
            Array.Clear(_change, 0, _change.Length);
            Array.Clear(_flowVelocitySum, 0, _flowVelocitySum.Length);
            Array.Clear(_flowWeight, 0, _flowWeight.Length);
            for (int i = 0; i < _moving.Length; i++)
            {
                float speed = _velocity[i].magnitude;
                float remainingSpeed = Mathf.Max(0f, speed - _settings.MovingFriction * G * dt);
                _velocity[i] = speed > EPSILON ? _velocity[i] * (remainingSpeed / speed) : Vector2.zero;
            }
            for (int z = 0; z < L; z++)
            for (int x = 1; x < W; x++)
            {
                int a = z * W + x - 1, b = a + 1, f = z * (W + 1) + x;
                _fluxX[f] = FlowRequest(a, b, Bed(x - 1, z), Bed(x, z), _barrierX[f], dt, true);
                RegisterOutgoing(a, b, _fluxX[f]);
            }
            for (int z = 1; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int a = (z - 1) * W + x, b = a + W, f = z * W + x;
                _fluxZ[f] = FlowRequest(a, b, Bed(x, z - 1), Bed(x, z), _barrierZ[f], dt, false);
                RegisterOutgoing(a, b, _fluxZ[f]);
            }
            for (int x = 0; x < W; x++)
            {
                float bed = Bed(x, 0);
                float outsideBed = AvalancheTerrain.Height(Center(x, 0).x, -DX);
                float edgeDepth = Mathf.Max(0f, bed + _moving[x] - Mathf.Max(bed, outsideBed));
                _fluxZ[x] = edgeDepth * _settings.FlowResponse * dt;
                _outgoing[x] += _fluxZ[x];
            }
            for (int z = 0; z < L; z++)
            for (int x = 1; x < W; x++)
            {
                int a = z * W + x - 1, f = z * (W + 1) + x;
                Transfer(a, a + 1, _fluxX[f], Bed(x - 1, z), Bed(x, z), _barrierX[f], true);
            }
            for (int z = 1; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int a = (z - 1) * W + x, f = z * W + x;
                Transfer(a, a + W, _fluxZ[f], Bed(x, z - 1), Bed(x, z), _barrierZ[f], false);
            }
            for (int x = 0; x < W; x++)
            {
                float amount = _fluxZ[x] * TransferScale(x);
                _change[x] -= amount;
                float speed = Mathf.Min(_settings.MaximumFlowSpeed, Mathf.Sqrt(2f * G * Mathf.Max(0f, _moving[x])));
                _flowVelocitySum[x] += Vector2.down * speed * amount;
                _flowWeight[x] += amount;
                OutflowVolume += amount * DX * DX;
            }
            for (int i = 0; i < _moving.Length; i++)
            {
                float depth = Mathf.Max(0f, _moving[i] + _change[i]);
                if (_flowWeight[i] > EPSILON)
                {
                    Vector2 flowVelocity = _flowVelocitySum[i] / _flowWeight[i];
                    float blend = Mathf.Clamp01(_flowWeight[i] / Mathf.Max(EPSILON, depth));
                    _velocity[i] = Vector2.Lerp(_velocity[i], flowVelocity, blend);
                }
                if (depth <= EPSILON) _velocity[i] = Vector2.zero;
                _velocity[i] = Vector2.ClampMagnitude(_velocity[i], _settings.MaximumFlowSpeed);
                _moving[i] = depth;
                bool resting = _velocity[i].magnitude < _settings.SettleSpeed && _slope[i] <= _settings.RestingFriction;
                _depositClock[i] = resting ? _depositClock[i] + dt : 0f;
                if (_depositClock[i] < _settings.SettleDelay) continue;
                float deposited = depth < 0.005f ? depth : depth * (1f - Mathf.Exp(-0.693147f * dt / _settings.DepositHalfLife));
                _moving[i] -= deposited;
                _settled[i] += deposited;
            }
        }

        /// <summary>Combines exposed snow height difference and residual cell drift into a bounded face transfer request.</summary>
        private float FlowRequest(int a, int b, float bedA, float bedB, float barrier, float dt, bool xAxis)
        {
            float crest = Mathf.Max(bedA, Mathf.Max(bedB, barrier));
            float hA = Mathf.Max(0f, bedA + _moving[a] - crest);
            float hB = Mathf.Max(0f, bedB + _moving[b] - crest);
            float velocityA = xAxis ? _velocity[a].x : _velocity[a].y;
            float velocityB = xAxis ? _velocity[b].x : _velocity[b].y;
            float drift = 0.5f * (velocityA + velocityB) * Mathf.Min(hA, hB) * dt / DX;
            return (hA - hB) * _settings.FlowResponse * dt + drift;
        }

        /// <summary>Accumulates requested donor outflow before limiting it to available cell volume.</summary>
        private void RegisterOutgoing(int a, int b, float amount)
        {
            if (amount >= 0f) _outgoing[a] += amount;
            else _outgoing[b] -= amount;
        }

        /// <summary>Moves a capped amount of snow across a grid face and records its gameplay flow direction.</summary>
        private void Transfer(int a, int b, float signedAmount, float bedA, float bedB, float barrier, bool xAxis)
        {
            if (Mathf.Abs(signedAmount) <= EPSILON) return;
            int donor = signedAmount >= 0f ? a : b;
            float amount = signedAmount * TransferScale(donor);
            float crest = Mathf.Max(bedA, Mathf.Max(bedB, barrier));
            float hA = Mathf.Max(0f, bedA + _moving[a] - crest);
            float hB = Mathf.Max(0f, bedB + _moving[b] - crest);
            float velocityA = xAxis ? _velocity[a].x : _velocity[a].y;
            float velocityB = xAxis ? _velocity[b].x : _velocity[b].y;
            float speed = Mathf.Min(_settings.MaximumFlowSpeed,
                Mathf.Max(Mathf.Sqrt(2f * G * Mathf.Abs(hA - hB)), Mathf.Abs(0.5f * (velocityA + velocityB))));
            Vector2 direction = xAxis ? Vector2.right : Vector2.up;
            Vector2 flowVelocity = direction * Mathf.Sign(signedAmount) * speed;
            float flowWeight = Mathf.Abs(amount);
            _change[a] -= amount;
            _change[b] += amount;
            _flowVelocitySum[a] += flowVelocity * flowWeight;
            _flowVelocitySum[b] += flowVelocity * flowWeight;
            _flowWeight[a] += flowWeight;
            _flowWeight[b] += flowWeight;
        }

        /// <summary>Limits each donor to a fixed fraction of its moving snow for one solver tick.</summary>
        private float TransferScale(int donor)
        {
            return Mathf.Min(1f, _moving[donor] * _settings.MaximumCellTransferFraction / Mathf.Max(EPSILON, _outgoing[donor]));
        }

        /// <summary>Samples moving thickness, speed and deposited thickness at a world position.</summary>
        public bool Sample(Vector3 point, out float moving, out float deposited, out Vector2 velocity)
        {
            int x = Mathf.FloorToInt((point.x + 18f) / DX), z = Mathf.FloorToInt(point.z / DX);
            moving = deposited = 0f; velocity = Vector2.zero;
            if (x < 0 || x >= W || z < 0 || z >= L) return false;
            int i = z * W + x;
            moving = _moving[i] + _pendingMoving[i]; deposited = _settled[i] + _pendingSettled[i];
            velocity = moving > EPSILON ? (_velocity[i] * _moving[i] + _pendingMomentum[i]) / moving : Vector2.zero;
            return true;
        }

        /// <summary>Uploads a white moving layer and a muted stationary layer using reusable mesh arrays.</summary>
        private void RefreshMesh()
        {
            for (int z = 0; z < L; z++)
            for (int x = 0; x < W; x++)
            {
                int i = z * W + x;
                float total = _moving[i] + _settled[i] + _pendingMoving[i] + _pendingSettled[i];
                Color color = Color.Lerp(new Color(0.66f, 0.76f, 0.85f), Color.white, Mathf.Clamp01(_velocity[i].magnitude / 3f));
                color.a = total > 0.002f ? 1f : 0f;
                for (int j = 0; j < 4; j++)
                {
                    int v = i * 4 + j;
                    _vertices[v].y = Mathf.Max(AvalancheTerrain.Height(_vertices[v].x, _vertices[v].z), _solidTop[i]) + total + 0.015f;
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
