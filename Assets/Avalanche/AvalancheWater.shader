Shader "Avalanche/Flow surface"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Input { float4 positionOS : POSITION; half4 color : COLOR; float2 flow : TEXCOORD0; };
            struct Output { float4 positionCS : SV_POSITION; half4 color : COLOR; float3 positionWS : TEXCOORD0; float2 flow : TEXCOORD1; };
            Output Vert(Input input)
            {
                Output output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.color = input.color;
                output.flow = input.flow;
                return output;
            }
            half4 Frag(Output input) : SV_Target
            {
                clip(input.color.a - 0.01);
                float2 uv = input.positionWS.xz - input.flow * _Time.y * 0.25;
                float grain = sin(uv.x * 12.7 + uv.y * 7.1) * sin(uv.y * 13.1 - uv.x * 9.9);
                return half4(input.color.rgb * (0.94 + grain * 0.035), 1);
            }
            ENDHLSL
        }
    }
}
