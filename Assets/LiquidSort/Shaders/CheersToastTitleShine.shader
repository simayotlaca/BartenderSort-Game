// Wordmark-only glint and a small alpha inset that removes the source artwork's pale matte fringe.
Shader "LiquidSort/UI/CheersToastTitleShine"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _EdgeInsetTexels ("Wordmark Edge Inset (Texture Pixels)", Range(0,4)) = 2.5
        [HideInInspector] _SpriteUVRect ("Sprite UV Rect", Vector) = (0,0,1,1)
        [HideInInspector] _ShinePosition ("Shine Position", Float) = -0.3
        [HideInInspector] _ShineAmount ("Shine Amount", Range(0,1)) = 0
        [HideInInspector] _FlashAmount ("Flash Amount", Range(0,1)) = 0
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
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "CheersToastTitleShine"

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
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _TextureSampleAdd;
            fixed4 _Color;
            float4 _ClipRect;
            float4 _SpriteUVRect;
            float _ShinePosition;
            half _ShineAmount;
            half _FlashAmount;
            float _EdgeInsetTexels;

            half WordmarkAlpha(float2 uv)
            {
                // Keep samples within this sprite when Unity packs it into an atlas.
                float2 insideMin = step(_SpriteUVRect.xy, uv);
                float2 insideMax = step(uv, _SpriteUVRect.zw);
                return tex2D(_MainTex, clamp(uv, _SpriteUVRect.xy, _SpriteUVRect.zw)).a
                    * insideMin.x * insideMin.y * insideMax.x * insideMax.y;
            }

            half CleanWordmarkAlpha(float2 uv, half originalAlpha)
            {
                float2 d = abs(_MainTex_TexelSize.xy) * _EdgeInsetTexels;
                half alpha = originalAlpha;
                alpha = min(alpha, WordmarkAlpha(uv + float2(d.x, 0)));
                alpha = min(alpha, WordmarkAlpha(uv - float2(d.x, 0)));
                alpha = min(alpha, WordmarkAlpha(uv + float2(0, d.y)));
                alpha = min(alpha, WordmarkAlpha(uv - float2(0, d.y)));
                d *= 0.70710678;
                alpha = min(alpha, WordmarkAlpha(uv + d));
                alpha = min(alpha, WordmarkAlpha(uv - d));
                alpha = min(alpha, WordmarkAlpha(uv + float2(d.x, -d.y)));
                alpha = min(alpha, WordmarkAlpha(uv + float2(-d.x, d.y)));
                return alpha;
            }

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.worldPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 source = tex2D(_MainTex, i.texcoord) + _TextureSampleAdd;
                // Only contract the alpha boundary. Interior colors and their white highlights stay intact.
                source.a = CleanWordmarkAlpha(i.texcoord, source.a);
                float2 spriteUV = (i.texcoord - _SpriteUVRect.xy)
                    / max(_SpriteUVRect.zw - _SpriteUVRect.xy, float2(0.00001, 0.00001));
                float diagonal = spriteUV.x + (0.5 - spriteUV.y) * 0.32;
                float distanceToShine = abs(diagonal - _ShinePosition);
                half band = 1.0h - smoothstep(0.025, 0.095, distanceToShine);
                half highlight = saturate(_FlashAmount + band * _ShineAmount);
                source.rgb = lerp(source.rgb, fixed3(1, 1, 1), highlight);
                fixed4 outputColor = source * i.color;

                #ifdef UNITY_UI_CLIP_RECT
                outputColor.a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(outputColor.a - 0.001h);
                #endif

                // The glint never adds alpha outside the original letter pixels.
                return outputColor;
            }
            ENDCG
        }
    }
    Fallback Off
}
