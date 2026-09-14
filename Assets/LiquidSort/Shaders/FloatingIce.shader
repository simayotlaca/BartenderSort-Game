Shader "LiquidSort/FloatingIce"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
        _Color ("Tint", Color) = (1,1,1,1)
        [PerRendererData] _RendererColor ("Renderer Color", Color) = (1,1,1,1)
        [PerRendererData] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _SurfaceWorldY ("Liquid Surface World Y", Float) = 0
        [PerRendererData] _LiquidColor ("Liquid Colour", Color) = (0.3,0.7,1,1)
        _SubmergedTintStrength ("Submerged Tint", Range(0,1)) = 0.28
        _SubmergedAlpha ("Submerged Alpha", Range(0,1)) = 0.70
        _ContactStrength ("Waterline Highlight", Range(0,1)) = 0.20
        _SparkleColor ("Small Ice Sparkle", Color) = (0.851,0.969,1.0,1)
        // Other floating garnishes share this shader. Their materials opt in separately.
        _SparkleStrength ("Small Ice Sparkle Strength", Range(0,1)) = 0
        // Only mug foam enables this. Each renderer owns its clock, leaving ice and mint unchanged.
        _FoamFxStrength ("Foam Motion Strength", Range(0,1)) = 0
        [PerRendererData] _FoamBubbleState ("Foam Bubble State", Vector) = (0,0,0,0)
        _FoamBubbleColor ("Foam Bubble Colour", Color) = (1,0.985,0.92,1)
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
            #pragma multi_compile_instancing
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            struct IceAppData
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct IceVaryings
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                float worldY : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float4 _MainTex_ST;
            float _SurfaceWorldY;
            fixed4 _LiquidColor;
            float _SubmergedTintStrength;
            float _SubmergedAlpha;
            float _ContactStrength;
            fixed4 _SparkleColor;
            float _SparkleStrength;
            float _FoamFxStrength;
            float4 _FoamBubbleState;
            fixed4 _FoamBubbleColor;
            float4 _MainTex_TexelSize;

            IceVaryings vert(IceAppData input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                IceVaryings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float4 vertex = UnityFlipSprite(input.vertex, _Flip);
                output.vertex = UnityObjectToClipPos(vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color * _Color * _RendererColor;
                output.worldY = mul(unity_ObjectToWorld, vertex).y;
                return output;
            }

            fixed4 frag(IceVaryings input) : SV_Target
            {
                fixed4 ice = SampleSpriteTexture(input.uv) * input.color;
                clip(ice.a - (1.0 / 255.0));

                float pixelY = max(fwidth(input.worldY), 1e-5);
                float surfaceDistance = input.worldY - _SurfaceWorldY;
                float submerged = 1.0 - smoothstep(
                    -pixelY * 0.45, pixelY * 0.45, surfaceDistance);

                // I tint submerged ice with the drink colour while protecting its brightest highlights.
                float luminance = dot(ice.rgb, float3(0.299, 0.587, 0.114));
                float highlightProtection = 1.0
                    - 0.45 * smoothstep(0.78, 0.98, luminance);
                float tintAmount = submerged
                    * saturate(_SubmergedTintStrength)
                    * highlightProtection;
                ice.rgb = lerp(ice.rgb, saturate(_LiquidColor.rgb), tintAmount);
                ice.a *= lerp(1.0, saturate(_SubmergedAlpha), submerged);

                // I draw a two-tone, one-pixel meniscus to seat the ice in the liquid without changing alpha or
                // adding line objects.
                float contactCoordinate = surfaceDistance / pixelY;
                float upperContact = 1.0 - smoothstep(
                    0.08, 0.72, abs(contactCoordinate - 0.18));
                float lowerContact = 1.0 - smoothstep(
                    0.05, 0.56, abs(contactCoordinate + 0.34));
                float contactStrength = saturate(_ContactStrength) * ice.a;
                ice.rgb = lerp(ice.rgb, float3(0.88, 0.98, 1.0),
                    upperContact * contactStrength);
                float3 submergedLine = lerp(
                    saturate(_LiquidColor.rgb),
                    float3(0.46, 0.88, 1.0), 0.36);
                ice.rgb = lerp(ice.rgb, submergedLine,
                    lowerContact * contactStrength * 0.48);

                // I lift the small painted top highlight only within its brightness headroom so the cube stays
                // clear at phone scale.
                float2 sparklePoint = (input.uv - float2(0.75, 0.72))
                                    / float2(0.075, 0.055);
                float sparkle = saturate(1.0 - dot(sparklePoint, sparklePoint));
                sparkle *= sparkle;
                sparkle *= lerp(1.0, 0.42, submerged) * ice.a;
                float sparkleAmount = saturate(
                    sparkle * _SparkleStrength * _SparkleColor.a);
                ice.rgb += (1.0 - saturate(ice.rgb))
                         * _SparkleColor.rgb * sparkleAmount;

                // I animate a short-lived ring in foam RGB only, preserving sprite alpha and clean edges.
                UNITY_BRANCH
                if (_FoamFxStrength > 1e-4 && _FoamBubbleState.x > 1e-4)
                {
                    float textureAspect = _MainTex_TexelSize.z
                        / max(_MainTex_TexelSize.w, 1.0);
                    float2 foamPoint = (input.uv - float2(0.59, 0.46))
                        * float2(textureAspect, 1.0);
                    float progress = saturate(_FoamBubbleState.y);
                    float radius = lerp(0.017, 0.047,
                        smoothstep(0.0, 1.0, progress));
                    float distanceToCentre = length(foamPoint);
                    float ringAA = max(fwidth(distanceToCentre), 0.0035);
                    float outer = 1.0 - smoothstep(
                        radius - ringAA, radius + ringAA,
                        distanceToCentre);
                    float innerRadius = radius * 0.54;
                    float inner = 1.0 - smoothstep(
                        innerRadius - ringAA, innerRadius + ringAA,
                        distanceToCentre);
                    float ring = saturate(outer - inner);
                    float bubbleAmount = saturate(ring
                        * _FoamBubbleState.x * _FoamFxStrength
                        * _FoamBubbleColor.a);
                    // I use warm lower and ivory upper edges so foam bubbles stay readable when small.
                    float bubbleFacing = saturate(
                        0.5 + foamPoint.y / max(radius * 1.7, 1e-4));
                    float3 bubbleColour = lerp(
                        float3(0.96, 0.78, 0.43),
                        _FoamBubbleColor.rgb,
                        bubbleFacing);
                    ice.rgb = lerp(ice.rgb, bubbleColour,
                        bubbleAmount * ice.a * 0.78);
                }
                return ice;
            }
            ENDCG
        }
    }

    Fallback "Sprites/Default"
}
