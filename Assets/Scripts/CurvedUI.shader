Shader "Custom/CurvedUI"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _CurveStrength ("Curve Strength", Range(0, 2)) = 0.5
        _CurveRadius ("Curve Radius", Float) = 10
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100

        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off

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
                float4 color : COLOR;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float _CurveStrength;
            float _CurveRadius;

            v2f vert (appdata v)
            {
                v2f o;
                
                float4 pos = v.vertex;
                
                // 弧形弯曲：根据X坐标偏移Z
                // 公式：z = -x² / (2 * r) 形成圆弧
                float x = pos.x;
                float curve = (x * x) / (_CurveRadius * 2.0);
                pos.z -= curve * _CurveStrength;
                
                // 可选：Y轴也稍微弯曲形成球面
                float y = pos.y;
                float yCurve = (y * y) / (_CurveRadius * 4.0);
                pos.z -= yCurve * _CurveStrength * 0.5;
                
                o.vertex = UnityObjectToClipPos(pos);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * _Color * i.color;
                return col;
            }
            ENDCG
        }
    }
}
