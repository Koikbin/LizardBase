// 橫向移動的隧道效果:包住頭的小球,從裡面看,視線正前方透明、周圍變暗。URP 用,支援 Quest 雙眼單通道。
// 每個像素的角度用「這隻眼睛的觀看座標」算(UNITY_MATRIX_V 在雙眼時是各眼自己的),
// 不依賴 _WorldSpaceCameraPos,所以兩眼的洞都在各自的正中央。
Shader "Lizard/LateralTunnel"
{
    Properties
    {
        _Color ("Color", Color) = (0, 0, 0, 1)
        _Alpha ("Alpha", Range(0, 1)) = 0
        _ClearCos ("Clear Cone Cos (fully clear inside)", Range(-1, 1)) = 0.985
        _FullCos ("Full Dark Cos (fully dark outside)", Range(-1, 1)) = 0.82
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+200" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            Cull Front      // 從球裡面看
            ZWrite Off
            ZTest Always    // 一律畫在最上層

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Alpha;
                float _ClearCos;
                float _FullCos;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionVS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(input.positionOS.xyz);
                o.positionVS = TransformWorldToView(ws);
                o.positionCS = TransformWorldToHClip(ws);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // 觀看座標裡眼睛看向 -Z;cosAngle = 1 是正前方
                float cosAngle = -normalize(i.positionVS).z;
                float t = saturate((_ClearCos - cosAngle) / max(1e-4, _ClearCos - _FullCos));
                t = smoothstep(0, 1, t);
                return half4(_Color.rgb, _Color.a * t * _Alpha);
            }
            ENDHLSL
        }
    }
}
