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
        private const int MAX_PARTICLES = 24000;
        private const int MAX_BODIES = 384;
        private const int MAX_COLLIDERS = 256;
        private const int PRESSURE_ITERATIONS = 22;
        private const float CELL_SIZE = 0.6f;
        private const float IMPULSE_SCALE = 2048f;
        private const float PARTICLE_EFFECTIVE_MASS = 0.14f;
        private const float SPAWN_INTERVAL = 0.12f;

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
        private readonly ColliderData[] _colliderData = new ColliderData[MAX_COLLIDERS];
        private readonly ComputeBuffer _particles;
        private readonly ComputeBuffer _accum;
        private readonly ComputeBuffer _velocity;
        private readonly ComputeBuffer _before;
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
        private readonly int _gridCount;
        private readonly int _clearKernel;
        private readonly int _classifyKernel;
        private readonly int _spawnKernel;
        private readonly int _p2gKernel;
        private readonly int _normalizeKernel;
        private readonly int _pressureKernel;
        private readonly int _projectKernel;
        private readonly int _g2pKernel;
        private readonly int _pressureBodyKernel;
        private readonly int _waterfallKernel;
        private float _spawnClock;
        private float _elapsed;
        private int _particleCount;
        private int _seed;
        private bool _pendingReadback;
        private bool _disposed;

        public ComputeBuffer Particles => _particles;
        public int ParticleCount => _particleCount;
        public float InletSpeed { get; }
        public Bounds RenderBounds { get; }

        /// <summary>Builds a bounded GPU fluid domain centered at x=0 and a 24m wide river.</summary>
        public GuardWaveFlipFluid(float startZ, float endZ, float groundY,
            float waterfallEndY, float waveHeight, float travelDuration, IList<GuardWavePiece> pieces,
            Rigidbody flagBody)
        {
            if (!SystemInfo.supportsComputeShaders)
                throw new NotSupportedException("GPU FLIP water requires compute shader support.");

            ComputeShader asset = Resources.Load<ComputeShader>("GuardWaveFlip");
            if (asset == null)
                throw new InvalidOperationException("Missing Resources/GuardWaveFlip.compute");
            _compute = UnityEngine.Object.Instantiate(asset);
            _floorY = groundY;
            _waveHeight = Mathf.Clamp(waveHeight, 1f, 6f);
            _startZ = Mathf.Max(startZ, endZ + 8f);
            _endZ = endZ;
            InletSpeed = Mathf.Clamp((_startZ-_endZ) /
                Mathf.Max(4f, travelDuration * 2f), 6f, 13f);
            _grid = new Vector3Int(
                Mathf.CeilToInt((24f + CELL_SIZE*2f) / CELL_SIZE),
                Mathf.CeilToInt((9f + CELL_SIZE) / CELL_SIZE),
                Mathf.CeilToInt((_startZ - _endZ + CELL_SIZE*3f) / CELL_SIZE));
            _origin = new Vector3(-12f-CELL_SIZE, groundY-CELL_SIZE,
                _endZ-CELL_SIZE);
            _gridCount = _grid.x * _grid.y * _grid.z;
            // Include lower-cliff free-fall particles in procedural renderer culling.
            RenderBounds = new Bounds(
                new Vector3(0, (groundY+9f+waterfallEndY)*0.5f,
                    (_startZ+_endZ-80f)*0.5f),
                new Vector3(27f, groundY+13f-waterfallEndY,
                    _startZ-_endZ+86f));

            _particles = new ComputeBuffer(MAX_PARTICLES, Marshal.SizeOf<Particle>());
            _particles.SetData(new Particle[MAX_PARTICLES]);
            _accum = new ComputeBuffer(_gridCount, sizeof(int)*4);
            _velocity = new ComputeBuffer(_gridCount, sizeof(float)*3);
            _before = new ComputeBuffer(_gridCount, sizeof(float)*3);
            _pressureA = new ComputeBuffer(_gridCount, sizeof(float));
            _pressureB = new ComputeBuffer(_gridCount, sizeof(float));
            _solid = new ComputeBuffer(_gridCount, sizeof(uint));
            _colliderBuffer = new ComputeBuffer(MAX_COLLIDERS, Marshal.SizeOf<ColliderData>());
            _impulses = new ComputeBuffer(MAX_BODIES * 2, sizeof(int)*4);

            _clearKernel = _compute.FindKernel("ClearGrid");
            _classifyKernel = _compute.FindKernel("ClassifyGrid");
            _spawnKernel = _compute.FindKernel("Spawn");
            _p2gKernel = _compute.FindKernel("ParticleToGrid");
            _normalizeKernel = _compute.FindKernel("NormalizeGrid");
            _pressureKernel = _compute.FindKernel("JacobiPressure");
            _projectKernel = _compute.FindKernel("ProjectGrid");
            _g2pKernel = _compute.FindKernel("GridToParticle");
            _pressureBodyKernel = _compute.FindKernel("PressureToBodies");
            _waterfallKernel = _compute.FindKernel("Waterfall");

            _compute.SetInts("_GridSize", _grid.x, _grid.y, _grid.z);
            _compute.SetVector("_GridOrigin", _origin);
            _compute.SetFloat("_CellSize", CELL_SIZE);
            _compute.SetFloat("_FloorY", groundY);
            _compute.SetFloat("_ExitZ", endZ);
            _compute.SetFloat("_WaterfallEndY", waterfallEndY);
            _compute.SetFloat("_HalfWidth", 12f);
            _compute.SetFloat("_InletZ", _startZ-CELL_SIZE);
            _compute.SetFloat("_WaveHeight", _waveHeight);
            _compute.SetFloat("_InletVelocity", InletSpeed);
            _compute.SetFloat("_Gravity", Mathf.Abs(Physics.gravity.y));
            _compute.SetFloat("_FlipFraction", 0.92f);
            _compute.SetFloat("_ParticleMass", PARTICLE_EFFECTIVE_MASS);
            _compute.SetFloat("_HydroDensity", 0.7f);
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
            Bind(_classifyKernel, "_Solid", _solid);
            Bind(_classifyKernel, "_Colliders", _colliderBuffer);
            Bind(_spawnKernel, "_Particles", _particles);
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
            Bind(_pressureBodyKernel, "_Accum", _accum);
            Bind(_pressureBodyKernel, "_Solid", _solid);
            Bind(_pressureBodyKernel, "_Colliders", _colliderBuffer);
            Bind(_pressureBodyKernel, "_BodyImpulses", _impulses);
            Bind(_waterfallKernel, "_Particles", _particles);
            CollectBodies(pieces, flagBody);
            UpdateColliders();
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
            foreach (Rigidbody body in UnityEngine.Object.FindObjectsByType<Rigidbody>(
                FindObjectsSortMode.None))
                if (body != null && body.gameObject.scene == flag.gameObject.scene)
                    AddBody(body);
            foreach (Rigidbody body in _bodies)
                foreach (Collider collider in body.GetComponentsInChildren<Collider>())
                    if (collider != null && !collider.isTrigger)
                        _colliders.Add(collider);
        }

        /// <summary>Registers a distinct Rigidbody and its impulse-buffer index.</summary>
        private void AddBody(Rigidbody body)
        {
            if (body == null || _indices.ContainsKey(body) || _bodies.Count >= MAX_BODIES)
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
                if (count >= MAX_COLLIDERS) break;
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                    continue;
                Bounds bounds = collider.bounds;
                if (bounds.min.x > 12.5f || bounds.max.x < -12.5f ||
                    bounds.max.z < _endZ - CELL_SIZE || bounds.min.z > _startZ + CELL_SIZE)
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
                data.CenterBody = new Vector4(center.x,center.y,center.z,index);
                data.ExtentType = new Vector4(half.x,half.y,half.z,type);
                data.Rotation = new Vector4(rotation.x,rotation.y,rotation.z,rotation.w);
                if (body != null && !body.isKinematic)
                {
                    Vector3 v = body.linearVelocity;
                    Vector3 w = body.angularVelocity;
                    data.Velocity = new Vector4(v.x,v.y,v.z,bounds.extents.magnitude+CELL_SIZE);
                    data.Angular = new Vector4(w.x,w.y,w.z,0);
                }
                if (body == null || body.isKinematic)
                    data.Velocity.w = bounds.extents.magnitude + CELL_SIZE;
                Vector3 bodyCenter = body != null ? body.worldCenterOfMass : center;
                data.BodyCenter = new Vector4(bodyCenter.x, bodyCenter.y, bodyCenter.z, 0f);
                _colliderData[count++] = data;
            }
            if (count > 0) _colliderBuffer.SetData(_colliderData, 0, 0, count);
            _compute.SetInt("_ColliderCount", count);
            _compute.SetInt("_BodyCount", _bodies.Count);
        }

        /// <summary>Advances 3D GPU water through a PIC/FLIP + affine P2G/G2P pressure substep.</summary>
        public void Step(float dt, float inletFraction)
        {
            if (_disposed) return;
            _elapsed += dt;
            _compute.SetFloat("_Dt", dt);
            UpdateColliders();
            Dispatch(_clearKernel, Mathf.Max(_gridCount, _bodies.Count*2));
            Dispatch(_classifyKernel, _gridCount);

            _spawnClock += dt;
            if (inletFraction > 0.05f && _spawnClock >= SPAWN_INTERVAL)
            {
                _spawnClock -= SPAWN_INTERVAL;
                int widthCells = Mathf.FloorToInt(24f/(CELL_SIZE*0.85f));
                int heightCells = Mathf.Max(1,Mathf.FloorToInt(_waveHeight/(CELL_SIZE*0.85f)));
                int count = Mathf.Min(MAX_PARTICLES-_particleCount,
                    Mathf.RoundToInt(widthCells*heightCells*3f*inletFraction));
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
            for (int i = 0; i < PRESSURE_ITERATIONS; i++)
            {
                _compute.SetBuffer(_pressureKernel, "_PressureSrc", src);
                _compute.SetBuffer(_pressureKernel, "_PressureDst", dst);
                Dispatch(_pressureKernel, _gridCount);
                (src,dst) = (dst,src);
            }
            _compute.SetBuffer(_projectKernel, "_PressureSrc", src);
            Dispatch(_projectKernel, _gridCount);
            _compute.SetBuffer(_pressureBodyKernel, "_PressureSrc", src);
            Dispatch(_pressureBodyKernel, _gridCount);
            Dispatch(_g2pKernel, _particleCount);
            Dispatch(_waterfallKernel, _particleCount);
            if (!_pendingReadback && _bodies.Count > 0)
            {
                _pendingReadback = true;
                AsyncGPUReadback.Request(_impulses, OnImpulsesReady);
            }
        }

        /// <summary>Runs the selected compute kernel with 128 threads per workgroup.</summary>
        private void Dispatch(int kernel, int count)
        {
            _compute.Dispatch(kernel, Mathf.Max(1, (count+THREADS-1)/THREADS), 1, 1);
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
                Int4 force = values[i*2];
                Int4 torque = values[i*2+1];
                Vector3 linear = new Vector3(force.X,force.Y,force.Z) / IMPULSE_SCALE;
                Vector3 angular = new Vector3(torque.X,torque.Y,torque.Z) / IMPULSE_SCALE;
                float limit = Mathf.Max(0.25f,body.mass*3f);
                body.AddForce(Vector3.ClampMagnitude(linear,limit), ForceMode.Impulse);
                body.AddTorque(Vector3.ClampMagnitude(angular,limit*3f), ForceMode.Impulse);
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
            _pressureA.Release();
            _pressureB.Release();
            _solid.Release();
            _colliderBuffer.Release();
            _impulses.Release();
            UnityEngine.Object.Destroy(_compute);
        }
    }
}
