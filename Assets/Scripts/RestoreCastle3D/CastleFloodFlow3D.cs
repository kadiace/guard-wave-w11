using System.Collections.Generic;
using UnityEngine;

namespace RestoreCastle3D
{
    /// <summary>강바닥 위의 수심과 수평 유량을 격자로 계산하고 잠긴 Rigidbody에 물의 힘을 전달한다.</summary>
    internal sealed class CastleFloodFlow3D
    {
        private const float CELL_SIZE = 0.5f;
        private const float WET_DEPTH = 0.03f;
        private const float MOVING_WATER_SPEED = 0.5f;
        private const float FLOW_DAMPING = 0.985f;
        private const float OBSTACLE_BLOCKAGE = 0.95f;
        private const float MAX_DEPTH_MULTIPLIER = 1.5f;
        private const float WATER_DENSITY = 2f;
        private const float DRAG_STRENGTH = 4.5f;
        private const float MAX_BODY_ACCELERATION = 8f;
        private const float FLAG_MAX_ACCELERATION = 32f;
        private const float MAX_IMPACT_ACCELERATION = 40f;
        private const float FLAG_MAX_IMPACT_ACCELERATION = 80f;
        private const float MAX_BUOYANCY_WEIGHT_FRACTION = 0.5f;

        private readonly int _columns;
        private readonly int _rows;
        private readonly float _minX;
        private readonly float _minZ;
        private readonly float _cellX;
        private readonly float _cellZ;
        private readonly float _groundY;
        private readonly float _targetDepth;
        private readonly float _flowSpeed;
        private readonly float[] _blockage;
        private readonly float[] _outgoingScale;
        private readonly float[] _incomingScale;
        private readonly float[] _flowX;
        private readonly float[] _flowZ;
        private float[] _depth;
        private float[] _nextDepth;

        public int Columns => _columns;
        public int Rows => _rows;
        public float GroundY => _groundY;
        public float FrontZ { get; private set; }
        public float WaterVolume { get; private set; }
        public float MovingWaterVolume { get; private set; }
        public float CellX => _cellX;
        public float CellZ => _cellZ;

        /// <summary>물 격자를 강의 XZ 범위에 맞춰 만들고 상류에서 하류로 흐를 목표 속도를 정한다.</summary>
        public CastleFloodFlow3D(float centerX, float width, float startZ, float endZ,
            float groundY, float targetDepth, float travelDuration)
        {
            _columns = Mathf.Clamp(Mathf.CeilToInt(width / CELL_SIZE), 4, 48);
            _rows = Mathf.Clamp(Mathf.CeilToInt((startZ - endZ) / CELL_SIZE), 4, 160);
            _minX = centerX - width * 0.5f;
            _minZ = endZ;
            _cellX = width / _columns;
            _cellZ = (startZ - endZ) / _rows;
            _groundY = groundY;
            _targetDepth = Mathf.Max(0.1f, targetDepth);
            _flowSpeed = Mathf.Clamp((startZ - endZ) / Mathf.Max(0.1f, travelDuration), 2f, 35f);
            _depth = new float[_columns * _rows];
            _nextDepth = new float[_depth.Length];
            _blockage = new float[_depth.Length];
            _outgoingScale = new float[_depth.Length];
            _incomingScale = new float[_depth.Length];
            _flowX = new float[(_columns + 1) * _rows];
            _flowZ = new float[_columns * (_rows + 1)];
            FrontZ = startZ;
        }

        /// <summary>격자의 해당 칸의 현재 수심을 반환한다.</summary>
        public float DepthAt(int column, int row) => _depth[Index(column, row)];

        /// <summary>격자 칸의 월드 XZ 중심을 반환한다.</summary>
        public Vector2 CenterAt(int column, int row)
        {
            return new Vector2(_minX + (column + 0.5f) * _cellX,
                _minZ + (row + 0.5f) * _cellZ);
        }

        /// <summary>고정 시간 단계에서 상류 물을 공급하고 유량·수심·기물 반응을 계산한다.</summary>
        public void Step(float deltaTime, IList<CastlePiece3D> pieces, Rigidbody flagBody,
            Collider flagCollider, float inletFraction)
        {
            float waveSpeed = Mathf.Sqrt(Physics.gravity.magnitude * _targetDepth * 2f);
            float substep = Mathf.Min(_cellX, _cellZ) /
                Mathf.Max(1f, _flowSpeed + waveSpeed) * 0.7f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(deltaTime / substep));
            float stepTime = deltaTime / steps;
            UpdateBlockage(pieces, flagCollider);
            for (int i = 0; i < steps; i++)
            {
                AdvanceWater(stepTime, inletFraction);
            }
            UpdateFront();
            ApplyBodyForces(deltaTime, pieces, flagBody, flagCollider);
        }

        /// <summary>유량 계산과 외부 힘 적용에 사용할 위치의 수면 높이와 물속도를 반환한다.</summary>
        private bool Sample(float x, float z, out float surfaceY, out Vector3 velocity,
            out int column, out int row)
        {
            column = Mathf.FloorToInt((x - _minX) / _cellX);
            row = Mathf.FloorToInt((z - _minZ) / _cellZ);
            surfaceY = _groundY;
            velocity = Vector3.zero;
            if (column < 0 || column >= _columns || row < 0 || row >= _rows)
                return false;

            float depth = _depth[Index(column, row)];
            if (depth < WET_DEPTH) return false;
            surfaceY += depth;
            float xFlow = (_flowX[XFace(column, row)] + _flowX[XFace(column + 1, row)]) * 0.5f;
            float zFlow = (_flowZ[ZFace(column, row)] + _flowZ[ZFace(column, row + 1)]) * 0.5f;
            velocity = new Vector3(xFlow / depth, 0f, zFlow / depth);
            return true;
        }

        /// <summary>월드 XZ 위치의 수심을 반환하며 격자 밖은 마른 땅으로 취급한다.</summary>
        public float DepthAtWorld(float x, float z)
        {
            int column = Mathf.FloorToInt((x - _minX) / _cellX);
            int row = Mathf.FloorToInt((z - _minZ) / _cellZ);
            if (column < 0 || column >= _columns || row < 0 || row >= _rows)
                return 0f;
            return _depth[Index(column, row)];
        }

        /// <summary>기물이 각 격자의 물속 면적을 차지하는 비율만큼 유량을 막는다.</summary>
        private void UpdateBlockage(IList<CastlePiece3D> pieces, Collider flagCollider)
        {
            System.Array.Clear(_blockage, 0, _blockage.Length);
            foreach (CastlePiece3D piece in pieces)
            {
                if (piece == null) continue;
                AddBlockage(piece.GetComponent<Collider>().bounds);
            }
            AddBlockage(flagCollider.bounds);
        }

        /// <summary>충돌체가 차지하는 격자 면적과 물속 높이를 차단율에 반영한다.</summary>
        private void AddBlockage(Bounds bounds)
        {
            if (bounds.max.y <= _groundY)
                return;
            if (bounds.max.x < _minX || bounds.min.x > _minX + _columns * _cellX ||
                bounds.max.z < _minZ || bounds.min.z > _minZ + _rows * _cellZ)
                return;
            int minX = Mathf.Clamp(Mathf.FloorToInt((bounds.min.x - _minX) / _cellX), 0, _columns - 1);
            int maxX = Mathf.Clamp(Mathf.FloorToInt((bounds.max.x - _minX) / _cellX), 0, _columns - 1);
            int minZ = Mathf.Clamp(Mathf.FloorToInt((bounds.min.z - _minZ) / _cellZ), 0, _rows - 1);
            int maxZ = Mathf.Clamp(Mathf.FloorToInt((bounds.max.z - _minZ) / _cellZ), 0, _rows - 1);
            for (int row = minZ; row <= maxZ; row++)
                for (int column = minX; column <= maxX; column++)
                {
                    int cell = Index(column, row);
                    float depth = _depth[cell];
                    if (depth < WET_DEPTH) continue;
                    float cellMinX = _minX + column * _cellX;
                    float cellMinZ = _minZ + row * _cellZ;
                    float overlapX = Mathf.Max(0f, Mathf.Min(bounds.max.x, cellMinX + _cellX)
                        - Mathf.Max(bounds.min.x, cellMinX));
                    float overlapZ = Mathf.Max(0f, Mathf.Min(bounds.max.z, cellMinZ + _cellZ)
                        - Mathf.Max(bounds.min.z, cellMinZ));
                    float overlapY = Mathf.Max(0f, Mathf.Min(bounds.max.y, _groundY + depth)
                        - Mathf.Max(bounds.min.y, _groundY));
                    float occupied = overlapX * overlapZ / (_cellX * _cellZ)
                        * Mathf.Clamp01(overlapY / depth);
                    _blockage[cell] = Mathf.Min(OBSTACLE_BLOCKAGE,
                        _blockage[cell] + occupied * OBSTACLE_BLOCKAGE);
                }
        }

        /// <summary>수면 높이 차와 하류 유속으로 면 유량을 구하고 각 칸의 물 보존량을 갱신한다.</summary>
        private void AdvanceWater(float deltaTime, float inletFraction)
        {
            for (int column = 0; column < _columns; column++)
            {
                int inlet = Index(column, _rows - 1);
                _depth[inlet] = _targetDepth * inletFraction;
            }

            for (int row = 0; row < _rows; row++)
            {
                _flowX[XFace(0, row)] = 0f;
                _flowX[XFace(_columns, row)] = 0f;
                for (int column = 1; column < _columns; column++)
                {
                    int left = Index(column - 1, row);
                    int right = Index(column, row);
                    float depth = Mathf.Max(_depth[left], _depth[right]);
                    float pressure = Physics.gravity.magnitude * depth *
                        (_depth[left] - _depth[right]) / _cellX;
                    float permeability = 1f - Mathf.Max(_blockage[left], _blockage[right]);
                    _flowX[XFace(column, row)] = Mathf.Clamp(
                        (_flowX[XFace(column, row)] * FLOW_DAMPING + pressure * deltaTime)
                        * permeability, -_flowSpeed * depth, _flowSpeed * depth);
                }
            }

            for (int column = 0; column < _columns; column++)
            {
                _flowZ[ZFace(column, _rows)] = 0f;
                _flowZ[ZFace(column, 0)] = -_flowSpeed * _depth[Index(column, 0)];
                for (int row = 1; row < _rows; row++)
                {
                    int down = Index(column, row - 1);
                    int up = Index(column, row);
                    float targetFlow = -_flowSpeed * _depth[up];
                    float permeability = 1f - Mathf.Max(_blockage[down], _blockage[up]);
                    _flowZ[ZFace(column, row)] = targetFlow * permeability;
                }
            }

            for (int row = 0; row < _rows; row++)
                for (int column = 0; column < _columns; column++)
                {
                    float left = _flowX[XFace(column, row)];
                    float right = _flowX[XFace(column + 1, row)];
                    float down = _flowZ[ZFace(column, row)];
                    float up = _flowZ[ZFace(column, row + 1)];
                    float outgoing = deltaTime *
                        ((Mathf.Max(0f, right) + Mathf.Max(0f, -left)) / _cellX +
                         (Mathf.Max(0f, up) + Mathf.Max(0f, -down)) / _cellZ);
                    int cell = Index(column, row);
                    _outgoingScale[cell] = outgoing > _depth[cell] && outgoing > 0f
                        ? _depth[cell] / outgoing : 1f;
                }

            for (int row = 0; row < _rows; row++)
                for (int column = 1; column < _columns; column++)
                {
                    int face = XFace(column, row);
                    int donor = _flowX[face] > 0f ? Index(column - 1, row) : Index(column, row);
                    _flowX[face] *= _outgoingScale[donor];
                }
            for (int column = 0; column < _columns; column++)
                for (int row = 0; row < _rows; row++)
                {
                    int face = ZFace(column, row);
                    if (_flowZ[face] < 0f)
                        _flowZ[face] *= _outgoingScale[Index(column, row)];
                    else if (row > 0)
                        _flowZ[face] *= _outgoingScale[Index(column, row - 1)];
                }

            for (int row = 0; row < _rows; row++)
                for (int column = 0; column < _columns; column++)
                {
                    float left = _flowX[XFace(column, row)];
                    float right = _flowX[XFace(column + 1, row)];
                    float down = _flowZ[ZFace(column, row)];
                    float up = _flowZ[ZFace(column, row + 1)];
                    float incoming = deltaTime *
                        ((Mathf.Max(0f, left) + Mathf.Max(0f, -right)) / _cellX +
                         (Mathf.Max(0f, down) + Mathf.Max(0f, -up)) / _cellZ);
                    float outgoing = deltaTime *
                        ((Mathf.Max(0f, right) + Mathf.Max(0f, -left)) / _cellX +
                         (Mathf.Max(0f, up) + Mathf.Max(0f, -down)) / _cellZ);
                    int cell = Index(column, row);
                    float available = _targetDepth * MAX_DEPTH_MULTIPLIER
                        - _depth[cell] + outgoing;
                    _incomingScale[cell] = incoming > 0f
                        ? Mathf.Clamp01(available / incoming) : 1f;
                }

            for (int row = 0; row < _rows; row++)
                for (int column = 1; column < _columns; column++)
                {
                    int face = XFace(column, row);
                    int receiver = _flowX[face] > 0f
                        ? Index(column, row) : Index(column - 1, row);
                    _flowX[face] *= _incomingScale[receiver];
                }
            for (int column = 0; column < _columns; column++)
                for (int row = 1; row < _rows; row++)
                {
                    int face = ZFace(column, row);
                    int receiver = _flowZ[face] < 0f
                        ? Index(column, row - 1) : Index(column, row);
                    _flowZ[face] *= _incomingScale[receiver];
                }

            for (int row = 0; row < _rows; row++)
                for (int column = 0; column < _columns; column++)
                {
                    int cell = Index(column, row);
                    float divergence =
                        (_flowX[XFace(column + 1, row)] - _flowX[XFace(column, row)]) / _cellX +
                        (_flowZ[ZFace(column, row + 1)] - _flowZ[ZFace(column, row)]) / _cellZ;
                    _nextDepth[cell] = Mathf.Max(0f, _depth[cell] - divergence * deltaTime);
                }
            (_depth, _nextDepth) = (_nextDepth, _depth);
        }

        /// <summary>기물과 깃발에 부력, 유속 저항, 앞면에 부딪힌 물의 운동량을 전달한다.</summary>
        private void ApplyBodyForces(float deltaTime, IList<CastlePiece3D> pieces,
            Rigidbody flagBody, Collider flagCollider)
        {
            foreach (CastlePiece3D piece in pieces)
            {
                if (piece == null || piece.Body.isKinematic) continue;
                ApplyBodyForce(deltaTime, piece.Body, piece.GetComponent<Collider>().bounds,
                    MAX_BODY_ACCELERATION, MAX_IMPACT_ACCELERATION);
            }
            ApplyBodyForce(deltaTime, flagBody, flagCollider.bounds, FLAG_MAX_ACCELERATION,
                FLAG_MAX_IMPACT_ACCELERATION);
        }

        /// <summary>잠긴 앞면에 들어오는 물의 부피와 상대 속도로 충돌 힘을 계산해 Rigidbody에 가한다.</summary>
        private void ApplyBodyForce(float deltaTime, Rigidbody body, Bounds bounds,
            float maxAcceleration, float maxImpactAcceleration)
        {
            float volume = bounds.size.x * bounds.size.y * bounds.size.z;
            if (volume <= 0f) return;
            Vector3 appliedDrag = Vector3.zero;
            for (int sample = 0; sample < 4; sample++)
            {
                float x = Mathf.Lerp(bounds.min.x, bounds.max.x, (sample & 1) == 0 ? 0.25f : 0.75f);
                float z = Mathf.Lerp(bounds.min.z, bounds.max.z, (sample & 2) == 0 ? 0.25f : 0.75f);
                if (!Sample(x, z, out float surfaceY, out Vector3 waterVelocity,
                        out int column, out int row)) continue;
                float submerged = Mathf.Clamp01((surfaceY - bounds.min.y) /
                    Mathf.Max(0.01f, bounds.size.y));
                if (submerged <= 0f) continue;
                Vector3 point = new Vector3(x, bounds.min.y + bounds.size.y * submerged * 0.5f, z);
                float sampleVolume = volume * submerged * 0.25f;
                Vector3 relativeVelocity = waterVelocity - body.GetPointVelocity(point);
                relativeVelocity.y = 0f;
                Vector3 drag = Vector3.ClampMagnitude(
                    relativeVelocity * DRAG_STRENGTH * sampleVolume,
                    body.mass * maxAcceleration * 0.25f);
                appliedDrag += drag;
                float buoyancy = Mathf.Min(WATER_DENSITY * Physics.gravity.magnitude * sampleVolume,
                    body.mass * Physics.gravity.magnitude * MAX_BUOYANCY_WEIGHT_FRACTION * 0.25f);
                Vector3 force = drag + Vector3.up * buoyancy;
                body.AddForceAtPosition(force, point, ForceMode.Force);

                float reaction = deltaTime / (WATER_DENSITY * _cellX * _cellZ);
                int xFace = XFace(column, row);
                int zFace = ZFace(column, row);
                _flowX[xFace] = Mathf.Clamp(_flowX[xFace] - force.x * reaction,
                    -_flowSpeed * _targetDepth, _flowSpeed * _targetDepth);
                _flowZ[zFace] = Mathf.Clamp(_flowZ[zFace] - force.z * reaction,
                    -_flowSpeed * _targetDepth, _flowSpeed * _targetDepth);
            }

            float upstreamDepth = DepthAtWorld(bounds.center.x, bounds.max.z + _cellZ * 0.5f);
            float downstreamDepth = DepthAtWorld(bounds.center.x, bounds.min.z - _cellZ * 0.5f);
            float frontalDepth = Mathf.Max(0f,
                Mathf.Min(bounds.max.y, _groundY + upstreamDepth)
                - Mathf.Max(bounds.min.y, _groundY));
            if (frontalDepth > 0f)
            {
                float frontalArea = bounds.size.x * frontalDepth;
                Sample(bounds.center.x, bounds.max.z + _cellZ * 0.5f,
                    out _, out Vector3 upstreamVelocity, out _, out _);
                float relativeSpeed = Mathf.Max(0f, -upstreamVelocity.z + body.linearVelocity.z);
                float incomingVolume = frontalArea * relativeSpeed * deltaTime;
                float impactForce = WATER_DENSITY * incomingVolume * relativeSpeed
                    / Mathf.Max(deltaTime, 0.0001f);
                float pressureForce = WATER_DENSITY * Physics.gravity.magnitude
                    * frontalArea * Mathf.Max(0f, upstreamDepth - downstreamDepth);
                float forceLimit = Mathf.Max(0f,
                    body.mass * maxImpactAcceleration - appliedDrag.magnitude);
                float forwardForce = Mathf.Min(impactForce + pressureForce, forceLimit);
                Vector3 contact = new Vector3(bounds.center.x,
                    Mathf.Max(bounds.min.y, _groundY) + frontalDepth * 0.5f,
                    bounds.max.z);
                body.AddForceAtPosition(Vector3.back * forwardForce, contact, ForceMode.Force);
            }
        }

        /// <summary>파도 전면과 남은 물의 양, 일정 속도 이상으로 흐르는 물의 양을 갱신한다.</summary>
        private void UpdateFront()
        {
            FrontZ = _minZ + _rows * _cellZ;
            WaterVolume = 0f;
            MovingWaterVolume = 0f;
            float cellArea = _cellX * _cellZ;
            for (int row = 0; row < _rows; row++)
                for (int column = 0; column < _columns; column++)
                {
                    float depth = _depth[Index(column, row)];
                    if (depth < WET_DEPTH) continue;
                    FrontZ = Mathf.Min(FrontZ, _minZ + row * _cellZ);
                    float volume = depth * cellArea;
                    WaterVolume += volume;
                    float xFlow = (_flowX[XFace(column, row)] +
                        _flowX[XFace(column + 1, row)]) * 0.5f;
                    float zFlow = (_flowZ[ZFace(column, row)] +
                        _flowZ[ZFace(column, row + 1)]) * 0.5f;
                    float speedThreshold = depth * MOVING_WATER_SPEED;
                    if (xFlow * xFlow + zFlow * zFlow > speedThreshold * speedThreshold)
                        MovingWaterVolume += volume;
                }
        }

        /// <summary>격자 좌표를 수심 배열의 인덱스로 변환한다.</summary>
        private int Index(int column, int row) => row * _columns + column;

        /// <summary>X 방향 경계면의 유량 배열 인덱스를 반환한다.</summary>
        private int XFace(int column, int row) => row * (_columns + 1) + column;

        /// <summary>Z 방향 경계면의 유량 배열 인덱스를 반환한다.</summary>
        private int ZFace(int column, int row) => row * _columns + column;
    }
}
