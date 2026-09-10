Shader "LiquidSort/DeliveryReadyBadge"
{
    Properties
    {
        [PerRendererData] _MainTex ("Badge Sprite", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _FaceGain ("Emerald Brightness", Range(1,2.5)) = 1.8
        _GoldGain ("Gold Brightness", Range(1,1.5)) = 1.16
        [Toggle] _GlowOnly ("Background Highlight", Float) = 0
        _GlowColor ("Highlight Color", Color) = (1,0.83,0.39,0.62)
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
            #pragma fragment ReadyBadgeFrag
            #pragma target 2.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            float _FaceGain, _GoldGain, _GlowOnly;
            fixed4 _GlowColor;

            fixed4 ReadyBadgeFrag(v2f input) : SV_Target
            {
                if (_GlowOnly > 0.5)
                {
                    // Fade fully inside the source sprite's tight mesh, without an extra texture.
                    float radius = length(input.texcoord - float2(0.5, 0.5));
                    float glow = 1.0 - smoothstep(0.26, 0.425, radius);
                    float alpha = glow * _GlowColor.a * input.color.a;
                    return fixed4(_GlowColor.rgb * input.color.rgb * alpha, alpha);
                }

                fixed4 source = SampleSpriteTexture(input.texcoord);
                float3 original = source.rgb;
                float emerald = smoothstep(0.015, 0.10,
                    original.g - max(original.r, original.b));
                float gold = smoothstep(0.12, 0.38, original.r - original.b)
                    * (1.0 - emerald);
                // Keep the painted shading and bevel while lifting the muted green face.
                float3 vivid = saturate(original * float3(0.78, _FaceGain, 1.2));
                float3 color = lerp(original, vivid, emerald);
                color = lerp(color, saturate(original * _GoldGain), gold);
                float tick = smoothstep(0.72, 0.90, min(original.r, original.g))
                    * (1.0 - gold) * (1.0 - emerald);
                color = lerp(color, float3(1.0, 0.995, 0.94), tick * 0.78);
                float alpha = source.a * input.color.a;
                return fixed4(color * input.color.rgb * alpha, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
