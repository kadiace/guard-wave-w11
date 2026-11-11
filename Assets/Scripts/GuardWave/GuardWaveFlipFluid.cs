using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// GPU-resident three-dimensional FLIP/APIC liquid with a pressure-projected Eulerian grid.
    /// Physics interaction is bidirectional: collider velocities go to the GPU, compact impulses return.
    /// </summary>
    internal sealed class GuardWaveFlipFluid : IDisposable
    {
        private const int THREADS = 128;
        private const float IMPULSE_SCALE = 2048f;
        // Leave an actual open-water buffer past the z=-15 lip, rather than
        // placing the pressure grid's solid boundary against the waterfall.
        private const int OUTFLOW_BUFFER_CELLS = 4;
        private const int UPSTREAM_BUFFER_CELLS = 2;
        private readonly GuardWaveFluidSettings _settings;
        private readonly float _cellSize;

        [StructLayout(LayoutKind.Sequential)]
        private struct Particle
        {
            public Vector4 Position;
            public Vector4 Velocity;
            public Vector4 AffineX;
            public Vector4 AffineY;
            public Vector4 AffineZ;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ColliderData
        {
            public Vector4 CenterBody;
            public Vector4 ExtentType;
            public Vector4 Rotation;
            public Vector4 Velocity;
            public Vector4 Angular;
            public Vector4 BodyCenter;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Int4
        {
            public int X;
            public int Y;
            public int Z;
            public int W;
        }

        private readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        private readonly List<Collider> _colliders = new List<Collider>();
        private readonly Dictionary<Rigidbody, int> _indices = new Dictionary<Rigidbody, int>();
        private readonly ColliderData[] _colliderData;
        private readonly ComputeBuffer _particles;
        private readonly ComputeBuffer _accum;
        private readonly ComputeBuffer _velocity;
        private readonly ComputeBuffer _before;
        private readonly ComputeBuffer _faceVelocity;
        private readonly ComputeBuffer _divergenceStats;
        private readonly ComputeBuffer _pressureA;
        private readonly ComputeBuffer _pressureB;
        private readonly ComputeBuffer _solid;
        private readonly ComputeBuffer _colliderBuffer;
        private readonly ComputeBuffer _impulses;
        private readonly ComputeShader _compute;
        private readonly Vector3Int _grid;
        private readonly Vector3 _origin;
        private readonly float _startZ;
        private readonly float _endZ;
        private readonly float _floorY;
        private readonly float _waveHeight;
        private readonly bool _ambientFlow;
        private readonly ComputeBuffer _flowCounters;
        private readonly int _initializeAmbientKernel;
        private readonly int _recycleAmbientKernel;
        private readonly int _driveInletKernel;
        private readonly int _shiftAmbientSpeedKernel;
        private readonly int _emitCrestKernel;
        private readonly ComputeBuffer _crestCounter;
        private int _ambientInitialCount;
        private int _ambientColumns;
        private readonly int _gridCount;
        private readonly int _clearKernel;
        private readonly int _classifyKernel;
        private readonly int _spawnKernel;
        private readonly int _p2gKernel;
        private readonly int _normalizeKernel;
        private readonly int _buildFaceKernel;
        private readonly int _measureDivergenceKernel;
        private readonly int _pressureKernel;
        private readonly int _projectFacesKernel;
        private readonly int _reconstructGridKernel;
        private readonly int _g2pKernel;
        private readonly int _pressureBodyKernel;
        private readonly int _waterfallKernel;
        private float _spawnClock;
        private float _elapsed;
        // Fractional refill credits prevent rounding drift at low flow speeds.
        private double _ambientRefillFraction;
        private float _currentAmbientSpeed;
        private int _particleCount;
        private int _seed;
        private bool _pendingReadback;
        private bool _flowReadbackPending;
        private float _flowReadbackClock;
        private bool _divergenceReadbackPending;
        private float _divergenceReadbackClock;
        private bool _disposed;

        public ComputeBuffer Particles => _particles;
        public int ParticleCount => _particleCount;
        public float InletSpeed { get; }
        public float CurrentAmbientSpeed => _currentAmbientSpeed;
        public Bounds RenderBounds { get; }
        public float DivergenceBeforeProjection { get; private set; }
        public float DivergenceAfterProjection { get; private set; }
        // GPU flow measurements; refreshed at most once per second without logs.
        public uint TotalCliffExits { get; private set; }
        public uint TotalAmbientRefills { get; private set; }
        public uint PendingAmbientRefills { get; private set; }

        /// <summary>Builds a bounded GPU fluid domain centered at x=0 and a 24m wide river.</summary>
        public GuardWaveFlipFluid(float startZ, float endZ, float groundY,
            float waterfallEndY, float waveHeight, float travelDuration, IList<GuardWavePiece> pieces,
            Rigidbody flagBody, GuardWaveFluidSettings settings, bool ambientFlow = false)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ambientFlow = ambientFlow;
            _cellSize = _settings.CellSize;
            _colliderData = new ColliderData[_settings.MaxColliders];
            if (!SystemInfo.supportsComputeShaders)
                throw new NotSupportedException("GPU FLIP water requires compute shader support.");

            ComputeShader asset = Resources.Load<ComputeShader>("GuardWaveFlip");
            if (asset == null)
                throw new InvalidOperationException("Missing Resources/GuardWaveFlip.compute");
            _compute = UnityEngine.Object.Instantiate(asset);
            _floorY = groundY;
            _waveHeight = ambientFlow ? _settings.AmbientWaterDepth :
                Mathf.Clamp(waveHeight, 1f, 6f);
            _startZ = Mathf.Max(startZ, endZ + 8f);
            _endZ = endZ;
            InletSpeed = ambientFlow ? _settings.AmbientFlowSpeed :
                Mathf.Clamp((_startZ - _endZ) /
                    Mathf.Max(4f, travelDuration * _settings.TravelDurationMultiplier),
                    _settings.MinInletSpeed, _settings.MaxInletSpeed);
            _currentAmbientSpeed = InletSpeed;
            _grid = new Vector3Int(
                Mathf.CeilToInt((24f + _cellSize * 2f) / _cellSize),
                Mathf.CeilToInt((9f + _cellSize) / _cellSize),
                Mathf.CeilToInt((_startZ - _endZ +
                    _cellSize * (OUTFLOW_BUFFER_CELLS + UPSTREAM_BUFFER_CELLS)) / _cellSize));
            _origin = new Vector3(-12f - _cellSize, groundY - _cellSize,
                _endZ - _cellSize * OUTFLOW_BUFFER_CELLS);
            _gridCount = _grid.x * _grid.y * _grid.z;
            // Include lower-cliff free-fall particles in procedural renderer culling.
            RenderBounds = new Bounds(
                new Vector3(0, (groundY + 9f + waterfallEndY) * 0.5f,
                    (_startZ + _endZ - 80f) * 0.5f),
                new Vector3(27f, groundY + 13f - waterfallEndY,
                    _startZ - _endZ + 86f));

            _particles = new ComputeBuffer(_settings.MaxParticles, Marshal.SizeOf<Particle>());
            _particles.SetData(new Particle[_settings.MaxParticles]);
            _accum = new ComputeBuffer(_gridCount, sizeof(int) * 4);
            _velocity = new ComputeBuffer(_gridCount, sizeof(float) * 3);
            _before = new ComputeBuffer(_gridCount, sizeof(float) * 3);
            _faceVelocity = new ComputeBuffer(_gridCount, sizeof(float) * 3);
            _divergenceStats = new ComputeBuffer(4, sizeof(uint));
            _pressureA = new ComputeBuffer(_gridCount, sizeof(float));
            _pressureB = new ComputeBuffer(_gridCount, sizeof(float));
            _solid = new ComputeBuffer(_gridCount, sizeof(uint));
            _colliderBuffer = new ComputeBuffer(_settings.MaxColliders, Marshal.SizeOf<ColliderData>());
            _impulses = new ComputeBuffer(_settings.MaxBodies * 2, sizeof(int) * 4);
            _flowCounters = new ComputeBuffer(4, sizeof(uint));
            _flowCounters.SetData(new uint[4]);
            _crestCounter = new ComputeBuffer(1, sizeof(uint));

            _clearKernel = _compute.FindKernel("ClearGrid");
            _classifyKernel = _compute.FindKernel("ClassifyGrid");
            _spawnKernel = _compute.FindKernel("Spawn");
            _initializeAmbientKernel = _compute.FindKernel("InitializeAmbient");
            _recycleAmbientKernel = _compute.FindKernel("RecycleAmbient");
            _driveInletKernel = _compute.FindKernel("DriveInlet");
            _shiftAmbientSpeedKernel = _compute.FindKernel("ShiftAmbientSpeed");
            _emitCrestKernel = _compute.FindKernel("EmitCrest");
            _p2gKernel = _compute.FindKernel("ParticleToGrid");
            _normalizeKernel = _compute.FindKernel("NormalizeGrid");
            _buildFaceKernel = _compute.FindKernel("BuildFaceVelocities");
            _measureDivergenceKernel = _compute.FindKernel("MeasureDivergence");
            _pressureKernel = _compute.FindKernel("JacobiPressure");
            _projectFacesKernel = _compute.FindKernel("ProjectFaces");
            _reconstructGridKernel = _compute.FindKernel("ReconstructGridVelocity");
            _g2pKernel = _compute.FindKernel("GridToParticle");
            _pressureBodyKernel = _compute.FindKernel("PressureToBodies");
            _waterfallKernel = _compute.FindKernel("Waterfall");

            _compute.SetInts("_GridSize", _grid.x, _grid.y, _grid.z);
            _compute.SetVector("_GridOrigin", _origin);
            _compute.SetFloat("_CellSize", _cellSize);
            _compute.SetFloat("_FloorY", groundY);
            _compute.SetFloat("_ExitZ", endZ);
            _compute.SetFloat("_WaterfallEndY", waterfallEndY);
            _compute.SetFloat("_HalfWidth", 12f);
            _compute.SetFloat("_InletZ", _startZ - _cellSize);
            _compute.SetFloat("_WaveHeight", _waveHeight);
            _compute.SetFloat("_InletVelocity", InletSpeed);
            _compute.SetInt("_AmbientEnabled", ambientFlow ? 1 : 0);
            _compute.SetFloat("_AmbientDepth", _settings.AmbientWaterDepth);
            _compute.SetFloat("_Gravity", Mathf.Abs(Physics.gravity.y) * _settings.GravityMultiplier);
            _compute.SetFloat("_FlipFraction", _settings.FlipFraction);
            // Preview uses calm flow even if the saved wave profile has 100x impact tuning.
            float forceScale = ambientFlow ? _settings.AmbientImpactScale : 1f;
            _compute.SetFloat("_ParticleMass", _settings.ParticleEffectiveMass * forceScale);
            _compute.SetFloat("_HydroDensity", _settings.HydroDensity * forceScale);
            _compute.SetFloat("_PressureClamp", _settings.PressureClamp);
            _compute.SetFloat("_PressureThreshold", _settings.PressureThreshold);
            _compute.SetFloat("_CollisionResponse", _settings.CollisionResponse);
            _compute.SetFloat("_CollisionSkin", _settings.CollisionSkin);
            _compute.SetFloat("_MaxFluidVelocity", _settings.MaxParticleSpeed);
            _compute.SetFloat("_WakePreserveRadius", _settings.WakePreserveRadius);
            _compute.SetFloat("_WakeFadeDistance", _settings.WakeFadeDistance);
            _compute.SetFloat("_WakeReverseDamping", _settings.WakeReverseDamping);
            _compute.SetFloat("_WakeMaxLength", _settings.WakeMaxLength);
            _compute.SetFloat("_WakeLateralRadius", _settings.WakeLateralRadius);
            _compute.SetInt("_BodyCount", 0);
            _compute.SetInt("_ColliderCount", 0);
            // Bind only the resources actually used by each kernel; Unity strips
            // unused compute resources from individual kernels.
            Bind(_clearKernel, "_Accum", _accum);
            Bind(_clearKernel, "_GridVelocity", _velocity);
            Bind(_clearKernel, "_GridBefore", _before);
            Bind(_clearKernel, "_PressureSrc", _pressureA);
            Bind(_clearKernel, "_PressureDst", _pressureB);
            Bind(_clearKernel, "_BodyImpulses", _impulses);
            Bind(_clearKernel, "_FlowCounters", _flowCounters);
            Bind(_clearKernel, "_DivergenceStats", _divergenceStats);
            Bind(_classifyKernel, "_Solid", _solid);
            Bind(_classifyKernel, "_Colliders", _colliderBuffer);
            Bind(_spawnKernel, "_Particles", _particles);
            Bind(_initializeAmbientKernel, "_Particles", _particles);
            Bind(_recycleAmbientKernel, "_Particles", _particles);
            Bind(_recycleAmbientKernel, "_FlowCounters", _flowCounters);
            Bind(_driveInletKernel, "_Particles", _particles);
            Bind(_shiftAmbientSpeedKernel, "_Particles", _particles);
            Bind(_emitCrestKernel, "_Particles", _particles);
            Bind(_emitCrestKernel, "_CrestCounter", _crestCounter);
            Bind(_p2gKernel, "_Particles", _particles);
            Bind(_p2gKernel, "_Accum", _accum);
            Bind(_p2gKernel, "_Solid", _solid);
            Bind(_normalizeKernel, "_Accum", _accum);
            Bind(_normalizeKernel, "_GridBefore", _before);
            Bind(_normalizeKernel, "_GridVelocity", _velocity);
            Bind(_normalizeKernel, "_Solid", _solid);
            Bind(_buildFaceKernel, "_Solid", _solid);
            Bind(_buildFaceKernel, "_Accum", _accum);
            Bind(_buildFaceKernel, "_GridVelocity", _velocity);
            Bind(_buildFaceKernel, "_FaceVelocity", _faceVelocity);
            Bind(_measureDivergenceKernel, "_Solid", _solid);
            Bind(_measureDivergenceKernel, "_Accum", _accum);
            Bind(_measureDivergenceKernel, "_FaceVelocity", _faceVelocity);
            Bind(_measureDivergenceKernel, "_DivergenceStats", _divergenceStats);
            Bind(_pressureKernel, "_FaceVelocity", _faceVelocity);
            Bind(_pressureKernel, "_Accum", _accum);
            Bind(_pressureKernel, "_Solid", _solid);
            Bind(_projectFacesKernel, "_Accum", _accum);
            Bind(_projectFacesKernel, "_Solid", _solid);
            Bind(_projectFacesKernel, "_FaceVelocity", _faceVelocity);
            Bind(_reconstructGridKernel, "_Accum", _accum);
            Bind(_reconstructGridKernel, "_Solid", _solid);
            Bind(_reconstructGridKernel, "_FaceVelocity", _faceVelocity);
            Bind(_reconstructGridKernel, "_GridVelocity", _velocity);
            Bind(_g2pKernel, "_Particles", _particles);
            Bind(_g2pKernel, "_Accum", _accum);
            Bind(_g2pKernel, "_Solid", _solid);
            Bind(_g2pKernel, "_GridVelocity", _velocity);
            Bind(_g2pKernel, "_GridBefore", _before);
            Bind(_g2pKernel, "_Colliders", _colliderBuffer);
            Bind(_g2pKernel, "_BodyImpulses", _impulses);
            Bind(_g2pKernel, "_FlowCounters", _flowCounters);
            Bind(_pressureBodyKernel, "_Accum", _accum);
            Bind(_pressureBodyKernel, "_Solid", _solid);
            Bind(_pressureBodyKernel, "_Colliders", _colliderBuffer);
            Bind(_pressureBodyKernel, "_BodyImpulses", _impulses);
            Bind(_waterfallKernel, "_Particles", _particles);
            CollectBodies(pieces, flagBody);
            UpdateColliders();
            if (_ambientFlow) InitializeAmbientWater();
        }

        /// <summary>Starts with a full 1m creek and reserves unused GPU slots for refill.</summary>
        private void InitializeAmbientWater()
        {
            float spacing = _cellSize * 0.85f;
            int columns = Mathf.Max(1, Mathf.FloorToInt(24f / spacing));
            int layers = Mathf.Max(1, Mathf.CeilToInt(_settings.AmbientWaterDepth / spacing));
            // Sample the same 1m-deep creek more densely along its length:
            // doubling longitudinal particle density doubles initial liquid
            // particles, actual cliff departures and speed-scaled replenishment.
            // Preserve the 1m depth and 3m additional crest height.
            int rows = Mathf.Max(1, Mathf.RoundToInt(
                (_startZ - _endZ) / spacing * _settings.AmbientFlowVolumeMultiplier));
            long requested = (long)columns * layers * rows;
            if (requested > _settings.MaxParticles)
                throw new InvalidOperationException(
                    "Ambient creek particle density exceeds MaxParticles. Increase the GPU budget.");
            int initialCount = (int)requested;
            _ambientInitialCount = initialCount;
            _ambientColumns = columns;
            _ambientRefillFraction = 0d;
            _particleCount = _settings.MaxParticles;
            _compute.SetInts("_AmbientGrid", columns, layers, rows);
            _compute.SetInt("_AmbientInitialCount", initialCount);
            _compute.SetInt("_ParticleCount", _particleCount);
            Dispatch(_initializeAmbientKernel, _particleCount);
        }

        /// <summary>
        /// Adds a single additional crest above the already-filled 1m creek.
        /// Only inactive GPU particle slots are used. No new solver is constructed.
        /// </summary>
        public void EmitSingleCrest()
        {
            if (_disposed || !_ambientFlow)
                throw new InvalidOperationException("A wave crest requires a running ambient FLIP creek.");

            float amplitude = _settings.WaveCrestAmplitude;
            float spacing = _cellSize * 0.85f;
            int columns = _ambientColumns;
            int layers = Mathf.Max(1, Mathf.CeilToInt(amplitude / spacing));
            // Match the doubled baseline particle density along Z so the
            // crest is one continuous, densely filled volume (not dotted spray).
            int rows = Mathf.Max(1, Mathf.RoundToInt(
                _settings.WaveCrestLength / spacing *
                _settings.AmbientFlowVolumeMultiplier));
            long candidateCount = (long)columns * layers * rows;
            if (candidateCount > _settings.MaxParticles - _ambientInitialCount)
                throw new InvalidOperationException(
                    "The 3m crest exceeds the spare GPU particle budget. Increase MaxParticles or CellSize.");

            _crestCounter.SetData(new uint[1]);
            _compute.SetInts("_CrestGrid", columns, layers, rows);
            _compute.SetFloat("_CrestAmplitude", amplitude);
            // The entire creek was boosted immediately before this call.
            // Crest and ambient flow therefore share exactly the same speed.
            _compute.SetFloat("_CrestSpeed", _currentAmbientSpeed);
            _compute.SetFloat("_CrestWidth", _settings.WaveCrestWidth);
            _compute.SetFloat("_CrestLength", _settings.WaveCrestLength);
            _compute.SetFloat("_CrestCenterZ",
                _startZ - _cellSize - _settings.WaveCrestLength * 0.5f);
            Dispatch(_emitCrestKernel, _particleCount);
        }

        /// <summary>
        /// Changes the velocity of the entire currently flowing creek once,
        /// preserving eddies and local differences. New inlet particles use
        /// the new speed until it is reset after the stage.
        /// </summary>
        public void SetAmbientSpeed(float speed)
        {
            if (_disposed || !_ambientFlow)
                throw new InvalidOperationException("Ambient speed can change only for an active creek.");
            speed = Mathf.Max(0.01f, speed);
            float delta = speed - _currentAmbientSpeed;
            if (Mathf.Abs(delta) < 0.0001f) return;
            _compute.SetFloat("_FlowSpeedDelta", delta);
            Dispatch(_shiftAmbientSpeedKernel, _particleCount);
            _currentAmbientSpeed = speed;
            _compute.SetFloat("_InletVelocity", speed);
        }

        /// <summary>Assigns GPU resources to a kernel without binding resources it never uses.</summary>
        private void Bind(int kernel, string name, ComputeBuffer buffer)
        {
            _compute.SetBuffer(kernel, name, buffer);
        }

        /// <summary>Collects scene bodies once; dynamically updating their collider poses every step.</summary>
        private void CollectBodies(IList<GuardWavePiece> pieces, Rigidbody flag)
        {
            AddBody(flag);
            foreach (GuardWavePiece piece in pieces)
                if (piece != null) AddBody(piece.Body);
            // Keep initially inactive authored sticks registered in GPU
            // collider lists so activating their parent works mid-simulation.
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsByType<Rigidbody>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (body != null && body.gameObject.scene == flag.gameObject.scene)
                    AddBody(body);
            foreach (Rigidbody body in _bodies)
                foreach (Collider collider in body.GetComponentsInChildren<Collider>(true))
                    if (collider != null && !collider.isTrigger)
                        _colliders.Add(collider);
        }

        /// <summary>Registers a distinct Rigidbody and its impulse-buffer index.</summary>
        private void AddBody(Rigidbody body)
        {
            if (body == null || _indices.ContainsKey(body) || _bodies.Count >= _settings.MaxBodies)
                return;
            _indices.Add(body, _bodies.Count);
            _bodies.Add(body);
        }

        /// <summary>Uploads up to 256 live collider proxies and their current body velocities.</summary>
        private void UpdateColliders()
        {
            int count = 0;
            foreach (Collider collider in _colliders)
            {
                if (count >= _settings.MaxColliders) break;
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                    continue;
                Bounds bounds = collider.bounds;
                if (bounds.min.x > 12.5f || bounds.max.x < -12.5f ||
                    bounds.max.z < _endZ - _cellSize || bounds.min.z > _startZ + _cellSize)
                    continue;
                Rigidbody body = collider.attachedRigidbody;
                int index = body != null && _indices.TryGetValue(body, out int found)
                    && !body.isKinematic ? found : -1;
                ColliderData data = new ColliderData();
                Quaternion rotation = collider.transform.rotation;
                Vector3 half = bounds.extents;
                Vector3 center = bounds.center;
                float type = 0f;
                if (collider is BoxCollider box)
                {
                    center = collider.transform.TransformPoint(box.center);
                    Vector3 s = collider.transform.lossyScale;
                    half = Vector3.Scale(box.size, new Vector3(Mathf.Abs(s.x),
                        Mathf.Abs(s.y), Mathf.Abs(s.z))) * 0.5f;
                }
                else if (collider is SphereCollider sphere)
                {
                    center = collider.transform.TransformPoint(sphere.center);
                    Vector3 s = collider.transform.lossyScale;
                    half.x = sphere.radius * Mathf.Max(Mathf.Abs(s.x),
                        Mathf.Abs(s.y), Mathf.Abs(s.z));
                    type = 1f;
                }
                else if (collider is CapsuleCollider capsule)
                {
                    center = collider.transform.TransformPoint(capsule.center);
                    Vector3 s = collider.transform.lossyScale;
                    Vector3 axis = capsule.direction == 0 ? Vector3.right
                        : capsule.direction == 2 ? Vector3.forward : Vector3.up;
                    float axisScale = capsule.direction == 0 ? Mathf.Abs(s.x) :
                        capsule.direction == 2 ? Mathf.Abs(s.z) : Mathf.Abs(s.y);
                    float sideScale = capsule.direction == 0 ?
                        Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)) :
                        capsule.direction == 2 ? Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y)) :
                        Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                    half.x = capsule.radius * sideScale;
                    half.y = Mathf.Max(0f, capsule.height * axisScale * 0.5f - half.x);
                    rotation *= Quaternion.FromToRotation(Vector3.up, axis);
                    type = 2f;
                }
                else if (collider is MeshCollider mesh && mesh.sharedMesh != null)
                {
                    // An oriented local-mesh AABB avoids oversized world-space boxes for
                    // rotated convex sticks; concave details need a separately baked SDF.
                    Bounds local = mesh.sharedMesh.bounds;
                    center = collider.transform.TransformPoint(local.center);
                    Vector3 scale = collider.transform.lossyScale;
                    half = Vector3.Scale(local.extents, new Vector3(
                        Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                }
                else
                {
                    rotation = Quaternion.identity;
                }
                data.CenterBody = new Vector4(center.x, center.y, center.z, index);
                data.ExtentType = new Vector4(half.x, half.y, half.z, type);
                data.Rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w);
                if (body != null && !body.isKinematic)
                {
                    Vector3 v = body.linearVelocity;
                    Vector3 w = body.angularVelocity;
                    data.Velocity = new Vector4(v.x, v.y, v.z, bounds.extents.magnitude + _cellSize);
                    data.Angular = new Vector4(w.x, w.y, w.z, 0);
                }
                if (body == null || body.isKinematic)
                    data.Velocity.w = bounds.extents.magnitude + _cellSize;
                Vector3 bodyCenter = body != null ? body.worldCenterOfMass : center;
                data.BodyCenter = new Vector4(bodyCenter.x, bodyCenter.y, bodyCenter.z, 0f);
                _colliderData[count++] = data;
            }
            if (count > 0) _colliderBuffer.SetData(_colliderData, 0, 0, count);
            _compute.SetInt("_ColliderCount", count);
            _compute.SetInt("_BodyCount", _bodies.Count);
        }

        /// <summary>
        /// Use enough fluid-only substeps to keep motion under half a cell
        /// per step. This stabilizes a connected 3m crest at 20m/s.
        /// </summary>
        public void Step(float dt, float inletFraction)
        {
            if (_disposed || dt <= 0f) return;
            float fastest = _ambientFlow
                ? Mathf.Max(_currentAmbientSpeed, _settings.AmbientFlowSpeed * 2f)
                : InletSpeed;
            int steps = Mathf.Clamp(Mathf.CeilToInt(
                fastest * dt / (_cellSize * 0.5f)), 1, _settings.MaxFluidSubsteps);
            float subDt = dt / steps;
            UpdateColliders();
            for (int i = 0; i < steps; i++)
                SimulateSubstep(subDt, inletFraction, i == 0);

            if (_ambientFlow)
            {
                _flowReadbackClock += dt;
                if (_flowReadbackClock >= 1f && !_flowReadbackPending)
                {
                    _flowReadbackClock = 0f;
                    _flowReadbackPending = true;
                    AsyncGPUReadback.Request(_flowCounters, OnFlowCountersReady);
                }
                _divergenceReadbackClock += dt;
                if (_divergenceReadbackClock >= 1f && !_divergenceReadbackPending)
                {
                    _divergenceReadbackClock = 0f;
                    _divergenceReadbackPending = true;
                    AsyncGPUReadback.Request(_divergenceStats, OnDivergenceStatsReady);
                }
            }
            if (!_pendingReadback && _bodies.Count > 0)
            {
                _pendingReadback = true;
                AsyncGPUReadback.Request(_impulses, OnImpulsesReady);
            }
        }

        private void SimulateSubstep(float dt, float inletFraction, bool firstSubstep)
        {
            _elapsed += dt;
            _compute.SetFloat("_Dt", dt);
            _compute.SetInt("_ClearBodyImpulses", firstSubstep ? 1 : 0);
            Dispatch(_clearKernel, Mathf.Max(_gridCount, _bodies.Count * 2));
            Dispatch(_classifyKernel, _gridCount);
            if (_ambientFlow) Dispatch(_driveInletKernel, _particleCount);

            _spawnClock += dt;
            if (!_ambientFlow && inletFraction > 0.05f &&
                _spawnClock >= _settings.SpawnInterval)
            {
                _spawnClock -= _settings.SpawnInterval;
                int widthCells = Mathf.FloorToInt(24f / (_cellSize * 0.85f));
                int heightCells = Mathf.Max(1, Mathf.FloorToInt(_waveHeight / (_cellSize * 0.85f)));
                int count = Mathf.Min(_settings.MaxParticles - _particleCount,
                    Mathf.RoundToInt(widthCells * heightCells * _settings.SpawnRateMultiplier * inletFraction));
                if (count > 0)
                {
                    _compute.SetInt("_SpawnStart", _particleCount);
                    _compute.SetInt("_SpawnCount", count);
                    _compute.SetInt("_SpawnSeed", ++_seed);
                    Dispatch(_spawnKernel, count);
                    _particleCount += count;
                }
            }
            if (_particleCount == 0) return;
            _compute.SetInt("_ParticleCount", _particleCount);
            Dispatch(_p2gKernel, _particleCount);
            Dispatch(_normalizeKernel, _gridCount);
            Dispatch(_buildFaceKernel, _gridCount);
            _compute.SetInt("_DivergencePhase", 0);
            Dispatch(_measureDivergenceKernel, _gridCount);

            ComputeBuffer src = _pressureA;
            ComputeBuffer dst = _pressureB;
            for (int i = 0; i < _settings.PressureIterations; i++)
            {
                _compute.SetBuffer(_pressureKernel, "_PressureSrc", src);
                _compute.SetBuffer(_pressureKernel, "_PressureDst", dst);
                Dispatch(_pressureKernel, _gridCount);
                (src, dst) = (dst, src);
            }
            _compute.SetBuffer(_projectFacesKernel, "_PressureSrc", src);
            Dispatch(_projectFacesKernel, _gridCount);
            _compute.SetInt("_DivergencePhase", 1);
            Dispatch(_measureDivergenceKernel, _gridCount);
            Dispatch(_reconstructGridKernel, _gridCount);
            _compute.SetBuffer(_pressureBodyKernel, "_PressureSrc", src);
            Dispatch(_pressureBodyKernel, _gridCount);
            // Existing spray continues; newly exited particles begin falling
            // on their transition tick in GridToParticle.
            Dispatch(_waterfallKernel, _particleCount);
            Dispatch(_g2pKernel, _particleCount);

            if (_ambientFlow)
            {
                double riverLength = Math.Max(0.1, _startZ - _endZ);
                double particlesPerSecond = _ambientInitialCount *
                    _currentAmbientSpeed / riverLength;
                _ambientRefillFraction += particlesPerSecond * dt;
                int supplyBudget = (int)Math.Min(_settings.MaxParticles,
                    Math.Floor(_ambientRefillFraction));
                _ambientRefillFraction -= supplyBudget;
                if (supplyBudget > 0)
                {
                    _compute.SetInt("_AmbientRefillBudget", supplyBudget);
                    _compute.SetInt("_SpawnSeed", ++_seed);
                    Dispatch(_recycleAmbientKernel, _particleCount);
                }
            }
        }

        private void OnDivergenceStatsReady(AsyncGPUReadbackRequest request)
        {
            _divergenceReadbackPending = false;
            if (_disposed || request.hasError) return;
            NativeArray<uint> data = request.GetData<uint>();
            DivergenceBeforeProjection = data[1] == 0 ? 0f
                : (float)data[0] / (64f * data[1]);
            DivergenceAfterProjection = data[3] == 0 ? 0f
                : (float)data[2] / (64f * data[3]);
        }

        /// <summary>Samples actual exit/refill totals for inspecting flow in the debugger.</summary>
        private void OnFlowCountersReady(AsyncGPUReadbackRequest request)
        {
            _flowReadbackPending = false;
            if (_disposed || request.hasError) return;
            NativeArray<uint> values = request.GetData<uint>();
            PendingAmbientRefills = values[0];
            TotalAmbientRefills = values[1];
            TotalCliffExits = values[3];
        }

        /// <summary>Runs the selected compute kernel with 128 threads per workgroup.</summary>
        private void Dispatch(int kernel, int count)
        {
            _compute.Dispatch(kernel, Mathf.Max(1, (count + THREADS - 1) / THREADS), 1, 1);
        }

        /// <summary>Applies bounded fluid reaction impulses to scene Rigidbodies on the main thread.</summary>
        private void OnImpulsesReady(AsyncGPUReadbackRequest request)
        {
            _pendingReadback = false;
            if (_disposed || request.hasError) return;
            NativeArray<Int4> values = request.GetData<Int4>();
            for (int i = 0; i < _bodies.Count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null || body.isKinematic || !body.gameObject.activeInHierarchy)
                    continue;
                Int4 force = values[i * 2];
                Int4 torque = values[i * 2 + 1];
                Vector3 linear = new Vector3(force.X, force.Y, force.Z) / IMPULSE_SCALE;
                Vector3 angular = new Vector3(torque.X, torque.Y, torque.Z) / IMPULSE_SCALE;
                float limit = Mathf.Max(_settings.MinImpulseLimit,
                    body.mass * _settings.MaxImpulsePerBodyMass);
                body.AddForce(Vector3.ClampMagnitude(linear, limit), ForceMode.Impulse);
                body.AddTorque(Vector3.ClampMagnitude(angular, limit * _settings.AngularImpulseMultiplier), ForceMode.Impulse);
            }
        }

        /// <summary>Releases every allocated GPU buffer when the wave ends or is rolled back.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _particles.Release();
            _accum.Release();
            _velocity.Release();
            _before.Release();
            _faceVelocity.Release();
            _divergenceStats.Release();
            _pressureA.Release();
            _pressureB.Release();
            _solid.Release();
            _colliderBuffer.Release();
            _impulses.Release();
            _flowCounters.Release();
            _crestCounter.Release();
            UnityEngine.Object.Destroy(_compute);
        }
    }
}
