// I add a narrow upper shelf highlight from source alpha, matching the overhead key without another texture.
Shader "LiquidSort/ShelfSurfaceLight"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _HighlightColor ("Upper Catch", Color) = (1.0,0.851,0.627,0.30)
        _HighlightWidthPx ("Catch Width (screen pixels)", Range(0.5,4)) = 1.5
        _HighlightFeatherPx ("Catch Feather (screen pixels)", Range(0.5,5)) = 2.5

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
            // Edge detection treats UV 0/1 as the authored sprite boundary.
            "CanUseSpriteAtlas" = "False"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment ShelfSurfaceFrag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            fixed4 _HighlightColor;
            float _HighlightWidthPx;
            float _HighlightFeatherPx;

            float SpriteAlpha(float2 uv)
            {
                // I return transparent outside the sprite rect because Clamp would repeat its opaque top row and
                // hide the edge.
                float inside = step(0.0, uv.x) * step(uv.x, 1.0)
                             * step(0.0, uv.y) * step(uv.y, 1.0);
                return SampleSpriteTexture(saturate(uv)).a * inside;
            }

            fixed4 ShelfSurfaceFrag(v2f input) : SV_Target
            {
                fixed4 source = SampleSpriteTexture(input.texcoord);
                float sourceAlpha = saturate(source.a * input.color.a);

                // I sample upward in V and use screen derivatives to keep the catch 1-2 physical pixels wide.
                float uvPerScreenPixelY = max(length(float2(
                    ddx(input.texcoord.y), ddy(input.texcoord.y))), 1e-6);
                float hardAbove = SpriteAlpha(input.texcoord + float2(
                    0.0, uvPerScreenPixelY * max(_HighlightWidthPx, 0.5)));
                float softAbove = SpriteAlpha(input.texcoord + float2(
                    0.0, uvPerScreenPixelY
                       * max(_HighlightWidthPx + _HighlightFeatherPx, 1.0)));

                float hardEdge = saturate(source.a - hardAbove);
                float softEdge = saturate(source.a - softAbove);
                float upperCatch = smoothstep(0.025, 0.72,
                    hardEdge * 0.76 + softEdge * 0.24);

                // I favour the left/centre and fade at both shelf ends so the highlight does not become a neon
                // outline.
                float endFade = smoothstep(0.012, 0.065, input.texcoord.x)
                              * smoothstep(0.012, 0.065,
                                           1.0 - input.texcoord.x);
                float directional = lerp(0.82, 1.0,
                    1.0 - saturate(input.texcoord.x));
                float highlightAlpha = saturate(_HighlightColor.a
                    * upperCatch * endFade * directional * sourceAlpha);

                float3 basePremul = source.rgb * input.color.rgb * sourceAlpha;
                float3 litPremul = _HighlightColor.rgb * highlightAlpha
                                  + basePremul * (1.0 - highlightAlpha);
                float outputAlpha = highlightAlpha
                                  + sourceAlpha * (1.0 - highlightAlpha);

                clip(outputAlpha - (1.0 / 255.0));
                return fixed4(saturate(litPremul), saturate(outputAlpha));
            }
            ENDCG
        }
    }

    Fallback Off
}
