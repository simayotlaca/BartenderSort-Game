// Native uGUI port of the approved toast preview's liquid crest and masked title slosh.
Shader "LiquidSort/UI/CheersToastRefinedLiquid"
{
    Properties
    {
        [PerRendererData] _MainTex ("Artwork", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _MaskTex ("Glass interior", 2D) = "white" {}
        _Mode ("0 liquid, 1 title", Float) = 0
        _Wave ("Crest, return, source U, opposite U", Vector) = (0,0,.718,.34)
        _Geometry ("Liquid height, crest height, glass width, glass height", Vector) = (387,35,310,387)
        _MaskRect ("Mask center and size", Vector) = (0,0,1,1)
        _Slosh ("Liquid translation Y and angle", Vector) = (0,0,0,0)
        _TitleCrop ("Title UV rectangle", Vector) = (0,0,1,1)
        _TitleWave ("Amplitude, direction, word height, orange flag", Vector) = (0,1,143.55,1)
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
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
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
            sampler2D _MainTex, _MaskTex;
            fixed4 _Color, _TextureSampleAdd;
            float4 _ClipRect, _Wave, _Geometry, _MaskRect, _Slosh, _TitleCrop, _TitleWave;
            float _Mode;

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

            float smooth01(float v) { v = saturate(v); return v * v * (3 - 2 * v); }
            float faceCoverage(float3 color)
            {
                #ifndef UNITY_COLORSPACE_GAMMA
                color = LinearToGammaSpace(color);
                #endif
                color *= 255;
                if (_TitleWave.w > .5)
                    return smooth01((color.r - 140) / 45) * smooth01((color.g - 38) / 52)
                        * smooth01((color.r - color.b - 100) / 70);
                return smooth01((78 - color.r) / 45) * smooth01((color.g - 80) / 60)
                    * smooth01((color.b - 145) / 70);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 color;
                if (_Mode > .5)
                {
                    // The supplied artwork is always the base. An unmoving color-selected
                    // mask admits only the shifted front faces; outlines/glints never move.
                    color = tex2D(_MainTex, i.texcoord) + _TextureSampleAdd;
                    float nx = ((i.texcoord.x - _TitleCrop.x) / _TitleCrop.z) * 2 - 1;
                    float shift = _TitleWave.x * _TitleWave.y
                        * (.84 * nx + .22 * sin(nx * UNITY_PI));
                    float2 movingUV = i.texcoord;
                    movingUV.y += shift / _TitleWave.z * _TitleCrop.w;
                    fixed4 moving = tex2D(_MainTex, movingUV) + _TextureSampleAdd;
                    float withinCrop = step(_TitleCrop.y, movingUV.y)
                        * step(movingUV.y, _TitleCrop.y + _TitleCrop.w);
                    float overlayAlpha = moving.a * faceCoverage(moving.rgb)
                        * color.a * faceCoverage(color.rgb) * withinCrop;
                    color.rgb = lerp(color.rgb, moving.rgb, overlayAlpha);
                }
                else
                {
                    float hump = exp(-pow((i.texcoord.x - _Wave.z) / .145, 2));
                    float opposite = exp(-pow((i.texcoord.x - _Wave.w) / .19, 2));
                    float waveDown = -_Geometry.y * _Wave.x * hump
                        + _Geometry.y * _Wave.y * (.17 * hump - .10 * opposite);
                    float2 sampleUV = i.texcoord;
                    sampleUV.y += waveDown / _Geometry.x;
                    // Preview keeps only the authored lower 75.5% of the liquid canvas.
                    clip(sampleUV.y);
                    clip(.755 - sampleUV.y);
                    clip(sampleUV.x);
                    clip(1 - sampleUV.x);
                    color = tex2D(_MainTex, sampleUV) + _TextureSampleAdd;

                    // Recover glass-local coordinates after the slosh transform. Multiplying
                    // the alpha mask retains its feathered edge in addition to uGUI stencil.
                    float2 p = (i.texcoord - .5) * _Geometry.x;
                    float sine = sin(_Slosh.y), cosine = cos(_Slosh.y);
                    p = float2(cosine * p.x - sine * p.y,
                        sine * p.x + cosine * p.y + _Slosh.x);
                    clip(_Geometry.z * .5 - abs(p.x));
                    clip(_Geometry.w * .5 - abs(p.y));
                    float2 maskUV = (p - _MaskRect.xy) / _MaskRect.zw + .5;
                    clip(maskUV.x); clip(1 - maskUV.x);
                    clip(maskUV.y); clip(1 - maskUV.y);
                    color.a *= tex2D(_MaskTex, maskUV).a;
                }
                color *= i.color;
                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(i.worldPosition.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - .001);
                #endif
                return color;
            }
            ENDCG
        }
    }
    Fallback Off
}
