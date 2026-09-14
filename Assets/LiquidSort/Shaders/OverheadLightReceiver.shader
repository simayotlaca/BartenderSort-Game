// The wall is invisible in the base pass; only the overhead light's ForwardAdd pass draws here.
Shader "LiquidSort/OverheadLightReceiver"
{
    Properties
    {
        _Tint ("Receiver Tint", Color) = (1,1,1,1)
        _Strength ("Receiver Strength", Range(0,0.12)) = 0.06
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background+1"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Cull Off
        ZWrite Off

        // Keep the receiver completely invisible when no real-time pixel light reaches it.
        Pass
        {
            Name "BASE"
            Tags { "LightMode" = "ForwardBase" }
            ColorMask 0

            CGPROGRAM
            #pragma vertex BaseVert
            #pragma fragment BaseFrag
            #include "UnityCG.cginc"

            struct BaseInput
            {
                float4 vertex : POSITION;
            };

            struct BaseOutput
            {
                float4 position : SV_POSITION;
            };

            BaseOutput BaseVert(BaseInput input)
            {
                BaseOutput output;
                output.position = UnityObjectToClipPos(input.vertex);
                return output;
            }

            fixed4 BaseFrag(BaseOutput input) : SV_Target
            {
                return 0;
            }
            ENDCG
        }

        // Point and spot lights are evaluated here in Unity's Built-in Forward renderer.
        Pass
        {
            Name "REAL_LIGHT"
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            ColorMask RGB

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex LightVert
            #pragma fragment LightFrag
            #pragma multi_compile_fwdadd

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            struct LightInput
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct LightOutput
            {
                float4 position : SV_POSITION;
                float3 worldPosition : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                UNITY_LIGHTING_COORDS(2, 3)
            };

            fixed4 _Tint;
            float _Strength;

            LightOutput LightVert(LightInput v)
            {
                LightOutput output;
                output.position = UnityObjectToClipPos(v.vertex);
                output.worldPosition = mul(unity_ObjectToWorld, v.vertex).xyz;
                output.worldNormal = UnityObjectToWorldNormal(v.normal);
                UNITY_TRANSFER_LIGHTING(output, float2(0, 0));
                return output;
            }

            fixed4 LightFrag(LightOutput input) : SV_Target
            {
                UNITY_LIGHT_ATTENUATION(attenuation, input, input.worldPosition);
                float3 lightDirection = normalize(
                    UnityWorldSpaceLightDir(input.worldPosition));
                float facing = saturate(abs(dot(
                    normalize(input.worldNormal), lightDirection)));
                float3 contribution = _LightColor0.rgb * _Tint.rgb
                                    * attenuation * facing * _Strength;
                return fixed4(contribution, 0);
            }
            ENDCG
        }
    }

    Fallback Off
}
