Shader "LiquidSort/LayerLockOutlined"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _OutlineColor ("Outline Color", Color) = (0.07058824,0.1372549,0.2431373,1)
        _OutlinePixels ("Outline Width (Screen Pixels)", Range(0,3)) = 1
        [MaterialToggle] PixelSnap ("Pixel Snap", Float) = 0
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
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment OutlinedLockFrag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma multi_compile_local _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            fixed4 _OutlineColor;
            float _OutlinePixels;

            fixed4 OutlinedLockFrag(v2f input) : SV_Target
            {
                fixed4 source = SampleSpriteTexture(input.texcoord);

                // UV derivatives keep the contour thin as the icon and camera scale.
                // The lock's full-rect sprite mesh includes transparent edge padding.
                float2 dx = ddx(input.texcoord) * _OutlinePixels;
                float2 dy = ddy(input.texcoord) * _OutlinePixels;
                float2 diagonalA = (dx + dy) * 0.70710678;
                float2 diagonalB = (dx - dy) * 0.70710678;
                fixed expandedAlpha = source.a;
                expandedAlpha = max(expandedAlpha, SampleSpriteTexture(input.texcoord + dx).a);
                expandedAlpha = max(expandedAlpha, SampleSpriteTexture(input.texcoord - dx).a);
                expandedAlpha = max(expandedAlpha, SampleSpriteTexture(input.texcoord + dy).a);
                expandedAlpha = max(expandedAlpha, SampleSpriteTexture(input.texcoord - dy).a);
                expandedAlpha = max(expandedAlpha, SampleSpriteTexture(input.texcoord + diagonalA).a);
                expandedAlpha = max(expandedAlpha, SampleSpriteTexture(input.texcoord - diagonalA).a);
                expandedAlpha = max(expandedAlpha, SampleSpriteTexture(input.texcoord + diagonalB).a);
                expandedAlpha = max(expandedAlpha, SampleSpriteTexture(input.texcoord - diagonalB).a);

                fixed4 color = source * input.color;
                fixed outlineAlpha = (expandedAlpha - source.a) * input.color.a * _OutlineColor.a;
                // Add only the expanded coverage, retaining the original white artwork.
                color.rgb = color.rgb * color.a + _OutlineColor.rgb * outlineAlpha;
                color.a += outlineAlpha;
                return color;
            }
            ENDCG
        }
    }

    Fallback "Sprites/Default"
}
