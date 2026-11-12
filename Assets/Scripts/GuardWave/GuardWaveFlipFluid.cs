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
        private const float AMBIENT_SUPPLY_RATE_MULTIPLIER = 1f;
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
            // x density (0 = not a buoyant piece), y grounded flow factor,
            // z Rigidbody mass share, w sampling shape (box=0, cylinder=1, sphere=2).
            public Vector4 FluidProperties;
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
        // Only scene-authored Initial Wall logs carry these components.
        private readonly List<GuardWaveInitialLogJoint> _initialLogJoints =
            new List<GuardWaveInitialLogJoint>();
        private readonly List<GuardWavePiece> _bodyPieces = new List<GuardWavePiece>();
        private readonly List<Collider[]> _bodySampleColliders = new List<Collider[]>();
        private readonly float[] _recentWaterline;
        private readonly float[] _recentFluidCoverage;
        private readonly float[] _recentWaterlineTime;
        private readonly float[] _smoothedBodyWaterline;
        private readonly bool[] _hasSmoothedBodyWaterline;
        private readonly Vector2[] _recentFlowXZ;
        private readonly Vector2[] _smoothedBodyFlowXZ;
        private readonly bool[] _hasSmoothedBodyFlowXZ;
        private const float WATERLINE_SAMPLE_MAX_AGE = 0.25f;
        private readonly List<Collider> _colliders = new List<Collider>();
        private readonly Dictionary<Rigidbody, int> _indices = new Dictionary<Rigidbody, int>();
        private readonly ColliderData[] _colliderData;
        private readonly int[] _bodyColliderCounts;
        private readonly ComputeBuffer _particles;
        private readonly ComputeBuffer _accum;
        private readonly ComputeBuffer _velocity;
        private readonly ComputeBuffer _before;
        private readonly ComputeBuffer _pressureA;
        private readonly ComputeBuffer _pressureB;
        private readonly ComputeBuffer _solid;
        private readonly ComputeBuffer _colliderBuffer;
        // Packed z-slice broadphase: each GPU cell tests only colliders whose
        // bounding spheres reach its longitudinal slice.
        private readonly ComputeBuffer _colliderZOffsetsBuffer;
        private readonly ComputeBuffer _colliderZIndicesBuffer;
        private readonly int[] _colliderZOffsetsData;
        private readonly int[] _colliderZIndicesData;
        private readonly ComputeBuffer _impulses;
        private readonly ComputeBuffer _waterSurface;
        private readonly ComputeBuffer _smoothedWaterSurface;
        private readonly ComputeBuffer _waterFlow;
        private readonly ComputeShader _compute;
        private readonly Vector3Int _grid;
        private readonly Vector3 _origin;
        private readonly float _startZ;
        private readonly float _endZ;
        private readonly float _floorY;
        private readonly float _waveHeight;
        private readonly bool _ambientFlow;
        private readonly ComputeBuffer _flowCounters;
        private readonly ComputeBuffer _crestMetrics;
        private readonly int _initializeAmbientKernel;
        private readonly int _recycleAmbientKernel;
        private readonly int _driveInletKernel;
        private readonly int _emitCrestKernel;
        private int _crestSlotStart;
        private int _crestCandidateCount;
        private bool _crestEmitted;
        private int _ambientTargetCount;
        private int _ambientColumns;
        private readonly int _gridCount;
        private readonly int _clearKernel;
        private readonly int _clearWaterColumnsKernel;
        private readonly int _classifyKernel;
        private readonly int _spawnKernel;
        private readonly int _p2gKernel;
        private readonly int _normalizeKernel;
        private readonly int _pressureKernel;
        private readonly int _projectKernel;
        private readonly int _g2pKernel;
        private readonly int _pressureBodyKernel;
        private readonly int _waterColumnsKernel;
        private readonly int _smoothWaterColumnsKernel;
        private readonly int _buoyancyBodyKernel;
        private readonly int _waterfallKernel;
        private readonly int[] _shaderWarmupKernels;
        private int _shaderWarmupIndex;
        public bool IsShaderPrewarmed => _shaderWarmupIndex >= _shaderWarmupKernels.Length;
        private float _spawnClock;
        private float _elapsed;
        // Fractional refill credits prevent rounding drift at low flow speeds.
        private double _ambientRefillFraction;
        private int _particleCount;
        private int _seed;
        // Multiple in-flight snapshots preserve continuous forces across fixed steps.
        private int _pendingImpulseReadbacks;
        private const int MAX_PENDING_IMPULSE_READBACKS = 8;
        // Buffer two readbacks, then apply one GPU frame per Unity physics tick.
        private const int IMPULSE_PLAYBACK_BUFFER_STEPS = 2;
        private sealed class ImpulseFrame
        {
            public long SourceStep;
            public float SourceTime;
            public Vector3[] Linear;
            public Vector3[] Angular;
            public float[] Waterline;
            public float[] FluidCoverage;
            public Vector2[] FlowXZ;
        }
        private readonly Dictionary<long, ImpulseFrame> _completedImpulseFrames =
            new Dictionary<long, ImpulseFrame>();
        private long _gpuStep;
        private long _requestedImpulseFrame;
        private long _nextImpulseFrameToApply = 1;
        private bool _impulsePlaybackStarted;
        private bool _flowReadbackPending;
        private float _flowReadbackClock;
        private bool _crestMetricsReadbackPending;
        private float _crestMetricsClock;
        private bool _disposed;

        public ComputeBuffer Particles => _particles;
        public int ParticleCount => _particleCount;
        public float InletSpeed { get; }
        public Bounds RenderBounds { get; }
        // GPU flow measurements; refreshed at most once per second without logs.
        public uint TotalCliffExits { get; private set; }
        public uint TotalAmbientRefills { get; private set; }
        public uint PendingAmbientRefills { get; private set; }
        // Read-only live diagnostics for the GPU wave; no per-frame logs.
        public uint ActiveCrestParticles { get; private set; }
        public uint ReleasedCrestParticles { get; private set; }
        public float HighestCrestDepth { get; private set; }

        /// <summary>Builds a bounded GPU fluid domain centered at x=0 and a 24m wide river.</summary>
        public GuardWaveFlipFluid(float startZ, float endZ, float groundY,
            float waterfallEndY, float waveHeight, float travelDuration, IList<GuardWavePiece> pieces,
            Rigidbody flagBody, GuardWaveFluidSettings settings, bool ambientFlow = false)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ambientFlow = ambientFlow;
            _cellSize = _settings.CellSize;
            _colliderData = new ColliderData[_settings.MaxColliders];
            _bodyColliderCounts = new int[_settings.MaxBodies];
            _recentWaterline = new float[_settings.MaxBodies];
            _recentFluidCoverage = new float[_settings.MaxBodies];
            _recentWaterlineTime = new float[_settings.MaxBodies];
            _smoothedBodyWaterline = new float[_settings.MaxBodies];
            _hasSmoothedBodyWaterline = new bool[_settings.MaxBodies];
            _recentFlowXZ = new Vector2[_settings.MaxBodies];
            _smoothedBodyFlowXZ = new Vector2[_settings.MaxBodies];
            _hasSmoothedBodyFlowXZ = new bool[_settings.MaxBodies];
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
            _pressureA = new ComputeBuffer(_gridCount, sizeof(float));
            _pressureB = new ComputeBuffer(_gridCount, sizeof(float));
            _solid = new ComputeBuffer(_gridCount, sizeof(uint));
            _colliderBuffer = new ComputeBuffer(_settings.MaxColliders, Marshal.SizeOf<ColliderData>());
            _colliderZOffsetsBuffer = new ComputeBuffer(_grid.z + 1, sizeof(int));
            _colliderZIndicesBuffer = new ComputeBuffer(
                _grid.z * _settings.MaxColliders, sizeof(int));
            _colliderZOffsetsData = new int[_grid.z + 1];
            _colliderZIndicesData = new int[_grid.z * _settings.MaxColliders];
            _impulses = new ComputeBuffer(_settings.MaxBodies * 2, sizeof(int) * 4);
            _waterSurface = new ComputeBuffer(_grid.x * _grid.z, sizeof(int));
            _smoothedWaterSurface = new ComputeBuffer(_grid.x * _grid.z, sizeof(int));
            // Unlike raw water columns, this history must survive fixed steps.
            _smoothedWaterSurface.SetData(new int[_grid.x * _grid.z]);
            _waterFlow = new ComputeBuffer(_grid.x * _grid.z, sizeof(int) * 4);
            _flowCounters = new ComputeBuffer(4, sizeof(uint));
            _flowCounters.SetData(new uint[4]);
            _crestMetrics = new ComputeBuffer(3, sizeof(uint));
            _crestMetrics.SetData(new uint[3]);

            _clearKernel = _compute.FindKernel("ClearGrid");
            _clearWaterColumnsKernel = _compute.FindKernel("ClearWaterColumns");
            _classifyKernel = _compute.FindKernel("ClassifyGrid");
            _spawnKernel = _compute.FindKernel("Spawn");
            _initializeAmbientKernel = _compute.FindKernel("InitializeAmbient");
            _recycleAmbientKernel = _compute.FindKernel("RecycleAmbient");
            _driveInletKernel = _compute.FindKernel("DriveInlet");
            _emitCrestKernel = _compute.FindKernel("EmitCrest");
            _p2gKernel = _compute.FindKernel("ParticleToGrid");
            _normalizeKernel = _compute.FindKernel("NormalizeGrid");
            _pressureKernel = _compute.FindKernel("JacobiPressure");
            _projectKernel = _compute.FindKernel("ProjectGrid");
            _g2pKernel = _compute.FindKernel("GridToParticle");
            _pressureBodyKernel = _compute.FindKernel("PressureToBodies");
            _waterColumnsKernel = _compute.FindKernel("BuildWaterColumns");
            _smoothWaterColumnsKernel = _compute.FindKernel("SmoothWaterColumns");
            _buoyancyBodyKernel = _compute.FindKernel("RelativeDensityToBodies");
            _waterfallKernel = _compute.FindKernel("Waterfall");
            _shaderWarmupKernels = new[]
            {
                _clearKernel, _clearWaterColumnsKernel, _classifyKernel,
                _spawnKernel, _recycleAmbientKernel, _driveInletKernel,
                _emitCrestKernel, _p2gKernel, _normalizeKernel, _pressureKernel,
                _projectKernel, _g2pKernel, _pressureBodyKernel,
                _waterColumnsKernel, _smoothWaterColumnsKernel,
                _buoyancyBodyKernel, _waterfallKernel
            };
            _compute.SetInt("_WarmupOnly", 0);

            _compute.SetInts("_GridSize", _grid.x, _grid.y, _grid.z);
            _compute.SetVector("_GridOrigin", _origin);
            _compute.SetFloat("_CellSize", _cellSize);
            // Non-ambient preview uses approximately cubic source particles;
            // ambient/wave uses the exact seeded river-lattice volume below.
            _compute.SetFloat("_LiquidParticleVolume", Mathf.Pow(_cellSize * 0.85f, 3f));
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
            // Increase physical pressure and collision response without altering
            // the displacement-based buoyancy that defines relative density.
            float forceScale = (ambientFlow ? _settings.AmbientImpactScale : 1f) *
                _settings.FluidForceMultiplier;
            _compute.SetFloat("_ParticleMass", _settings.ParticleEffectiveMass * forceScale);
            _compute.SetFloat("_HydroDensity", _settings.HydroDensity * forceScale);
            _compute.SetFloat("_FlowForceMultiplier", _settings.FluidForceMultiplier);
            _compute.SetFloat("_PressureClamp", _settings.PressureClamp);
            _compute.SetFloat("_PressureThreshold", _settings.PressureThreshold);
            _compute.SetFloat("_CollisionResponse", _settings.CollisionResponse);
            _compute.SetFloat("_CollisionSkin", _settings.CollisionSkin);
            _compute.SetFloat("_FlowResponse", _settings.WaterFlowResponse);
            _compute.SetFloat("_VerticalFlowResponseScale", _settings.VerticalFlowResponseScale);
            _compute.SetFloat("_FloatVerticalDamping", _settings.FloatingVerticalDamping);
            _compute.SetFloat("_FloatTorqueScale", _settings.FloatingTorqueScale);
            _compute.SetFloat("_FloatMaxBuoyancyRatio", _settings.FloatingMaxBuoyancyRatio);
            _compute.SetFloat("_FloatUprightStrength", _settings.FloatingUprightStrength);
            _compute.SetFloat("_FloatFlowAlignmentStrength", _settings.FloatingFlowAlignmentStrength);
            _compute.SetFloat("_FloatFlowAlignmentFullSpeed", _settings.FloatingFlowAlignmentFullSpeed);
            _compute.SetFloat("_MaxFluidVelocity", _settings.MaxParticleSpeed);
            _compute.SetFloat("_CrestHeightStiffness", _settings.CrestHeightStiffness);
            _compute.SetFloat("_CrestHeightDamping", _settings.CrestHeightDamping);
            _compute.SetFloat("_CrestForwardDamping", _settings.CrestForwardDamping);
            _compute.SetFloat("_CrestReleaseDistance", _settings.CrestReleaseDistance);
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
            Bind(_clearWaterColumnsKernel, "_WaterSurface", _waterSurface);
            Bind(_clearWaterColumnsKernel, "_WaterFlow", _waterFlow);
            Bind(_clearKernel, "_FlowCounters", _flowCounters);
            Bind(_clearKernel, "_CrestMetrics", _crestMetrics);
            Bind(_classifyKernel, "_Solid", _solid);
            Bind(_classifyKernel, "_Colliders", _colliderBuffer);
            Bind(_classifyKernel, "_ColliderZOffsets", _colliderZOffsetsBuffer);
            Bind(_classifyKernel, "_ColliderZIndices", _colliderZIndicesBuffer);
            Bind(_spawnKernel, "_Particles", _particles);
            Bind(_initializeAmbientKernel, "_Particles", _particles);
            Bind(_recycleAmbientKernel, "_Particles", _particles);
            Bind(_recycleAmbientKernel, "_FlowCounters", _flowCounters);
            Bind(_driveInletKernel, "_Particles", _particles);
            Bind(_emitCrestKernel, "_Particles", _particles);
            Bind(_p2gKernel, "_Particles", _particles);
            Bind(_p2gKernel, "_Accum", _accum);
            Bind(_p2gKernel, "_Solid", _solid);
            Bind(_normalizeKernel, "_Accum", _accum);
            Bind(_normalizeKernel, "_GridBefore", _before);
            Bind(_normalizeKernel, "_GridVelocity", _velocity);
            Bind(_normalizeKernel, "_Solid", _solid);
            Bind(_pressureKernel, "_Accum", _accum);
            Bind(_pressureKernel, "_Solid", _solid);
            Bind(_pressureKernel, "_GridVelocity", _velocity);
            // These ping-pong bindings are overwritten during real steps.
            // Initialize them now so warmup dispatches have valid resources.
            Bind(_pressureKernel, "_PressureSrc", _pressureA);
            Bind(_pressureKernel, "_PressureDst", _pressureB);
            Bind(_projectKernel, "_PressureSrc", _pressureA);
            Bind(_pressureBodyKernel, "_PressureSrc", _pressureA);
            Bind(_projectKernel, "_Accum", _accum);
            Bind(_projectKernel, "_Solid", _solid);
            Bind(_projectKernel, "_GridVelocity", _velocity);
            Bind(_g2pKernel, "_Particles", _particles);
            Bind(_g2pKernel, "_Accum", _accum);
            Bind(_g2pKernel, "_Solid", _solid);
            Bind(_g2pKernel, "_GridVelocity", _velocity);
            Bind(_g2pKernel, "_GridBefore", _before);
            Bind(_g2pKernel, "_Colliders", _colliderBuffer);
            Bind(_g2pKernel, "_BodyImpulses", _impulses);
            Bind(_g2pKernel, "_FlowCounters", _flowCounters);
            Bind(_g2pKernel, "_CrestMetrics", _crestMetrics);
            Bind(_pressureBodyKernel, "_Accum", _accum);
            Bind(_pressureBodyKernel, "_Solid", _solid);
            Bind(_pressureBodyKernel, "_Colliders", _colliderBuffer);
            Bind(_pressureBodyKernel, "_BodyImpulses", _impulses);
            Bind(_waterColumnsKernel, "_Particles", _particles);
            Bind(_waterColumnsKernel, "_WaterSurface", _waterSurface);
            Bind(_waterColumnsKernel, "_WaterFlow", _waterFlow);
            Bind(_smoothWaterColumnsKernel, "_WaterSurface", _waterSurface);
            Bind(_smoothWaterColumnsKernel, "_WaterFlow", _waterFlow);
            Bind(_smoothWaterColumnsKernel, "_SmoothedWaterSurface", _smoothedWaterSurface);
            Bind(_buoyancyBodyKernel, "_Colliders", _colliderBuffer);
            Bind(_buoyancyBodyKernel, "_SmoothedWaterSurface", _smoothedWaterSurface);
            Bind(_buoyancyBodyKernel, "_WaterFlow", _waterFlow);
            Bind(_buoyancyBodyKernel, "_Accum", _accum);
            Bind(_buoyancyBodyKernel, "_Solid", _solid);
            Bind(_buoyancyBodyKernel, "_BodyImpulses", _impulses);
            Bind(_waterfallKernel, "_Particles", _particles);
            CollectBodies(pieces, flagBody);
            UpdateColliders();
            if (_ambientFlow) InitializeAmbientWater();
        }

        /// <summary>
        /// Compile one GPU simulation kernel per render frame while the player
        /// prepares. A shader-side early return prevents any fluid mutation.
        /// </summary>
        public void PrewarmOneKernel()
        {
            if (_disposed || IsShaderPrewarmed) return;
            _compute.SetInt("_WarmupOnly", 1);
            try
            {
                Dispatch(_shaderWarmupKernels[_shaderWarmupIndex], 1);
                _shaderWarmupIndex++;
            }
            finally
            {
                _compute.SetInt("_WarmupOnly", 0);
            }
        }

        /// <summary>Starts with an empty creek while reserving baseline and crest particle capacity.</summary>
        private void InitializeAmbientWater()
        {
            float spacing = _cellSize * 0.85f;
            int columns = Mathf.Max(1, Mathf.FloorToInt(24f / spacing));
            int layers = Mathf.Max(1, Mathf.CeilToInt(_settings.AmbientWaterDepth / spacing));
            // The full 1m creek lattice is the target baseline water volume,
            // not a startup spawn. All baseline parcels enter at the upstream boundary.
            // Keep its density and the 3m crest reservation unchanged.
            int rows = Mathf.Max(1, Mathf.RoundToInt(
                (_startZ - _endZ) / spacing * _settings.AmbientFlowVolumeMultiplier));
            long requested = (long)columns * layers * rows;
            if (requested > _settings.MaxParticles)
                throw new InvalidOperationException(
                    "Ambient creek particle density exceeds MaxParticles. Increase the GPU budget.");
            int targetCount = (int)requested;
            int crestLayers = Mathf.Max(1, Mathf.CeilToInt(
                _settings.WaveCrestAmplitude / spacing));
            int crestRows = Mathf.Max(1, Mathf.RoundToInt(
                _settings.WaveCrestLength / spacing *
                _settings.AmbientFlowVolumeMultiplier));
            long crestCandidates = (long)columns * crestLayers * crestRows;
            // Reserve the full wave lattice for the lifetime of this creek.
            // Waterfall and ambient refills use only the preceding slots.
            if (crestCandidates > int.MaxValue ||
                targetCount + crestCandidates >= _settings.MaxParticles)
                throw new InvalidOperationException(
                    "GPU particle capacity cannot fit both the base river and reserved crest.");
            _crestCandidateCount = (int)crestCandidates;
            _crestSlotStart = _settings.MaxParticles - _crestCandidateCount;
            _compute.SetInt("_CrestSlotStart", _crestSlotStart);
            _ambientTargetCount = targetCount;
            // Each live particle represents a fixed slice of the designed
            // river volume; wave crest lattice spacing is approximately equal.
            // This prevents a sparse group of droplets from supplying enough
            // buoyancy for a whole object.
            float riverVolume = 24f * _settings.AmbientWaterDepth * (_startZ - _endZ);
            _compute.SetFloat("_LiquidParticleVolume", riverVolume / targetCount);
            _ambientColumns = columns;
            _ambientRefillFraction = 0d;
            _particleCount = _settings.MaxParticles;
            _compute.SetInts("_AmbientGrid", columns, layers, rows);
            // Queue the whole missing initial volume as supply debt. Future
            // cliff losses add to the very same counter on the GPU.
            _flowCounters.SetData(new uint[] { (uint)targetCount, 0u, 0u, 0u });
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
            // Use the same longitudinal sample density as the 2x base river
            // so the crest forms a contiguous volume.
            int rows = Mathf.Max(1, Mathf.RoundToInt(
                _settings.WaveCrestLength / spacing *
                _settings.AmbientFlowVolumeMultiplier));
            long candidateCount = (long)columns * layers * rows;
            if (candidateCount != _crestCandidateCount)
                throw new InvalidOperationException(
                    "Reserved crest slot count does not match the wave lattice.");
            _compute.SetInts("_CrestGrid", columns, layers, rows);
            _compute.SetFloat("_CrestAmplitude", amplitude);
            _compute.SetFloat("_CrestSpeed", InletSpeed);
            _compute.SetFloat("_CrestWidth", _settings.WaveCrestWidth);
            _compute.SetFloat("_CrestLength", _settings.WaveCrestLength);
            _compute.SetFloat("_CrestCenterZ",
                _startZ - _cellSize - _settings.WaveCrestLength * 0.5f);
            // Dispatch only the reserved lattice indices, not random free slots.
            Dispatch(_emitCrestKernel, _crestCandidateCount);
            _crestEmitted = true;
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

        /// <summary>
        /// The ambient creek is created before players place palette items.
        /// Register newly spawned bodies immediately, otherwise they never reach
        /// the GPU collider/impulse buffers and cannot receive buoyancy.
        /// </summary>
        public void RegisterPiece(GuardWavePiece piece)
        {
            if (_disposed || piece == null) return;
            Rigidbody body = piece.Body;
            if (body == null) return;

            AddBody(body);
            if (!_indices.ContainsKey(body)) return;

            foreach (Collider collider in body.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || collider.isTrigger ||
                    collider.attachedRigidbody != body ||
                    _colliders.Contains(collider))
                    continue;
                // Prioritize current palette items if the collider GPU budget
                // is crowded by scene-authored or inactive Beaver House sticks.
                _colliders.Insert(0, collider);
            }
        }

        /// <summary>Registers a distinct Rigidbody and its impulse-buffer index.</summary>
        private void AddBody(Rigidbody body)
        {
            if (body == null || _indices.ContainsKey(body) || _bodies.Count >= _settings.MaxBodies)
                return;
            _indices.Add(body, _bodies.Count);
            _bodies.Add(body);
            _initialLogJoints.Add(body.GetComponent<GuardWaveInitialLogJoint>());
            _bodyPieces.Add(body.GetComponent<GuardWavePiece>());
            // Cache once; never allocate child-collider arrays in FixedUpdate.
            _bodySampleColliders.Add(body.GetComponentsInChildren<Collider>(true));
        }

        /// <summary>Uploads up to 256 live collider proxies and their current body velocities.</summary>
        private void UpdateColliders()
        {
            int count = 0;
            // Destroyed runtime pieces must not accumulate stale collider slots.
            for (int i = _colliders.Count - 1; i >= 0; i--)
                if (_colliders[i] == null) _colliders.RemoveAt(i);
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
                // Once the center of mass passes the waterfall lip there is
                // no supporting creek water. Do not send these bodies back to
                // GPU collision, buoyancy, alignment, or pressure kernels.
                if (body != null && !body.isKinematic &&
                    body.worldCenterOfMass.z <= _endZ)
                    continue;
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
                if (index >= 0)
                {
                    GuardWavePiece piece = body.GetComponent<GuardWavePiece>();
                    if (piece != null && piece.enabled)
                    {
                        float grounded = piece.IsRestingOnGround
                            && piece.RelativeFluidDensity >= 1f
                            ? _settings.GroundedFlowMultiplier : 1f;
                        float shape = collider is SphereCollider ? 2f :
                            collider is CapsuleCollider ||
                            piece.MaterialId == "beaver_stick" ? 1f : 0f;
                        data.FluidProperties = new Vector4(piece.RelativeFluidDensity,
                            grounded, body.mass, shape);
                        // Only logs/sticks and the flag seek a low projected-area
                        // attitude in moving water. Do not tip ducks/other props.
                        if (piece.MaterialId == "log" ||
                            piece.MaterialId == "beaver_stick" ||
                            piece.MaterialId == "default_flag")
                            data.Angular.w = 1f;
                        if (piece.RelativeFluidDensity < 1f)
                            body.angularDamping = Mathf.Max(body.angularDamping,
                                _settings.FloatingAngularDamping);
                    }
                }
                Vector3 bodyCenter = body != null ? body.worldCenterOfMass : center;
                data.BodyCenter = new Vector4(bodyCenter.x, bodyCenter.y, bodyCenter.z, 0f);
                _colliderData[count++] = data;
            }
            // A compound Rigidbody should receive one mass-equivalent total
            // buoyancy, not an independent body-mass force for each collider.
            Array.Clear(_bodyColliderCounts, 0, _bodies.Count);
            for (int i = 0; i < count; i++)
            {
                int bi = (int)_colliderData[i].CenterBody.w;
                if (bi >= 0 && _colliderData[i].FluidProperties.x > 0f)
                    _bodyColliderCounts[bi]++;
            }
            for (int i = 0; i < count; i++)
            {
                ColliderData data = _colliderData[i];
                int bi = (int)data.CenterBody.w;
                if (bi < 0 || data.FluidProperties.x <= 0f) continue;
                data.FluidProperties.z /= Mathf.Max(1, _bodyColliderCounts[bi]);
                _colliderData[i] = data;
            }
            if (count > 0) _colliderBuffer.SetData(_colliderData, 0, 0, count);

            // Conservative z-only broadphase matches the shader's previous
            // bounding-sphere rejection, but avoids checking every body at
            // every x/y cell in the 3D pressure grid.
            int packedCount = 0;
            for (int z = 0; z < _grid.z; ++z)
            {
                _colliderZOffsetsData[z] = packedCount;
                float worldZ = _origin.z + (z + 0.5f) * _cellSize;
                for (int i = 0; i < count; ++i)
                {
                    ColliderData candidate = _colliderData[i];
                    if (Mathf.Abs(candidate.CenterBody.z - worldZ) >
                        candidate.Velocity.w) continue;
                    _colliderZIndicesData[packedCount++] = i;
                }
            }
            _colliderZOffsetsData[_grid.z] = packedCount;
            _colliderZOffsetsBuffer.SetData(_colliderZOffsetsData);
            if (packedCount > 0)
                _colliderZIndicesBuffer.SetData(_colliderZIndicesData, 0, 0, packedCount);
            _compute.SetInt("_ColliderCount", count);
            _compute.SetInt("_BodyCount", _bodies.Count);
        }

        /// <summary>Advances 3D GPU water through a PIC/FLIP + affine P2G/G2P pressure substep.</summary>
        public void Step(float dt, float inletFraction)
        {
            if (_disposed) return;
            _elapsed += dt;
            _gpuStep++;
            _compute.SetFloat("_Dt", dt);
            UpdateColliders();
            Dispatch(_clearKernel, Mathf.Max(_gridCount, _bodies.Count * 2));
            Dispatch(_clearWaterColumnsKernel, _grid.x * _grid.z);
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

            ComputeBuffer src = _pressureA;
            ComputeBuffer dst = _pressureB;
            for (int i = 0; i < _settings.PressureIterations; i++)
            {
                _compute.SetBuffer(_pressureKernel, "_PressureSrc", src);
                _compute.SetBuffer(_pressureKernel, "_PressureDst", dst);
                Dispatch(_pressureKernel, _gridCount);
                (src, dst) = (dst, src);
            }
            _compute.SetBuffer(_projectKernel, "_PressureSrc", src);
            Dispatch(_projectKernel, _gridCount);
            _compute.SetBuffer(_pressureBodyKernel, "_PressureSrc", src);
            Dispatch(_pressureBodyKernel, _gridCount);
            // Continue pre-existing spray first; newly exited fluid starts falling next tick.
            Dispatch(_waterfallKernel, _particleCount);
            Dispatch(_g2pKernel, _particleCount);
            // Real particle heights and velocities, not the editor's static water plane.
            Dispatch(_waterColumnsKernel, _particleCount);
            Dispatch(_smoothWaterColumnsKernel, _grid.x * _grid.z);
            if (_bodies.Count > 0)
                Dispatch(_buoyancyBodyKernel, _settings.MaxColliders);
            if (_ambientFlow)
            {
                // Fill the initial deficit and replace later losses using one
                // upstream supply budget. Keep the original steady-state
                // water density, but never exceed the configured supply cap.
                double riverLength = Math.Max(0.1, _startZ - _endZ);
                double steadyRate = _ambientTargetCount * InletSpeed / riverLength * AMBIENT_SUPPLY_RATE_MULTIPLIER;
                double particlesPerSecond = Math.Min(steadyRate,
                    _settings.AmbientMaxSupplyParticlesPerSecond);
                _ambientRefillFraction += particlesPerSecond * dt;
                int supplyBudget = (int)Math.Min(_settings.MaxParticles,
                    Math.Floor(_ambientRefillFraction));
                _ambientRefillFraction -= supplyBudget;
                if (supplyBudget > 0)
                {
                    // Supply only the outstanding deficit: initially the empty
                    // river's target volume, then newly lost baseline parcels.
                    _compute.SetInt("_AmbientRefillBudget", supplyBudget);
                    _compute.SetInt("_SpawnSeed", ++_seed);
                    Dispatch(_recycleAmbientKernel, _particleCount);
                }
            }
            if (_ambientFlow)
            {
                _crestMetricsClock += dt;
                if (_crestMetricsClock >= 0.5f && !_crestMetricsReadbackPending)
                {
                    _crestMetricsClock = 0f;
                    _crestMetricsReadbackPending = true;
                    AsyncGPUReadback.Request(_crestMetrics, OnCrestMetricsReady);
                }
                _flowReadbackClock += dt;
                if (_flowReadbackClock >= 1f && !_flowReadbackPending)
                {
                    _flowReadbackClock = 0f;
                    _flowReadbackPending = true;
                    AsyncGPUReadback.Request(_flowCounters, OnFlowCountersReady);
                }
            }
            if (_bodies.Count > 0 &&
                _pendingImpulseReadbacks < MAX_PENDING_IMPULSE_READBACKS)
            {
                _pendingImpulseReadbacks++;
                // A palette item may be registered while an older GPU snapshot
                // is in flight. Never read uninitialized slots for that item.
                int requestedBodyCount = _bodies.Count;
                long requestFrame = ++_requestedImpulseFrame;
                long sourceStep = _gpuStep;
                float sourceTime = _elapsed;
                AsyncGPUReadback.Request(_impulses,
                    request => OnImpulsesReady(request, requestedBodyCount,
                        requestFrame, sourceStep, sourceTime));
            }
        }

        /// <summary>Samples visible wave particle height to distinguish loss from collapse.</summary>
        private void OnCrestMetricsReady(AsyncGPUReadbackRequest request)
        {
            _crestMetricsReadbackPending = false;
            if (_disposed || request.hasError) return;
            NativeArray<uint> data = request.GetData<uint>();
            ActiveCrestParticles = data[0];
            HighestCrestDepth = data[1] / 100f;
            ReleasedCrestParticles = data[2];
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
        // Readback callbacks may arrive in bursts. Copy GPU data, but do not
        // apply any Rigidbody force outside the physics timestep.
        private void OnImpulsesReady(AsyncGPUReadbackRequest request, int requestedBodyCount,
            long requestFrame, long sourceStep, float sourceTime)
        {
            _pendingImpulseReadbacks = Mathf.Max(0, _pendingImpulseReadbacks - 1);
            if (_disposed) return;
            if (request.hasError)
            {
                // Preserve request ordering after a failed GPU readback.
                _completedImpulseFrames.Add(requestFrame, null);
                return;
            }
            NativeArray<Int4> values = request.GetData<Int4>();
            int bodyCount = Mathf.Min(requestedBodyCount, values.Length / 2);
            ImpulseFrame frame = new ImpulseFrame
            {
                SourceStep = sourceStep,
                SourceTime = sourceTime,
                Linear = new Vector3[bodyCount],
                Angular = new Vector3[bodyCount],
                Waterline = new float[bodyCount],
                FluidCoverage = new float[bodyCount],
                FlowXZ = new Vector2[bodyCount]
            };
            for (int i = 0; i < bodyCount; i++)
            {
                Int4 force = values[i * 2];
                Int4 torque = values[i * 2 + 1];
                frame.Linear[i] = new Vector3(force.X, force.Y, force.Z) / IMPULSE_SCALE;
                frame.Angular[i] = new Vector3(torque.X, torque.Y, torque.Z) / IMPULSE_SCALE;
                // Both surface and current are summed with the same
                // fixed-point wet-coverage weight. Their ratio is already in
                // world units; do not divide by IMPULSE_SCALE again.
                frame.Waterline[i] = torque.W > 0
                    ? (float)force.W / torque.W
                    : float.NaN;
                frame.FlowXZ[i] = torque.W > 0
                    ? new Vector2((float)force.X / torque.W,
                        (float)force.Z / torque.W)
                    : Vector2.zero;
                // For float bodies, force.Y counts ALL collider footprints,
                // including entirely dry colliders. torque.W sums wet
                // fractions, so their quotient is the true body-wide
                // observed water coverage (zero on a dry GPU readback).
                frame.FluidCoverage[i] = force.Y > 0
                    ? Mathf.Clamp01((float)torque.W / force.Y)
                    : 0f;
            }
            _completedImpulseFrames.Add(requestFrame, frame);
        }

        /// <summary>
        /// Match GPU's 5x5x5 immersed-volume sampling against the CURRENT
        /// world-space collider pose. This avoids applying outdated buoyancy
        /// when a floating body has already risen above the water surface.
        /// </summary>
        private static float CurrentSubmergedFraction(Collider collider, float waterline,
            float cellSize)
        {
            Vector3 center;
            Vector3 halfSize;
            int shape = 0;
            Transform transform = collider.transform;
            if (collider is BoxCollider box)
            {
                center = box.center;
                halfSize = box.size * 0.5f;
            }
            else if (collider is MeshCollider mesh && mesh.sharedMesh != null)
            {
                Bounds bounds = mesh.sharedMesh.bounds;
                center = bounds.center;
                halfSize = bounds.extents;
            }
            else if (collider is SphereCollider sphere)
            {
                center = sphere.center;
                halfSize = Vector3.one * sphere.radius;
                shape = 2;
            }
            else if (collider is CapsuleCollider capsule)
            {
                center = capsule.center;
                halfSize = Vector3.one * capsule.radius;
                if (capsule.direction == 0)
                    halfSize.x = Mathf.Max(capsule.radius, capsule.height * 0.5f);
                else if (capsule.direction == 1)
                    halfSize.y = Mathf.Max(capsule.radius, capsule.height * 0.5f);
                else
                    halfSize.z = Mathf.Max(capsule.radius, capsule.height * 0.5f);
                shape = 1;
            }
            else
                return 0f;

            float sum = 0f;
            int count = 0;
            float softness = Mathf.Max(0.001f, cellSize * 0.25f);
            for (int x = -2; x <= 2; ++x)
            for (int y = -2; y <= 2; ++y)
            for (int z = -2; z <= 2; ++z)
            {
                Vector3 q = new Vector3(x, y, z) * 0.4f;
                if (shape == 2 && q.sqrMagnitude > 1.001f) continue;
                if (shape == 1)
                {
                    float radial = collider is CapsuleCollider capsule && capsule.direction == 0
                        ? q.y * q.y + q.z * q.z
                        : collider is CapsuleCollider capsuleZ && capsuleZ.direction == 2
                            ? q.x * q.x + q.y * q.y
                            : q.x * q.x + q.z * q.z;
                    if (radial > 1.001f) continue;
                }
                Vector3 point = transform.TransformPoint(
                    center + Vector3.Scale(q, halfSize));
                float relativeHeight = (waterline - point.y) / softness + 0.5f;
                sum += Mathf.Clamp01(relativeHeight);
                ++count;
            }
            return count > 0 ? sum / count : 0f;
        }

        /// <summary>
        /// Apply buoyancy and vertical viscosity each physics tick using the
        /// LIVE Rigidbody pose and the most recently observed FLIP waterline.
        /// GPU readback latency may delay the waterline but can no longer
        /// replay an obsolete upward kick calculated for a previous pose.
        /// </summary>
        private void ApplyLiveFloatingBuoyancy()
        {
            float dt = Time.fixedDeltaTime;
            float gravity = Mathf.Abs(Physics.gravity.y) * _settings.GravityMultiplier;
            // The restoring stiffness near the waterline varies greatly with
            // collider pose. A horizontal narrow log is much stiffer than an
            // upright one, so a fixed damping rate creates an underdamped
            // spring that bobs even in calm water.
            // Float height responds to measured buoyancy and viscosity; no rise-speed cap.
            for (int i = 0; i < _bodies.Count; ++i)
            {
                GuardWavePiece piece = _bodyPieces[i];
                if (piece == null || !piece.enabled || piece.RelativeFluidDensity >= 1f)
                    continue;
                Rigidbody body = _bodies[i];
                if (body == null || body.isKinematic || !body.gameObject.activeInHierarchy)
                    continue;
                // The moment a piece passes the lip, drop all remembered
                // hydrodynamic support, even if readbacks from its past creek
                // position are still queued. It now free-falls with its XZ
                // momentum unchanged.
                if (body.worldCenterOfMass.z <= _endZ)
                {
                    _recentFluidCoverage[i] = 0f;
                    _recentWaterlineTime[i] = 0f;
                    _hasSmoothedBodyWaterline[i] = false;
                    _hasSmoothedBodyFlowXZ[i] = false;
                    continue;
                }
                if (_recentWaterlineTime[i] <= 0f ||
                    Time.fixedTime - _recentWaterlineTime[i] > WATERLINE_SAMPLE_MAX_AGE)
                {
                    // After a dry gap or a GPU readback stall, restart surface
                    // filtering from the next real sample instead of blending
                    // from an old water height.
                    _hasSmoothedBodyWaterline[i] = false;
                    _hasSmoothedBodyFlowXZ[i] = false;
                    continue;
                }

                // Zero measured water coverage disables ALL fluid-driven
                // linear motion, regardless of past surface height or flow.
                float fluidCoverage = _recentFluidCoverage[i];
                if (fluidCoverage <= 0f)
                {
                    _hasSmoothedBodyWaterline[i] = false;
                    _hasSmoothedBodyFlowXZ[i] = false;
                    continue;
                }
                float waterline = _recentWaterline[i];
                if (!_hasSmoothedBodyWaterline[i])
                {
                    _smoothedBodyWaterline[i] = waterline;
                    _hasSmoothedBodyWaterline[i] = true;
                }
                else
                {
                    // Suppress small frame-to-frame FLIP surface disturbances
                    // while still following a crest and crossing spatial water
                    // gradients as the Rigidbody travels downstream.
                    Vector3 horizontalVelocity = body.linearVelocity;
                    horizontalVelocity.y = 0f;
                    float surfaceResponse = _settings.FloatingSurfaceSmoothingRate +
                        0.4f * horizontalVelocity.magnitude / Mathf.Max(0.1f, _cellSize);
                    if (_crestEmitted) surfaceResponse = Mathf.Max(surfaceResponse, 18f);
                    float smoothing = 1f - Mathf.Exp(-surfaceResponse * dt);
                    _smoothedBodyWaterline[i] = Mathf.Lerp(
                        _smoothedBodyWaterline[i], waterline, smoothing);
                }
                waterline = _smoothedBodyWaterline[i];
                float immersed = 0f;
                int colliders = 0;
                foreach (Collider collider in _bodySampleColliders[i])
                {
                    if (collider == null || !collider.enabled ||
                        !collider.gameObject.activeInHierarchy ||
                        collider.isTrigger || collider.attachedRigidbody != body)
                        continue;
                    immersed += CurrentSubmergedFraction(collider, waterline, _cellSize);
                    ++colliders;
                }
                if (colliders == 0) continue;
                // Same displaced-volume definition as the GPU torque path:
                // submerged shape fraction times observed local water cover.
                // Preserve the master's 3D water-coverage model.
                immersed = immersed / colliders * fluidCoverage;
                if (immersed <= 0.0001f) continue;

                float density = Mathf.Max(0.01f, piece.RelativeFluidDensity);
                float buoyancyRatio = Mathf.Min(immersed / density,
                    _settings.FloatingMaxBuoyancyRatio);
                float viscosity = Mathf.Clamp01(immersed / density);
                // Experiment: fixed vertical viscosity without pose-specific critical damping.
                float damping = 1f - Mathf.Exp(-_settings.FloatingVerticalDamping * dt);
                Vector3 velocity = body.linearVelocity;
                float liftImpulse = body.mass * gravity * dt * buoyancyRatio;
                float dragImpulse = -body.mass * damping * viscosity * velocity.y;
                Vector3 buoyancyImpulse = Vector3.up * liftImpulse;
                Vector3 verticalDampingImpulse = Vector3.up * dragImpulse;
                body.AddForce(buoyancyImpulse + verticalDampingImpulse, ForceMode.Impulse);

                // A GPU readback may arrive every few physics steps. Follow
                // the latest measured water current continuously using the
                // CURRENT Rigidbody XZ velocity, rather than applying an old
                // velocity-difference impulse only when a readback arrives.
                if (!_hasSmoothedBodyFlowXZ[i])
                {
                    _smoothedBodyFlowXZ[i] = _recentFlowXZ[i];
                    _hasSmoothedBodyFlowXZ[i] = true;
                }
                else
                {
                    float rate = _crestEmitted
                        ? Mathf.Max(14f, _settings.WaterFlowResponse * 2f)
                        : Mathf.Max(1f, _settings.WaterFlowResponse);
                    float alpha = 1f - Mathf.Exp(-rate * dt);
                    _smoothedBodyFlowXZ[i] = Vector2.Lerp(
                        _smoothedBodyFlowXZ[i], _recentFlowXZ[i], alpha);
                }
                Vector2 currentXZ = _smoothedBodyFlowXZ[i];
                Vector2 bodyVelocityXZ = new Vector2(velocity.x, velocity.z);
                float responseRate = _settings.WaterFlowResponse * 2f;
                float following = (1f - Mathf.Exp(-responseRate * dt)) * viscosity;
                Vector2 flowImpulseXZ = body.mass * following *
                    (currentXZ - bodyVelocityXZ) * _settings.FluidForceMultiplier;
                flowImpulseXZ = Vector2.ClampMagnitude(flowImpulseXZ,
                    body.mass * 0.65f * _settings.FluidForceMultiplier);
                Vector3 flowImpulse = new Vector3(flowImpulseXZ.x, 0f, flowImpulseXZ.y);
                body.AddForce(flowImpulse, ForceMode.Impulse);
                GuardWaveStickForceProbe.RecordCpuImpulses(body, buoyancyImpulse,
                    verticalDampingImpulse, flowImpulse);
                if (_initialLogJoints[i] != null)
                    _initialLogJoints[i].ObserveFluidImpulse(flowImpulse);
            }
        }

        /// <summary>Apply at most one ordered GPU impulse frame per physics tick.</summary>
        public void ApplyPendingImpulses()
        {
            if (_disposed) return;
            ApplyLiveFloatingBuoyancy();
            if (!_impulsePlaybackStarted)
            {
                if (_completedImpulseFrames.Count < IMPULSE_PLAYBACK_BUFFER_STEPS)
                    return;
                _impulsePlaybackStarted = true;
            }
            // Explicit sequence IDs also handle callbacks arriving out of order.
            if (!_completedImpulseFrames.TryGetValue(_nextImpulseFrameToApply,
                    out ImpulseFrame frame))
                return;
            _completedImpulseFrames.Remove(_nextImpulseFrameToApply++);
            if (frame == null) return;

            int count = Mathf.Min(frame.Linear.Length, _bodies.Count);
            for (int i = 0; i < count; i++)
            {
                Rigidbody body = _bodies[i];
                if (body == null || body.isKinematic || !body.gameObject.activeInHierarchy)
                    continue;
                // Readbacks can be several frames late; never replay earlier
                // creek impulses once the body is beyond the waterfall edge.
                if (body.worldCenterOfMass.z <= _endZ)
                    continue;
                float limit = Mathf.Max(_settings.MinImpulseLimit,
                    body.mass * _settings.MaxImpulsePerBodyMass);
                Vector3 appliedImpulse = Vector3.ClampMagnitude(frame.Linear[i], limit);
                GuardWavePiece piece = _bodyPieces[i];
                if (piece != null && piece.enabled && piece.RelativeFluidDensity < 1f)
                {
                    // For buoyant bodies, the first-slot linear channels
                    // encode sampled water current rather than force. All
                    // linear buoyancy/drag is computed from live pose/speed
                    // once per FixedUpdate in ApplyLiveFloatingBuoyancy().
                    float waterline = frame.Waterline[i];
                    _recentFluidCoverage[i] = frame.FluidCoverage[i];
                    if (_recentFluidCoverage[i] > 0f &&
                        !float.IsNaN(waterline) && !float.IsInfinity(waterline))
                    {
                        _recentWaterline[i] = waterline;
                        _recentFlowXZ[i] = frame.FlowXZ[i];
                        _recentWaterlineTime[i] = Time.fixedTime;
                    }
                    else
                    {
                        _recentFluidCoverage[i] = 0f;
                        _recentWaterlineTime[i] = 0f;
                        _hasSmoothedBodyWaterline[i] = false;
                        _hasSmoothedBodyFlowXZ[i] = false;
                    }
                    appliedImpulse = Vector3.zero;
                }
                Vector3 appliedAngularImpulse = Vector3.ClampMagnitude(
                    frame.Angular[i], limit * _settings.AngularImpulseMultiplier);
                body.AddForce(appliedImpulse, ForceMode.Impulse);
                body.AddTorque(appliedAngularImpulse, ForceMode.Impulse);
                if (_initialLogJoints[i] != null)
                    _initialLogJoints[i].ObserveFluidImpulse(appliedImpulse);
                GuardWaveStickForceProbe.RecordImpulse(body, appliedImpulse,
                    appliedAngularImpulse, frame.SourceStep, frame.SourceTime);
            }
        }

        /// <summary>Releases every allocated GPU buffer when the wave ends or is rolled back.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _completedImpulseFrames.Clear();
            _particles.Release();
            _accum.Release();
            _velocity.Release();
            _before.Release();
            _pressureA.Release();
            _pressureB.Release();
            _solid.Release();
            _colliderBuffer.Release();
            _colliderZOffsetsBuffer.Release();
            _colliderZIndicesBuffer.Release();
            _impulses.Release();
            _waterSurface.Release();
            _smoothedWaterSurface.Release();
            _waterFlow.Release();
            _flowCounters.Release();
            _crestMetrics.Release();
            UnityEngine.Object.Destroy(_compute);
        }
    }
}
