using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace GuardWave
{
    /// <summary>Renders a stable, screen-space hover silhouette over the SSFR water surface.</summary>
    public sealed class GuardWaveOutlineFeature : ScriptableRendererFeature
    {
        [SerializeField] private Material _yellowMaterial;
        private OutlinePass _pass;

        public override void Create()
        {
            _pass = _yellowMaterial == null ? null : new OutlinePass(_yellowMaterial);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer,
            ref RenderingData renderingData)
        {
            if (_pass != null) renderer.EnqueuePass(_pass);
        }

        private sealed class OutlinePass : ScriptableRenderPass
        {
            private static readonly List<ShaderTagId> ShaderTags = new List<ShaderTagId>
            {
                new ShaderTagId("SRPDefaultUnlit"),
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly")
            };
            private readonly Material _material;

            public OutlinePass(Material material)
            {
                _material = material;
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalRenderingData rendering = frameData.Get<UniversalRenderingData>();
                UniversalCameraData camera = frameData.Get<UniversalCameraData>();
                UniversalLightData lights = frameData.Get<UniversalLightData>();

                DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(ShaderTags,
                    rendering, camera, lights, camera.defaultOpaqueSortFlags);
                drawing.overrideMaterial = _material;
                FilteringSettings filter = new FilteringSettings(RenderQueueRange.opaque)
                {
                    renderingLayerMask = 2u
                };
                RendererListHandle outlines = graph.CreateRendererList(
                    new RendererListParams(rendering.cullResults, drawing, filter));
                using IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<PassData>(
                    "GuardWave Hover Outline", out PassData pass);
                pass.Outlines = outlines;
                builder.UseRendererList(outlines);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    context.cmd.DrawRendererList(data.Outlines));
            }

            private sealed class PassData
            {
                public RendererListHandle Outlines;
            }
        }
    }
