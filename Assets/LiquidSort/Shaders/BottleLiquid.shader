// I draw up to eight liquid bands in one pass. Waterlines stay world-level, and covered colours share one curved
// edge.
Shader "LiquidSort/BottleLiquid"
{
    Properties
    {
        _MaskTex ("Interior Mask", 2D) = "white" {}
        // The reference cap is 19.5 px deep on a 143 px chord, so its half-depth ratio is 0.136.
        _Bulge ("Cap Depth / Chord", Range(0.02,0.20)) = 0.135
        // I cap the depth so wide glasses do not get a top face as deep as the bowl.
        _BulgeMax ("Cap Depth Ceiling", Float) = 0.16
        // I share one rounded edge between colours to avoid gaps and overlapping ellipses.
        _InnerCurve ("Inner Junction Curve", Range(0,1)) = 1.0
        _SurfaceScale ("Exposed Surface Depth Scale", Range(0,1)) = 1.0
        // _RoyalUnitsPerPixel converts reference pixels to vessel-local units; zero uses screen derivatives. The
        // mask already has an inset, so a second cap inset would leave a gap.
        _CapWallInset ("Top Face Wall Inset (Royal pixels)", Range(0,3)) = 0
        _RoyalUnitsPerPixel ("Royal Local Units Per Pixel", Float) = 0
        // The junction sags 14 px per 143 px chord (0.098). I keep this separate from cap depth so wide bowls keep
        // curved bands and a shallow surface.
        _InnerBulge ("Junction Depth / Chord", Range(0,0.25)) = 0.098
        _InnerMax ("Junction Depth Ceiling", Float) = 0.36
        // I scale all channels together to brighten fallback caps without changing their hue or saturation.
        _CapValue ("Top Face Value", Range(1,3)) = 1.22
        // Old serialized setting only. Live caps use LiquidPalette colours, without grey or white mixing.
        _CapDesat ("Top Face Desaturate", Range(0,1)) = 0.10
        _CapDesatPale ("Top Face Desaturate (pale)", Range(0,1)) = 0.45
        // Far-rim brightness: 1 keeps the full lift; 0 returns to the band colour.
        _CapFalloff ("Top Face Far Shade", Range(0,1)) = 0.72
        _EdgeShade ("Edge Shade", Range(0,1)) = 0.0
        // I use glass-local X for body lighting so it follows the glass while waterlines stay level.
        _CylinderKey ("Cylinder Key", Range(0,1)) = 0.0
        _CylinderShade ("Cylinder Shade", Range(0,0.5)) = 0.0
        // The overhead light mainly hits the top face, with a soft fill on the body.
        _OverheadColor ("Scene Overhead Light", Color) = (1.0,0.97,0.91,1)
        _OverheadStrength ("Scene Overhead Strength", Range(0,0.30)) = 0.0
        _FrontLightStrength ("Hue-preserving Front Light", Range(0,0.12)) = 0.055
        _DepthTint ("Hue-shifted Depth Tint", Range(0,1)) = 0.14
        // I blend cap and shade colours over the visible fill height. This only changes colour.
        _VolumeGradient ("Top-to-bottom Liquid Gradient", Range(0,1)) = 0.0
        _BodyShade ("Depth Shade", Range(0,1)) = 0
        _DepthRange ("Depth Range", Float) = 2.0
        // I sample the interior mask to shade the real walls; quad-edge distance fails on tapered glasses.
        _WallShade ("Inner Wall Shade", Range(0,1)) = 0.10
        _WallWidth ("Inner Wall Width", Float) = 0.05
        // The key light is up and to the left, so the left wall catches far less shade.
        _WallBias ("Lit Side Wall Shade", Range(0,1)) = 0.45
        // I bend the liquid's own lighting near the walls for cheap refraction. No scene grab or extra pass is
        // needed.
        _RefractionStrength ("Inner Wall Refraction", Range(0,0.4)) = 0.16
        _RefractionWidthPx ("Refraction Width (screen pixels)", Range(0.5,4)) = 2.4
        _BaseRefraction ("Glass Base Thickness", Range(0,0.4)) = 0.12
        _FloorShade ("Floor Shade", Range(0,1)) = 0.0
        // Dark colours get a small floor bounce. Pale colours get body shading when their caps cannot brighten any
        // further.
        _RoundShade ("Volume Shade", Range(0,1)) = 0.0
        // I darken the body when the cap cannot brighten enough to stand out.
        _BodySettle ("Body Settle", Range(0.5,1)) = 1.0
        _LiquidSaturation ("Liquid Saturation", Range(1,1.5)) = 1.15
        // These only change colour, not alpha or liquid geometry.
        _BandRampStrength ("Per-Band Top / Body / Depth Ramp", Range(0,1)) = 0.0
        _InnerVolumeLight ("Soft Inner Volume Light", Range(0,0.5)) = 0.0
        // Floor bounce is off by default because it can look like light leaking onto the glass art.
        _FloorGlow ("Floor Glow", Range(0,1)) = 0.0
        _FloorGlowWidth ("Floor Glow Width", Float) = 0.26
        _FloorGlowLift ("Floor Caustic Lightness", Range(0,1)) = 0.35
        _FloorGlowFocus ("Floor Caustic Horizontal Focus", Range(0,1)) = 0.72
        _CausticHeightPx ("Floor Caustic Height (Royal pixels)", Range(0,64)) = 24
        // Optional shadow between colours. Zero keeps the stack seamless; it never opens a gap.
        _BoundaryShade ("Boundary Depth", Range(0,1)) = 0.0
        _CapRim ("Cap Rim Light", Range(0,1)) = 0.58
        _FarRim ("Far Meniscus Light", Range(0,1)) = 0.36
        // I outline the exposed disc so its curve stays visible on a small phone screen.
        _SurfaceLine ("Surface Ellipse Highlight", Range(0,1)) = 0.82
        _SurfaceLineWidthPx ("Surface Ellipse Width (screen pixels)", Range(0.5,2.5)) = 1.45
        // Optional art setting. Contact FX keeps it at zero so a local splash cannot bleach the whole surface.
        _CapFlash ("Top Face Flash", Range(0,1)) = 0.0
        // Allows HDR glints for bloom. 1 keeps the output clamped.
        _Overbright ("Glint Overbright", Range(1,4)) = 1.0
        _Shine ("Top Glint", Range(0,1)) = 0.0
        _ShineX ("Top Glint X", Range(-1,1)) = -0.15
        _ShineWidth ("Top Glint Width", Range(0.01,1)) = 0.42
        // I draw a glint and, on wide surfaces, a hollow bubble in the liquid pass so both stay inside the mask.
        _SurfaceAccentStrength ("Tiny Surface Accents", Range(0,1)) = 0.58
        // Floating-ice data: xyz is gravity-aligned liquid geometry; w is the faded strength.
        [PerRendererData] _FloatingGarnishCaustic ("Floating Garnish Caustic", Vector) = (0,0,0,0)
        _FloatingGarnishCausticColor ("Floating Garnish Caustic Colour", Color) = (0.92,0.98,1.0,1)
        // x is the profile strength; y is a stable glass phase. C# disables x during transfers.
        [PerRendererData] _AmbientBubbleParams ("Ambient Rising Bubble", Vector) = (0,0,0,0)
        // Local foam and post-return colour reveal use separate effects without moving the waterline.
        _RevealTurbulenceAmount ("Hidden Reveal Turbulence Amount", Range(0,1)) = 0.0
        _RevealTurbulenceLife ("Hidden Reveal Turbulence Life", Range(0,1)) = 0.0
        // Completion uses its own clock so SetUnits cannot cut it off or change contact geometry.
        [PerRendererData] _CompletionFxProgress ("Completion FX Progress", Range(0,1)) = 0.0
        [PerRendererData] _CompletionFxColor ("Completion FX Colour", Color) = (1,0.72,0.24,1)
        _Alpha ("Alpha", Range(0,1)) = 1.0
        _QuadSize ("Quad Size", Vector) = (1,1,0,0)
        // (anchorX, spriteLocalWidth, designPixels, unused) matches RoyalGlassSprite stretch. y = 1, z = 0 means
        // no stretch.
        [PerRendererData] _GlassExpansion ("Glass Expansion", Vector) = (0,1,0,0)
        [PerRendererData] _RightInteriorClip ("Right Interior Clip", Vector) = (0,0,0,0)
        [PerRendererData] _BottomInteriorInset ("Bottom Interior Inset (local)", Float) = 0
        [PerRendererData] _BottomInteriorFloor ("Bottom Interior Floor (local)", Float) = -9999
        // xy is the local-x range; z enables it. Each glass supplies 32 y samples for its own arc.
        [PerRendererData] _LiquidFloorRange ("Curved Liquid Floor Range", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            #define MAX_BANDS 8

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;
            };

            sampler2D _MaskTex;

            // (u0, v0, du, dv) sub rect of the mask texture used by this bottle.
            float4 _MaskUV;
            // rgb = band colour.
            float4 _BandColor[MAX_BANDS];
            // Top face per band. w is 1 when the colour was authored, 0 to derive it.
            float4 _BandCap[MAX_BANDS];
            // Dense, hue-shifted colour used at the lower depth and inner walls.
            float4 _BandShade[MAX_BANDS];
            // x = waterline height, y = chord centre, z = chord half width (liquid frame).
            float4 _BandInfo[MAX_BANDS];
            float _BandCount;
            // Rotation that maps object space into the liquid frame, in radians.
            float _Angle;
            float2 _Interior;

            float _Bulge;
            float _InnerCurve;
            float _SurfaceScale;
            float _CapWallInset;
            float _RoyalUnitsPerPixel;
            float _InnerBulge;
            float _InnerMax;
            float _BulgeMax;
            float _CapValue;
            float _CapDesat;
            float _CapDesatPale;
            float _CapFalloff;
            float _EdgeShade;
            float _CylinderKey;
            float _CylinderShade;
            fixed4 _OverheadColor;
            float _OverheadStrength;
            float _FrontLightStrength;
            float _DepthTint;
            float _VolumeGradient;
            float _BodyShade;
            float _DepthRange;
            float _WallShade;
            float _WallWidth;
            float _WallBias;
            float _RefractionStrength;
            float _RefractionWidthPx;
            float _BaseRefraction;
            float _FloorShade;
            float _RoundShade;
            float _BodySettle;
            float _LiquidSaturation;
            float _BandRampStrength;
            float _InnerVolumeLight;
            float _FloorGlow;
            float _FloorGlowWidth;
            float _FloorGlowLift;
            float _FloorGlowFocus;
            float _CausticHeightPx;
            float _BoundaryShade;
            float _CapRim;
            float _FarRim;
            float _SurfaceLine;
            float _SurfaceLineWidthPx;
            float _CapFlash;
            float _Overbright;
            float _Shine;
            float _ShineX;
            float _ShineWidth;
            float _SurfaceAccentStrength;
            float4 _FloatingGarnishCaustic;
            fixed4 _FloatingGarnishCausticColor;
            float4 _AmbientBubbleParams;
            float _RevealTurbulenceAmount;
            float _RevealTurbulenceLife;
            float _CompletionFxProgress;
            float4 _CompletionFxColor;
            float _Alpha;
            float2 _QuadSize;
            float4 _GlassExpansion;
            float4 _RightInteriorClip;
            float _BottomInteriorInset;
            float _BottomInteriorFloor;
            float4 _LiquidFloorRange;
            float _LiquidFloorSamples[32];

            /// <summary>I calculate screen pixels per local unit like RoyalGlassSprite so both passes stretch equally.</summary>
            float PixelsPerLocalX(float4 vertex)
            {
                float4 here = UnityObjectToClipPos(vertex);
                float4 stepX = UnityObjectToClipPos(vertex + float4(1, 0, 0, 0));
                float2 screenHere = here.xy / max(abs(here.w), 1e-5)
                                  * (0.5 * _ScreenParams.xy);
                float2 screenStep = stepX.xy / max(abs(stepX.w), 1e-5)
                                  * (0.5 * _ScreenParams.xy);
                return max(length(screenStep - screenHere), 1e-3);
            }

            v2f vert(appdata v)
            {
                v2f o;

                // I stretch POSITION to match the front glass. The authored local coordinates stay unchanged for
                // all liquid math.
                float designScale = max(0.5, min(_ScreenParams.x / 720.0,
                                                 _ScreenParams.y / 1280.0));
                float spriteWidth = max(_GlassExpansion.y, 1e-4);
                float contentLocal = max(_GlassExpansion.z, 0.0) * designScale
                                   / PixelsPerLocalX(v.vertex);
                float scaleX = (spriteWidth + contentLocal * 2.0) / spriteWidth;

                float4 stretched = v.vertex;
                stretched.x = _GlassExpansion.x
                            + (v.vertex.x - _GlassExpansion.x) * scaleX;

                o.pos = UnityObjectToClipPos(stretched);
                o.uv = v.uv;
                o.local = v.vertex.xy;
                return o;
            }

            /// <summary>I scale all channels together for fallback cap lighting, preserving the hue.</summary>
            float3 LitFace(float3 c)
            {
                float v = max(c.r, max(c.g, c.b));
                float targetValue = min(1.0, v * _CapValue);
                return v > 1e-4 ? saturate(c * (targetValue / v)) : c;
            }

            // I boost chroma in the 0..1 base, then restore HDR excess so _Overbright still works.
            float3 BoostSaturation(float3 c)
            {
                float3 baseColor = saturate(c);
                float3 hdrExcess = max(c - baseColor, 0.0);
                float hi = max(baseColor.r, max(baseColor.g, baseColor.b));
                float lo = min(baseColor.r, min(baseColor.g, baseColor.b));
                float chroma = hi - lo;
                if (chroma < 1e-4) return baseColor + hdrExcess;
                // I keep a 4% colour floor on saturated endpoints to avoid neon clipping and Gamma/LDR banding.
                float saturationCeiling = max(
                    1.0, 0.96 * hi / max(chroma, 1e-4));
                float scale = min(max(_LiquidSaturation, 1.0),
                                  saturationCeiling);
                float3 saturatedBase = saturate(
                    hi.xxx + (baseColor - hi.xxx) * scale);
                return saturatedBase + hdrExcess;
            }

            // I draw antialiased contact drops in this pass, without particles or per-pour allocations.
            float DropCoverage(float2 p, float2 centre, float radius)
            {
                float distanceToEdge = length(p - centre) - radius;
                float aa = max(fwidth(distanceToEdge), radius * 0.12);
                return 1.0 - smoothstep(-aa, aa, distanceToEdge);
            }

            // I size this soft ellipse in liquid-frame units with a pixel minimum so small shelves keep it
            // visible.
            float SurfaceEllipseCoverage(float2 p, float2 centre, float2 radii)
            {
                float2 safeRadii = max(radii, float2(1e-5, 1e-5));
                float normalizedDistance = length((p - centre) / safeRadii) - 1.0;
                float distanceToEdge = normalizedDistance * min(safeRadii.x, safeRadii.y);
                float aa = max(fwidth(distanceToEdge), min(safeRadii.x, safeRadii.y) * 0.18);
                return 1.0 - smoothstep(-aa, aa, distanceToEdge);
            }

            // I randomize each bubble once per cycle so its lane, size and wobble stay steady during a rise.
            float2 AmbientNoise(float lane, float cycleIndex)
            {
                float2 seed = float2(lane * 37.719 + cycleIndex * 11.413,
                                     lane * 91.377 - cycleIndex * 7.239);
                float2 f = frac(sin(float2(dot(seed, float2(127.1, 311.7)),
                                           dot(seed, float2(269.5, 183.3))))
                                * 43758.5453);
                return f;
            }

            // One completion bubble rises using fixed offsets and drifts, without _Time or random state.
            float CompletionBubble(float2 p, float clock, float start,
                                   float travelTime, float startX, float drift,
                                   float radius)
            {
                float phase = saturate((clock - start) / max(travelTime, 1e-4));
                float appear = smoothstep(
                    start, start + travelTime * 0.10, clock);
                float disappear = 1.0 - smoothstep(
                    start + travelTime * 0.78, start + travelTime, clock);
                float2 centre = float2(
                    startX + drift * phase * phase,
                    lerp(0.045, 0.88, phase));
                float outer = DropCoverage(p, centre, radius);
                float inner = DropCoverage(p, centre, radius * 0.58);
                return saturate(outer - inner * 0.92) * appear * disappear;
            }

            // Staggered open rings make the liquid churn without textures or particles.
            float CurlCoverage(float2 p, float2 centre, float radius,
                               float thickness, float2 openingDirection)
            {
                float2 q = p - centre;
                float qLength = length(q);
                float distanceToRing = abs(qLength - radius) - thickness;
                float aa = max(fwidth(distanceToRing), thickness * 0.38);
                float ring = 1.0 - smoothstep(-aa, aa, distanceToRing);
                float2 direction = q / max(qLength, 1e-4);
                float openArc = smoothstep(-0.48, -0.04,
                    dot(direction, openingDirection));
                return ring * openArc;
            }

            // I clip handle holes with the profile's body wall. Keep these mask coordinates vessel-local; only
            // waterline math uses the rotated liquid frame.
            float RightInteriorCoverage(float2 local)
            {
                float distance = _RightInteriorClip.y
                               + _RightInteriorClip.z * local.y
                               - local.x;
                float aa = max(fwidth(distance),
                               max(_QuadSize.x / 2048.0, 1e-5));
                float coverage = smoothstep(-aa, aa, distance);
                return lerp(1.0, coverage, saturate(_RightInteriorClip.x));
            }

            float SampleInteriorMaskRaw(float2 uv, float2 local)
            {
                float sampled = tex2D(
                    _MaskTex, _MaskUV.xy + uv * _MaskUV.zw).a;
                return sampled * RightInteriorCoverage(local);
            }

            float SampleInteriorMask(float2 uv, float2 local)
            {
                float sampled = SampleInteriorMaskRaw(uv, local);
                // I sample the mask a few Royal pixels lower to trim base leaks without changing waterlines or the
                // quad.
                float inset = max(_BottomInteriorInset, 0.0);
                float enabled = step(1e-5, inset);
                float2 downLocal = float2(0.0, -inset);
                float2 downUv = downLocal
                    / max(_QuadSize, float2(1e-4, 1e-4));
                float sampledBelow = SampleInteriorMaskRaw(
                    uv + downUv, local + downLocal);
                float aa = max(fwidth(local.y) * 0.75,
                    max(_RoyalUnitsPerPixel * 0.45, 1e-5));
                float bottomWindow = 1.0 - smoothstep(
                    _BottomInteriorFloor + inset,
                    _BottomInteriorFloor + inset + aa * 2.0,
                    local.y);
                float eroded = min(sampled, sampledBelow);
                return lerp(sampled, eroded, enabled * bottomWindow);
            }

            float SampleLiquidFloorY(float localX)
            {
                float width = max(_LiquidFloorRange.y - _LiquidFloorRange.x,
                    1e-5);
                // Samples cluster near wall/floor corners. Invert x = .5 - .5*cos(pi*t) to find their interval.
                float normalizedX = saturate(
                    (localX - _LiquidFloorRange.x) / width);
                float samplePosition = acos(clamp(1.0 - 2.0 * normalizedX,
                    -1.0, 1.0)) * (31.0 * 0.31830988618);
                int sample0 = min((int)floor(samplePosition), 30);
                float sampleBlend = saturate(samplePosition - sample0);
                return lerp(_LiquidFloorSamples[sample0],
                    _LiquidFloorSamples[sample0 + 1], sampleBlend);
            }

            half4 frag(v2f i) : SV_Target
            {
                // I sample the body and lower cap masks later. Clipping here would remove the back half of the
                // surface.

                // Object space to world-aligned liquid space, including slosh. ly is the world-height axis.
                float sa, ca;
                sincos(_Angle, sa, ca);
                float ly = i.local.x * sa + i.local.y * ca;
                float lx = i.local.x * ca - i.local.y * sa;

                int count = min(max((int)_BandCount, 0), MAX_BANDS);
                clip((float)count - 0.5);
                int topIndex = max(count - 1, 0);

                // A covered colour keeps one convex crest; a full lit ellipse would make it look like a separate
                // floating disc.
                float3 bodyColor = _BandColor[0].rgb;
                float4 firstCap = _BandCap[0];
                float3 bodyKeyColor = lerp(LitFace(bodyColor), firstCap.rgb, firstCap.w);
                float3 bodyShadeColor = _BandShade[0].rgb;
                float boundaryContact = 0.0;
                int activeBandIndex = 0;

                for (int k = 0; k < MAX_BANDS - 1; k++)
                {
                    if (k >= count - 1) break;

                    float4 info = _BandInfo[k];
                    float halfChord = max(info.z, 1e-4);
                    float chord = halfChord * 2.0;
                    float across = saturate(abs(lx - info.y) / halfChord);
                    float ellipse = sqrt(saturate(1.0 - across * across));
                    // I use the disc's near half for covered boundaries, dipping at the centre. A plus sign would
                    // expose the far rim and make the lower colour bulge upward.
                    float halfDepth = min(
                        chord * max(_InnerBulge, 0.001), _InnerMax) * _InnerCurve;
                    float nearY = info.x - halfDepth * ellipse;
                    float signedBoundary = ly - nearY;
                    float aa = max(fwidth(signedBoundary) * 0.85, chord * 0.0015);
                    float crossed = smoothstep(-aa, aa, signedBoundary);

                    // I shade only the shared colour edge. Use 2.125 * _RoyalUnitsPerPixel for a scale-independent
                    // AA floor and cap it at twice the authored width.
                    float contactFloor = _RoyalUnitsPerPixel > 0.0
                        ? _RoyalUnitsPerPixel * 2.125
                        : aa * 2.5;
                    float contactWidth = max(chord * 0.009,
                                             min(contactFloor, chord * 0.018));
                    // I keep the inner AA ramp narrow so it cannot become a thick stripe on small glasses.
                    float contactBand = 1.0 - smoothstep(
                        min(aa * 0.35, contactWidth * 0.25),
                        contactWidth, abs(signedBoundary));
                    float3 nextBodyColor = _BandColor[k + 1].rgb;
                    // I skip contact shadows between matching colours so they remain one solid volume.
                    float3 colourDelta = abs(bodyColor - nextBodyColor);
                    float colourDistance = max(colourDelta.r,
                        max(colourDelta.g, colourDelta.b));
                    float differentColour = smoothstep(0.004, 0.020,
                        colourDistance);
                    boundaryContact = max(boundaryContact,
                        contactBand * differentColour);

                    float4 nextCap = _BandCap[k + 1];
                    float3 nextKeyColor = lerp(LitFace(nextBodyColor), nextCap.rgb, nextCap.w);
                    float3 nextShadeColor = _BandShade[k + 1].rgb;
                    bodyColor = lerp(bodyColor, nextBodyColor, crossed);
                    bodyKeyColor = lerp(bodyKeyColor, nextKeyColor, crossed);
                    bodyShadeColor = lerp(bodyShadeColor, nextShadeColor, crossed);
                    // This index only controls colour; the AA transition and geometry stay unchanged.
                    if (crossed >= 0.5)
                        activeBandIndex = k + 1;
                }

                float4 topInfo = _BandInfo[topIndex];
                float topHalfChord = max(topInfo.z, 1e-4);
                float topChord = topHalfChord * 2.0;
                // BandInfo keeps the exact volume chord. Only the visible face uses the fixed Royal-local inset;
                // unpublished materials fall back to screen derivatives.
                float localUnitsPerPixel = _RoyalUnitsPerPixel > 0.0
                    ? _RoyalUnitsPerPixel
                    : length(float2(ddx(lx), ddy(lx)));
                float capWallInset = min(
                    localUnitsPerPixel * max(_CapWallInset, 0.0),
                    topHalfChord * 0.15);
                float capHalfChord = max(topHalfChord - capWallInset, 1e-4);
                float capEdgeDistance = capHalfChord - abs(lx - topInfo.y);
                float capEdgeAA = max(
                    fwidth(capEdgeDistance) * 0.75,
                    topChord * 0.0015);
                float capCoverage = smoothstep(
                    -capEdgeAA, capEdgeAA, capEdgeDistance);

                float topAcross = saturate(abs(lx - topInfo.y) / capHalfChord);
                float topEllipse = sqrt(saturate(1.0 - topAcross * topAcross));
                float topHalfDepth = min(topChord * max(_Bulge, 0.001), _BulgeMax)
                                   * saturate(_SurfaceScale);
                // I keep the cap still and draw the contact mesh above it.
                float topCentre = topInfo.x;
                float nearTop = topCentre - topHalfDepth * topEllipse;
                float farTop = topCentre + topHalfDepth * topEllipse;

                float outerDistance = ly - farTop;
                float outerAA = max(fwidth(outerDistance), topChord * 0.0015);
                float liquidCoverage = 1.0 - smoothstep(-outerAA, outerAA, outerDistance);

                float surfaceDistance = ly - nearTop;
                float surfaceAA = max(fwidth(surfaceDistance) * 0.85, topChord * 0.0015);
                float surface = smoothstep(-surfaceAA, surfaceAA, surfaceDistance);
                surface *= capCoverage;

                // I combine the projected cap with the current-row mask so sloping walls cannot cut the surface
                // short.
                float2 quadSize = max(_QuadSize, float2(1e-4, 1e-4));
                float bodyMask = SampleInteriorMask(i.uv, i.local);

                float rise = max(0.0, ly - nearTop);
                float2 shiftLocal = float2(-rise * sa, -rise * ca);
                float2 shift = shiftLocal / quadSize;
                float2 surfaceUv = i.uv + shift;
                float2 surfaceLocal = i.local + shiftLocal;
                float2 capInsetLocal = float2(ca, -sa) * capWallInset;
                float2 capInsetUv = capInsetLocal / quadSize;
                float surfaceMask = SampleInteriorMaskRaw(
                    surfaceUv, surfaceLocal);
                float surfaceMaskLeft = SampleInteriorMaskRaw(
                    surfaceUv - capInsetUv, surfaceLocal - capInsetLocal);
                float surfaceMaskRight = SampleInteriorMaskRaw(
                    surfaceUv + capInsetUv, surfaceLocal + capInsetLocal);
                surfaceMask = min(surfaceMask, min(surfaceMaskLeft, surfaceMaskRight));
                surfaceMask = max(surfaceMask, bodyMask);

                float mask = lerp(bodyMask, surfaceMask, surface);
                // I clip the final result at the inner cavity floor. Feather only inward, using vessel-local
                // coordinates so nothing leaks into the base at tilt.
                float liquidFloorY = SampleLiquidFloorY(i.local.x);
                float liquidFloorDistance = i.local.y - liquidFloorY;
                float liquidFloorAA = max(fwidth(liquidFloorDistance),
                    max(_RoyalUnitsPerPixel, 1e-5));
                float liquidFloorCoverage = smoothstep(
                    0.0, liquidFloorAA, liquidFloorDistance);
                float floorCoverage = lerp(1.0, liquidFloorCoverage,
                    step(0.5, _LiquidFloorRange.z));
                mask *= floorCoverage;
                clip(mask - (1.0 / 255.0));

                // I sample the mask left, right and below to find wall/floor contact on any vessel shape.
                float2 sideStepLocal = float2(_WallWidth, 0.0);
                float2 floorStepLocal = float2(0.0, _WallWidth * 1.5);
                float2 sideStep = sideStepLocal / quadSize;
                float2 floorStep = floorStepLocal / quadSize;
                float openLeft = SampleInteriorMask(
                    i.uv - sideStep, i.local - sideStepLocal);
                float openRight = SampleInteriorMask(
                    i.uv + sideStep, i.local + sideStepLocal);
                float openBelow = SampleInteriorMask(
                    i.uv - floorStep, i.local - floorStepLocal);
                float wallAO = saturate((1.0 - openRight) + (1.0 - openLeft) * _WallBias);
                float floorAO = saturate(1.0 - openBelow);

                float2 n = i.local / max(_Interior, float2(1e-4, 1e-4));

                // Two sample distances soften the caustic. Royal-pixel units keep its relative size consistent.
                float causticReach = _RoyalUnitsPerPixel > 0.0
                    ? _RoyalUnitsPerPixel * max(_CausticHeightPx, 0.0)
                    : max(_FloorGlowWidth, 1e-4);
                float2 glowStepLocal = float2(0.0, causticReach);
                float2 glowStep = glowStepLocal / quadSize;
                float glowNear = SampleInteriorMask(
                    i.uv - glowStep * 0.45,
                    i.local - glowStepLocal * 0.45);
                float glowFar = SampleInteriorMask(
                    i.uv - glowStep, i.local - glowStepLocal);

                // I use QuadRect padding to blend between the two floor taps, avoiding stepped bands without a
                // third texture read.
                const float surfaceAllowancePadding = 0.8 * 1.15;
                float quadBottom = i.local.y - i.uv.y * quadSize.y;
                float cavityBottom = quadBottom + 0.02
                    + _BulgeMax * surfaceAllowancePadding;
                float causticT = saturate(
                    (i.local.y - cavityBottom) / max(causticReach, 1e-4));
                float tapBlend = smoothstep(0.45, 1.0, causticT);
                float sampledOpening = saturate(
                    1.0 - lerp(glowFar, glowNear, tapBlend));
                float floorGlow = (1.0 - smoothstep(0.0, 1.0, causticT))
                                * sampledOpening;
                floorGlow *= floorGlow;
                float horizontalFocus = 1.0 - smoothstep(
                    saturate(_FloorGlowFocus), 1.0, saturate(abs(n.x)));
                float edge = saturate(abs(n.x));
                float depth = saturate((topInfo.x - ly) / max(_DepthRange, 1e-4));

                // I blend each fill from its cap colour to its shade. Upright fills use the visible floor; tilted
                // fills ease back to gravity-frame depth.
                float gradientFloorObjectY = lerp(
                    cavityBottom, liquidFloorY, step(0.5, _LiquidFloorRange.z));
                float gradientFloorLiquidY =
                    i.local.x * sa + gradientFloorObjectY * ca;
                float normalizedVolumeDepth = saturate(
                    (topInfo.x - ly)
                    / max(topInfo.x - gradientFloorLiquidY,
                        localUnitsPerPixel * 4.0));
                float pourGradientBlend = smoothstep(0.35, 0.82, abs(sa));
                float volumeDepth = lerp(
                    normalizedVolumeDepth, depth, pourGradientBlend);

                // A broad left key and a soft right shade give the bands one cylindrical shape, using their own
                // palette colours.
                float cylinderKey = 1.0 - smoothstep(0.0, 0.62, abs(n.x + 0.38));
                float cylinderShade = smoothstep(-0.05, 0.95, n.x);
                cylinderShade *= cylinderShade;
                bodyColor = lerp(bodyColor, bodyKeyColor, _CylinderKey * cylinderKey);
                bodyColor = lerp(bodyColor, bodyShadeColor,
                    saturate(_CylinderShade * cylinderShade));

                float volumeTopLight = 1.0 - smoothstep(
                    0.0, 0.42, volumeDepth);
                float volumeBottomShade = smoothstep(
                    0.22, 1.0, volumeDepth);
                bodyColor = lerp(bodyColor, bodyKeyColor,
                    saturate(_VolumeGradient * 0.52 * volumeTopLight));
                bodyColor = lerp(bodyColor, bodyShadeColor,
                    saturate(_VolumeGradient * 0.58 * volumeBottomShade));

                // I rebuild each band's visible bounds from the shared curves to shade it separately. Only RGB
                // changes.
                float activeBandTopY = nearTop;
                if (activeBandIndex < topIndex)
                {
                    float4 activeInfo = _BandInfo[activeBandIndex];
                    float activeHalfChord = max(activeInfo.z, 1e-4);
                    float activeAcross = saturate(
                        abs(lx - activeInfo.y) / activeHalfChord);
                    float activeEllipse = sqrt(saturate(
                        1.0 - activeAcross * activeAcross));
                    float activeHalfDepth = min(
                        activeHalfChord * 2.0 * max(_InnerBulge, 0.001),
                        _InnerMax) * _InnerCurve;
                    activeBandTopY = activeInfo.x
                        - activeHalfDepth * activeEllipse;
                }

                float activeBandBottomY = gradientFloorLiquidY;
                if (activeBandIndex > 0)
                {
                    float4 lowerInfo = _BandInfo[activeBandIndex - 1];
                    float lowerHalfChord = max(lowerInfo.z, 1e-4);
                    float lowerAcross = saturate(
                        abs(lx - lowerInfo.y) / lowerHalfChord);
                    float lowerEllipse = sqrt(saturate(
                        1.0 - lowerAcross * lowerAcross));
                    float lowerHalfDepth = min(
                        lowerHalfChord * 2.0 * max(_InnerBulge, 0.001),
                        _InnerMax) * _InnerCurve;
                    activeBandBottomY = lowerInfo.x
                        - lowerHalfDepth * lowerEllipse;
                }

                float activeBandSpan = max(
                    activeBandTopY - activeBandBottomY,
                    localUnitsPerPixel * 3.0);
                float bandDepth = saturate(
                    (activeBandTopY - ly) / activeBandSpan);
                float bandTopLight = 1.0 - smoothstep(0.02, 0.46, bandDepth);
                float bandDepthTone = smoothstep(0.58, 0.98, bandDepth);
                float bodyOnly = 1.0 - surface;
                // I fade the local ramp at the AA hand-off so changing activeBandIndex cannot leave a one-pixel
                // seam.
                float bandRampGate = bodyOnly
                    * (1.0 - saturate(boundaryContact));
                bodyColor = lerp(bodyColor, bodyKeyColor,
                    saturate(_BandRampStrength * 0.21
                        * bandTopLight * bandRampGate));
                bodyColor = lerp(bodyColor, bodyShadeColor,
                    saturate(_BandRampStrength * 0.30
                        * bandDepthTone * bandRampGate));

                // I use the band's TOP colour for a broad inner glow in gravity-aligned liquid space.
                float volumeAcross = (lx - topInfo.y) / topHalfChord;
                float innerLightAcross = 1.0 - smoothstep(
                    0.06, 0.96, abs(volumeAcross + 0.18));
                float innerLightHeight = 1.0 - smoothstep(
                    0.06, 0.82, volumeDepth);
                float innerVolumeLight = innerLightAcross * innerLightAcross
                    * lerp(0.42, 1.0, innerLightHeight) * bodyOnly;
                bodyColor = lerp(bodyColor, bodyKeyColor,
                    saturate(_InnerVolumeLight * innerVolumeLight));

                // _LiquidSaturation affects the whole body while preserving value. TOP and DEPTH still control
                // lighting.
                bodyColor = BoostSaturation(bodyColor);

                // I shade with the palette's saturated depth colour. The volume gradient reduces the old depth
                // tint so the floor is not darkened twice.
                float residualDepthTint = _DepthTint * (1.0 - _VolumeGradient);
                bodyColor = lerp(bodyColor, bodyShadeColor,
                    saturate(residualDepthTint * depth * depth));
                bodyColor = lerp(bodyColor, bodyShadeColor,
                    saturate(_BodyShade * depth));
                bodyColor = lerp(bodyColor, bodyShadeColor,
                    saturate(_EdgeShade * edge * edge));
                bodyColor = lerp(bodyColor, bodyShadeColor,
                    saturate(_BoundaryShade * boundaryContact));

                float3 topColor = _BandColor[topIndex].rgb;
                float3 topShadeColor = _BandShade[topIndex].rgb;
                // I centre surface shading on the live chord, since handled glasses can have offset pivots and
                // masks.
                float capX = (lx - topInfo.y) / capHalfChord;
                float capEdge = saturate(abs(capX));
                float capCylinderShade = smoothstep(-0.05, 0.95, capX);
                capCylinderShade *= capCylinderShade;
                // I scale the band colour by value so the fallback keeps its hue.
                float3 derivedCap = LitFace(topColor);
                // I prefer authored cap colours so already-bright liquids still have a visible top face.
                float4 authoredCap = _BandCap[topIndex];
                float3 surfaceColor = lerp(derivedCap, authoredCap.rgb, authoredCap.w);
                surfaceColor = BoostSaturation(surfaceColor);
                surfaceColor *= 1.0 - (_EdgeShade * 0.30) * capEdge * capEdge;
                surfaceColor *= 1.0 - (_CylinderShade * 0.25) * capCylinderShade;

                // The lip and glint use the cap colour; white belongs to the glass pass. Bound the AA minimum so
                // small glasses keep the same rim spacing.
                float capSpanFloor = _RoyalUnitsPerPixel > 0.0
                    ? _RoyalUnitsPerPixel * 1.7   // == surfaceAA * 2.0 at Royal's framing
                    : surfaceAA * 2.0;
                float capSpan = max(farTop - nearTop,
                                    min(capSpanFloor, topHalfDepth * 0.5 + 1e-5));
                float capT = saturate(surfaceDistance / capSpan);

                // I fade the far rim toward the band colour so the top face can never become darker than its body.
                surfaceColor = lerp(surfaceColor, lerp(topColor, surfaceColor, _CapFalloff), capT);

                float capRim = (1.0 - smoothstep(0.0, 0.10, capT)) * surface;
                float3 liquidAccent = BoostSaturation(surfaceColor) * 1.06;
                surfaceColor = lerp(surfaceColor, liquidAccent,
                    saturate(_CapRim * capRim + _CapFlash * surface));

                // A softer far-side highlight adds gloss inside the surface mask without changing the waterline.
                float farRim = saturate(1.0 - abs(capT - 0.88) / 0.10);
                farRim *= farRim * surface;
                surfaceColor = lerp(surfaceColor, liquidAccent,
                    saturate(_FarRim * farRim));

                // _ShineX and _ShineWidth are chord-local top-glint controls too.
                float glintX = saturate(1.0 - abs(capX - _ShineX) / max(_ShineWidth, 1e-4));
                float glintY = saturate(1.0 - abs(capT - 0.34) / 0.085);
                float glint = glintX * glintX * glintY * glintY * surface;
                surfaceColor = lerp(surfaceColor, liquidAccent,
                    saturate(_Shine * glint));
                // Only the glint may exceed 0..1, so bloom cannot light up the whole liquid body.
                surfaceColor *= 1.0 + (_Overbright - 1.0) * glint;

                // I clip the two idle details to the top face in liquid space and hide them when the glass tips.
                float screenPixelLocal = max(
                    length(float2(ddx(lx), ddy(lx))),
                    length(float2(ddx(ly), ddy(ly))));
                float accentRadius = max(
                    localUnitsPerPixel * 1.70,
                    screenPixelLocal * 1.35);
                float bubbleAcross = 0.30;
                float glintAcross = -0.24;
                float bubbleArc = sqrt(saturate(1.0 - bubbleAcross * bubbleAcross));
                float glintArc = sqrt(saturate(1.0 - glintAcross * glintAcross));
                float2 liquidPoint = float2(lx, ly);
                float2 bubbleCentre = float2(
                    topInfo.y + capHalfChord * bubbleAcross,
                    topCentre + topHalfDepth * bubbleArc * 0.10);
                float2 glintCentre = float2(
                    topInfo.y + capHalfChord * glintAcross,
                    topCentre - topHalfDepth * glintArc * 0.12);
                float bubbleOuter = DropCoverage(
                    liquidPoint, bubbleCentre, accentRadius * 1.55);
                float bubbleInner = DropCoverage(
                    liquidPoint, bubbleCentre, accentRadius * 0.82);
                float bubbleAccent = saturate(bubbleOuter - bubbleInner * 0.96);
                float glintAccent = SurfaceEllipseCoverage(
                    liquidPoint, glintCentre,
                    float2(accentRadius * 1.75, accentRadius * 0.52));

                // Only wide, settled surfaces show the second ring. Both details fade out during a pour.
                float chordRoom = smoothstep(
                    accentRadius * 8.0, accentRadius * 13.0, topChord);
                float settled = 1.0 - smoothstep(
                    0.12, 0.20, abs(sin(_Angle)));
                float accentGate = _SurfaceAccentStrength
                    * smoothstep(0.35, 0.80, _SurfaceScale)
                    * settled * surface * capCoverage
                    // I hide the generic glint and bubble until the floating-ice reflection fades.
                    * (1.0 - smoothstep(0.008, 0.045,
                        _FloatingGarnishCaustic.w));
                float tinyAccent = max(
                    glintAccent * 0.72,
                    bubbleAccent * 0.50 * chordRoom);
                surfaceColor = lerp(surfaceColor, liquidAccent,
                    saturate(tinyAccent * accentGate));

                float surfaceShading = surface;
                float3 c = lerp(bodyColor, surfaceColor, surfaceShading);

                float3 activeShadeColor = lerp(
                    bodyShadeColor, topShadeColor, surfaceShading);

                // The cap gets only some wall shade. Floor shade stays on the body.
                c = lerp(c, activeShadeColor,
                    saturate(_WallShade * wallAO
                        * lerp(1.0, 0.35, surfaceShading)));
                // I darken the body by any cap-lighting shortfall so pale liquids still look round. Dark colours
                // keep their existing contrast.
                float bodyValue = max(c.r, max(c.g, c.b));
                float wantedLift = bodyValue * max(_CapValue - 1.0, 1e-4);
                float gotLift = min(1.0, bodyValue * _CapValue) - bodyValue;
                float headroom = saturate(gotLift / max(wantedLift, 1e-4));
                float roundShade = _RoundShade * (1.0 - headroom);

                // If the cap is already too bright to lift, I darken the body to keep its edge visible.
                c *= 1.0 - (1.0 - _BodySettle) * (1.0 - headroom)
                   * (1.0 - surfaceShading);

                c = lerp(c, activeShadeColor,
                    saturate(roundShade * wallAO
                        * lerp(1.0, 0.35, surfaceShading)));
                c = lerp(c, activeShadeColor,
                    saturate(roundShade * 0.70 * floorAO
                        * (1.0 - surfaceShading)));

                c = lerp(c, activeShadeColor,
                    saturate(_FloorShade * floorAO
                        * (1.0 - surfaceShading)));
                // I apply a soft camera-facing fill by scaling RGB equally, preserving hue and saturation.
                float bodyFrontFacing = 1.0 - smoothstep(
                    0.55, 1.0, abs(n.x));
                float capFrontFacing = 1.0 - smoothstep(
                    0.55, 1.0, abs(capX));
                float frontFacing = lerp(
                    bodyFrontFacing, capFrontFacing, surfaceShading);
                float frontAmount = saturate(_FrontLightStrength)
                                  * lerp(0.55, 1.0, frontFacing)
                                  * lerp(1.0, 0.60, surfaceShading);
                float frontPeak = max(c.r, max(c.g, c.b));
                float frontTargetPeak = frontPeak
                    + (1.0 - saturate(frontPeak)) * frontAmount;
                c *= frontPeak > 1e-4
                    ? frontTargetPeak / frontPeak
                    : 1.0;

                // The 4*t*(1-t) curve boosts chroma only between fully lit and shaded regions.
                float keyTransition = 4.0 * cylinderKey * (1.0 - cylinderKey)
                    * saturate(_CylinderKey * 8.0);
                float depthTransition = 4.0 * depth * (1.0 - depth)
                    * saturate(_DepthTint * 6.0);
                float wallTransition = 4.0 * wallAO * (1.0 - wallAO)
                    * saturate(_WallShade * 6.0);
                float capTransition = 4.0 * capT * (1.0 - capT)
                    * surfaceShading;
                float chromaTransition = saturate(max(
                    max(keyTransition, depthTransition),
                    max(wallTransition, capTransition))
                    + _BoundaryShade * boundaryContact);
                c = lerp(c, BoostSaturation(c), chromaTransition);
                float causticAmount = saturate(
                    _FloorGlow * floorGlow * horizontalFocus)
                    * (1.0 - surfaceShading);
                float3 causticColor = BoostSaturation(bodyKeyColor)
                    * lerp(1.0, 1.10, saturate(_FloorGlowLift));
                c = lerp(c, causticColor, causticAmount);

                // I sample the real wall for a 2-3 pixel refraction cue using the liquid's key/shade colours.
                // Geometry and alpha stay unchanged.
                float refractionReach = max(
                    screenPixelLocal * max(_RefractionWidthPx, 0.5),
                    topChord * 0.0015);
                float2 refractionStepLocal = float2(refractionReach, 0.0);
                float2 refractionStepUv = refractionStepLocal / quadSize;
                // bodyMask already handles erosion, so side taps can use one raw mask read each.
                float refractOpenLeft = SampleInteriorMaskRaw(
                    i.uv - refractionStepUv,
                    i.local - refractionStepLocal);
                float refractOpenRight = SampleInteriorMaskRaw(
                    i.uv + refractionStepUv,
                    i.local + refractionStepLocal);
                float refractLeft = saturate(1.0 - refractOpenLeft);
                float refractRight = saturate(1.0 - refractOpenRight);
                float sideLens = saturate(max(refractLeft, refractRight))
                               * bodyMask * liquidCoverage
                               * (1.0 - surfaceShading);

                // I keep the left wall lighter and the right wall deeper to match the upper-left key, using
                // palette colours.
                float3 refractedLight = BoostSaturation(bodyKeyColor);
                float3 refractedShade = BoostSaturation(bodyShadeColor);
                float3 refractedColor = lerp(
                    refractedLight, refractedShade,
                    saturate(refractRight * 0.62));
                float sideRefraction = saturate(
                    _RefractionStrength * sideLens
                    * lerp(0.86, 1.0, refractLeft));
                c = lerp(c, refractedColor, sideRefraction);

                // A wider lens follows the curved floor and stays inside the liquid mask.
                float baseLens = 1.0 - smoothstep(
                    0.0, refractionReach * 1.15,
                    max(liquidFloorDistance, 0.0));
                baseLens *= floorCoverage * bodyMask * liquidCoverage
                          * (1.0 - surfaceShading)
                          * step(0.5, _LiquidFloorRange.z);
                float3 baseRefractedColor = BoostSaturation(
                    lerp(c, bodyKeyColor, 0.58));
                c = lerp(c, baseRefractedColor,
                    saturate(_BaseRefraction * baseLens));

                // I measure the ice lens from nearTop so it follows the curved front edge, with a softer lobe on
                // the cap.
                UNITY_BRANCH
                if (_FloatingGarnishCaustic.w > 1e-4)
                {
                    float causticHalfWidth = max(
                        _FloatingGarnishCaustic.y,
                        localUnitsPerPixel * 2.0);
                    float causticDepth = max(
                        _FloatingGarnishCaustic.z,
                        localUnitsPerPixel * 2.0);
                    float causticAcross =
                        (lx - _FloatingGarnishCaustic.x) / causticHalfWidth;
                    float causticAcrossSq = causticAcross * causticAcross;

                    float bodyDrop = max(nearTop - ly, 0.0) / causticDepth;
                    float bodyLens = saturate(
                        1.0 - causticAcrossSq - bodyDrop * bodyDrop);
                    bodyLens *= bodyLens * (1.0 - surfaceShading);

                    float capHalfHeight = max(
                        topHalfDepth * 1.10,
                        localUnitsPerPixel * 2.0);
                    float capDrop = (ly - topCentre) / capHalfHeight;
                    float capLens = saturate(
                        1.0 - causticAcrossSq - capDrop * capDrop);
                    capLens *= capLens * surfaceShading * 0.34;

                    // I reduce the ice light on bright colours so pale layers do not turn white.
                    float liquidValue = max(c.r, max(c.g, c.b));
                    float headroomProtection = lerp(
                        1.0, 0.55,
                        smoothstep(0.65, 0.95, liquidValue));
                    float iceLight = saturate(max(bodyLens, capLens)
                        * _FloatingGarnishCaustic.w
                        * headroomProtection
                        * liquidCoverage);
                    float3 iceLiquidLight = BoostSaturation(
                        lerp(c, bodyKeyColor, 0.72)) * 1.05;
                    c = lerp(c, iceLiquidLight, saturate(iceLight * 0.46));
                }

                // I clip the rising bubble to the floor, mask and live fill. The last quarter-cycle stays empty to
                // hide the wrap back to the bottom.
                UNITY_BRANCH
                if (_AmbientBubbleParams.x > 1e-4)
                {
                    // Strength controls bubble count and frequency as well as opacity. Zero disables the work.
                    float ambientStrength = saturate(_AmbientBubbleParams.x);
                    float ambientPeriod = lerp(4.25, 2.75, ambientStrength);
                    // Fixed lanes and phase offsets keep bubbles apart and avoid an obvious repeating beat.
                    const float3 ambientLaneAcross = float3(0.22, -0.31, 0.06);
                    const float3 ambientPhaseOffset = float3(0.0, 0.41, 0.73);
                    float3 ambientWeight = float3(
                        1.0,
                        smoothstep(0.26, 0.60, ambientStrength),
                        smoothstep(0.55, 0.92, ambientStrength));
                    float ambientTotal = 0.0;

                    [unroll]
                    for (int ambientIndex = 0; ambientIndex < 3; ambientIndex++)
                    {
                        // I multiply by weight at the end to keep dynamic branches out of the unrolled loop.
                        float laneWeight = ambientWeight[ambientIndex];

                        float ambientClock = _Time.y / ambientPeriod
                            + _AmbientBubbleParams.y
                            + ambientPhaseOffset[ambientIndex];
                        float ambientCycle = frac(ambientClock);
                        // I randomize once per rise so the path changes between bubbles without jittering.
                        float2 ambientRoll = AmbientNoise(
                            ambientPhaseOffset[ambientIndex], floor(ambientClock));
                        float ambientTravel = saturate(ambientCycle / 0.72);
                        ambientTravel = ambientTravel * ambientTravel
                            * (3.0 - 2.0 * ambientTravel);
                        float ambientAppear = smoothstep(
                            0.0, 0.055, ambientCycle);
                        float ambientDisappear = 1.0 - smoothstep(
                            0.57, 0.72, ambientCycle);

                        float ambientAcross = ambientLaneAcross[ambientIndex]
                            + (ambientRoll.x - 0.5) * 0.34;
                        float ambientLaneX = topInfo.y
                            + capHalfChord * ambientAcross;
                        float ambientLaneEllipse = sqrt(saturate(
                            1.0 - ambientAcross * ambientAcross));
                        float ambientTopY = topCentre
                            - topHalfDepth * ambientLaneEllipse;
                        float sampledAmbientFloor = SampleLiquidFloorY(ambientLaneX);
                        float ambientFloorY = lerp(
                            _BottomInteriorFloor,
                            sampledAmbientFloor,
                            step(0.5, _LiquidFloorRange.z));

                        // The lead bubble is the fattest; the followers read as its wake.
                        float ambientSizeScale = lerp(1.18, 0.82,
                            ambientIndex * 0.5) * lerp(0.84, 1.26, ambientRoll.y);
                        // I give bubbles a pixel minimum so they stay visible in small glasses.
                        float ambientRadius = max(
                            localUnitsPerPixel * 3.0,
                            topChord * 0.042) * ambientSizeScale;
                        ambientRadius *= lerp(1.0, 0.62,
                            smoothstep(0.54, 0.72, ambientCycle));
                        float ambientBottomY = ambientFloorY + ambientRadius * 1.6;
                        float ambientEndY = ambientTopY - ambientRadius * 1.45;
                        float ambientRoom = ambientEndY - ambientBottomY;
                        float ambientDepthGate = smoothstep(
                            ambientRadius * 3.5,
                            ambientRadius * 7.0,
                            ambientRoom);
                        // Two soft lobes make the bubble sway upward instead of sliding sideways.
                        float ambientWobblePhase = ambientRoll.x * UNITY_PI * 2.0;
                        float ambientDrift = sin(ambientTravel * UNITY_PI)
                            * capHalfChord * 0.035
                            + sin(ambientTravel * UNITY_PI * 4.0 + ambientWobblePhase)
                            * capHalfChord * lerp(0.012, 0.030, ambientRoll.y)
                            * sin(ambientTravel * UNITY_PI);
                        float2 ambientBubbleCentre = float2(
                            ambientLaneX + ambientDrift,
                            lerp(ambientBottomY, ambientEndY, ambientTravel));
                        float ambientOuter = DropCoverage(
                            liquidPoint, ambientBubbleCentre, ambientRadius);
                        float ambientInner = DropCoverage(
                            liquidPoint, ambientBubbleCentre,
                            ambientRadius * 0.53);
                        float ambientRing = saturate(
                            ambientOuter - ambientInner * 0.96);

                        ambientTotal = max(ambientTotal,
                            ambientRing * ambientAppear * ambientDisappear
                            * ambientDepthGate * laneWeight);
                    }

                    float ambientGate = ambientTotal * settled
                        * mask * liquidCoverage * (1.0 - surface)
                        * (1.0 - saturate(_RevealTurbulenceAmount * 4.0))
                        * (1.0 - saturate(_CompletionFxProgress * 4.0));
                    float ambientAmount = saturate(
                        ambientGate * lerp(0.62, 0.86, ambientStrength));
                    // I brighten the ring toward white so it stands out from the liquid.
                    float3 ambientBubbleColour = lerp(
                        liquidAccent, float3(1.0, 1.0, 1.0), 0.42);
                    c = lerp(c, ambientBubbleColour, ambientAmount);
                }

                // Hidden-colour reveal uses its own branch and current-chord units, so clearing contact FX cannot
                // reset it.
                UNITY_BRANCH
                if (_RevealTurbulenceAmount > 1e-4)
                {
                    float activeTurbulenceAmount = _RevealTurbulenceAmount;
                    float churnLife = saturate(_RevealTurbulenceLife);
                    float churnCentreX = topInfo.y;
                    float topBandFloor = topInfo.x - topChord * 0.62;
                    if (topIndex > 0)
                        topBandFloor = _BandInfo[topIndex - 1].x;
                    float topBandSpan = max(topInfo.x - topBandFloor,
                                            topChord * 0.24);
                    float centreDrop = min(topChord * 0.46,
                                           topBandSpan * 0.62);
                    float centreY = topInfo.x - centreDrop
                                  + topChord * lerp(-0.025, 0.045, churnLife);
                    float2 churnPoint = float2(
                        (lx - churnCentreX) / topChord,
                        (ly - centreY) / topChord);

                    // I reveal the shape with one rotation and expansion, without flashing.
                    float spin = churnLife * UNITY_PI * 1.10;
                    float spinSin, spinCos;
                    sincos(spin, spinSin, spinCos);
                    float2 rotatedChurn = float2(
                        churnPoint.x * spinCos + churnPoint.y * spinSin,
                       -churnPoint.x * spinSin + churnPoint.y * spinCos);
                    float expansion = 1.0 - pow(1.0 - churnLife, 3.0);
                    float2 motifPoint = rotatedChurn
                                      / lerp(1.05, 1.55, expansion);
                    float churn = 0.0;
                    churn = max(churn, CurlCoverage(motifPoint,
                        float2(-0.105, 0.050), 0.050, 0.010,
                        float2(0.72, 0.69)));
                    churn = max(churn, CurlCoverage(motifPoint,
                        float2(0.000, 0.092), 0.041, 0.009,
                        float2(-0.78, 0.63)));
                    churn = max(churn, CurlCoverage(motifPoint,
                        float2(0.108, 0.028), 0.054, 0.010,
                        float2(-0.35, -0.94)));
                    churn = max(churn, CurlCoverage(motifPoint,
                        float2(-0.052, -0.065), 0.045, 0.009,
                        float2(0.90, -0.43)));
                    churn = max(churn, CurlCoverage(motifPoint,
                        float2(0.070, -0.073), 0.037, 0.008,
                        float2(-0.88, -0.48)));
                    churn = max(churn, CurlCoverage(motifPoint,
                        float2(0.008, -0.005), 0.030, 0.008,
                        float2(0.20, 0.98)));
                    churn = max(churn, DropCoverage(motifPoint,
                        float2(-0.158, -0.010), 0.014));
                    churn = max(churn, DropCoverage(motifPoint,
                        float2(0.158, 0.082), 0.012));
                    churn = max(churn, DropCoverage(motifPoint,
                        float2(0.128, -0.100), 0.010));
                    float scatter = smoothstep(0.10, 0.50, churnLife)
                                  * (1.0 - smoothstep(0.72, 1.0, churnLife));
                    churn = max(churn, DropCoverage(motifPoint,
                        float2(-0.235, 0.105), 0.009) * scatter);
                    churn = max(churn, DropCoverage(motifPoint,
                        float2(0.245, -0.055), 0.008) * scatter);
                    churn = max(churn, DropCoverage(motifPoint,
                        float2(0.035, 0.245), 0.008) * scatter);
                    churn = max(churn, DropCoverage(motifPoint,
                        float2(-0.070, -0.235), 0.009) * scatter);

                    float bandAA = max(fwidth(ly), topChord * 0.002);
                    float topBandGate = topIndex > 0
                        ? smoothstep(topBandFloor - bandAA,
                                     topBandFloor + bandAA * 2.0, ly)
                        : 1.0;
                    float churnCoverage = saturate(churn * activeTurbulenceAmount)
                                        * topBandGate * (1.0 - surface)
                                        * liquidCoverage;
                    float3 revealColor = BoostSaturation(bodyKeyColor) * 1.06;
                    c = lerp(c, revealColor, churnCoverage);
                }

                // Completion bubbles rise once after the external helix, clipped to the liquid and exact vessel
                // mask.
                UNITY_BRANCH
                if (_CompletionFxProgress > 1e-4)
                {
                    // I use the external effect's normalized clock so shader timing follows C# duration changes.
                    float completionClock = saturate(_CompletionFxProgress);
                    float quadLeft = i.local.x - i.uv.x * quadSize.x;
                    float completionAspect = quadSize.x / quadSize.y;
                    float2 completionPoint = float2(
                        (i.local.x - (quadLeft + quadSize.x * 0.5)) / quadSize.y,
                        (i.local.y - quadBottom) / quadSize.y);
                    float bubble = 0.0;
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.300, 0.440,
                        -completionAspect * 0.31, completionAspect * 0.08, 0.010));
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.365, 0.500,
                         completionAspect * 0.19, -completionAspect * 0.06, 0.014));
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.405, 0.360,
                         completionAspect * 0.40, -completionAspect * 0.10, 0.010));
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.430, 0.455,
                        -completionAspect * 0.05, completionAspect * 0.04, 0.019));
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.485, 0.430,
                         completionAspect * 0.34, -completionAspect * 0.09, 0.024));
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.505, 0.480,
                        -completionAspect * 0.22, completionAspect * 0.09, 0.016));
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.545, 0.410,
                        -completionAspect * 0.38, completionAspect * 0.07, 0.012));
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.590, 0.360,
                         completionAspect * 0.03, completionAspect * 0.03, 0.022));
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.615, 0.340,
                         completionAspect * 0.12, -completionAspect * 0.05, 0.013));
                    bubble = max(bubble, CompletionBubble(completionPoint,
                        completionClock, 0.700, 0.270,
                        -completionAspect * 0.12, completionAspect * 0.04, 0.020));

                    float bubbleGate = bubble * liquidCoverage
                                     * (1.0 - surface) * mask;
                    float3 bubbleColour = lerp(
                        _CompletionFxColor.rgb, float3(1.0, 1.0, 1.0), 0.30);
                    c = lerp(c, bubbleColour, saturate(bubbleGate * 0.34));
                    c += bubbleColour * bubbleGate * 0.02;
                }

                // I outline only the exposed ellipse. max joins its halves without bright tips, and derivatives
                // keep the line near one screen pixel.
                float surfaceLineFloor = topChord * 0.00075;
                float nearSurfaceLineWidth = max(
                    fwidth(surfaceDistance), surfaceLineFloor)
                    * max(_SurfaceLineWidthPx, 0.5);
                float farSurfaceLineWidth = max(
                    fwidth(outerDistance), surfaceLineFloor)
                    * max(_SurfaceLineWidthPx, 0.5);
                float nearSurfaceLine = 1.0 - smoothstep(
                    nearSurfaceLineWidth * 0.12, nearSurfaceLineWidth,
                    abs(surfaceDistance));
                float farSurfaceLine = 1.0 - smoothstep(
                    farSurfaceLineWidth * 0.12, farSurfaceLineWidth,
                    abs(outerDistance));
                float endpointQuiet = lerp(0.68, 1.0,
                    1.0 - smoothstep(0.84, 0.99, topAcross));
                float surfaceLine = max(nearSurfaceLine, farSurfaceLine * 0.76)
                                  * capCoverage * endpointQuiet;
                c = lerp(c, liquidAccent,
                    saturate(_SurfaceLine * surfaceLine));

                float alpha = mask * liquidCoverage * _Alpha;
                clip(alpha - (1.0 / 255.0));
                return half4(max(c, 0.0), alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
