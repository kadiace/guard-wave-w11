Shader "Avalanche/Coloured terrain"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Input { float4 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; };
            struct Output { float4 positionCS : SV_POSITION; half4 color : COLOR; float3 normalWS : TEXCOORD0; };
            Output Vert(Input input)
            {
                Output output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = input.color;
                return output;
            }
            half4 Frag(Output input) : SV_Target
            {
                half shade = 0.65 + 0.35 * saturate(dot(normalize(input.normalWS), normalize(float3(-0.3, 1, -0.4))));
                return half4(input.color.rgb * shade, 1);
            }
            ENDHLSL
        }
    }
}
