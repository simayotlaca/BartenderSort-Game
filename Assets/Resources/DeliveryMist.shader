// UV-addressed, alpha-blended UI wisps and dust. Motion is deterministic when sampled for capture.
Shader "LiquidSort/UI/DeliveryMist"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Intensity ("Light Intensity", Range(0,4)) = 1
        _MistTime ("Delivery Clock", Float) = 0
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _Stencil ("Stencil ID", Float) = 0
        [HideInInspector] _StencilOp ("Stencil Operation", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 255
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
        [HideInInspector] _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        // Match the ordinary UGUI blend state for faint wisps and feathered glints.
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "DeliveryMist"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                half4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                half4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            half4 _Color;
            half _Intensity;
            float _MistTime;
            float4 _ClipRect;

            v2f vert(appdata_t input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.worldPosition = input.vertex;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.texcoord = input.texcoord;
                output.color = input.color * _Color;
                return output;
            }

            float mistHash(float2 p)
            {
                float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float mistNoise(float2 p)
            {
                float2 cell = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(mistHash(cell), mistHash(cell + float2(1, 0)), f.x),
                    lerp(mistHash(cell + float2(0, 1)), mistHash(cell + float2(1, 1)), f.x),
                    f.y);
            }

            float mistFbm(float2 p)
            {
                // Three fixed octaves add gentle filament breakup without texture fetches or loops.
                float n = mistNoise(p) * 0.571429;
                p = float2(p.x * 1.6 - p.y * 1.2, p.x * 1.2 + p.y * 1.6) + 7.31;
                n += mistNoise(p) * 0.285714;
                p = float2(p.x * 1.6 - p.y * 1.2, p.x * 1.2 + p.y * 1.6) + 13.17;
                return n + mistNoise(p) * 0.142857;
            }

            half4 frag(v2f input) : SV_Target
            {
                half4 result = input.color;
                half isSmoke = step(1.5, input.texcoord.x);

                if (isSmoke > 0.5h)
                {
                    // Each quad carries its own 0..1 UV and stable seed; Canvas batching cannot
                    // change the shape or make it depend on another object's local coordinates.
                    float seed = floor(input.texcoord.x * 0.5);
                    float2 uv = float2(input.texcoord.x - 2.0 * seed, input.texcoord.y);
                    float2 p = uv * 2.0 - 1.0;
                    float clock = saturate(_MistTime * 2.0);
                    float phase = seed * 1.731 + clock * 1.45;

                    float2 flow = float2(clock * 0.36, -clock * 0.67);
                    float detail = mistFbm(p * float2(3.4, 2.2) + flow
                        + float2(seed * 8.37, seed * 3.19));

                    float density;
                    half lavender;
                    if (input.texcoord.x >= 8.0)
                    {
                        // Seeds 3..5 select tiny detached cloudlets. A broad falloff and rolling
                        // asymmetry keep them translucent and fluffy without a solid round rim.
                        float2 rolled = p + float2(
                            sin(p.y * 3.7 + phase) * 0.105,
                            sin(p.x * 3.1 - phase) * 0.085);
                        float radius = max(0.0, length(rolled) + (detail - 0.5) * 0.22);
                        float softVolume = 1.0 - smoothstep(0.10, 0.86, radius);
                        density = softVolume * lerp(0.50, 0.78,
                            smoothstep(0.22, 0.73, detail));
                        lavender = (half)saturate(0.18 + radius * 0.24
                            + (1.0 - detail) * 0.25);
                    }
                    else
                    {
                        // Most of a wisp quad stays empty; two uneven S-shaped filaments have
                        // feathered tips and never form a disc or filled cloud.
                        float center = sin(p.y * 3.25 + phase) * 0.235
                            + sin(p.y * 6.8 - phase * 0.7) * 0.060;
                        center += (detail - 0.5) * 0.065;
                        float width = lerp(0.060, 0.125, detail);
                        float primary = 1.0 - smoothstep(width * 0.10, width * 2.0,
                            abs(p.x - center));
                        float secondaryCenter = center + 0.22
                            + sin(p.y * 2.4 - phase) * 0.08;
                        float secondary = 1.0 - smoothstep(width * 0.10, width * 1.55,
                            abs(p.x - secondaryCenter));
                        float tips = smoothstep(-0.94, -0.53, p.y)
                            * (1.0 - smoothstep(0.37, 0.93, p.y));
                        float breakup = lerp(0.20, 0.82, smoothstep(0.22, 0.74, detail));
                        density = saturate(primary * 0.74 + secondary * 0.21)
                            * tips * breakup;
                        lavender = (half)saturate(0.35 + p.y * 0.22
                            + (1.0 - detail) * 0.25);
                    }

                    // The guard band stays outside the visible filaments and prevents quad seams.
                    float border = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                    density *= smoothstep(0.0, 0.045, border);

                    half3 warm = half3(1.0h, 0.95h, 0.85h);
                    half3 lilac = half3(0.85h, 0.79h, 1.0h);
                    result.rgb *= lerp(warm, lilac, lavender);
                    result.a *= (half)density;
                }

                result.a = saturate(result.a);
                result.rgb *= max(_Intensity, 0.0h);

                #ifdef UNITY_UI_CLIP_RECT
                result.a *= UnityGet2DClipping(input.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(result.a - 0.001h);
                #endif

                return result;
            }
            ENDCG
        }
    }
    Fallback Off
}
