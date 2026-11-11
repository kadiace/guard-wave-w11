using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GuardWave
{
    /// <summary>
    /// Renders the GPU-resident FLIP/APIC water without copying particle positions to CPU.
    /// Static river/waterfall surfaces remain authored directly in Forest.unity.
    /// </summary>
    internal sealed class GuardWaveFlipRenderer : IDisposable
    {
        private readonly Material _material;
        private bool _disposed;

        /// <summary>Loads the project-owned procedural GPU water shader from Resources.</summary>
        public GuardWaveFlipRenderer(GuardWaveFluidSettings settings)
        {
            Shader shader = Resources.Load<Shader>("GuardWaveFlipWater");
            if (shader == null)
                throw new InvalidOperationException("Resources/GuardWaveFlipWater.shader is missing.");
            _material = new Material(shader) { name = "GuardWave FLIP/APIC Water" };
            _material.SetFloat("_Radius", settings.ParticleRenderRadius);
        }

        /// <summary>Submits one GPU instanced draw over live and falling fluid particles.</summary>
        public void Render(GuardWaveFlipFluid fluid)
        {
            if (_disposed || fluid == null || fluid.ParticleCount == 0) return;
            _material.SetBuffer("_Particles", fluid.Particles);
            RenderParams parameters = new RenderParams(_material)
            {
                worldBounds = fluid.RenderBounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false
            };
            Graphics.RenderPrimitives(parameters, MeshTopology.Triangles, 6, fluid.ParticleCount);
        }

        /// <summary>Releases the dynamic render material without destroying scene geometry.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnityEngine.Object.Destroy(_material);
        }
    }
}
