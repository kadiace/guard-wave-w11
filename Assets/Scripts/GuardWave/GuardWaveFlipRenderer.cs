using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// Renders GPU-resident FLIP/APIC particles and the scene's stationary shallow water.
    /// The rendered particles use the physical solver positions, never a scripted wave height.
    /// </summary>
    internal sealed class GuardWaveFlipRenderer : IDisposable
    {
        private readonly List<GameObject> _backdrops = new List<GameObject>();
        private readonly List<Mesh> _backdropMeshes = new List<Mesh>();
        private readonly Material _waterMaterial;
        private readonly Material _backdropMaterial;
        private bool _disposed;

        /// <summary>Constructs GPU particle and shallow river materials for the URP renderer.</summary>
        public GuardWaveFlipRenderer()
        {
            Shader waterShader = Shader.Find("GuardWave/FlipWater");
            if (waterShader == null)
                throw new InvalidOperationException("GuardWave/FlipWater shader was not imported.");
            _waterMaterial = new Material(waterShader) { name = "GuardWave GPU FLIP Water" };
            _waterMaterial.SetFloat("_Radius", 0.45f);

            Shader backdropShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (backdropShader == null)
                throw new InvalidOperationException("URP/Unlit is required to display the river backdrop.");
            _backdropMaterial = new Material(backdropShader) { name = "GuardWave Shallow Water" };
            Color tint = new Color(0.08f, 0.42f, 0.85f, 0.35f);
            if (_backdropMaterial.HasProperty("_BaseColor"))
                _backdropMaterial.SetColor("_BaseColor", tint);
            _backdropMaterial.SetFloat("_Surface", 1f);
            _backdropMaterial.SetFloat("_Blend", 0f);
            _backdropMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _backdropMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _backdropMaterial.SetInt("_ZWrite", 0);
            _backdropMaterial.SetOverrideTag("RenderType", "Transparent");
            _backdropMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _backdropMaterial.renderQueue = (int)RenderQueue.Transparent;
        }

        /// <summary>Displays static background creek water; it is not the flood simulator.</summary>
        public void ShowShallow(Bounds upperBed, Bounds lowerBed, float upperY, float lowerY)
        {
            if (_backdrops.Count > 0) return;
            CreateBackdrop("Upper Shallow Creek", new[]
            {
                new Vector3(upperBed.min.x, upperY, upperBed.min.z),
                new Vector3(upperBed.min.x, upperY, upperBed.max.z),
                new Vector3(upperBed.max.x, upperY, upperBed.max.z),
                new Vector3(upperBed.max.x, upperY, upperBed.min.z)
            });
            CreateBackdrop("Lower Shallow Creek", new[]
            {
                new Vector3(lowerBed.min.x, lowerY, lowerBed.min.z),
                new Vector3(lowerBed.min.x, lowerY, lowerBed.max.z),
                new Vector3(lowerBed.max.x, lowerY, lowerBed.max.z),
                new Vector3(lowerBed.max.x, lowerY, lowerBed.min.z)
            });
            CreateBackdrop("Existing Creek Cliff Waterfall", new[]
            {
                new Vector3(upperBed.min.x, upperY, upperBed.min.z - 0.1f),
                new Vector3(upperBed.min.x, lowerY, lowerBed.max.z - 0.25f),
                new Vector3(upperBed.max.x, lowerY, lowerBed.max.z - 0.25f),
                new Vector3(upperBed.max.x, upperY, upperBed.min.z - 0.1f)
            });
        }

        /// <summary>Creates a non-colliding visual surface owned by the current Forest scene.</summary>
        private void CreateBackdrop(string name, Vector3[] corners)
        {
            GameObject surface = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            surface.layer = 2;
            Mesh mesh = new Mesh { name = name + " Mesh" };
            mesh.vertices = corners;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            surface.GetComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = surface.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _backdropMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _backdrops.Add(surface);
            _backdropMeshes.Add(mesh);
        }

        /// <summary>
        /// Enqueues a single procedural instanced draw for the solver's live GPU particles.
        /// No particle-position CPU readback or per-particle GameObjects are required.
        /// </summary>
        public void Render(GuardWaveFlipFluid fluid)
        {
            if (_disposed || fluid == null || fluid.ParticleCount == 0) return;
            _waterMaterial.SetBuffer("_Particles", fluid.Particles);
            RenderParams parameters = new RenderParams(_waterMaterial)
            {
                worldBounds = fluid.RenderBounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false
            };
            Graphics.RenderPrimitives(parameters, MeshTopology.Triangles, 6, fluid.ParticleCount);
        }

        /// <summary>Releases the three backdrop meshes and GPU-particle render material.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (GameObject surface in _backdrops)
                if (surface != null) UnityEngine.Object.Destroy(surface);
            foreach (Mesh mesh in _backdropMeshes)
                if (mesh != null) UnityEngine.Object.Destroy(mesh);
            UnityEngine.Object.Destroy(_waterMaterial);
            UnityEngine.Object.Destroy(_backdropMaterial);
            _backdrops.Clear();
            _backdropMeshes.Clear();
        }
    }
}
