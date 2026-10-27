Shader "Hunnedalen/LateStageRock"
{
    Properties { _BaseColor("Earth and rock tint", Color) = (0.48,0.43,0.34,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Cull Off
        Pass
        {
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; half3 normalWS : TEXCOORD1; half4 color : COLOR; half fog : TEXCOORD2; UNITY_VERTEX_OUTPUT_STEREO };
            Varyings Vert(Attributes a)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(a);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(a.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(a.normalOS);
                o.color = a.color;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }
            half Hash(float3 p) { return frac(sin(dot(floor(p),float3(12.9898,78.233,45.164)))*43758.5453); }
            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                clip(i.color.a - 0.015h);
                half3 normal = normalize(i.normalWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half grain = lerp(0.72h,1.16h,Hash(i.positionWS * 3.5));
                half strata = 0.91h + 0.09h * sin(i.positionWS.y * 2.1 + i.positionWS.x * 0.35);
                half3 base = _BaseColor.rgb * i.color.rgb * grain * strata;
                half3 lighting = max(SampleSH(normal),half3(0.18,0.18,0.18)) + sun.color * saturate(dot(normal,sun.direction)) * sun.shadowAttenuation;
                return half4(MixFog(base * lighting, i.fog),1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            struct A { float4 positionOS:POSITION; float3 normalOS:NORMAL; half4 color:COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS:SV_POSITION; half alpha:TEXCOORD0; };
            V ShadowVert(A a)
            {
                UNITY_SETUP_INSTANCE_ID(a);
                V o;
                float3 p=TransformObjectToWorld(a.positionOS.xyz);
                float3 n=TransformObjectToWorldNormal(a.normalOS);
                o.positionCS=TransformWorldToHClip(ApplyShadowBias(p,n,_LightDirection));
                #if UNITY_REVERSED_Z
                    o.positionCS.z=min(o.positionCS.z,o.positionCS.w*UNITY_NEAR_CLIP_VALUE);
                #else
                    o.positionCS.z=max(o.positionCS.z,o.positionCS.w*UNITY_NEAR_CLIP_VALUE);
                #endif
                o.alpha=a.color.a; return o;
            }
            half4 ShadowFrag(V i):SV_Target { clip(i.alpha-0.015h); return 0; }
            ENDHLSL
        }
    }
}
