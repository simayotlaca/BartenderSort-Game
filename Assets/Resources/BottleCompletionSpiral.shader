// One clock drives the ring, ribbon and sparkles across the front and back passes.
Shader "LiquidSort/BottleCompletionSpiral"
{
    Properties
    {
        [PerRendererData] _Progress ("Progress", Range(0,1)) = 0
        [PerRendererData] _Duration ("Duration", Float) = 1.78
        [PerRendererData] _Tint ("Liquid Accent", Color) = (0.52,0.92,1,1)
        [PerRendererData] _FrontPass ("Front Pass", Range(0,1)) = 1
        [PerRendererData] _Aspect ("Quad Width / Height", Float) = 0.42
        [PerRendererData] _BodyBottom ("Body Bottom", Range(0,1)) = 0.05
        [PerRendererData] _BodyTop ("Body Top", Range(0,1)) = 0.78
        [PerRendererData] _BodyRadius ("Body Radius", Float) = 0.18
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
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            float _Progress;
            float _Duration;
            float4 _Tint;
            float _FrontPass;
            float _Aspect;
            float _BodyBottom;
            float _BodyTop;
            float _BodyRadius;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float Hash11(float value)
            {
                return frac(sin(value * 91.173 + 17.139) * 43758.5453);
            }

            float SparkleGrid(float2 pointValue, float seconds)
            {
                float2 grid = pointValue * 24.0;
                float2 cell = floor(grid);
                float seed = dot(cell, float2(37.17, 91.73));
                float2 randomPoint = 0.12 + 0.76 * float2(
                    Hash11(seed + 2.31), Hash11(seed + 7.93));
                float distanceToBead = length(frac(grid) - randomPoint);
                float radius = lerp(0.16, 0.31, Hash11(seed + 13.47));
                float aa = max(fwidth(distanceToBead), 0.045);
                float bead = 1.0 - smoothstep(
                    radius - aa, radius + aa, distanceToBead);
                float present = step(0.34, Hash11(seed + 21.19));
                float twinkle = 0.58 + 0.42 * saturate(
                    sin(seconds * 27.0 + Hash11(seed + 31.61) * 6.28318)
                    * 0.5 + 0.5);
                return bead * present * twinkle;
            }

            float CircleFromDistance(float distanceToCentre, float radius,
                                     float derivativeWidth)
            {
                float edgeDistance = distanceToCentre - radius;
                float aa = max(derivativeWidth, radius * 0.12);
                return 1.0 - smoothstep(-aa, aa, edgeDistance);
            }

            float Circle(float2 pointValue, float2 centre, float radius)
            {
                float distanceToCentre = length(pointValue - centre);
                return CircleFromDistance(
                    distanceToCentre, radius, fwidth(distanceToCentre));
            }

            float Line(float distanceToLine, float halfWidth)
            {
                float aa = max(fwidth(distanceToLine), halfWidth * 0.14);
                return 1.0 - smoothstep(halfWidth - aa,
                                        halfWidth + aa, abs(distanceToLine));
            }

            float EllipseRing(float2 pointValue, float2 centre,
                              float2 radius, float halfWidth)
            {
                float2 safeRadius = max(radius, float2(1e-4, 1e-4));
                float normalizedDistance = length((pointValue - centre) / safeRadius);
                float distanceToRing = abs(normalizedDistance - 1.0)
                                     * min(safeRadius.x, safeRadius.y);
                return Line(distanceToRing, halfWidth);
            }

            float PassGate(float depth)
            {
                float front = smoothstep(-0.08, 0.08, depth);
                return lerp(1.0 - front, front, step(0.5, _FrontPass));
            }

            float EaseOutCubic(float value)
            {
                float inverse = 1.0 - saturate(value);
                return 1.0 - inverse * inverse * inverse;
            }

            half4 frag(v2f i) : SV_Target
            {
                const float Tau = UNITY_PI * 2.0;
                float seconds = saturate(_Progress) * max(_Duration, 1e-3);
                float safeAspect = max(_Aspect, 0.04);
                // y is one quad height and x uses that same physical unit. This keeps
                // beads round on both the short shot glass and the tall handled glass.
                float2 p = float2((i.uv.x - 0.5) * safeAspect, i.uv.y);
                float bottom = saturate(_BodyBottom);
                float top = max(bottom + 0.08, saturate(_BodyTop));
                float span = top - bottom;
                float bodyRadius = max(_BodyRadius, span * 0.04);
                float passStrength = lerp(0.58, 1.0, step(0.5, _FrontPass));
                // Ribbon widths scale with bodyRadius, so short and tall glasses look consistent.
                float ribbonAuraWidth = bodyRadius * 0.18;
                float ribbonPaleWidth = bodyRadius * 0.14;
                float ribbonColourWidth = bodyRadius * 0.12;
                float ribbonHotWidth = bodyRadius * 0.085;
                float2 sparkleCoordinates = float2(
                    p.x / max(span, 1e-4), (p.y - bottom) / span);
                float sparkleGrain = SparkleGrid(sparkleCoordinates, seconds);

                float warmAura = 0.0;
                float creamBand = 0.0;
                float colourCore = 0.0;
                float whiteCore = 0.0;
                float gold = 0.0;

                // 0.00-0.56 s: a quick left-originating floor-ring draw leaves a soft
                // tinted base while the ribbon begins climbing out of it at 90 ms.
                float ringSweep = saturate(seconds / 0.09);
                float2 ringCentre = float2(0.0, bottom + span * 0.020);
                float2 ringRadius = float2(bodyRadius * 1.05, span * 0.060);
                float2 ringQ = (p - ringCentre) / max(ringRadius, 1e-4);
                float ringAngle = atan2(ringQ.y, ringQ.x);
                float angularPosition = frac((ringAngle + 2.38) / Tau);
                float sweepGate = 1.0 - smoothstep(
                    ringSweep, ringSweep + 0.050, angularPosition);
                float ringLife = smoothstep(0.00, 0.035, seconds)
                               * (1.0 - smoothstep(0.40, 0.56, seconds));
                float ringGate = sweepGate * ringLife * PassGate(-ringQ.y);
                warmAura += EllipseRing(
                    p, ringCentre, ringRadius, span * 0.052) * ringGate * 0.58;
                creamBand += EllipseRing(
                    p, ringCentre, ringRadius, span * 0.028) * ringGate * 0.88;
                colourCore += EllipseRing(
                    p, ringCentre, ringRadius, span * 0.016) * ringGate * 0.92;
                whiteCore += EllipseRing(
                    p, ringCentre, ringRadius, span * 0.0065) * ringGate * 0.78;
                float ringDust = EllipseRing(
                    p, ringCentre, ringRadius, span * 0.092)
                    * ringGate * sparkleGrain;
                warmAura += ringDust * 0.32;
                creamBand += ringDust * 0.62;
                colourCore += ringDust * 0.46;
                whiteCore += ringDust * 0.82;

                // Rise: 0.09-1.12 s. Heights 0.25/0.75 map to 0.5/1.9 turns.
                float lower = EaseOutCubic(saturate((seconds - 0.09) / 0.17));
                float middle = smoothstep(0.0, 1.0,
                    saturate((seconds - 0.26) / 0.68));
                float neck = EaseOutCubic(saturate((seconds - 0.94) / 0.18));
                float headVertical = 0.015 + 0.235 * lower
                                   + 0.500 * middle + 0.235 * neck;
                float headY = bottom + span * headVertical;
                float vertical = saturate((p.y - bottom) / span);
                float verticalGate = smoothstep(bottom - span * 0.018,
                                                bottom + span * 0.012, p.y)
                                   * (1.0 - smoothstep(headY,
                                                      headY + span * 0.018, p.y));
                float helixLife = smoothstep(0.09, 0.15, seconds)
                                * (1.0 - smoothstep(1.12, 1.42, seconds));
                float turns = 2.4 * vertical - 0.10 * sin(Tau * vertical);
                float phase = Tau * turns - 0.78;
                float turnsDerivative = 2.4
                    - 0.20 * UNITY_PI * cos(Tau * vertical);
                float phaseSin, phaseCos;
                sincos(phase, phaseSin, phaseCos);
                float radiusShape = lerp(0.76, 1.0,
                    smoothstep(0.0, 0.24, vertical));
                radiusShape *= lerp(1.0, 0.88,
                    smoothstep(0.88, 1.0, vertical));
                float radius = bodyRadius * radiusShape;
                float curveSlope = phaseCos * radius
                                 * (Tau * turnsDerivative) / span;
                float distanceScale = rsqrt(1.0 + curveSlope * curveSlope);
                float curveDistance = (p.x - phaseSin * radius) * distanceScale;
                float layer = PassGate(phaseCos);
                float headDistance = max(headY - p.y, 0.0);
                float nearHead = 1.0 - smoothstep(span * 0.02,
                                                 span * 0.25, headDistance);
                float trailStrength = verticalGate * helixLife
                                    * lerp(0.82, 1.0, nearHead);
                warmAura += Line(curveDistance, ribbonAuraWidth)
                          * layer * trailStrength * 0.62;
                creamBand += Line(curveDistance, ribbonPaleWidth)
                           * layer * trailStrength * 0.90;
                colourCore += Line(curveDistance, ribbonColourWidth)
                            * layer * trailStrength * 0.96;
                whiteCore += Line(curveDistance, ribbonHotWidth)
                           * layer * trailStrength * lerp(0.76, 1.0, nearHead);

                float dustLife = smoothstep(0.09, 0.15, seconds)
                               * (1.0 - smoothstep(1.42, 1.76, seconds));
                float dustEnvelope = Line(curveDistance, bodyRadius * 0.25)
                                   * layer * verticalGate * dustLife
                                   * sparkleGrain
                                   * lerp(0.72, 1.18,
                                       smoothstep(0.48, 1.08, seconds));
                warmAura += dustEnvelope * 0.36;
                creamBand += dustEnvelope * 0.70;
                colourCore += dustEnvelope * 0.54;
                whiteCore += dustEnvelope * 0.94;

                // One moving head follows the strand.
                float headTurns = 2.4 * headVertical
                    - 0.10 * sin(Tau * headVertical);
                float headPhase = Tau * headTurns - 0.78;
                float headSin, headCos;
                sincos(headPhase, headSin, headCos);
                float headRadiusShape = lerp(0.76, 1.0,
                    smoothstep(0.0, 0.24, headVertical));
                headRadiusShape *= lerp(1.0, 0.88,
                    smoothstep(0.88, 1.0, headVertical));
                float2 headPoint = float2(
                    headSin * bodyRadius * headRadiusShape, headY);
                float headWindow = smoothstep(0.09, 0.15, seconds)
                                 * (1.0 - smoothstep(1.12, 1.30, seconds))
                                 * PassGate(headCos);
                warmAura += Circle(p, headPoint, bodyRadius * 0.195)
                          * headWindow * 0.60;
                creamBand += Circle(p, headPoint, bodyRadius * 0.16)
                           * headWindow * 0.88;
                colourCore += Circle(p, headPoint, bodyRadius * 0.14)
                            * headWindow * 0.92;
                whiteCore += Circle(p, headPoint, bodyRadius * 0.10)
                           * headWindow;

                [unroll]
                for (int sparkleIndex = 0; sparkleIndex < 8; sparkleIndex++)
                {
                    float index = (float)sparkleIndex + 1.0;
                    float seedY = lerp(0.08, 0.94, Hash11(index * 1.73));
                    float birth = 0.18 + seedY * 0.70
                                + Hash11(index * 3.11) * 0.12;
                    float life = lerp(0.48, 0.78, Hash11(index * 5.27));
                    float sparkleAge = saturate((seconds - birth) / life);
                    float sparkleLife = smoothstep(0.0, 0.12, sparkleAge)
                                      * (1.0 - smoothstep(0.68, 1.0, sparkleAge))
                                      * step(birth, seconds)
                                      * (1.0 - smoothstep(1.70, 1.78, seconds));
                    float sparkleVertical = saturate(seedY + sparkleAge * 0.075);
                    float sparkleTurns = 2.4 * sparkleVertical
                        - 0.10 * sin(Tau * sparkleVertical);
                    float sparklePhase = Tau * sparkleTurns - 0.78;
                    float sparkleSin, sparkleCos;
                    sincos(sparklePhase, sparkleSin, sparkleCos);
                    float sparkleRadiusShape = lerp(0.76, 1.0,
                        smoothstep(0.0, 0.24, sparkleVertical));
                    sparkleRadiusShape *= lerp(1.0, 0.88,
                        smoothstep(0.88, 1.0, sparkleVertical));
                    float sparkleX = sparkleSin * bodyRadius * sparkleRadiusShape
                                   + (Hash11(index * 7.43) - 0.5)
                                     * bodyRadius * 0.42;
                    float sparkleY = bottom + sparkleVertical * span;
                    float sparkleLayer = PassGate(sparkleCos);
                    float sparkleRadius = bodyRadius * lerp(
                        0.012, 0.032, Hash11(index * 9.19));
                    float sparkleGate = sparkleLife * sparkleLayer;
                    float sparkleDistance = length(
                        p - float2(sparkleX, sparkleY));
                    float sparkleDerivative = fwidth(sparkleDistance);
                    warmAura += CircleFromDistance(sparkleDistance,
                        sparkleRadius * 2.5, sparkleDerivative)
                        * sparkleGate * 0.30;
                    creamBand += CircleFromDistance(sparkleDistance,
                        sparkleRadius * 1.6, sparkleDerivative)
                        * sparkleGate * 0.58;
                    colourCore += CircleFromDistance(sparkleDistance,
                        sparkleRadius * 1.16, sparkleDerivative)
                        * sparkleGate * 0.56;
                    whiteCore += CircleFromDistance(sparkleDistance,
                        sparkleRadius, sparkleDerivative)
                        * sparkleGate * 0.92;
                }

                // A front-rim glint runs at 1.04-1.32 s before the check badge.
                float glintLayer = step(0.5, _FrontPass);
                float glintLife = smoothstep(1.04, 1.10, seconds)
                                * (1.0 - smoothstep(1.22, 1.32, seconds));
                float glintTravel = EaseOutCubic(
                    saturate((seconds - 1.04) / 0.24));
                float2 glintCentre = float2(
                    lerp(-bodyRadius * 0.92, bodyRadius * 0.92, glintTravel),
                    top + span * lerp(-0.025, 0.025, glintTravel));
                float2 glintPoint = p - glintCentre;
                float glintWindow = 1.0 - smoothstep(
                    span * 0.035, span * 0.095, length(glintPoint));
                float slashA = Line(glintPoint.x * 0.38 - glintPoint.y,
                                    span * 0.0032);
                float slashB = Line(glintPoint.x * 0.38 + glintPoint.y,
                                    span * 0.0020);
                float glint = max(slashA, slashB * 0.18)
                            * glintWindow * glintLife * glintLayer;
                warmAura += glint * 0.18;
                creamBand += glint * 0.58;
                whiteCore += glint * 0.74;
                gold += glint * 0.22;

                float auraWeight = saturate(warmAura * 0.78);
                float creamWeight = saturate(creamBand * 0.96);
                float colourWeight = saturate(colourCore * 0.94);
                float whiteWeight = saturate(whiteCore);
                float goldWeight = saturate(gold * 0.72);

                // Blend glow as coverage to preserve depth and avoid white overlap.
                float auraAlpha = auraWeight * 0.14;
                float creamAlpha = creamWeight * 0.24;
                float colourAlpha = colourWeight * 0.46;
                float whiteAlpha = whiteWeight * 0.68;
                float glintAlpha = goldWeight * 0.28;
                float transmittance = (1.0 - auraAlpha)
                                    * (1.0 - creamAlpha)
                                    * (1.0 - colourAlpha)
                                    * (1.0 - whiteAlpha)
                                    * (1.0 - glintAlpha);
                float alpha = (1.0 - transmittance) * passStrength * 0.98;
                clip(alpha - (1.0 / 255.0));

                float weight = max(auraWeight + creamWeight + colourWeight
                                 + whiteWeight + goldWeight, 1e-4);
                float3 accent = saturate(_Tint.rgb);
                float3 paleAccent = lerp(accent, float3(1.0, 1.0, 1.0), 0.10);
                float3 hotAccent = lerp(accent, float3(1.0, 1.0, 1.0), 0.18);
                float3 rgb = (accent * auraWeight
                            + paleAccent * creamWeight
                            + accent * colourWeight
                            + hotAccent * whiteWeight
                            + hotAccent * goldWeight) / weight;

                // The project's bloom threshold is above 1.0. Let only the tiny front
                // core and individual beads cross it; the broad aura and rear pass stay
                // LDR, so the flourish reads brightly without washing out the vessel.
                float hotCore = smoothstep(0.45, 0.72, whiteWeight)
                              * smoothstep(0.42, 0.72, alpha)
                              * step(0.5, _FrontPass);
                float3 premultiplied = saturate(rgb) * alpha
                                     + hotAccent * hotCore * 0.34;
                premultiplied = min(
                    premultiplied, float3(1.16, 1.16, 1.16));
                return half4(premultiplied, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
