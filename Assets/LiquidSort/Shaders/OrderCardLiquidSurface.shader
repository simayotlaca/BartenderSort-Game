// I draw only the card's exposed ellipse here; the existing masked UI bands still draw the body.
Shader "LiquidSort/UI/OrderCardLiquidSurface"
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
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "OrderCardLiquidSurface"

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

                // The RectTransform matches the Royal cap bounds; an analytic ellipse keeps small cards smooth.
                float2 centred = i.texcoord * 2.0 - 1.0;
                float radiusSquared = dot(centred, centred);
                float ellipseAA = max(fwidth(radiusSquared) * 0.72, 0.0025);
                float ellipse = 1.0 - smoothstep(
                    1.0 - ellipseAA, 1.0 + ellipseAA, radiusSquared);

                // I use LiquidPalette.CapFor like the shelf shader, with a soft back lift and far-rim catch.
                float backLift = lerp(0.94, 1.035, i.texcoord.y);
                float farRim = 1.0 - saturate(abs(i.texcoord.y - 0.79) / 0.12);
                farRim *= farRim;
                float sideFade = smoothstep(0.0, 0.16, 1.0 - abs(centred.x));
                float light = backLift + farRim * sideFade * 0.035;
                float3 surface = saturate(i.color.rgb * light);

                float outputAlpha = saturate(source.a) * i.color.a * ellipse;
                float3 premul = surface * outputAlpha;

                #ifdef UNITY_UI_CLIP_RECT
                float clipFactor = UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                premul *= clipFactor;
                outputAlpha *= clipFactor;
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(outputAlpha - 0.001);
                #endif

                return fixed4(premul, saturate(outputAlpha));
            }
            ENDCG
        }
    }
    Fallback Off
}
