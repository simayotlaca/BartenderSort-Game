// Enriches only the cyan-blue face while retaining the authored gold, lighting and alpha.
Shader "LiquidSort/UI/LevelButtonArtwork"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _FaceColor ("Blue Face Midtone", Color) = (0.035,0.61,1,1)
        _SourceFaceColor ("Original Face Midtone", Color) = (0.21960784,0.65098039,0.83921569,1)
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
            Name "LevelButtonArtwork"

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
            fixed4 _FaceColor;
            fixed4 _SourceFaceColor;
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
                float sourceValue = max(source.r, max(source.g, source.b));
                float sourceMinimum = min(source.r, min(source.g, source.b));
                float sourceSaturation = (sourceValue - sourceMinimum) / max(sourceValue, 0.001);

                // Gold, neutral light, red and magenta all have a nonpositive cyan signal.
                float cyanSignal = min(source.g, source.b) - source.r;
                float faceMask = smoothstep(0.025, 0.14, cyanSignal)
                    * smoothstep(0.04, 0.15, sourceSaturation);

                float referenceValue = max(_SourceFaceColor.r, max(_SourceFaceColor.g, _SourceFaceColor.b));
                float referenceMinimum = min(_SourceFaceColor.r, min(_SourceFaceColor.g, _SourceFaceColor.b));
                float referenceSaturation = (referenceValue - referenceMinimum) / max(referenceValue, 0.001);
                float shade = sourceValue / max(referenceValue, 0.001);
                // Less saturated source paint is a white bevel or shine, retained over the richer face.
                float whiteShine = saturate(1.0 - sourceSaturation / max(referenceSaturation, 0.001));
                float3 mapped = saturate(lerp(_FaceColor.rgb, float3(1, 1, 1), whiteShine) * shade);

                // A more saturated blue must not make the painted gradient darker.
                float sourceLuma = dot(source.rgb, float3(0.2126, 0.7152, 0.0722));
                float mappedLuma = dot(mapped, float3(0.2126, 0.7152, 0.0722));
                float lightLift = saturate((sourceLuma - mappedLuma) / max(1.0 - mappedLuma, 0.001));
                mapped = lerp(mapped, float3(1, 1, 1), lightLift);
                fixed4 outputColor = fixed4(lerp(source.rgb, mapped, faceMask), source.a) * i.color;

                #ifdef UNITY_UI_CLIP_RECT
                outputColor.a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(outputColor.a - 0.001h);
                #endif

                return outputColor;
            }
            ENDCG
        }
    }
    Fallback Off
}
