Shader "GuardWave/FogBackgroundTowerFade"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Opacity ("Wall Opacity", Range(0, 1)) = 0.2
        _EdgeColor ("Outline Color", Color) = (0.12, 0.19, 0.24, 1)
        _EdgeWidth ("Edge Width (World Units)", Float) = 0.18
        _OutlineEnabled ("Draw Tower Edges", Float) = 0
        _WorldBoundsMin ("World Bounds Min", Vector) = (0, 0, 0, 0)
        _WorldBoundsMax ("World Bounds Max", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _EdgeColor;
                float4 _WorldBoundsMin;
                float4 _WorldBoundsMax;
                float _Opacity;
                float _EdgeWidth;
                float _OutlineEnabled;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                Light mainLight = GetMainLight();
                half lambert = saturate(dot(normalize(input.normalWS), mainLight.direction));
                baseColor.rgb *= (0.55h + 0.45h * lambert);
                baseColor.a *= (half)_Opacity;

                if (_OutlineEnabled > 0.5)
                {
                    float3 fromMin = abs(input.positionWS - _WorldBoundsMin.xyz);
                    float3 fromMax = abs(input.positionWS - _WorldBoundsMax.xyz);
                    float3 edgeDistance = min(fromMin, fromMax);
                    float width = max(0.001, _EdgeWidth);
                    bool verticalEdge = edgeDistance.x < width && edgeDistance.z < width;
                    bool horizontalEdge = edgeDistance.y < width &&
                                          (edgeDistance.x < width || edgeDistance.z < width);
                    if (verticalEdge || horizontalEdge)
                    {
                        baseColor.rgb = _EdgeColor.rgb;
                        baseColor.a *= _EdgeColor.a;
                    }
                }
                return baseColor;
            }
            ENDHLSL
        }
    }
}
