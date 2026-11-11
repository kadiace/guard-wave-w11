using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GuardWave
{
    /// <summary>
    /// An independent three-dimensional Eulerian smoke/fog solver. FLIP water is never used.
    /// Runs semi-Lagrangian velocity/density advection, Jacobi pressure projection and
    /// collision-aware open-boundary flow on GPU-resident 3D textures.
    /// </summary>
    internal sealed class FogFluidSolver : IDisposable
    {
        private const int MaxObstacles = 256;

        [StructLayout(LayoutKind.Sequential)]
        private struct Obstacle
        {
            public Vector4 Center;
            public Vector4 HalfExtent;
            public Vector4 Rotation;
        }

        private readonly ComputeShader _compute;
        private readonly ComputeBuffer _obstacles;
        private readonly Obstacle[] _obstacleData = new Obstacle[MaxObstacles];
        private readonly RenderTexture _velocityA, _velocityB, _densityA, _densityB;
        private readonly RenderTexture _pressureA, _pressureB, _divergence, _obstacleSdf;
        private readonly int _clear, _classify, _advectVelocity, _divergenceKernel;
        private readonly int _jacobi, _project, _advectDensity;
        private readonly Vector3Int _size;
        private readonly float _cell;
        private readonly int _pressureIterations;
        private readonly Scene _scene;
        private bool _densityIsA = true;
        private int _step;
        private float _time;
        private bool _disposed;

        public Bounds WorldBounds { get; }
        public RenderTexture Density => _densityIsA ? _densityA : _densityB;

        public FogFluidSolver(Scene scene, Vector3 origin, Vector3 extent, float cellSize, int pressureIterations)
        {
            if (!SystemInfo.supportsComputeShaders)
                throw new NotSupportedException("Fog requires compute shader support.");
            ComputeShader asset = Resources.Load<ComputeShader>("GuardWaveFog");
            if (asset == null)
                throw new InvalidOperationException("Missing Assets/Resources/GuardWaveFog.compute.");
            _scene = scene;
            _cell = Mathf.Max(0.4f, cellSize);
            _pressureIterations = Mathf.Clamp(pressureIterations, 8, 64);
            _size = new Vector3Int(
                Mathf.CeilToInt(extent.x / _cell),
                Mathf.CeilToInt(extent.y / _cell),
                Mathf.CeilToInt(extent.z / _cell));
            Vector3 realSize = new Vector3(_size.x, _size.y, _size.z) * _cell;
            WorldBounds = new Bounds(origin + realSize * 0.5f, realSize);
            _compute = UnityEngine.Object.Instantiate(asset);
            _velocityA = MakeTexture("Fog Velocity A");
            _velocityB = MakeTexture("Fog Velocity B");
            _densityA = MakeTexture("Fog Density A");
            _densityB = MakeTexture("Fog Density B");
            _pressureA = MakeTexture("Fog Pressure A");
            _pressureB = MakeTexture("Fog Pressure B");
            _divergence = MakeTexture("Fog Divergence");
            _obstacleSdf = MakeTexture("Fog Obstacle SDF");
            _obstacles = new ComputeBuffer(MaxObstacles, Marshal.SizeOf<Obstacle>());
            _clear = _compute.FindKernel("ClearFog");
            _classify = _compute.FindKernel("BuildObstacleSdf");
            _advectVelocity = _compute.FindKernel("AdvectFogVelocity");
            _divergenceKernel = _compute.FindKernel("FogDivergence");
            _jacobi = _compute.FindKernel("JacobiFogPressure");
            _project = _compute.FindKernel("ProjectFogVelocity");
            _advectDensity = _compute.FindKernel("AdvectFogDensity");
            _compute.SetInts("_GridSize", _size.x, _size.y, _size.z);
            _compute.SetVector("_WorldMin", origin);
            _compute.SetFloat("_CellSize", _cell);
            _compute.SetBuffer(_classify, "_ObstacleBoxes", _obstacles);
            Bind(_clear, "_VelocityOut", _velocityA);
            Bind(_clear, "_VelocityAlt", _velocityB);
            Bind(_clear, "_DensityOut", _densityA);
            Bind(_clear, "_DensityAlt", _densityB);
            Bind(_clear, "_PressureOut", _pressureA);
            Bind(_clear, "_PressureAlt", _pressureB);
            Bind(_clear, "_DivergenceOut", _divergence);
            Bind(_clear, "_ObstacleOut", _obstacleSdf);
            Dispatch(_clear);
            RefreshObstacleField();
        }

        private RenderTexture MakeTexture(string name)
        {
            var texture = new RenderTexture(_size.x, _size.y, 0, RenderTextureFormat.ARGBHalf)
            {
                name = name,
                dimension = TextureDimension.Tex3D,
                volumeDepth = _size.z,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
                autoGenerateMips = false
            };
            if (!texture.Create())
                throw new InvalidOperationException("Unable to allocate 3D fog texture: " + name);
            return texture;
        }

        private void Bind(int kernel, string name, RenderTexture texture) =>
            _compute.SetTexture(kernel, name, texture);

        private void Dispatch(int kernel) => _compute.Dispatch(kernel,
            (_size.x + 7) / 8, (_size.y + 3) / 4, (_size.z + 7) / 8);

        /// <summary>Approximate scene primitives and rotated mesh bounds with GPU oriented-box SDFs.</summary>
        private void RefreshObstacleField()
        {
            int count = 0;
            foreach (Collider collider in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (count >= MaxObstacles) break;
                if (collider == null || !collider.enabled || collider.isTrigger ||
                    !collider.gameObject.activeInHierarchy || collider.gameObject.scene != _scene)
                    continue;
                Bounds b = collider.bounds;
                if (!b.Intersects(WorldBounds) || b.size.y < 0.22f ||
                    b.size.x > 22f || b.size.z > 34f) continue;

                Vector3 center = b.center;
                Vector3 half = b.extents;
                Quaternion rotation = Quaternion.identity;
                Vector3 scale = collider.transform.lossyScale;
                Vector3 absScale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                if (collider is BoxCollider box)
                {
                    center = collider.transform.TransformPoint(box.center);
                    half = Vector3.Scale(box.size, absScale) * 0.5f;
                    rotation = collider.transform.rotation;
                }
                else if (collider is MeshCollider mesh && mesh.sharedMesh != null)
                {
                    center = collider.transform.TransformPoint(mesh.sharedMesh.bounds.center);
                    half = Vector3.Scale(mesh.sharedMesh.bounds.extents, absScale);
                    rotation = collider.transform.rotation;
                }
                else if (collider is SphereCollider sphere)
                {
                    center = collider.transform.TransformPoint(sphere.center);
                    float radius = sphere.radius * Mathf.Max(absScale.x, absScale.y, absScale.z);
                    half = Vector3.one * radius;
                }
                else if (collider is CapsuleCollider capsule)
                {
                    center = collider.transform.TransformPoint(capsule.center);
                    // A conservative oriented box; enough for fog collision without mesh voxelization.
                    half = b.extents;
                }
                else continue;

                // Omit very broad scenery such as river banks; their AABBs would
                // block the entire channel. House sticks, placed pieces and props remain.
                if (half.x > 12f || half.z > 18f) continue;
                _obstacleData[count++] = new Obstacle
                {
                    Center = new Vector4(center.x, center.y, center.z, 0f),
                    HalfExtent = new Vector4(
                        Mathf.Max(0.05f, half.x), Mathf.Max(0.05f, half.y), Mathf.Max(0.05f, half.z), 0f),
                    Rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w)
                };
            }

            if (count > 0) _obstacles.SetData(_obstacleData, 0, 0, count);
            _compute.SetInt("_ObstacleCount", count);
            Bind(_classify, "_ObstacleOut", _obstacleSdf);
            Dispatch(_classify);
        }

        public void Step(float deltaTime, float inlet, float windSpeed, float baseY)
        {
            if (_disposed) return;
            float dt = Mathf.Clamp(deltaTime, 0.001f, 0.07f);
            _time += dt;
            if ((_step++ % 8) == 0) RefreshObstacleField();
            _compute.SetFloat("_Dt", dt);
            _compute.SetFloat("_SimulationTime", _time);
            _compute.SetFloat("_WindSpeed", windSpeed);
            _compute.SetFloat("_FogBaseY", baseY);
            _compute.SetFloat("_InletStrength", Mathf.Clamp01(inlet));
            _compute.SetFloat("_InletZ", WorldBounds.max.z - _cell * 1.5f);

            Bind(_advectVelocity, "_VelocityRead", _velocityA);
            Bind(_advectVelocity, "_ObstacleRead", _obstacleSdf);
            Bind(_advectVelocity, "_VelocityWrite", _velocityB);
            Dispatch(_advectVelocity);

            Bind(_divergenceKernel, "_VelocityRead", _velocityB);
            Bind(_divergenceKernel, "_ObstacleRead", _obstacleSdf);
            Bind(_divergenceKernel, "_DivergenceWrite", _divergence);
            Dispatch(_divergenceKernel);

            RenderTexture pressureRead = _pressureA, pressureWrite = _pressureB;
            Bind(_jacobi, "_ObstacleRead", _obstacleSdf);
            Bind(_jacobi, "_DivergenceRead", _divergence);
            for (int i = 0; i < _pressureIterations; i++)
            {
                Bind(_jacobi, "_PressureRead", pressureRead);
                Bind(_jacobi, "_PressureWrite", pressureWrite);
                Dispatch(_jacobi);
                (pressureRead, pressureWrite) = (pressureWrite, pressureRead);
            }
            // Preserve the final solution as the next frame's initial pressure guess.
            if (pressureRead != _pressureA)
                Graphics.CopyTexture(pressureRead, _pressureA);

            Bind(_project, "_VelocityRead", _velocityB);
            Bind(_project, "_ObstacleRead", _obstacleSdf);
            Bind(_project, "_PressureRead", pressureRead);
            Bind(_project, "_VelocityWrite", _velocityA);
            Dispatch(_project);

            RenderTexture src = Density, dst = _densityIsA ? _densityB : _densityA;
            Bind(_advectDensity, "_VelocityRead", _velocityA);
            Bind(_advectDensity, "_ObstacleRead", _obstacleSdf);
            Bind(_advectDensity, "_DensityRead", src);
            Bind(_advectDensity, "_DensityWrite", dst);
            Dispatch(_advectDensity);
            _densityIsA = !_densityIsA;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _obstacles?.Release();
            foreach (RenderTexture t in new[] {
                _velocityA, _velocityB, _densityA, _densityB,
                _pressureA, _pressureB, _divergence, _obstacleSdf })
            {
                if (t == null) continue;
                t.Release();
                UnityEngine.Object.Destroy(t);
            }
            if (_compute != null) UnityEngine.Object.Destroy(_compute);
        }
    }
}
