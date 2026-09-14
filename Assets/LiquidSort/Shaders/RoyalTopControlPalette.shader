// I recolour only gold paint and the optional Settings glyph, preserving the sprite's blue face, bevel and alpha.
Shader "LiquidSort/RoyalTopControlPalette"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _GoldColor ("Gold Midtone", Color) = (0.90980392,0.69803922,0.29019608,1)
        _SourceGold ("Original Gold Midtone", Color) = (1,0.70196078,0,1)
        _GlyphFace ("Glyph Face", Color) = (1,1,1,1)
        _GlyphOutline ("Glyph Outline", Color) = (0.11764706,0.29019608,0.41960784,1)
        _RecolorGlyph ("Recolour Settings Glyph", Float) = 0
        _GlyphRect ("Glyph Bounds In Source UV", Vector) = (0.235,0.21,0.765,0.755)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="False" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment TopControlFrag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"
            fixed4 _GoldColor, _SourceGold, _GlyphFace, _GlyphOutline;
            float _RecolorGlyph;
            float4 _GlyphRect;
            float Luma(float3 c) { return dot(c, float3(0.2126,0.7152,0.0722)); }
            fixed4 TopControlFrag(v2f input) : SV_Target
            {
                fixed4 source = SampleSpriteTexture(input.texcoord);
                float3 original = source.rgb;
                float luma = Luma(original);
                float chroma = max(original.r,max(original.g,original.b))-min(original.r,min(original.g,original.b));
                float goldSignal = (original.r-original.b)+(original.g-original.b)*0.44;
                float goldMask = smoothstep(0.04,0.28,goldSignal)*smoothstep(0.05,0.23,chroma);
                float goldLuma = Luma(_SourceGold.rgb);
                float shade = saturate(luma/max(goldLuma,0.001));
                float highlight = saturate((luma-goldLuma)/max(1-goldLuma,0.001));
                float3 gold = lerp(_GoldColor.rgb*shade,float3(1,1,1),highlight);
                float3 color = lerp(original,gold,goldMask);

                // Bounds exclude the button's external shadow and blue rim.
                float2 uv = input.texcoord;
                float inside = step(_GlyphRect.x,uv.x)*step(_GlyphRect.y,uv.y)
                             * step(uv.x,_GlyphRect.z)*step(uv.y,_GlyphRect.w)*saturate(_RecolorGlyph);
                float face = (1-smoothstep(0.03,0.10,chroma))*smoothstep(0.55,0.82,luma);
                float ink = 1-smoothstep(0.025,0.24,luma);
                color = lerp(color,_GlyphFace.rgb,inside*face);
                color = lerp(color,_GlyphOutline.rgb,inside*ink);
                float alpha = saturate(source.a*input.color.a);
                return fixed4(saturate(color*input.color.rgb)*alpha,alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
