// Legacy sprites pass through their encoded colour and intensity. Baked profiles use the interior mask for GPU
// highlights, without pixel readback or material clones.
Shader "LiquidSort/GlassLight"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
        [HideInInspector] _UseProfileMask ("Use Profile Mask", Float) = 0
        [HideInInspector] _ProfileMaskRect ("Profile Mask Rect", Vector) = (0,0,1,1)
        [HideInInspector] _ProfileLightRect ("Profile Light Rect", Vector) = (0,0,1,1)
        [HideInInspector] _PrimaryGloss ("Primary Gloss", Vector) = (-0.46,0.26,0.55,0.42)
        [HideInInspector] _SecondaryGloss ("Secondary Gloss", Vector) = (0.54,0.12,0.42,0)
        [HideInInspector] _ShoulderGloss ("Shoulder Gloss", Vector) = (0.84,0,0,0)
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

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment GlassLightFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            float _UseProfileMask;
            float4 _ProfileMaskRect;
            float4 _ProfileLightRect;
            float4 _PrimaryGloss;
            float4 _SecondaryGloss;
            float4 _ShoulderGloss;

            inline float GlassGauss(float value, float sigma)
            {
                float q = value / max(sigma, 1e-4);
                return exp(-q * q);
            }

            fixed4 GlassLightFrag(v2f IN) : SV_Target
            {
                // I keep the legacy default for shared-material users such as MechanicRevealPresenter, which
                // clears its property block.
                if (_UseProfileMask < 0.5)
                    return SpriteFrag(IN);

                // The baked mask holds coverage, not distance, so it cannot reproduce the old broad wall fade.
                // Campaign rim/fill stay zero unless a distance channel is baked.
                float coverage = SampleSpriteTexture(IN.texcoord).a;
                float2 localPosition = _ProfileMaskRect.xy
                                     + IN.texcoord * _ProfileMaskRect.zw;
                float2 lightUv = (localPosition - _ProfileLightRect.xy)
                               / max(_ProfileLightRect.zw, float2(1e-4, 1e-4));
                float u = lightUv.x * 2.0 - 1.0;
                float v = lightUv.y;
                // I split reflections into offset upper-left and lower-right lobes so they do not form a
                // full-height tube.
                float primaryLengthwise = smoothstep(0.42, 0.54, v)
                                        * (1.0 - smoothstep(0.80, 0.96, v));
                float secondaryLengthwise = smoothstep(0.15, 0.28, v)
                                          * (1.0 - smoothstep(0.52, 0.72, v));
                float streakLengthwise = smoothstep(0.62, 0.69, v)
                                       * (1.0 - smoothstep(0.84, 0.92, v));

                float primary = GlassGauss(u - _PrimaryGloss.x,
                    _PrimaryGloss.y) * _PrimaryGloss.z;
                float streakX = _PrimaryGloss.x + _PrimaryGloss.y * 0.55;
                // I lean and taper the short highlight slightly for a less rigid shape.
                float streakLean = (v - 0.76) * 0.10;
                float streakTaper = lerp(0.60, 1.0,
                    smoothstep(0.62, 0.86, v));
                float streak = GlassGauss(u - (streakX + streakLean),
                    _PrimaryGloss.y * 0.14 * streakTaper) * _PrimaryGloss.w;
                float secondary = GlassGauss(u - _SecondaryGloss.x,
                    max(0.02, _SecondaryGloss.y)) * _SecondaryGloss.z;
                float shoulder = GlassGauss(u - _PrimaryGloss.x * 0.75, 0.20)
                               * GlassGauss(v - _ShoulderGloss.x, 0.055)
                               * _SecondaryGloss.w;

                // I use a neutral silver key so this pass does not add another blue layer over the liquid.
                const float3 sky = float3(0.82, 0.90, 1.0);
                const float3 warm = float3(1.0, 0.97, 0.92);
                float3 lit = coverage
                           * (sky * (primary * primaryLengthwise
                                   + secondary * secondaryLengthwise)
                              + warm * (streak * streakLengthwise + shoulder));
                float peak = max(lit.r, max(lit.g, lit.b));
                float active = step(1.0 / 255.0, peak);
                lit *= active;
                peak *= active;

                // I match the old texture encoding and SpriteFrag premultiplication, applying renderer alpha
                // exactly once.
                float encodedAlpha = saturate(peak) * IN.color.a;
                float3 encodedRgb = saturate(lit / max(peak, 1e-6));
                return fixed4(encodedRgb * IN.color.rgb * encodedAlpha, encodedAlpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
