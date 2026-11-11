// 防禦網:玩家在球內看到的六角形網格。URP 用,支援 Quest 雙眼單通道。
// 用「方塊投影」把球面方向轉成六角格座標,避免上下兩極變形。
Shader "Lizard/DefenseNet"
{
    Properties
    {
        _Color ("Color", Color) = (0.3, 0.9, 1, 1)
        _Alpha ("Alpha", Range(0, 1)) = 0.5
        _HexDensity ("Hex Density", Float) = 7
        _LineWidth ("Line Width", Range(0, 0.5)) = 0.05
        _ClearCos ("Clear Cone Cos", Range(0, 1)) = 0.906
        _FullCos ("Full Ring Cos", Range(0, 1)) = 0.743
        _Scatter ("Scatter Amount", Range(0, 1)) = 0.45
        _HeadForward ("Head Forward", Vector) = (0, 0, 1, 0)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+100" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            // 顏色半透明疊加,alpha 也要寫入,疊在穿透畫面上才看得到
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Alpha;
                float _HexDensity;
                float _LineWidth;
                float _ClearCos;
                float _FullCos;
                float _Scatter;
                float4 _HeadForward;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.direction = input.positionOS.xyz;
                return output;
            }

            // 回傳到最近六角格邊線的距離(邊線上是 0),cell 是這一格的編號
            float HexCell(float2 p, out float2 cell)
            {
                const float2 s = float2(1.0, 1.7320508);
                float2 q = p - s * 0.5;
                float2 a = p - s * floor(p / s) - s * 0.5;
                float2 b = q - s * floor(q / s) - s * 0.5;
                bool useA = dot(a, a) < dot(b, b);
                cell = useA ? floor(p / s) : floor(q / s) + 0.5;
                float2 g = abs(useA ? a : b);
                return 0.5 - max(dot(g, float2(0.5, 0.8660254)), g.x);
            }

            float4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 dir = normalize(input.direction);
                float3 m = abs(dir);

                // 依最大的軸選方塊的一面
                float2 uv;
                float face;
                if (m.x >= m.y && m.x >= m.z) { uv = dir.yz / m.x; face = dir.x > 0.0 ? 1.0 : 2.0; }
                else if (m.y >= m.z)          { uv = dir.xz / m.y; face = dir.y > 0.0 ? 3.0 : 4.0; }
                else                          { uv = dir.xy / m.z; face = dir.z > 0.0 ? 5.0 : 6.0; }

                float2 cell;
                float edge = HexCell(uv * _HexDensity, cell);
                float aa = max(fwidth(edge), 0.0001);
                float line01 = 1.0 - smoothstep(_LineWidth, _LineWidth + aa, edge);

                // 只留下隨機的一部分格子,其餘空著,形成四散的碎片
                float rnd = frac(sin(dot(cell + face * 17.0, float2(127.1, 311.7))) * 43758.5453);
                float keep = step(rnd, _Scatter);
                float body = lerp(0.12, 1.0, line01);

                // 視線中央完全淨空,往外才慢慢出現
                float facing = saturate(dot(dir, normalize(_HeadForward.xyz)));
                float ring = 1.0 - smoothstep(_FullCos, _ClearCos, facing);

                return float4(_Color.rgb, keep * body * ring * _Alpha);
            }
            ENDHLSL
        }
    }
}
