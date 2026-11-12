Shader "Hidden/GuardWave/SSFR"
{
    Properties
    {
        _ShallowColor ("Shallow water", Color) = (0.13,0.60,0.87,1)
        _DeepColor ("Deep water", Color) = (0.015,0.24,0.52,1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

        struct Particle
        {
            float4 position;
            float4 velocity;
            float4 affineX;
            float4 affineY;
            float4 affineZ;
        };
        StructuredBuffer<Particle> _Particles;
        TEXTURE2D_X(_SsfrRawDepth);
        TEXTURE2D_X(_SsfrThickness);
        TEXTURE2D_X(_SsfrDepthHorizontal);
        TEXTURE2D_X(_SsfrSmoothDepth);
        float _Radius, _ThicknessScale, _BlurRadius, _DepthSigma;
        float _DebugExitZ;
        float _Absorption, _Refraction, _Opacity;
        float4 _ShallowColor, _DeepColor, _SsfrTexelSize;

        struct ParticleVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 disc : TEXCOORD0;
            float centerEyeDepth : TEXCOORD1;
            float alive : TEXCOORD2;
            float diagnosticSpeed : TEXCOORD3;
            float diagnosticZone : TEXCOORD4;
        };

        ParticleVaryings ParticleVert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
        {
            Particle p = _Particles[iid];
            float2 corner = vid == 0 ? float2(-1,-1) :
                vid == 1 ? float2(-1,1) :
                vid == 2 ? float2(1,1) :
                vid == 3 ? float2(-1,-1) :
                vid == 4 ? float2(1,1) : float2(1,-1);
            ParticleVaryings o;
            float3 centerVS = mul(UNITY_MATRIX_V, float4(p.position.xyz,1)).xyz;
            float3 vertexVS = centerVS + float3(corner * _Radius,0);
            o.positionCS = mul(UNITY_MATRIX_P, float4(vertexVS,1));
            o.disc = corner;
            o.centerEyeDepth = -centerVS.z;
            o.alive = p.position.w;
            o.diagnosticSpeed = p.velocity.z;
            o.diagnosticZone = p.position.w == 1.0 && abs(p.position.x) <= 8.0 &&
                p.position.z >= _DebugExitZ && p.position.z <= _DebugExitZ + 6.0 ? 1.0 : 0.0;
            return o;
        }

        float DiscFront(ParticleVaryings i)
        {
            clip(i.alive - 0.5);
            float r2 = dot(i.disc,i.disc);
            clip(1.0-r2);
            return max(0.001, i.centerEyeDepth - _Radius*sqrt(saturate(1.0-r2)));
        }

        float4 FrontDepthFrag(ParticleVaryings i) : SV_Target
        {
            return float4(DiscFront(i),0,0,1);
        }

        float4 ThicknessFrag(ParticleVaryings i) : SV_Target
        {
            DiscFront(i);
            float thickness = 2.0*_Radius*sqrt(saturate(1.0-dot(i.disc,i.disc)));
            return float4(thickness*_ThicknessScale,0,0,1);
        }

        struct FullscreenVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };
        FullscreenVaryings FullscreenVert(uint id : SV_VertexID)
        {
            FullscreenVaryings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(id);
            o.uv = GetFullScreenTriangleTexCoord(id);
            return o;
        }

        float ReadDepth(float2 uv, bool fromHorizontal)
        {
            return fromHorizontal
                ? SAMPLE_TEXTURE2D_X(_SsfrDepthHorizontal, sampler_LinearClamp, uv).r
                : SAMPLE_TEXTURE2D_X(_SsfrRawDepth, sampler_LinearClamp, uv).r;
        }

        float SmoothDepth(float2 uv, float2 direction, bool fromHorizontal)
        {
            float original = ReadDepth(uv,fromHorizontal);
            if (original > 9000.0) return 10000.0;
            float weighted = 0.0, sum = 0.0;
            [unroll]
            for (int j=-3;j<=3;j++)
            {
                float2 offset = direction * j * _SsfrTexelSize.xy * _BlurRadius;
                float sampleDepth = ReadDepth(saturate(uv+offset), fromHorizontal);
                if (sampleDepth > 9000.0) continue;
                float proximity = j*j/8.0;
                float difference = (sampleDepth-original)/max(0.03,_DepthSigma);
                float w = exp(-proximity - 0.5*difference*difference);
                weighted += w*sampleDepth;
                sum += w;
            }
            return weighted / max(0.00001,sum);
        }

        float4 BlurXFrag(FullscreenVaryings i) : SV_Target
        {
            return float4(SmoothDepth(i.uv, float2(1,0), false),0,0,1);
        }
        float4 BlurYFrag(FullscreenVaryings i) : SV_Target
        {
            return float4(SmoothDepth(i.uv, float2(0,1), true),0,0,1);
        }

        float SceneEyeDepth(float2 uv)
        {
            float raw = SampleSceneDepth(uv);
            float depth = LinearEyeDepth(raw, _ZBufferParams);
            #if UNITY_REVERSED_Z
                float ortho01 = 1.0 - raw;
            #else
                float ortho01 = raw;
            #endif
            float ortho = lerp(_ProjectionParams.y,_ProjectionParams.z,ortho01);
            return lerp(depth,ortho,unity_OrthoParams.w);
        }

        half4 CompositeFrag(FullscreenVaryings i) : SV_Target
        {
            float2 uv = i.uv;
            float eye = SAMPLE_TEXTURE2D_X(_SsfrSmoothDepth,sampler_LinearClamp,uv).r;
            if (eye > 9000.0 || eye >= SceneEyeDepth(uv)-0.015) discard;
            float thickness = SAMPLE_TEXTURE2D_X(_SsfrThickness,sampler_LinearClamp,uv).r;
            if (thickness < 0.001) discard;

            float2 onePixel = _SsfrTexelSize.xy;
            float x1 = SAMPLE_TEXTURE2D_X(_SsfrSmoothDepth,sampler_LinearClamp,
                saturate(uv+float2(onePixel.x,0))).r;
            float x0 = SAMPLE_TEXTURE2D_X(_SsfrSmoothDepth,sampler_LinearClamp,
                saturate(uv-float2(onePixel.x,0))).r;
            float y1 = SAMPLE_TEXTURE2D_X(_SsfrSmoothDepth,sampler_LinearClamp,
                saturate(uv+float2(0,onePixel.y))).r;
            float y0 = SAMPLE_TEXTURE2D_X(_SsfrSmoothDepth,sampler_LinearClamp,
                saturate(uv-float2(0,onePixel.y))).r;
            float2 gradient = float2(
                x1>9000 || x0>9000 ? 0 : (x1-x0),
                y1>9000 || y0>9000 ? 0 : (y1-y0));
            float3 normal = normalize(float3(-gradient*1.8,1));
            float2 refractUv = saturate(uv + normal.xy*_Refraction);
            half3 background = SampleSceneColor(refractUv);
            float deep = saturate(thickness*0.27);
            half3 color = lerp(_ShallowColor.rgb,_DeepColor.rgb,deep);
            float fresnel = pow(1.0-saturate(normal.z),2.0);
            float glint = pow(saturate(dot(normal,normalize(float3(0.45,0.5,0.75)))),16.0);
            half3 water = lerp(background,color,saturate(0.55+deep*0.3)) +
                glint*0.22 + fresnel*0.12;
            float alpha = saturate((1.0-exp(-thickness*_Absorption))*_Opacity);
            return half4(water,alpha);
        }
        half4 OutflowDebugFrag(ParticleVaryings i) : SV_Target
        {
            clip(i.diagnosticZone - 0.5);
            clip(1.0 - dot(i.disc, i.disc));
            float2 screenUV = GetNormalizedScreenSpaceUV(i.positionCS);
            clip(SceneEyeDepth(screenUV) - i.centerEyeDepth + 0.1);
            float3 color = i.diagnosticSpeed > 0.1 ? float3(1.0,0.20,0.10) :
                i.diagnosticSpeed < -0.1 ? float3(0.10,0.70,1.0) :
                float3(1.0,0.85,0.12);
            return half4(color, 0.78);
        }
        ENDHLSL

        Pass
        {
            Name "WaterFrontDepth"
            ZWrite Off ZTest Always
            BlendOp Min
            Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ParticleVert
            #pragma fragment FrontDepthFrag
            ENDHLSL
        }
        Pass
        {
            Name "WaterThickness"
            ZWrite Off ZTest Always
            Blend One One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ParticleVert
            #pragma fragment ThicknessFrag
            ENDHLSL
        }
        Pass
        {
            Name "BilateralHorizontal"
            ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex FullscreenVert
            #pragma fragment BlurXFrag
            ENDHLSL
        }
        Pass
        {
            Name "BilateralVertical"
            ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex FullscreenVert
            #pragma fragment BlurYFrag
            ENDHLSL
        }
        Pass
        {
            Name "WaterComposite"
            ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex FullscreenVert
            #pragma fragment CompositeFrag
            ENDHLSL
        }
        Pass
        {
            Name "OutflowVelocityDebug"
            ZWrite Off ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ParticleVert
            #pragma fragment OutflowDebugFrag
            ENDHLSL
        }
    }
}
