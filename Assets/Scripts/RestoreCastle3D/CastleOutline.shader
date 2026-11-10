Shader "RestoreCastle3D/Outline"
{
    Properties
    {
        _Outline_Color ("Outline Color", Color) = (1, 1, 0, 1)
        _Outline_Thickness ("Outline Thickness (Pixels)", Float) = 5
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _Outline_Color;
            float _Outline_Thickness;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 clip = TransformWorldToHClip(positionWS);
                float4 normalClip = TransformWorldToHClip(positionWS + normalWS * 0.1) - clip;
                float2 direction = normalize(normalClip.xy * clip.w - clip.xy * normalClip.w + 1e-6);
                clip.xy += direction * _Outline_Thickness * 2.0 / _ScreenParams.xy * clip.w;
                output.positionCS = clip;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target { return _Outline_Color; }
            ENDHLSL
        }
    }
}
