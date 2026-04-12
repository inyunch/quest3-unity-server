Shader "Custom/SegmentationMask"
{
    Properties
    {
        _MainTex ("Mask Texture", 2D) = "white" {}
        _Color ("Tint Color", Color) = (1,1,1,1)
        _Alpha ("Global Alpha", Range(0,1)) = 0.6
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "RenderType"="Transparent"
            "IgnoreProjector"="True"
        }

        LOD 100
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _Alpha;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Sample the mask texture
                fixed4 texColor = tex2D(_MainTex, i.uv);

                // Apply tint color to RGB only (multiply)
                fixed3 finalColor = texColor.rgb * _Color.rgb;

                // Use texture's alpha channel directly, then multiply by global alpha
                // This ensures transparent pixels (alpha=0) remain transparent
                fixed finalAlpha = texColor.a * _Alpha;

                return fixed4(finalColor, finalAlpha);
            }
            ENDCG
        }
    }

    Fallback "Unlit/Transparent"
}
