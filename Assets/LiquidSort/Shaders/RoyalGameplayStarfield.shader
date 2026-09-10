// I animate the forty quads from Gameplay_Starfield_Authored.obj without creating visual objects in C#.
Shader "LiquidSort/RoyalGameplayStarfield"
{
    Properties
    {
        _MainTex ("Five-point Star Mask", 2D) = "white" {}
        _SparkleStrength ("Sparkle Strength", Range(0,1.5)) = 0.62
        _SparkleSpeed ("Sparkle Speed", Range(0.25,2)) = 0.85
        _GoldSparkle ("Gold Glass Sparkle", Color) = (1,0.8392,0.4196,0.32)
        _LavenderSparkle ("Lavender Glass Sparkle", Color) = (0.6863,0.7882,1,0.20)
        _CyanSparkle ("Cyan Glass Sparkle", Color) = (0.4627,0.8745,1,0.17)

        _FocusCenterY ("Gameplay Focus Centre Y", Range(0.25,0.65)) = 0.42
        _FocusWidth ("Gameplay Focus Width", Range(0.40,0.90)) = 0.68
        _FocusHeight ("Gameplay Focus Height", Range(0.40,0.90)) = 0.62
        _DecorCenterVisibility ("Decor Centre Visibility", Range(0,1)) = 0.48
        _DecorEdgeVisibility ("Decor Edge Visibility", Range(0,1)) = 0.07
        _Vignette ("Edge Vignette", Range(0,0.40)) = 0.27
        _EdgeDesaturate ("Edge Desaturation", Range(0,0.30)) = 0.06
        [HideInInspector] _BackdropWidth ("Authored Backdrop Width", Float) = 8.54
        [HideInInspector] _BackdropHeight ("Authored Backdrop Height", Float) = 18.42
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background+2"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend One One
        ColorMask RGB

        Pass
        {
            CGPROGRAM
            #pragma vertex StarVert
            #pragma fragment StarFrag
            #pragma target 2.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _SparkleSpeed;
            float _SparkleStrength;
            float4 _GoldSparkle;
            float4 _LavenderSparkle;
            float4 _CyanSparkle;
            float _FocusCenterY;
            float _FocusWidth;
            float _FocusHeight;
            float _DecorCenterVisibility;
            float _DecorEdgeVisibility;
            float _Vignette;
            float _EdgeDesaturate;
            float _BackdropWidth;
            float _BackdropHeight;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 packedUv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 maskUv : TEXCOORD0;
                float3 starColor : TEXCOORD1;
            };

            float Twinkle(float phase, float speed)
            {
                float wave = 0.5 + 0.5 * sin(
                    _Time.y * speed * _SparkleSpeed + phase);
                float pulse = smoothstep(0.30, 0.90, wave);
                pulse = pulse * pulse * (3.0 - 2.0 * pulse);
                pulse *= pulse;
                return lerp(0.03, 1.0, pulse);
            }

            float3 StaticColour(float2 starUv, float palette)
            {
                float4 tint = palette < 0.5
                    ? _GoldSparkle
                    : palette < 1.5 ? _LavenderSparkle : _CyanSparkle;

                float focusX = (starUv.x - 0.5) / max(_FocusWidth, 0.001);
                float focusY = (starUv.y - _FocusCenterY)
                             / max(_FocusHeight, 0.001);
                float focusEdge = smoothstep(
                    0.52, 0.98, sqrt(focusX * focusX + focusY * focusY));
                float nearestEdge = min(
                    min(starUv.x, 1.0 - starUv.x),
                    min(starUv.y, 1.0 - starUv.y));
                float perimeterEdge = 1.0 - smoothstep(0.0, 0.18, nearestEdge);
                float edge = saturate(max(focusEdge, perimeterEdge));
                float decorVisibility = lerp(
                    _DecorCenterVisibility, _DecorEdgeVisibility, edge);
                float amount = tint.a * decorVisibility * _SparkleStrength
                             * (1.0 - edge * _Vignette);
                float3 colour = tint.rgb * amount;
                float luminance = dot(colour, float3(0.2126, 0.7152, 0.0722));
                return lerp(colour, luminance.xxx, edge * _EdgeDesaturate);
            }

            v2f StarVert(appdata input)
            {
                v2f output;

                // UV.x packs radius/palette in its integer part and mask corners in quarter steps. Position.z
                // packs phase/speed near zero for stable mesh bounds.
                float packedParams = floor(input.packedUv.x + 0.001);
                float radiusCode = fmod(packedParams, 4.0);
                float palette = floor(packedParams / 4.0);
                float2 maskUv = frac(input.packedUv) * 4.0;
                float radius = radiusCode < 0.5
                    ? 0.0035 : radiusCode < 1.5 ? 0.0047 : 0.0059;

                float packedMotion = input.vertex.z * 10000.0;
                float speedCode = floor(packedMotion / 10.0 + 0.001);
                float speed = speedCode / 100.0;
                float phase = packedMotion - speedCode * 10.0;
                float pulse = Twinkle(phase, speed);
                float animatedRadius = radius
                                     * lerp(0.76, 1.08, pulse)
                                     * (32.0 / 31.0);
                float2 localDirection = (maskUv * 2.0 - 1.0)
                                      * float2(_BackdropWidth, _BackdropHeight);
                float2 authoredOffset = (maskUv * 2.0 - 1.0)
                                      * float2(_BackdropWidth, _BackdropHeight)
                                      * radius;
                float4 centre = input.vertex;
                centre.xy -= authoredOffset;
                centre.z = 0.0;
                float screenAspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                float4 localPosition = centre;
                localPosition.xy += localDirection
                                  * animatedRadius
                                  * float2(1.0 / max(screenAspect, 0.001), 1.0);

                float2 starUv = centre.xy / float2(_BackdropWidth, _BackdropHeight)
                              + 0.5;

                output.vertex = UnityObjectToClipPos(localPosition);
                output.maskUv = maskUv;
                output.starColor = StaticColour(starUv, palette) * pulse;
                return output;
            }

            fixed4 StarFrag(v2f input) : SV_Target
            {
                fixed mask = tex2D(_MainTex, input.maskUv).a;
                return fixed4(input.starColor * mask, 0.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
