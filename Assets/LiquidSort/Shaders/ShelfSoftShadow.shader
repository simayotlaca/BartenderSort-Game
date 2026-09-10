// I expand the quad slightly, then blur and offset source alpha for a soft downward shadow and amber shelf bounce.
Shader "LiquidSort/ShelfSoftShadow"
{
    Properties
    {
        [PerRendererData] _MainTex ("Shelf Silhouette", 2D) = "white" {}
        _Color ("Shadow Color", Color) = (0.0588,0.0941,0.1882,0.25)
        _OffsetPx ("Downward Offset (screen pixels)", Range(0,16)) = 5
        _BlurPx ("Blur Radius (screen pixels)", Range(1,16)) = 8
        _ExpansionPx ("Quad Expansion (screen pixels)", Range(8,32)) = 22
        _WarmGlowColor ("Under-shelf Light Color", Color) = (1,0.72,0.38,1)
        _WarmGlowStrength ("Under-shelf Light Strength", Range(0,0.20)) = 0
        _WarmGlowReachPx ("Under-shelf Light Reach (screen pixels)", Range(4,24)) = 12

        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "False"
            "DisableBatching" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex ShelfShadowVert
            #pragma fragment ShelfShadowFrag
            #pragma target 3.0
            #pragma multi_compile_local _ PIXELSNAP_ON
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _RendererColor;
            float4 _Flip;
            float _OffsetPx;
            float _BlurPx;
            float _ExpansionPx;
            fixed4 _WarmGlowColor;
            float _WarmGlowStrength;
            float _WarmGlowReachPx;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                fixed4 color : COLOR;
                fixed fade : TEXCOORD1;
            };

            v2f ShelfShadowVert(appdata input)
            {
                v2f output;

                input.vertex.xy *= _Flip.xy;
                float2 originalHalfSize = max(abs(input.vertex.xy),
                    float2(1e-4, 1e-4));
                float2 geometrySide = step(float2(0.0, 0.0), input.vertex.xy)
                                    * 2.0 - 1.0;
                float2 uvSide = step(float2(0.5, 0.5), input.texcoord)
                              * 2.0 - 1.0;

                float4 clipHere = UnityObjectToClipPos(input.vertex);
                float4 clipX = UnityObjectToClipPos(input.vertex + float4(1,0,0,0));
                float4 clipY = UnityObjectToClipPos(input.vertex + float4(0,1,0,0));
                float2 screenHere = clipHere.xy / max(abs(clipHere.w), 1e-5)
                                  * (0.5 * _ScreenParams.xy);
                float2 screenX = clipX.xy / max(abs(clipX.w), 1e-5)
                               * (0.5 * _ScreenParams.xy);
                float2 screenY = clipY.xy / max(abs(clipY.w), 1e-5)
                               * (0.5 * _ScreenParams.xy);
                float2 pixelsPerLocal = max(float2(
                    length(screenX - screenHere), length(screenY - screenHere)),
                    float2(1e-3, 1e-3));

                float2 expansionLocal = max(_ExpansionPx, 0.0) / pixelsPerLocal;
                input.vertex.xy += geometrySide * expansionLocal;

                // I expand UVs with the quad so the painting stays in place and the added margin gives the blur
                // room.
                input.texcoord += uvSide * expansionLocal
                                / (originalHalfSize * 2.0);

                output.vertex = UnityObjectToClipPos(input.vertex);
                output.texcoord = TRANSFORM_TEX(input.texcoord, _MainTex);
                // I include _RendererColor so the shelf's fade also fades its shadow.
                output.color = input.color * _Color * _RendererColor;
                output.fade = saturate(input.color.a * _RendererColor.a);
                #ifdef PIXELSNAP_ON
                output.vertex = UnityPixelSnap(output.vertex);
                #endif
                return output;
            }

            float ShelfAlpha(float2 uv)
            {
                float inside = step(0.0, uv.x) * step(uv.x, 1.0)
                             * step(0.0, uv.y) * step(uv.y, 1.0);
                return tex2D(_MainTex, saturate(uv)).a * inside;
            }

            fixed4 ShelfShadowFrag(v2f input) : SV_Target
            {
                float du = max(length(float2(
                    ddx(input.texcoord.x), ddy(input.texcoord.x))), 1e-6);
                float dv = max(length(float2(
                    ddx(input.texcoord.y), ddy(input.texcoord.y))), 1e-6);
                float2 centre = input.texcoord
                              + float2(0.0, dv * max(_OffsetPx, 0.0));
                float2 radius = float2(du, dv) * max(_BlurPx, 1.0);

                // Eleven taps give an 8 px mobile blur without a full-screen pass.
                float alpha = ShelfAlpha(centre) * 0.18;
                alpha += ShelfAlpha(centre + float2( radius.x * 0.45, 0.0)) * 0.10;
                alpha += ShelfAlpha(centre + float2(-radius.x * 0.45, 0.0)) * 0.10;
                alpha += ShelfAlpha(centre + float2(0.0,  radius.y * 0.45)) * 0.10;
                alpha += ShelfAlpha(centre + float2(0.0, -radius.y * 0.45)) * 0.10;
                alpha += ShelfAlpha(centre + radius * float2( 0.52,  0.52)) * 0.08;
                alpha += ShelfAlpha(centre + radius * float2(-0.52,  0.52)) * 0.08;
                alpha += ShelfAlpha(centre + radius * float2( 0.52, -0.52)) * 0.08;
                alpha += ShelfAlpha(centre + radius * float2(-0.52, -0.52)) * 0.08;
                alpha += ShelfAlpha(centre + float2(0.0,  radius.y)) * 0.05;
                alpha += ShelfAlpha(centre + float2(0.0, -radius.y)) * 0.05;

                alpha = saturate(alpha * input.color.a);

                // Three silhouette taps add a short amber bounce that moves and fades with the shelf.
                float shelfHere = ShelfAlpha(input.texcoord);
                float outsideShelf = 1.0 - smoothstep(0.02, 0.24, shelfHere);
                float glowReach = max(_WarmGlowReachPx, 1.0);
                float glowNear = ShelfAlpha(input.texcoord
                    + float2(0.0, dv * glowReach * 0.30));
                float glowMiddle = ShelfAlpha(input.texcoord
                    + float2(0.0, dv * glowReach * 0.62));
                float glowFar = ShelfAlpha(input.texcoord
                    + float2(0.0, dv * glowReach));
                float warmGlow = (glowNear * 0.50
                                + glowMiddle * 0.32
                                + glowFar * 0.18)
                               * outsideShelf * input.fade;
                warmGlow = saturate(warmGlow
                    * _WarmGlowStrength * _WarmGlowColor.a);

                clip(max(alpha, warmGlow) - (1.0 / 255.0));
                float3 shadowRgb = input.color.rgb * alpha;
                float3 glowRgb = _WarmGlowColor.rgb * warmGlow;
                // Only the shadow writes alpha; the warm bounce stays additive.
                return fixed4(shadowRgb + glowRgb, alpha);
            }
            ENDCG
        }
    }

    Fallback Off
}
