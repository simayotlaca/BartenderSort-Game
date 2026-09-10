// I light only painted glass pixels, using source alpha and a local wall mask to keep the liquid centre clear.
Shader "LiquidSort/GlassThinFX"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _FxColor ("Key Glass Light", Color) = (0.82,0.94,1,1)
        _FxColor2 ("Fill Glass Light", Color) = (0.36,0.66,1,1)
        _SideStrength ("Side Strength", Range(0,1)) = 0.42
        _LightAngle ("Key Light Angle", Range(0,360)) = 135
        _BottomStrength ("Bottom Lens Strength", Range(0,1)) = 0.0
        _SideStart ("Side Start", Range(0,1)) = 0.28
        _SideFull ("Side Full", Range(0,1)) = 0.78
        _BottomBelow ("Floor Seam Search Margin Below", Range(0.001,0.25)) = 0.015
        _BottomHeight ("Floor Seam Search Margin Above", Range(0.001,0.35)) = 0.015
        [HideInInspector] _InteriorRect ("Interior Rect", Vector) = (-0.5,-0.5,0.5,0.5)
        [HideInInspector] _VisibleFloorY ("Optical Liquid Floor Y", Float) = -10000
        [HideInInspector] _VisibleBottomY ("Visible Liquid Bottom Y", Float) = -10000
        [HideInInspector] _MaskTex ("Interior Shape", 2D) = "black" {}
        [HideInInspector] _MaskRect ("Mask Local Rect", Vector) = (-0.5,-0.5,1,1)
        [HideInInspector] _MaskReach ("Mask Probe UV Reach", Vector) = (0.03,0.03,0,0)
        [HideInInspector] _UseMask ("Use Interior Shape", Float) = 0
        [HideInInspector] _AccessoryFx ("Handle, Stem/Foot, Feather, Stem Toon", Vector) = (0,0,0.025,0)
        [HideInInspector] _BottomRimStrength ("Lower Glass Mass Light", Range(0,1)) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
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
            // I disable dynamic batching because the fragment mask needs each vessel's object-space coordinates.
            "DisableBatching" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnityCG.cginc"
            #include "UnitySprites.cginc"

            struct appdata_fx
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f_fx
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float2 localPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _FxColor;
            fixed4 _FxColor2;
            float _SideStrength;
            float _LightAngle;
            float _BottomStrength;
            float _SideStart;
            float _SideFull;
            float _BottomBelow;
            float _BottomHeight;
            float4 _InteriorRect;
            float _VisibleFloorY;
            float _VisibleBottomY;
            float4 _MainTex_TexelSize;
            sampler2D _MaskTex;
            float4 _MaskRect;
            float4 _MaskReach;
            float _UseMask;
            float4 _AccessoryFx;
            float _BottomRimStrength;

            inline float GlassGauss(float value, float width)
            {
                float q = value / max(width, 1e-4);
                return exp(-q * q);
            }

            v2f_fx vert(appdata_fx input)
            {
                v2f_fx output;
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

            fixed4 frag(v2f_fx input) : SV_Target
            {
                fixed4 source = SampleSpriteTexture(input.texcoord);
                fixed sourceAlpha = source.a;
                float2 size = max(_InteriorRect.zw - _InteriorRect.xy, float2(1e-4, 1e-4));
                float2 p = (input.localPos - _InteriorRect.xy) / size;
                float angle = _LightAngle * (UNITY_PI / 180.0);
                float2 keyDirection = float2(cos(angle), sin(angle));

                // I sample the baked cavity edge and its gradient to find side walls and floors, excluding the
                // broad rim and handles.
                float sideDistance = abs(p.x * 2.0 - 1.0);
                // I start side light above the visible floor so it cannot form square columns on the base.
                float sideStartY = _VisibleBottomY + size.x * 0.018;
                float sideFullY = _VisibleBottomY + size.x * 0.075;
                float bodyGate = smoothstep(sideStartY, sideFullY, input.localPos.y)
                               * (1.0 - smoothstep(0.80, 0.98, p.y));
                // I search between the optical floor and first visible liquid row; source alpha selects the curved
                // seam within that range.
                float below = max(_BottomBelow * size.x, 1e-3);
                float above = max(_BottomHeight * size.x, 1e-3);
                float searchMin = min(_VisibleFloorY, _VisibleBottomY) - below;
                float searchMax = max(_VisibleFloorY, _VisibleBottomY) + above;
                float lowerFeather = max(below * 0.35, 1e-3);
                float upperFeather = max(above * 0.35, 1e-3);
                float bottomWindow = smoothstep(searchMin, searchMin + lowerFeather,
                                                input.localPos.y)
                                   * (1.0 - smoothstep(searchMax - upperFeather, searchMax,
                                                      input.localPos.y));

                // I detect the cavity-to-glass alpha edge across four source texels so it survives filtering and
                // downscaling without becoming a strip.
                float edgeStep = max(_MainTex_TexelSize.y * 4.0, 1e-5);
                float alphaAbove = SampleSpriteTexture(
                    input.texcoord + float2(0.0, edgeStep)).a;
                float alphaBelow = SampleSpriteTexture(
                    input.texcoord - float2(0.0, edgeStep)).a;
                float enteringGlass = saturate(alphaBelow - alphaAbove);
                float floorEdge = smoothstep(0.04, 0.30, enteringGlass);
                float sourceLum = dot(source.rgb, float3(0.299, 0.587, 0.114));
                float authoredDark = 1.0 - smoothstep(0.34, 0.72, sourceLum);
                floorEdge *= bottomWindow * lerp(0.35, 1.0, authoredDark);

                float leftSide;
                float rightSide;
                float bottom;
                // I retain cavity coverage so the dynamic pass can avoid relighting baked interior reflections.
                float cavityInterior = 0.0;

                UNITY_BRANCH
                if (_UseMask > 0.5)
                {
                    float2 uv = (input.localPos - _MaskRect.xy)
                              / max(_MaskRect.zw, float2(1e-4, 1e-4));
                    float2 r = max(_MaskReach.xy, float2(1e-4, 1e-4));
                    float mL = tex2D(_MaskTex, uv - float2(r.x, 0)).a;
                    float mR = tex2D(_MaskTex, uv + float2(r.x, 0)).a;
                    float mD = tex2D(_MaskTex, uv - float2(0, r.y)).a;
                    float mU = tex2D(_MaskTex, uv + float2(0, r.y)).a;
                    float mLD = tex2D(_MaskTex, uv - r).a;
                    float mRU = tex2D(_MaskTex, uv + r).a;
                    float mLU = tex2D(_MaskTex, uv + float2(-r.x, r.y)).a;
                    float mRD = tex2D(_MaskTex, uv + float2(r.x, -r.y)).a;

                    float minMask = min(min(mL, mR), min(min(mD, mU),
                        min(min(mLD, mRU), min(mLU, mRD))));
                    float maxMask = max(max(mL, mR), max(max(mD, mU),
                        max(max(mLD, mRU), max(mLU, mRD))));
                    float boundary = saturate(maxMask - minMask);
                    float2 into = float2(
                        (mRU + 2.0 * mR + mRD) - (mLU + 2.0 * mL + mLD),
                        (mLU + 2.0 * mU + mRU) - (mLD + 2.0 * mD + mRD));
                    float2 outward = -normalize(into + float2(1e-5, 1e-5));
                    float uvGate = smoothstep(-0.02, 0.01, uv.x)
                                 * (1.0 - smoothstep(0.99, 1.02, uv.x))
                                 * smoothstep(-0.02, 0.01, uv.y)
                                 * (1.0 - smoothstep(0.99, 1.02, uv.y));
                    boundary *= uvGate;

                    float side = boundary
                               * smoothstep(0.48, 0.90, abs(outward.x)) * bodyGate;
                    // I share one light direction and add a broader cool fill on the opposite side for dark
                    // backgrounds.
                    float keyFacing = pow(saturate(dot(outward, keyDirection)), 2.6);
                    float fillFacing = pow(saturate(dot(outward, -keyDirection)), 2.0);
                    leftSide = side * keyFacing;
                    rightSide = side * fillFacing * 0.62;
                    // The centre mask rejects the outer edge; source alpha limits seam lighting to painted pixels.
                    cavityInterior = tex2D(_MaskTex, uv).a;
                    float across = lerp(0.45, 1.0,
                        1.0 - smoothstep(0.52, 0.96, sideDistance));
                    bottom = floorEdge * smoothstep(0.08, 0.72, cavityInterior)
                           * across * uvGate;
                }
                else
                {
                    // Unbaked bottles use an analytic fallback inside the interior rect so handles and stems stay
                    // dark.
                    float rectGate = smoothstep(-0.075, -0.005, p.x)
                                   * (1.0 - smoothstep(1.005, 1.075, p.x));
                    float side = smoothstep(_SideStart,
                        max(_SideFull, _SideStart + 1e-3), sideDistance)
                        * bodyGate * rectGate;
                    float keySide = saturate(-keyDirection.x);
                    leftSide = side * saturate(1.0 - p.x) * keySide;
                    rightSide = side * saturate(p.x)
                              * lerp(0.42, 0.62, keySide);
                    float lensAcross = 1.0 - smoothstep(0.52, 0.96, sideDistance);
                    bottom = floorEdge * lerp(0.32, 1.0, lensAcross) * rectGate;
                }

                // Side light follows selection alpha. I keep floor correction separate so low resting intensity
                // cannot hide it.
                float sideAmount = sourceAlpha * input.color.a;
                float3 sideLight = (_FxColor.rgb * leftSide * _SideStrength * _FxColor.a
                                  + _FxColor2.rgb * rightSide * _SideStrength * _FxColor2.a)
                                 * sideAmount;
                float3 floorLight = _FxColor.rgb * bottom * _BottomStrength
                                  * _FxColor.a * sourceAlpha;

                // I add a short upper-left key, softer lower-right reflection and base bounce to mid-value glass
                // pixels. Source alpha keeps the cavity clear.
                float u = p.x * 2.0 - 1.0;
                float paintLuminance = dot(
                    source.rgb, float3(0.299, 0.587, 0.114));
                float modellingRoom = sourceAlpha
                    * (1.0 - smoothstep(0.80, 0.98, paintLuminance));
                float upperWindow = smoothstep(0.28, 0.46, p.y)
                                  * (1.0 - smoothstep(0.82, 0.98, p.y));
                float lowerWindow = smoothstep(0.06, 0.20, p.y)
                                  * (1.0 - smoothstep(0.48, 0.68, p.y));
                float keyColumn = GlassGauss(u + 0.76, 0.24) * upperWindow;
                float fillColumn = GlassGauss(u - 0.70, 0.34) * lowerWindow;

                float topWindow = smoothstep(0.82, 0.94, p.y)
                                * (1.0 - smoothstep(1.00, 1.10, p.y));
                float topSweep = GlassGauss(u + 0.38, 0.42)
                               * topWindow;
                float baseWindow = smoothstep(-0.24, -0.06, p.y)
                                 * (1.0 - smoothstep(0.08, 0.24, p.y));
                float baseBounce = GlassGauss(u + 0.20, 0.76)
                                 * baseWindow;

                float volumeAmount = _SideStrength * input.color.a;
                float3 modelledLight =
                    _FxColor.rgb * _FxColor.a
                        * (keyColumn * 0.62 + topSweep * 0.34)
                  + _FxColor2.rgb * _FxColor2.a
                        * (fillColumn * 0.34 + baseBounce * 0.22);
                // I suppress broad relighting inside the baked cavity while keeping wall, seam and part lights
                // active for selection.
                float bakedCavityReflectionGate = 1.0 - smoothstep(
                    0.65, 0.95, cavityInterior);
                modelledLight *= modellingRoom * volumeAmount
                               * bakedCavityReflectionGate;

                // I use outer alpha gradients and brightness ridges for profile part lights so the glass reflects
                // light without a broad colour wash.
                float partFeather = max(_AccessoryFx.z * size.x, 1e-4);
                float stemFootGate = 1.0 - smoothstep(
                    _InteriorRect.y - partFeather,
                    _InteriorRect.y + partFeather, input.localPos.y);
                float heavyBaseGate = 1.0 - smoothstep(
                    _VisibleBottomY - partFeather,
                    _VisibleBottomY + partFeather, input.localPos.y);
                float handleStrength = saturate(_AccessoryFx.x);
                float stemStrength = saturate(_AccessoryFx.y);
                float baseStrength = saturate(_BottomRimStrength);
                UNITY_BRANCH
                if (max(handleStrength, max(stemStrength, baseStrength)) <= 0.001)
                {
                    return fixed4(sideLight + floorLight + modelledLight, 0.0);
                }
                float insideX = smoothstep(_InteriorRect.x - partFeather,
                                           _InteriorRect.x + partFeather,
                                           input.localPos.x)
                              * (1.0 - smoothstep(_InteriorRect.z - partFeather,
                                                  _InteriorRect.z + partFeather,
                                                  input.localPos.x));
                float insideY = smoothstep(_InteriorRect.y - partFeather,
                                           _InteriorRect.y + partFeather,
                                           input.localPos.y)
                              * (1.0 - smoothstep(_InteriorRect.w - partFeather,
                                                  _InteriorRect.w + partFeather,
                                                  input.localPos.y));
                float handleRegion = (1.0 - insideX) * insideY * handleStrength;

                float2 partEdgeProbe = max(_MainTex_TexelSize.xy,
                                           fwidth(input.texcoord) * 0.70);
                fixed4 partL = SampleSpriteTexture(
                    input.texcoord - float2(partEdgeProbe.x, 0.0));
                fixed4 partR = SampleSpriteTexture(
                    input.texcoord + float2(partEdgeProbe.x, 0.0));
                fixed4 partD = SampleSpriteTexture(
                    input.texcoord - float2(0.0, partEdgeProbe.y));
                fixed4 partU = SampleSpriteTexture(
                    input.texcoord + float2(0.0, partEdgeProbe.y));
                float partMinAlpha = min(min(partL.a, partR.a),
                                         min(partD.a, partU.a));
                float partAlphaDrop = saturate(sourceAlpha - partMinAlpha);
                float partOuterEdge = smoothstep(0.04, 0.30, partAlphaDrop)
                    * (1.0 - smoothstep(0.28, 0.58, partMinAlpha));
                float2 partOutward = -normalize(float2(
                    partR.a - partL.a, partU.a - partD.a)
                    + float2(1e-5, 1e-5));
                float warmFacing = pow(saturate(
                    dot(partOutward, keyDirection)), 1.8);
                float coolFacing = pow(saturate(
                    dot(partOutward, -keyDirection)), 1.6);

                float partNeighbourLum = 0.25 * (
                    dot(partL.rgb, float3(0.299, 0.587, 0.114))
                  + dot(partR.rgb, float3(0.299, 0.587, 0.114))
                  + dot(partD.rgb, float3(0.299, 0.587, 0.114))
                  + dot(partU.rgb, float3(0.299, 0.587, 0.114)));
                float luminanceRidge = smoothstep(0.018, 0.055,
                    max(0.0, paintLuminance - partNeighbourLum));
                float neutralChroma = 1.0 - smoothstep(0.10, 0.22,
                    max(max(source.r, source.g), source.b)
                    - min(min(source.r, source.g), source.b));
                float authoredBand = smoothstep(0.60, 0.70, paintLuminance)
                    * (1.0 - smoothstep(0.88, 0.96, paintLuminance))
                    * neutralChroma;
                // I keep a small minimum in the ridge detector so downscaling cannot erase tiny glints.
                float authoredIsland = authoredBand
                    * lerp(0.22, 1.0, luminanceRidge);

                float centreX = (_InteriorRect.x + _InteriorRect.z) * 0.5;
                float partX = (input.localPos.x - centreX) / size.x;
                float stemDepth = (_InteriorRect.y - input.localPos.y) / size.x;
                float baseDepth = (_VisibleBottomY - input.localPos.y) / size.x;
                float stemKeySpot = exp(
                    -pow((partX + 0.20) / 0.08, 2.0)
                    -pow((stemDepth - 0.36) / 0.035, 2.0));
                float baseKeySpot = exp(
                    -pow((partX + 0.28) / 0.12, 2.0)
                    -pow((baseDepth - 0.055) / 0.035, 2.0));

                float stemPart = stemFootGate * stemStrength;
                float basePart = heavyBaseGate * baseStrength;
                float partCoverage = smoothstep(0.30, 0.60, sourceAlpha);
                float warmPart = partOuterEdge * warmFacing
                    * (stemPart + basePart + handleRegion) * 0.55
                    + authoredIsland
                    * (stemKeySpot * stemPart + baseKeySpot * basePart);
                float coolPart = partOuterEdge * coolFacing
                    * (stemPart + basePart + handleRegion);

                // I suppress the generic base bounce where a profile already provides its own part light.
                float dedicatedPart = saturate(
                    stemFootGate * step(0.001, stemStrength)
                  + heavyBaseGate * step(0.001, baseStrength));
                modelledLight *= 1.0 - dedicatedPart;

                float3 partLight =
                    (_FxColor.rgb * _FxColor.a * warmPart * 0.44
                   + _FxColor2.rgb * _FxColor2.a * coolPart * 0.30)
                    * partCoverage;

                return fixed4(sideLight + floorLight + modelledLight
                    + partLight, 0.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
