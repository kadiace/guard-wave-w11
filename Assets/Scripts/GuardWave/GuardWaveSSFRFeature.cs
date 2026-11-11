using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace GuardWave
{
    /// <summary>
    /// URP RenderGraph screen-space water: per-particle front depth, thickness,
    /// separable bilateral filtering and normal-based liquid composition.
    /// </summary>
    public sealed class GuardWaveSSFRFeature : ScriptableRendererFeature
    {
        private Material _material;
        private FluidPass _pass;

        /// <summary>Creates the reusable screen-space water shader and render pass.</summary>
        public override void Create()
        {
            CoreUtils.Destroy(_material);
            _pass = null;
            Shader shader = Resources.Load<Shader>("GuardWaveSSFR");
            if (shader == null) return;
            _material = CoreUtils.CreateEngineMaterial(shader);
            _pass = new FluidPass(_material);
        }

        /// <summary>Enqueues SSFR only for the active gameplay camera and live GPU water.</summary>
        public override void AddRenderPasses(ScriptableRenderer renderer,
            ref RenderingData renderingData)
        {
            if (_pass == null || renderingData.cameraData.cameraType != CameraType.Game ||
                GuardWaveFlipRenderer.ActiveFluid == null) return;
            renderer.EnqueuePass(_pass);
        }

        /// <summary>Disposes the renderer-local shader material.</summary>
        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }

        private sealed class FluidPass : ScriptableRenderPass
        {
            private static readonly int PARTICLES = Shader.PropertyToID("_Particles");
            private static readonly int RAW_DEPTH = Shader.PropertyToID("_SsfrRawDepth");
            private static readonly int THICKNESS = Shader.PropertyToID("_SsfrThickness");
            private static readonly int DEPTH_HORIZONTAL = Shader.PropertyToID("_SsfrDepthHorizontal");
            private static readonly int SMOOTH_DEPTH = Shader.PropertyToID("_SsfrSmoothDepth");
            private readonly Material _material;

            /// <summary>Schedules SSFR after opaque geometry and before transparent objects.</summary>
            public FluidPass(Material material)
            {
                _material = material;
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
                ConfigureInput(ScriptableRenderPassInput.Depth |
                    ScriptableRenderPassInput.Color);
            }

            /// <summary>Records a depth/thickness pair, bilateral blur and screen-space water composite.</summary>
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                GuardWaveFlipFluid fluid = GuardWaveFlipRenderer.ActiveFluid;
                GuardWaveFluidSettings settings = GuardWaveFlipRenderer.ActiveSettings;
                if (fluid == null || settings == null || fluid.ParticleCount == 0) return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData camera = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer) return;

                _material.SetBuffer(PARTICLES, fluid.Particles);
                _material.SetFloat("_Radius",
                    settings.ParticleRenderRadius * settings.SsfrRadiusScale);
                _material.SetFloat("_ThicknessScale", settings.SsfrThicknessScale);
                _material.SetFloat("_BlurRadius", settings.SsfrBlurRadius);
                _material.SetFloat("_DepthSigma", settings.SsfrDepthSigma);
                _material.SetFloat("_Absorption", settings.SsfrAbsorption);
                _material.SetFloat("_Refraction", settings.SsfrRefraction);
                _material.SetFloat("_Opacity", settings.SsfrOpacity);
                _material.SetColor("_ShallowColor", settings.SsfrShallowColor);
                _material.SetColor("_DeepColor", settings.SsfrDeepColor);

                RenderTextureDescriptor descriptor = camera.cameraTargetDescriptor;
                descriptor.width = Mathf.Max(1, Mathf.RoundToInt(
                    descriptor.width * settings.SsfrResolutionScale));
                descriptor.height = Mathf.Max(1, Mathf.RoundToInt(
                    descriptor.height * settings.SsfrResolutionScale));
                descriptor.msaaSamples = 1;
                descriptor.depthBufferBits = 0;
                descriptor.useMipMap = false;
                descriptor.autoGenerateMips = false;
                descriptor.graphicsFormat = GraphicsFormat.R32_SFloat;

                _material.SetVector("_SsfrTexelSize", new Vector4(
                    1f / descriptor.width, 1f / descriptor.height,
                    descriptor.width, descriptor.height));

                TextureHandle front = UniversalRenderer.CreateRenderGraphTexture(
                    graph, descriptor, "GuardWave SSFR front depth", false);
                descriptor.graphicsFormat = GraphicsFormat.R16_SFloat;
                TextureHandle thickness = UniversalRenderer.CreateRenderGraphTexture(
                    graph, descriptor, "GuardWave SSFR thickness", false);
                descriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
                TextureHandle horizontal = UniversalRenderer.CreateRenderGraphTexture(
                    graph, descriptor, "GuardWave SSFR bilateral X", false);
                TextureHandle smooth = UniversalRenderer.CreateRenderGraphTexture(
                    graph, descriptor, "GuardWave SSFR bilateral Y", false);

                DrawParticles(graph, "GuardWave SSFR Depth", front, 0,
                    fluid.ParticleCount, new Color(10000, 0, 0, 0), RAW_DEPTH);
                DrawParticles(graph, "GuardWave SSFR Thickness", thickness, 1,
                    fluid.ParticleCount, Color.clear, THICKNESS);
                DrawFullscreen(graph, "GuardWave SSFR Blur X", horizontal, 2,
                    RAW_DEPTH, DEPTH_HORIZONTAL);
                DrawFullscreen(graph, "GuardWave SSFR Blur Y", smooth, 3,
                    DEPTH_HORIZONTAL, SMOOTH_DEPTH);

                using (IRasterRenderGraphBuilder builder =
                    graph.AddRasterRenderPass<PassData>("GuardWave SSFR Composite",
                        out PassData data))
                {
                    data.Material = _material;
                    data.ShaderPass = 4;
                    data.VertexCount = 3;
                    data.InstanceCount = 1;
                    builder.UseGlobalTexture(SMOOTH_DEPTH, AccessFlags.Read);
                    builder.UseGlobalTexture(THICKNESS, AccessFlags.Read);
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0,
                        AccessFlags.ReadWrite);
                    builder.SetRenderFunc(static (PassData p, RasterGraphContext context) =>
                        context.cmd.DrawProcedural(Matrix4x4.identity, p.Material,
                            p.ShaderPass, MeshTopology.Triangles, p.VertexCount,
                            p.InstanceCount));
                }
            }

            /// <summary>Renders one procedural water particle pass to the specified RFloat surface.</summary>
            private void DrawParticles(RenderGraph graph, string name,
                TextureHandle target, int shaderPass, int count, Color clear, int globalId)
            {
                using (IRasterRenderGraphBuilder builder =
                    graph.AddRasterRenderPass<PassData>(name, out PassData data))
                {
                    data.Material = _material;
                    data.ShaderPass = shaderPass;
                    data.VertexCount = 6;
                    data.InstanceCount = count;
                    data.Clear = true;
                    data.ClearColor = clear;
                    builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(target, globalId);
                    builder.SetRenderFunc(static (PassData p, RasterGraphContext context) =>
                    {
                        context.cmd.ClearRenderTarget(false, true, p.ClearColor);
                        context.cmd.DrawProcedural(Matrix4x4.identity, p.Material,
                            p.ShaderPass, MeshTopology.Triangles, p.VertexCount,
                            p.InstanceCount);
                    });
                }
            }

            /// <summary>Runs one fullscreen bilateral blur pass using the previous global depth texture.</summary>
            private void DrawFullscreen(RenderGraph graph, string name,
                TextureHandle target, int shaderPass, int input, int output)
            {
                using (IRasterRenderGraphBuilder builder =
                    graph.AddRasterRenderPass<PassData>(name, out PassData data))
                {
                    data.Material = _material;
                    data.ShaderPass = shaderPass;
                    data.VertexCount = 3;
                    data.InstanceCount = 1;
                    builder.UseGlobalTexture(input, AccessFlags.Read);
                    builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(target, output);
                    builder.SetRenderFunc(static (PassData p, RasterGraphContext context) =>
                        context.cmd.DrawProcedural(Matrix4x4.identity, p.Material,
                            p.ShaderPass, MeshTopology.Triangles, p.VertexCount,
                            p.InstanceCount));
                }
            }

            private sealed class PassData
            {
                public Material Material;
                public int ShaderPass;
                public int VertexCount;
                public int InstanceCount;
                public bool Clear;
                public Color ClearColor;
            }
        }
    }
}
