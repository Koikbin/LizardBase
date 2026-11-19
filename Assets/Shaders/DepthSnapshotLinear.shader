// DepthSnapshot 用:把 Meta 環境深度圖(_EnvironmentDepthTexture,每眼一層)的一層轉成線性深度(公尺),
// 寫到 R 通道,讓 C# 讀回來存成圖片。換算公式和 Meta 的遮擋著色器相同。
Shader "Hidden/LizardBase/DepthSnapshotLinear"
{
    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma require 2darray
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_EnvironmentDepthTexture);
            float4 _EnvironmentDepthZBufferParams;
            float _Slice;

            float4 frag(v2f_img i) : SV_Target
            {
                float depth01 = UNITY_SAMPLE_TEX2DARRAY_LOD(_EnvironmentDepthTexture, float3(i.uv, _Slice), 0).r;
                float ndc = depth01 * 2.0 - 1.0;
                float linearDepth = (1.0 / (ndc + _EnvironmentDepthZBufferParams.y)) * _EnvironmentDepthZBufferParams.x;
                return float4(linearDepth, depth01, 0, 1);
            }
            ENDCG
        }
    }
}
