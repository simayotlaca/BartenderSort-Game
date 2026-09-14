Shader "LiquidSort/GlassUnlockSheen"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Progress ("Reflection Progress", Range(0,1)) = 0
        _Strength ("Reflection Strength", Range(0,1)) = 0
        _LocalRect ("Sprite Local Bounds", Vector) = (0,0,1,1)
        _RimWidthPixels ("Rim Width In Screen Pixels", Range(0.5,3)) = 1.4
        [HideInInspector] _SpriteUvRect ("Sprite UV Bounds", Vector) = (0,0,1,1)
        [HideInInspector] _RendererColor ("Renderer Color", Color) = (1,1,1,1)
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
            "DisableBatching" = "True"
        }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend One One
        ColorMask RGB
        Pass
        {
            CGPROGRAM
            #pragma vertex SheenVert
            #pragma fragment SheenFrag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            float _Progress;
            float _Strength;
            float4 _LocalRect;
            float4 _SpriteUvRect;
            float4 _MainTex_TexelSize;
            float _RimWidthPixels;

            struct sheen_v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                float2 localUv : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sheen_v2f SheenVert(appdata_t input)
            {
                sheen_v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                v2f sprite = SpriteVert(input);
                output.vertex = sprite.vertex;
                output.color = sprite.color;
                output.texcoord = sprite.texcoord;
                // Local geometry keeps the sweep independent of atlas packing and texture padding.
                output.localUv = (input.vertex.xy - _LocalRect.xy) / _LocalRect.zw;
                return output;
            }

            fixed4 SheenFrag(sheen_v2f input) : SV_Target
            {
                fixed4 source = SampleSpriteTexture(input.texcoord);
                // An inner alpha edge lights the real lip, sides and foot. Screen derivatives keep
                // it readable when high-resolution glass art is reduced to a small mobile vessel.
                float2 width = max(abs(_MainTex_TexelSize.xy),
                                   fwidth(input.texcoord) * _RimWidthPixels);
                float2 uvMin = _SpriteUvRect.xy;
                float2 uvMax = _SpriteUvRect.zw;
                float left = SampleSpriteTexture(clamp(input.texcoord - float2(width.x, 0), uvMin, uvMax)).a;
                float right = SampleSpriteTexture(clamp(input.texcoord + float2(width.x, 0), uvMin, uvMax)).a;
                float down = SampleSpriteTexture(clamp(input.texcoord - float2(0, width.y), uvMin, uvMax)).a;
                float up = SampleSpriteTexture(clamp(input.texcoord + float2(0, width.y), uvMin, uvMax)).a;
                float edge = smoothstep(0.02, 0.20, source.a - min(min(left, right), min(down, up)));
                // Ignore faint reflection patches inside the bowl so liquid colours stay clear.
                float coverage = smoothstep(0.18, 0.72, source.a);
                float diagonal = input.localUv.y - input.localUv.x * 0.18;
                float distance = abs(diagonal - lerp(-0.22, 1.12, _Progress));
                float sweep = 1.0 - smoothstep(0.025, 0.16, distance);
                float alpha = source.a * coverage * edge * (0.62 + 0.38 * sweep)
                    * _Strength * input.color.a;
                return fixed4(input.color.rgb * alpha, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
