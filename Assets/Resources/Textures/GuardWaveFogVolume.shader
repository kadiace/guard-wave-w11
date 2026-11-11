Shader "GuardWave/FogVolume"
{
    Properties
    {
        _FogTint ("Fog tint", Color) = (0.69,0.81,0.60,1)
        _FogExtinction ("Extinction", Range(0.1,3)) = 1.3
        _FogOpacity ("Opacity", Range(0,1)) = 1
        _FogShadowStrength ("Volume self shadow", Range(0,6)) = 2.1
        _FogDetailStrength ("Billow detail", Range(0,1)) = 0.65
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE3D(_FogDensityTex);
            SAMPLER(sampler_FogDensityTex);
            float4 _FogTint;
            float _FogExtinction, _FogOpacity, _FogShadowStrength, _FogDetailStrength;
            float3 _FogVolumeMin, _FogVolumeMax;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes a)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(a.positionOS.xyz);
                return o;
            }

            // Trilinear 3D value noise gives rolling surface detail without
            // another GPU texture or affecting the underlying fluid transport.
            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }
            float Noise3D(float3 p)
            {
                float3 cell = floor(p), t = frac(p);
                t = t*t*(3.0 - 2.0*t);
                float a = lerp(Hash31(cell + float3(0,0,0)), Hash31(cell + float3(1,0,0)), t.x);
                float b = lerp(Hash31(cell + float3(0,1,0)), Hash31(cell + float3(1,1,0)), t.x);
                float c = lerp(Hash31(cell + float3(0,0,1)), Hash31(cell + float3(1,0,1)), t.x);
                float d = lerp(Hash31(cell + float3(0,1,1)), Hash31(cell + float3(1,1,1)), t.x);
                return lerp(lerp(a,b,t.y), lerp(c,d,t.y), t.z);
            }
            half4 Frag(Varyings i) : SV_Target
            {
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                #if UNITY_REVERSED_Z
                    float nearDepth = 1.0, sampleDepth = 0.25;
                #else
                    float nearDepth = 0.0, sampleDepth = 0.75;
                #endif
                // The same depth-aware ray box intersection handles ISO and perspective.
                float3 rayOrigin = ComputeWorldSpacePosition(uv, nearDepth, UNITY_MATRIX_I_VP);
                float3 farPoint = ComputeWorldSpacePosition(uv, sampleDepth, UNITY_MATRIX_I_VP);
                float3 dir = normalize(farPoint - rayOrigin);
                float3 safeDir = float3(
                    abs(dir.x) < 0.0001 ? (dir.x < 0 ? -0.0001 : 0.0001) : dir.x,
                    abs(dir.y) < 0.0001 ? (dir.y < 0 ? -0.0001 : 0.0001) : dir.y,
                    abs(dir.z) < 0.0001 ? (dir.z < 0 ? -0.0001 : 0.0001) : dir.z);
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

                const int Steps = 48;
                float stride = (endT - startT) / Steps;
                float jitter = frac(sin(dot(i.positionCS.xy,float2(12.9898,78.233))) * 43758.5453);
                float transmittance = 1.0;
                float3 scattered = 0.0;
                Light mainLight = GetMainLight();
                float3 lightDir = normalize(mainLight.direction);
                float3 lightColor = mainLight.color.rgb;
                float lighting = 1.0;

                [loop] for (int k=0; k<Steps; ++k)
                {
                    float3 p = rayOrigin + dir * (startT + (k+jitter)*stride);
                    float3 coord = (p-_FogVolumeMin)/(_FogVolumeMax-_FogVolumeMin);
                    float density = SAMPLE_TEXTURE3D_LOD(_FogDensityTex, sampler_FogDensityTex, saturate(coord), 0).r;
                    if (density < 0.001) continue;

                    float3 noisePos = p*0.82 + float3(0.03,0.06,-0.11)*_Time.y;
                    float billow = Noise3D(noisePos)*0.72 + Noise3D(noisePos*2.2)*0.28;
                    density *= lerp(1.0, 0.67 + 0.75*billow, saturate(_FogDetailStrength));

                    // Reuse lighting for adjacent samples: three short optical
                    // depth taps every other step instead of expensive full rays.
                    if ((k & 1) == 0)
                    {
                        float occlusion = 0.0;
                        [unroll] for (int j=1; j<=3; ++j)
                        {
                            float3 lp = p + lightDir*(j*1.1);
                            float3 lc = (lp-_FogVolumeMin)/(_FogVolumeMax-_FogVolumeMin);
                            if (all(lc >= 0.0) && all(lc <= 1.0))
                                occlusion += SAMPLE_TEXTURE3D_LOD(
                                    _FogDensityTex,sampler_FogDensityTex,lc,0).r * 1.1;
                        }
                        lighting = exp(-occlusion * max(0.0,_FogShadowStrength));
                    }
                    float alpha = 1.0 - exp(-density * _FogExtinction * stride);
                    // Dark billow interiors, glowing translucent edges and
                    // a minimum of diffuse ambient light, even in shadow.
                    float3 lightTint = lerp(float3(0.82,0.84,0.82),lightColor,0.3);
                    float3 tint = _FogTint.rgb * lightTint * (0.26 + 0.74*lighting);
                    scattered += transmittance * alpha * tint;
                    transmittance *= (1.0-alpha);
                    if (transmittance < 0.015) break;
                }
                float opacity = saturate(_FogOpacity);
                return half4(scattered*opacity, (1.0-transmittance)*opacity);
            }
            ENDHLSL
        }
    }
}
