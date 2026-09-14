// I recolour the authored stroke on the GPU using its luminance and the scene palette, without CPU pixel reads or
// generated sprites.
Shader "LiquidSort/GlassContour"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _ContourDark ("Contour Dark", Color) = (0.157, 0.196, 0.298, 1)
        _ContourLight ("Contour Light", Color) = (0.722, 0.780, 0.878, 1)
        // Where the drawing's own luminance is taken to be fully dark and fully lit.
        _RampLow ("Ramp Low", Range(0,1)) = 0.10
        _RampHigh ("Ramp High", Range(0,1)) = 0.62
        // Degrees. 0 = from the right, 90 = from above.
        _LightAngle ("Light Angle", Range(0,360)) = 115
        _LightStrength ("Light Strength", Range(0,1)) = 0.22
        // I brighten only the source's brightest pixels to keep the artist's highlight placement.
        _SpecularColor ("Authored Specular Color", Color) = (0.76,0.94,1,1)
        _SpecularStrength ("Authored Specular Strength", Range(0,1)) = 0.22
        _SpecularLow ("Authored Specular Low", Range(0,1)) = 0.58
        _SpecularHigh ("Authored Specular High", Range(0,1)) = 0.92
        // Optional mouth-ring lift for CocktailGlassContour. Zero keeps the original look.
        _TopRimBoost ("Top Rim Brightness", Range(0,1)) = 0
        _TopRimY ("Top Rim Local Y", Float) = 10000
        _TopRimFeather ("Top Rim Feather", Range(0.001,1)) = 0.10
        [HideInInspector] _InteriorRect ("Interior Rect", Vector) = (-0.5,-0.5,0.5,0.5)
        [HideInInspector] _VisibleFloorY ("Optical Liquid Floor Y", Float) = -10000
        [HideInInspector] _VisibleBottomY ("Visible Liquid Bottom Y", Float) = -10000
        [HideInInspector] _ContactStrength ("Liquid/Glass Contact Lift", Range(0,1)) = 0
        [HideInInspector] _AccessoryFx ("Handle, Stem/Foot, Feather, Stem Toon", Vector) = (0,0,0.025,0)
        [HideInInspector] _BottomRimStrength ("Lower Silhouette Highlight", Range(0,1)) = 0
        [HideInInspector] _RimHotspotStrength ("Warm Upper-left Rim Hotspot", Range(0,1)) = 0
        [HideInInspector] _LiquidBounceColor ("Liquid Base Bounce", Color) = (0,0,0,0)
        [HideInInspector] _LiquidBounceStrength ("Liquid Base Bounce Strength", Range(0,1)) = 0
        [HideInInspector] _PaintedToyStrength ("Painted Toy Treatment", Range(0,1)) = 0
        [HideInInspector] _ToyMidColor ("Painted Toy Mid", Color) = (0.31,0.47,0.59,1)
        [HideInInspector] _ToyFillColor ("Painted Toy Cyan", Color) = (0.27,0.89,0.96,1)
        [HideInInspector] _Color ("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            fixed4 _ContourDark;
            fixed4 _ContourLight;
            float _RampLow;
            float _RampHigh;
            float _LightAngle;
            float _LightStrength;
            fixed4 _SpecularColor;
            float _SpecularStrength;
            float _SpecularLow;
            float _SpecularHigh;
            float _TopRimBoost;
            float _TopRimY;
            float _TopRimFeather;
            float4 _InteriorRect;
            float _VisibleFloorY;
            float _VisibleBottomY;
            float _ContactStrength;
            float4 _AccessoryFx;
            float _BottomRimStrength;
            float _RimHotspotStrength;
            fixed4 _LiquidBounceColor;
            float _LiquidBounceStrength;
            float _PaintedToyStrength;
            fixed4 _ToyMidColor;
            fixed4 _ToyFillColor;
            fixed4 _Color;
            fixed4 _RendererColor;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.local = v.vertex.xy;
                o.color = v.color * _Color * _RendererColor;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 src = tex2D(_MainTex, i.uv);
                clip(src.a - (1.0 / 255.0));

                // I use straight RGB for these straight-alpha sprites; dividing by alpha would create pale edge
                // halos.
                float lum = dot(src.rgb, float3(0.299, 0.587, 0.114));
                float ramp = smoothstep(_RampLow, _RampHigh, lum);

                float3 col = lerp(_ContourDark.rgb, _ContourLight.rgb, ramp);
                float2 interiorSize = max(_InteriorRect.zw - _InteriorRect.xy,
                                          float2(1e-4, 1e-4));
                float2 toyUv = (i.local - _InteriorRect.xy) / interiorSize;
                float2 vesselUv = saturate(toyUv);
                float toy = saturate(_PaintedToyStrength);

                // I use an upper-left warm key on authored highlights, leaving the opposite edge to the cyan ramp
                // and ThinFX fill.
                float a = radians(_LightAngle);
                float2 dir = float2(cos(a), sin(a));
                float facing = dot(normalize(i.local + float2(1e-5, 1e-5)), dir);
                float keyFacing = smoothstep(-0.15, 0.75, facing);

                // I lift the cocktail's dark mouth ring with a local-y mask so the other glasses stay unchanged.
                float rim = smoothstep(_TopRimY - max(_TopRimFeather, 1e-4),
                                       _TopRimY + max(_TopRimFeather, 1e-4),
                                       i.local.y) * saturate(_TopRimBoost);
                col = lerp(col, _ContourLight.rgb,
                    rim * lerp(0.35, 1.0, keyFacing));

                // Source alpha and brightness limit highlights to painted glass, keeping the liquid cavity clear.
                float specular = smoothstep(_SpecularLow,
                    max(_SpecularHigh, _SpecularLow + 1e-4), lum);
                col = lerp(col, _SpecularColor.rgb,
                    saturate(specular * _SpecularStrength * keyFacing));

                // I brighten the light-facing side as a cheap substitute for a surface normal.
                col *= 1.0 + _LightStrength * facing;

                // I add a warm key only to painted upper-left lip pixels; source alpha keeps it out of the cavity.
                float topZone = smoothstep(0.78, 0.98, vesselUv.y);
                float leftZone = 1.0 - smoothstep(0.30, 0.72, vesselUv.x);
                float authoredHotspot = smoothstep(0.18, 0.64, lum);
                float rimHotspot = topZone * leftZone * authoredHotspot
                                 * keyFacing * saturate(_RimHotspotStrength);
                col = lerp(col, _SpecularColor.rgb, saturate(rimHotspot));

                // I select solid glass parts from the profile, then use the source brightness for highlights so
                // the cavity stays clear.
                float feather = max(_AccessoryFx.z * interiorSize.x, 1e-4);
                float insideX = smoothstep(_InteriorRect.x - feather,
                                           _InteriorRect.x + feather, i.local.x)
                              * (1.0 - smoothstep(_InteriorRect.z - feather,
                                                  _InteriorRect.z + feather, i.local.x));
                float insideY = smoothstep(_InteriorRect.y - feather,
                                           _InteriorRect.y + feather, i.local.y)
                              * (1.0 - smoothstep(_InteriorRect.w - feather,
                                                  _InteriorRect.w + feather, i.local.y));
                float handleRegion = (1.0 - insideX) * insideY;
                float stemFootRegion = 1.0 - smoothstep(_InteriorRect.y - feather,
                                                       _InteriorRect.y + feather, i.local.y);
                float accessoryRegion = saturate(handleRegion * _AccessoryFx.x
                                               + stemFootRegion * _AccessoryFx.y);
                // I reduce stem and foot shading to three soft colour bands. The profile controls the blend; bowl
                // shading stays unchanged.
                float stemToon = stemFootRegion * saturate(_AccessoryFx.w);
                float toonMiddle = smoothstep(0.27, 0.36, lum);
                float toonHighlight = smoothstep(0.64, 0.75, lum);
                float toonRamp = 0.16 + toonMiddle * 0.36 + toonHighlight * 0.30;
                float3 toonAccessory = lerp(_ContourDark.rgb, _ContourLight.rgb,
                                            saturate(toonRamp));
                // I expand the source luminance ramp for crisp cyan highlights while keeping the navy edges dark.
                float authoredAccessory = smoothstep(0.30, 0.58, lum);
                authoredAccessory = lerp(authoredAccessory,
                                         smoothstep(0.63, 0.72, lum), stemToon);
                col = lerp(col, _SpecularColor.rgb,
                    saturate(accessoryRegion * authoredAccessory));
                // I apply the flat palette after broad reflections, then keep one small left shine patch.
                col = lerp(col, toonAccessory, stemToon);
                float stemX = (i.local.x - (_InteriorRect.x + _InteriorRect.z) * 0.5)
                            / interiorSize.x;
                float toyShineStripe = smoothstep(-0.42, -0.24, stemX)
                                      * (1.0 - smoothstep(-0.10, 0.08, stemX));
                float toyShine = toyShineStripe * stemToon * 0.28
                               + smoothstep(0.80, 0.92, lum) * stemToon * 0.24;
                col = lerp(col, _SpecularColor.rgb, saturate(toyShine));

                UNITY_BRANCH
                if (toy > 0.001)
                {
                    // I flatten the mug handle into cyan outer light, plum inner shade and a short warm key. Keep
                    // toyUv unsaturated so x > 1 selects the handle only.
                    float rightHandle = smoothstep(-0.015, 0.035, toyUv.x - 1.0)
                                      * smoothstep(0.18, 0.28, toyUv.y)
                                      * (1.0 - smoothstep(0.78, 0.86, toyUv.y));
                    col = lerp(col, _ToyMidColor.rgb,
                        saturate(rightHandle * toy * 0.90));

                    float handleX = (toyUv.x - 1.0) / 0.46;
                    float handleOuterCool = rightHandle
                        * smoothstep(0.42, 0.72, handleX)
                        * (1.0 - smoothstep(0.94, 1.05, handleX))
                        * smoothstep(0.27, 0.38, toyUv.y)
                        * (1.0 - smoothstep(0.70, 0.82, toyUv.y));
                    float3 coolHandle = lerp(
                        _ToyMidColor.rgb, _ToyFillColor.rgb, 0.48);
                    col = lerp(col, coolHandle,
                        saturate(handleOuterCool * toy * 0.82));

                    float handleInnerAo = rightHandle
                        * smoothstep(0.10, 0.24, handleX)
                        * (1.0 - smoothstep(0.62, 0.78, handleX))
                        * smoothstep(0.20, 0.29, toyUv.y)
                        * (1.0 - smoothstep(0.43, 0.56, toyUv.y));
                    col = lerp(col, _ContourDark.rgb,
                        saturate(handleInnerAo * toy * 0.82));

                    float handleWarmKey = rightHandle
                        * smoothstep(0.18, 0.34, handleX)
                        * (1.0 - smoothstep(0.73, 0.88, handleX))
                        * smoothstep(0.62, 0.69, toyUv.y)
                        * (1.0 - smoothstep(0.77, 0.84, toyUv.y));
                    col = lerp(col, _SpecularColor.rgb,
                        saturate(handleWarmKey * toy * 0.58));
                    // A tiny bright core adds gloss without whitening the whole handle.
                    float handleWarmCore = rightHandle
                        * smoothstep(0.47, 0.56, handleX)
                        * (1.0 - smoothstep(0.66, 0.74, handleX))
                        * smoothstep(0.69, 0.73, toyUv.y)
                        * (1.0 - smoothstep(0.755, 0.79, toyUv.y));
                    col = lerp(col, _SpecularColor.rgb,
                        saturate(handleWarmCore * toy * 0.76));

                    // I soften the middle of the left key so it does not become a full-height white stripe.
                    float leftWall = (1.0 - smoothstep(0.015, 0.105, toyUv.x))
                                   * smoothstep(0.16, 0.28, toyUv.y)
                                   * (1.0 - smoothstep(0.73, 0.86, toyUv.y));
                    float wallBreak = 1.0 - 0.24
                        * exp(-pow((toyUv.y - 0.49) / 0.085, 2.0));
                    float3 paintedKey = lerp(
                        _ToyMidColor.rgb, _SpecularColor.rgb, 0.64);
                    float leftPaint = leftWall * wallBreak * toy;
                    col = lerp(col, paintedKey, saturate(leftPaint * 0.68));
                    float leftWhiteCore = leftPaint * smoothstep(0.78, 0.90, lum);
                    col = lerp(col, _SpecularColor.rgb,
                        saturate(leftWhiteCore * 0.22));

                    // I add one short cream highlight near ten o'clock and keep the rest of the rim blue/plum.
                    float toyLip = smoothstep(0.925, 0.972, toyUv.y)
                                 * (1.0 - smoothstep(1.00, 1.035, toyUv.y))
                                 * smoothstep(0.04, 0.10, toyUv.x)
                                 * (1.0 - smoothstep(0.34, 0.46, toyUv.x))
                                 * smoothstep(0.20, 0.52, lum);
                    col = lerp(col, _SpecularColor.rgb,
                        saturate(toyLip * toy * 0.90));
                }

                // I lift dark glass pixels between the optical floor and first liquid row after directional
                // lighting. Source alpha keeps the seam curved and stable during pours.
                float contactLow = min(_VisibleFloorY, _VisibleBottomY);
                float contactHigh = max(_VisibleFloorY, _VisibleBottomY);
                float contactFeather = max(interiorSize.x * 0.018, 1e-4);
                float contactWindow = smoothstep(contactLow - contactFeather * 2.0,
                                                 contactLow + contactFeather, i.local.y)
                                    * (1.0 - smoothstep(contactHigh + contactFeather,
                                                       contactHigh + contactFeather * 3.0,
                                                       i.local.y));
                float authoredDark = 1.0 - smoothstep(0.30, 0.70, lum);
                float3 contactGlass = lerp(_ContourDark.rgb, _ContourLight.rgb, 0.44);
                col = lerp(col, contactGlass,
                    saturate(contactWindow * authoredDark * _ContactStrength
                             * lerp(1.0, 0.55, toy)));

                // I flatten the base to plum after contact correction, before adding the liquid reflection and
                // cyan edge.
                float toyBaseZone = 1.0 - smoothstep(
                    contactLow - interiorSize.x * 0.10,
                    contactLow + interiorSize.x * 0.045, i.local.y);
                float3 toyBaseColor = lerp(
                    _ContourDark.rgb, _ToyMidColor.rgb, 0.18);
                col = lerp(col, toyBaseColor,
                    saturate(toyBaseZone * toy * 0.82));

                // I reflect the lowest liquid colour into the nearby base using source alpha and brightness. The
                // cyan outer rim is added afterwards.
                float bounceReach = interiorSize.x * 0.16;
                float baseBounceWindow =
                    smoothstep(contactLow - bounceReach,
                               contactLow - contactFeather, i.local.y)
                  * (1.0 - smoothstep(contactHigh + contactFeather,
                                      contactHigh + contactFeather * 3.0,
                                      i.local.y));
                // I fade the liquid reflection just below the bowl so it cannot colour the whole stem and foot.
                float extensionStrength = lerp(0.85, 0.22,
                    saturate(_AccessoryFx.y));
                float regularBounceMask = max(contactWindow,
                                              baseBounceWindow * extensionStrength)
                                        * lerp(0.35, 1.0, authoredDark);
                // The mug gets one small upper-left liquid reflection instead of a fully tinted base.
                float toyBounceX = smoothstep(0.10, 0.20, toyUv.x)
                                 * (1.0 - smoothstep(0.42, 0.54, toyUv.x));
                float toyBounceY = smoothstep(
                    contactLow - interiorSize.x * 0.08,
                    contactLow - interiorSize.x * 0.025, i.local.y)
                    * (1.0 - smoothstep(
                        contactLow + interiorSize.x * 0.025,
                        contactLow + interiorSize.x * 0.075, i.local.y));
                float toyBounceMask = toyBaseZone * toyBounceX * toyBounceY
                                    * lerp(0.45, 1.0, ramp);
                float bounceMask = lerp(regularBounceMask, toyBounceMask, toy);
                float3 bounceTarget = lerp(_LiquidBounceColor.rgb,
                                           _SpecularColor.rgb, toy * 0.10);
                col = lerp(col, bounceTarget,
                    saturate(bounceMask * _LiquidBounceStrength
                             * _LiquidBounceColor.a));

                // I probe outside the texel for the true outer alpha edge; fwidth can miss fully opaque edges. The
                // outline stays near one output pixel.
                float belowCavity = 1.0 - smoothstep(_InteriorRect.y - feather,
                                                     _InteriorRect.y + feather, i.local.y);
                float2 edgeProbe = max(_MainTex_TexelSize.xy * 1.5,
                                       fwidth(i.uv) * 0.82);
                float neighbourAlpha = min(
                    min(tex2D(_MainTex, i.uv + float2(edgeProbe.x, 0.0)).a,
                        tex2D(_MainTex, i.uv - float2(edgeProbe.x, 0.0)).a),
                    min(tex2D(_MainTex, i.uv + float2(0.0, edgeProbe.y)).a,
                        tex2D(_MainTex, i.uv - float2(0.0, edgeProbe.y)).a));
                float alphaEdge = smoothstep(0.04, 0.72,
                                             saturate(src.a - neighbourAlpha));
                float bottomRim = alphaEdge * belowCavity * saturate(_BottomRimStrength)
                                * lerp(0.68, 1.0, ramp);
                col = lerp(col, _ContourLight.rgb, saturate(bottomRim));

                // I tint the opposite wall and handle with a 1-2 pixel cyan alpha edge.
                float rightProbe = max(_MainTex_TexelSize.x * 2.0,
                                       fwidth(i.uv.x) * 0.75);
                float alphaOutsideRight = tex2D(_MainTex,
                    i.uv + float2(rightProbe, 0.0)).a;
                float outerRightEdge = smoothstep(0.06, 0.48,
                    saturate(src.a - alphaOutsideRight));
                float toyRightRim = outerRightEdge
                    * smoothstep(0.90, 0.99, toyUv.x)
                    * smoothstep(0.12, 0.24, toyUv.y)
                    * (1.0 - smoothstep(0.86, 0.96, toyUv.y));
                col = lerp(col, _ToyFillColor.rgb,
                    saturate(toyRightRim * toy * 0.78));

                // I strengthen internal stem alpha for a solid cartoon look while preserving soft outer edges.
                float toyAlpha = 1.0 - pow(1.0 - saturate(src.a), 2.35);
                float alpha = lerp(src.a, toyAlpha, stemToon) * i.color.a;
                return fixed4(saturate(col) * alpha, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
