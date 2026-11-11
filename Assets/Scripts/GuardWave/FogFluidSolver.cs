using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityScene = UnityEngine.SceneManagement.Scene;

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
        private readonly RenderTexture _temperatureA, _temperatureB, _curl;
        private readonly RenderTexture _densityPredict, _densityReverse;
        private readonly RenderTexture _pressureA, _pressureB, _divergence, _obstacleSdf;
        private readonly int _clear, _classify, _advectVelocity, _curlKernel, _forces;
        private readonly int _divergenceKernel, _jacobi, _project, _advectTemperature;
        private readonly int _predictDensity, _reverseDensity, _advectDensity;
        private readonly Vector3Int _size;
        private readonly float _cell;
        private readonly int _pressureIterations;
        private readonly UnityScene _scene;
        private RenderTexture _currentPressure;
        private bool _velocityIsA = true;
        private bool _densityIsA = true;
        private bool _temperatureIsA = true;
        private readonly List<RankedObstacle> _ranked = new List<RankedObstacle>(MaxObstacles);
        private float _frontWindSpeed = 5.5f;
        private int _step;
        private float _time;
        private bool _disposed;

        private struct RankedObstacle
        {
            public float Score;
            public Obstacle Geometry;
        }

        public Bounds WorldBounds { get; }
        public RenderTexture Density => _densityIsA ? _densityA : _densityB;

        public FogFluidSolver(UnityScene scene, Vector3 origin, Vector3 extent, float cellSize, int pressureIterations)
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
            _temperatureA = MakeTexture("Fog Temperature A");
            _temperatureB = MakeTexture("Fog Temperature B");
            _densityPredict = MakeTexture("Fog Density Predictor");
            _densityReverse = MakeTexture("Fog Density Reverse");
            _curl = MakeTexture("Fog Vorticity");
            _pressureA = MakeTexture("Fog Pressure A");
            _pressureB = MakeTexture("Fog Pressure B");
            _divergence = MakeTexture("Fog Divergence");
            _obstacleSdf = MakeTexture("Fog Obstacle SDF");
            _obstacles = new ComputeBuffer(MaxObstacles, Marshal.SizeOf<Obstacle>());
            _clear = _compute.FindKernel("ClearFog");
            _classify = _compute.FindKernel("BuildObstacleSdf");
            _advectVelocity = _compute.FindKernel("AdvectFogVelocity");
            _curlKernel = _compute.FindKernel("ComputeFogVorticity");
            _forces = _compute.FindKernel("ApplyFogForces");
            _divergenceKernel = _compute.FindKernel("FogDivergence");
            _jacobi = _compute.FindKernel("JacobiFogPressure");
            _project = _compute.FindKernel("ProjectFogVelocity");
            _advectTemperature = _compute.FindKernel("AdvectFogTemperature");
            _predictDensity = _compute.FindKernel("AdvectFogDensityForward");
            _reverseDensity = _compute.FindKernel("AdvectFogDensityBackward");
            _advectDensity = _compute.FindKernel("AdvectFogDensity");
            _compute.SetInts("_GridSize", _size.x, _size.y, _size.z);
            _compute.SetVector("_WorldMin", origin);
            _compute.SetFloat("_CellSize", _cell);
            _compute.SetFloat("_FogBaseY", origin.y + 0.5f);
            _compute.SetFloat("_InletZ", WorldBounds.max.z - _cell * 1.5f);
            _compute.SetFloat("_InletStrength", 0f);
            ConfigureMotion(2.2f, 0.9f, 3.5f, 1.1f, 0.5f);
            _compute.SetBuffer(_classify, "_ObstacleBoxes", _obstacles);
            Bind(_clear, "_VelocityOut", _velocityA);
            Bind(_clear, "_VelocityAlt", _velocityB);
            Bind(_clear, "_DensityOut", _densityA);
            Bind(_clear, "_DensityAlt", _densityB);
            Bind(_clear, "_TemperatureOut", _temperatureA);
            Bind(_clear, "_TemperatureAlt", _temperatureB);
            Bind(_clear, "_DensityPredictOut", _densityPredict);
            Bind(_clear, "_DensityReverseOut", _densityReverse);
            Bind(_clear, "_CurlOut", _curl);
            Bind(_clear, "_PressureOut", _pressureA);
            Bind(_clear, "_PressureAlt", _pressureB);
            Bind(_clear, "_DivergenceWrite", _divergence);
            Bind(_clear, "_ObstacleOut", _obstacleSdf);
            Dispatch(_clear);
            _currentPressure = _pressureA;
            RefreshObstacleField();
        }

        public void ConfigureMotion(float vorticity, float turbulence, float expansion,
            float buoyancy, float weight)
        {
            _compute.SetFloat("_VorticityStrength", Mathf.Max(0f, vorticity));
            _compute.SetFloat("_TurbulenceStrength", Mathf.Max(0f, turbulence));
            _compute.SetFloat("_ExpansionStrength", Mathf.Max(0f, expansion));
            _compute.SetFloat("_Buoyancy", Mathf.Max(0f, buoyancy));
            _compute.SetFloat("_Weight", Mathf.Max(0f, weight));
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

        /// <summary>
        /// Select the most relevant scene colliders near the moving plume before
        /// voxelization. The old arbitrary first-256 enumeration could omit the
        /// defense structure entirely. Broad terrain remains handled separately.
        /// </summary>
        private void RefreshObstacleField()
        {
            _ranked.Clear();
            float front = Mathf.Clamp(
                WorldBounds.max.z - Mathf.Max(0f, _time - 0.8f) * _frontWindSpeed,
                WorldBounds.min.z, WorldBounds.max.z);
            foreach (Collider collider in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (collider == null || !collider.enabled || collider.isTrigger ||
                    !collider.gameObject.activeInHierarchy || collider.gameObject.scene.handle != _scene.handle)
                    continue;
                Bounds b = collider.bounds;
                if (!b.Intersects(WorldBounds) || b.size.x > 22f || b.size.z > 34f)
                    continue;

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
                    half = b.extents;
                }
                else continue;

                if (half.x > 12f || half.z > 18f) continue;
                var geometry = new Obstacle
                {
                    Center = new Vector4(center.x, center.y, center.z, 0f),
                    HalfExtent = new Vector4(
                        Mathf.Max(0.05f, half.x), Mathf.Max(0.05f, half.y), Mathf.Max(0.05f, half.z), 0f),
                    Rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w)
                };
                // Forward obstructions, the flag and occupied channel are preferred.
                float score = Mathf.Abs(center.z - front) +
                    0.38f * Mathf.Abs(center.x) + 0.10f * Mathf.Abs(center.y - WorldBounds.min.y);
                _ranked.Add(new RankedObstacle { Score = score, Geometry = geometry });
            }

            _ranked.Sort((a, b) => a.Score.CompareTo(b.Score));
            int count = Mathf.Min(MaxObstacles, _ranked.Count);
            for (int i = 0; i < count; i++) _obstacleData[i] = _ranked[i].Geometry;
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
            _frontWindSpeed = windSpeed;
            _compute.SetFloat("_Dt", dt);
            _compute.SetFloat("_SimulationTime", _time);
            _compute.SetFloat("_WindSpeed", windSpeed);
            _compute.SetFloat("_FogBaseY", baseY);
            _compute.SetFloat("_InletStrength", Mathf.Clamp01(inlet));
            _compute.SetFloat("_InletZ", WorldBounds.max.z - _cell * 1.5f);
            if ((_step++ % 8) == 0) RefreshObstacleField();

            // Ping-pong velocity across advection, vorticity force and projection.
            RenderTexture velocityCurrent = _velocityIsA ? _velocityA : _velocityB;
            RenderTexture velocityNext = _velocityIsA ? _velocityB : _velocityA;
            Bind(_advectVelocity, "_VelocityRead", velocityCurrent);
            Bind(_advectVelocity, "_ObstacleRead", _obstacleSdf);
            Bind(_advectVelocity, "_VelocityWrite", velocityNext);
            Dispatch(_advectVelocity);

            Bind(_curlKernel, "_VelocityRead", velocityNext);
            Bind(_curlKernel, "_ObstacleRead", _obstacleSdf);
            Bind(_curlKernel, "_CurlOut", _curl);
            Dispatch(_curlKernel);

            RenderTexture temperatureCurrent = _temperatureIsA ? _temperatureA : _temperatureB;
            RenderTexture temperatureNext = _temperatureIsA ? _temperatureB : _temperatureA;
            Bind(_forces, "_VelocityRead", velocityNext);
            Bind(_forces, "_DensityRead", Density);
            Bind(_forces, "_TemperatureRead", temperatureCurrent);
            Bind(_forces, "_CurlRead", _curl);
            Bind(_forces, "_ObstacleRead", _obstacleSdf);
            Bind(_forces, "_VelocityWrite", velocityCurrent);
            Dispatch(_forces);

            Bind(_divergenceKernel, "_VelocityRead", velocityCurrent);
            Bind(_divergenceKernel, "_ObstacleRead", _obstacleSdf);
            Bind(_divergenceKernel, "_DivergenceWrite", _divergence);
            Dispatch(_divergenceKernel);

            RenderTexture pressureRead = _currentPressure;
            RenderTexture pressureWrite = pressureRead == _pressureA ? _pressureB : _pressureA;
            Bind(_jacobi, "_ObstacleRead", _obstacleSdf);
            Bind(_jacobi, "_DivergenceRead", _divergence);
            for (int i = 0; i < _pressureIterations; i++)
            {
                Bind(_jacobi, "_PressureRead", pressureRead);
                Bind(_jacobi, "_PressureWrite", pressureWrite);
                Dispatch(_jacobi);
                (pressureRead, pressureWrite) = (pressureWrite, pressureRead);
            }
            _currentPressure = pressureRead;

            Bind(_project, "_VelocityRead", velocityCurrent);
            Bind(_project, "_ObstacleRead", _obstacleSdf);
            Bind(_project, "_PressureRead", pressureRead);
            Bind(_project, "_VelocityWrite", velocityNext);
            Dispatch(_project);
            _velocityIsA = !_velocityIsA;

            Bind(_advectTemperature, "_VelocityRead", velocityNext);
            Bind(_advectTemperature, "_ObstacleRead", _obstacleSdf);
            Bind(_advectTemperature, "_TemperatureRead", temperatureCurrent);
            Bind(_advectTemperature, "_TemperatureWrite", temperatureNext);
            Dispatch(_advectTemperature);
            _temperatureIsA = !_temperatureIsA;

            // Monotonic MacCormack correction preserves dense, billowing cores
            // without overshoots or negative-density artifacts.
            RenderTexture densityCurrent = Density;
            RenderTexture densityNext = _densityIsA ? _densityB : _densityA;
            Bind(_predictDensity, "_VelocityRead", velocityNext);
            Bind(_predictDensity, "_ObstacleRead", _obstacleSdf);
            Bind(_predictDensity, "_DensityRead", densityCurrent);
            Bind(_predictDensity, "_DensityPredictOut", _densityPredict);
            Dispatch(_predictDensity);

            Bind(_reverseDensity, "_VelocityRead", velocityNext);
            Bind(_reverseDensity, "_ObstacleRead", _obstacleSdf);
            Bind(_reverseDensity, "_DensityPredictRead", _densityPredict);
            Bind(_reverseDensity, "_DensityReverseOut", _densityReverse);
            Dispatch(_reverseDensity);

            Bind(_advectDensity, "_VelocityRead", velocityNext);
            Bind(_advectDensity, "_ObstacleRead", _obstacleSdf);
            Bind(_advectDensity, "_DensityRead", densityCurrent);
            Bind(_advectDensity, "_DensityPredictRead", _densityPredict);
            Bind(_advectDensity, "_DensityReverseRead", _densityReverse);
            Bind(_advectDensity, "_DensityWrite", densityNext);
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
                _temperatureA, _temperatureB, _curl, _densityPredict, _densityReverse,
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
