// I recolour the five card glass sprites here; gameplay vessels use other shaders.
Shader "LiquidSort/UI/OrderCardGlass"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

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
            "CanUseSpriteAtlas" = "False"
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
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "OrderCardGlass"

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
            fixed4 _TextureSampleAdd;
            fixed4 _Color;
            float4 _ClipRect;

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
                float authoredAlpha = saturate(source.a);

                // I keep the authored bevel in two slate tones so large source sprites stay crisp as small icons.
                float sourceLuma = dot(source.rgb, float3(0.299, 0.587, 0.114));
                float sourceValue = max(source.r, max(source.g, source.b));
                float renderedValue = lerp(sourceLuma, sourceValue, 0.22);
                float lineValue = smoothstep(0.20, 0.78, renderedValue);
                float3 glassInk = float3(0.16, 0.22, 0.32);
                float3 glassHighlight = float3(0.49, 0.64, 0.78);
                float3 cardGlass = lerp(glassInk, glassHighlight,
                    lineValue * 0.82);
                float whiteGlint = smoothstep(0.82, 0.99, sourceValue);
                cardGlass = lerp(cardGlass, float3(0.90, 0.95, 1.0),
                    whiteGlint * 0.14);

                // I lift alpha slightly for a continuous small contour while preserving soft edges.
                float outputAlpha = saturate(authoredAlpha * 1.16) * i.color.a;
                float3 premul = cardGlass * i.color.rgb * outputAlpha;

                #ifdef UNITY_UI_CLIP_RECT
                float clipFactor = UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                premul *= clipFactor;
                outputAlpha *= clipFactor;
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(outputAlpha - 0.001);
                #endif

                return fixed4(saturate(premul), saturate(outputAlpha));
            }
            ENDCG
        }
    }
    Fallback Off
}
