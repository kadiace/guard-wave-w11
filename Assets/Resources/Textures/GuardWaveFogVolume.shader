Shader "GuardWave/FogVolume"
{
    Properties
    {
        _FogTint ("Fog tint", Color) = (0.81,0.87,0.89,1)
        _FogExtinction ("Extinction", Range(0.1,3)) = 0.95
        _FogOpacity ("Opacity", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+80" "RenderType"="Transparent" }
        Pass
        {
            Name "VolumetricFog"
            Tags { "LightMode"="UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Front
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE3D(_FogDensityTex);
            SAMPLER(sampler_FogDensityTex);
            float4 _FogTint;
            float _FogExtinction, _FogOpacity;
            float3 _FogVolumeMin, _FogVolumeMax;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes a)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(a.positionOS.xyz);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                #if UNITY_REVERSED_Z
                    float nearDepth = 1.0, sampleDepth = 0.25;
                #else
                    float nearDepth = 0.0, sampleDepth = 0.75;
                #endif
                // Reconstruct per-pixel rays: works for perspective AND orthographic ISO cameras.
                float3 rayOrigin = ComputeWorldSpacePosition(uv, nearDepth, UNITY_MATRIX_I_VP);
                float3 farPoint = ComputeWorldSpacePosition(uv, sampleDepth, UNITY_MATRIX_I_VP);
                float3 dir = normalize(farPoint - rayOrigin);
                float3 safeDir = float3(
                    abs(dir.x) < 0.0001 ? 0.0001 : dir.x,
                    abs(dir.y) < 0.0001 ? 0.0001 : dir.y,
                    abs(dir.z) < 0.0001 ? 0.0001 : dir.z);
                float3 t0 = (_FogVolumeMin - rayOrigin) / safeDir;
                float3 t1 = (_FogVolumeMax - rayOrigin) / safeDir;
                float3 lo = min(t0,t1), hi = max(t0,t1);
                float startT = max(0.0, max(lo.x, max(lo.y,lo.z)));
                float endT = min(hi.x, min(hi.y,hi.z));
                if (endT <= startT) discard;

                float rawDepth = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                    if (rawDepth > 0.00001)
                #else
                    if (rawDepth < 0.99999)
                #endif
                {
                    float3 scenePoint = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                    endT = min(endT, dot(scenePoint - rayOrigin, dir));
                }
                if (endT <= startT) discard;

                // Independent scene depth clipping keeps nearby objects legible.
                const int Steps = 48;
                float length = endT - startT;
                float stride = length / Steps;
                float jitter = frac(sin(dot(i.positionCS.xy, float2(12.9898,78.233))) * 43758.5453);
                float transmittance = 1.0;
                float3 scattered = 0;
                [loop] for (int k = 0; k < Steps; ++k)
                {
                    float3 p = rayOrigin + dir*(startT + (k+jitter)*stride);
                    float3 coord = (p - _FogVolumeMin)/(_FogVolumeMax-_FogVolumeMin);
                    float density = SAMPLE_TEXTURE3D(_FogDensityTex, sampler_FogDensityTex, saturate(coord)).r;
                    float detail = 0.87 + 0.13*sin(p.x*1.43 + p.y*1.8 + p.z*0.7 + _Time.y*0.35);
                    density *= detail;
                    float alpha = 1.0 - exp(-density * _FogExtinction * stride);
                    float3 tint = _FogTint.rgb * (0.86 + 0.14*saturate(p.y - _FogVolumeMin.y));
                    scattered += transmittance * alpha * tint;
                    transmittance *= 1.0 - alpha;
                    if (transmittance < 0.015) break;
                }
                float opacity = saturate(_FogOpacity);
                return half4(scattered*opacity, (1.0-transmittance)*opacity);
            }
            ENDHLSL
        }
    }
}
