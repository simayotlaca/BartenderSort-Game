// I premultiply the final baked glass art for sprite blending. Profiles can strengthen solid-part alpha without
// changing cavity or edge coverage.
Shader "LiquidSort/RoyalGlassSprite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        // BottleShell reads these compatibility flags during migration; they mark this as immutable final art.
        [HideInInspector] _StaticBakedFront ("Static Baked Front", Float) = 1
        [HideInInspector] _AuthoredPassthrough ("Authored Passthrough", Float) = 1
        [HideInInspector] _PearlMode ("Pearl Mode", Float) = 0
        [HideInInspector] _PreserveAuthoredGeometry ("Preserve Geometry", Float) = 1
        [HideInInspector] _InteriorRect ("Interior Rect", Vector) = (-0.5,-0.5,0.5,0.5)
        [HideInInspector] _VisibleBottomY ("Visible Liquid Bottom Y", Float) = -10000
        [HideInInspector] _GlassPartBacking ("Stem/Foot, Base, Feather", Vector) = (0,0,0.012,0)

        [HideInInspector] _RendererColor ("Renderer Color", Color) = (1,1,1,1)
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
            "CanUseSpriteAtlas" = "True"
            "DisableBatching" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex BakedGlassVert
            #pragma fragment FinalBakedGlassFrag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            struct appdata_baked
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f_baked
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float2 localPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _InteriorRect;
            float _VisibleBottomY;
            float4 _GlassPartBacking;

            v2f_baked BakedGlassVert(appdata_baked input)
            {
                v2f_baked output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float4 local = UnityFlipSprite(input.vertex, _Flip);
                output.vertex = UnityObjectToClipPos(local);
                output.texcoord = input.texcoord;
                output.localPos = local.xy;
                output.color = input.color * _Color * _RendererColor;
                #ifdef PIXELSNAP_ON
                output.vertex = UnityPixelSnap(output.vertex);
                #endif
                return output;
            }

            fixed4 FinalBakedGlassFrag(v2f_baked input) : SV_Target
            {
                fixed4 source = SampleSpriteTexture(input.texcoord);
                float sourceAlpha = source.a;
                float2 interiorSize = max(
                    _InteriorRect.zw - _InteriorRect.xy,
                    float2(1e-4, 1e-4));
                float feather = max(_GlassPartBacking.z * interiorSize.x, 1e-4);

                float stemFootRegion = 1.0 - smoothstep(
                    _InteriorRect.y - feather,
                    _InteriorRect.y + feather, input.localPos.y);
                float heavyBaseRegion = 1.0 - smoothstep(
                    _VisibleBottomY - feather,
                    _VisibleBottomY + feather, input.localPos.y);
                float backing = saturate(
                    stemFootRegion * _GlassPartBacking.x
                  + heavyBaseRegion * _GlassPartBacking.y);

                // I preserve alpha below 0.30 for soft edges and raise covered solid parts from roughly 0.68
                // toward 0.90 to reduce shelf bleed.
                float authoredInterior = smoothstep(0.30, 0.60, sourceAlpha);
                float packedAlpha = 1.0 - pow(1.0 - sourceAlpha, 2.4);
                source.a = lerp(sourceAlpha, packedAlpha,
                    backing * authoredInterior);

                fixed4 color = source * input.color;
                color.rgb *= color.a;
                return color;
            }
            ENDCG
        }
    }
}
