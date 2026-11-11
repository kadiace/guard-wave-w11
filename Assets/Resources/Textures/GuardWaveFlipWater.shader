Shader "GuardWave/FlipWater"
{
    Properties
    {
        _WaterColor ("Water tint", Color) = (0.08,0.48,0.92,0.65)
        _Radius ("Particle radius", Range(0.08,1)) = 0.43
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "GPUWaterParticles"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Particle
            {
                float4 position;
                float4 velocity;
                float4 affineX;
                float4 affineY;
                float4 affineZ;
            };
            StructuredBuffer<Particle> _Particles;
            float4 _WaterColor;
            float _Radius;
            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float active : TEXCOORD1;
            };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                Particle p = _Particles[input.instanceID];
                float2 vertex = input.vertexID == 0 ? float2(-1,-1) :
                    input.vertexID == 1 ? float2(-1,1) :
                    input.vertexID == 2 ? float2(1,1) :
                    input.vertexID == 3 ? float2(-1,-1) :
                    input.vertexID == 4 ? float2(1,1) : float2(1,-1);
                float3 facing = normalize(_WorldSpaceCameraPos - p.position.xyz);
                float3 right = normalize(cross(float3(0,1,0),facing));
                float3 up = normalize(cross(facing,right));
                float3 world = p.position.xyz + (right*vertex.x + up*vertex.y)*_Radius;
                o.positionCS = TransformWorldToHClip(world);
                o.uv = vertex;
                o.active = p.position.w;
                return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                if (i.active < 0.5) discard;
                float r2 = dot(i.uv,i.uv);
                if (r2 > 1) discard;
                float lighting = sqrt(saturate(1-r2));
                float foam = smoothstep(0.25,0.98,r2) * 0.18;
                float3 color = _WaterColor.rgb * (0.62+0.38*lighting) +
                    foam * float3(0.8,0.95,1);
                return half4(color, _WaterColor.a * saturate((1-r2)*3.0));
            }
            ENDHLSL
        }
    }
}
