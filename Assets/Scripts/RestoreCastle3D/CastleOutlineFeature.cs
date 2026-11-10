using System.Collections.Generic;

using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace RestoreCastle3D
{
    /// <summary>호버 기물과 접촉 이웃을 각각 다른 렌더링 레이어의 외곽선으로 그린다.</summary>
    public sealed class CastleOutlineFeature : ScriptableRendererFeature
    {
        [SerializeField] private Material _yellowMaterial;
        [SerializeField] private Material _cyanMaterial;

        private OutlinePass _yellowPass;
        private OutlinePass _cyanPass;
        private Material _maskMaterial;

        public override void Create()
        {
            CoreUtils.Destroy(_maskMaterial);
            _maskMaterial = null;
            _yellowPass = null;
            _cyanPass = null;
            if (_yellowMaterial == null || _cyanMaterial == null) return;

            _maskMaterial = new Material(_yellowMaterial) { hideFlags = HideFlags.HideAndDontSave };
            _maskMaterial.SetFloat("_Outline_Thickness", 0f);
            _yellowPass = new OutlinePass(2u, _yellowMaterial, _maskMaterial, 1 << 3);
            _cyanPass = new OutlinePass(4u, _cyanMaterial, _maskMaterial, 1 << 4);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_yellowPass == null) return;
            renderer.EnqueuePass(_cyanPass);
            renderer.EnqueuePass(_yellowPass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_maskMaterial);
            _maskMaterial = null;
        }

        private sealed class OutlinePass : ScriptableRenderPass
        {
            private static readonly List<ShaderTagId> ShaderTags = new List<ShaderTagId>
            {
                new ShaderTagId("SRPDefaultUnlit"),
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly")
            };

            private readonly uint _layer;
            private readonly Material _material;
            private readonly Material _maskMaterial;
            private readonly RenderStateBlock _maskState;
            private readonly RenderStateBlock _outlineState;

            /// <summary>렌더링 레이어별 스텐실 마스크와 외곽선 패스의 상태를 구성한다.</summary>
            public OutlinePass(uint layer, Material material, Material maskMaterial, int stencilBit)
            {
                _layer = layer;
                _material = material;
                _maskMaterial = maskMaterial;
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

                StencilState maskStencil = new StencilState(true, (byte)stencilBit,
                    (byte)stencilBit, CompareFunction.Always, StencilOp.Replace,
                    StencilOp.Keep, StencilOp.Keep);
                StencilState outlineStencil = new StencilState(true, (byte)stencilBit,
                    0, CompareFunction.NotEqual, StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
                BlendState blend = new BlendState();
                blend.blendState0 = new RenderTargetBlendState { writeMask = (ColorWriteMask)0 };
                _maskState = new RenderStateBlock(RenderStateMask.Depth | RenderStateMask.Stencil |
                    RenderStateMask.Raster | RenderStateMask.Blend)
                {
                    depthState = new DepthState(false, CompareFunction.Always),
                    stencilReference = stencilBit,
                    stencilState = maskStencil,
                    rasterState = new RasterState(CullMode.Off),
                    blendState = blend
                };
                _outlineState = new RenderStateBlock(RenderStateMask.Depth | RenderStateMask.Stencil |
                    RenderStateMask.Raster)
                {
                    depthState = new DepthState(false, CompareFunction.LessEqual),
                    stencilReference = stencilBit,
                    stencilState = outlineStencil,
                    rasterState = new RasterState(CullMode.Off)
                };
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalRenderingData rendering = frameData.Get<UniversalRenderingData>();
                UniversalCameraData camera = frameData.Get<UniversalCameraData>();
                UniversalLightData lights = frameData.Get<UniversalLightData>();
                RendererListHandle mask = MakeList(graph, rendering, camera, lights,
                    _maskMaterial, _maskState);
                RendererListHandle outline = MakeList(graph, rendering, camera, lights,
                    _material, _outlineState);

                using IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<PassData>(
                    "Castle Outline", out PassData data);
                data.Mask = mask;
                data.Outline = outline;
                builder.UseRendererList(mask);
                builder.UseRendererList(outline);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(pass.Mask);
                    context.cmd.DrawRendererList(pass.Outline);
                });
            }

            /// <summary>현재 레이어의 원래 지오메트리를 지정된 재질과 렌더 상태로 그릴 목록을 만든다.</summary>
            private RendererListHandle MakeList(RenderGraph graph, UniversalRenderingData rendering,
                UniversalCameraData camera, UniversalLightData lights, Material material,
                RenderStateBlock state)
            {
                DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(ShaderTags,
                    rendering, camera, lights, camera.defaultOpaqueSortFlags);
                drawing.overrideMaterial = material;
                FilteringSettings filter = new FilteringSettings(RenderQueueRange.opaque)
                {
                    renderingLayerMask = _layer
                };
                RendererListParams parameters = new RendererListParams(rendering.cullResults,
                    drawing, filter)
                {
                    tagValues = new NativeArray<ShaderTagId>(new[] { ShaderTagId.none }, Allocator.Temp),
                    stateBlocks = new NativeArray<RenderStateBlock>(new[] { state }, Allocator.Temp)
                };
                return graph.CreateRendererList(parameters);
            }

            private sealed class PassData
            {
                public RendererListHandle Mask;
                public RendererListHandle Outline;
            }
        }
    }
}
