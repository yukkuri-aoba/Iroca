// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
//
// プレビューの差分表示(PreviewDiffDrawer が IMGUI の中で描く)。変更後(_MainTex)と変更前(_BeforeTex。同じ寸法)を
// 同じ画素で読み、8 ビットの値で RGB の差の合計が 10 を超えた画素を (255,220,0,160) で塗り、それ以外は透明にする。
// 描いている 2 枚のテクスチャからその場で作るので、差分が画像より遅れて届くことが無い。
// テクスチャは sRGB として読まれる(リニアのプロジェクトでは読むときに線形の値になる)ので、8 ビットの値へ戻してから比べる。
// 出す色も同じ理由で、リニアのプロジェクトでは線形の値にして返す(画面へ書くときに sRGB へ戻る)。
// _Smooth が 1 なら、周りの 4 画素の判定をバイリニアで混ぜて縁をなじませる(テクスチャの Bilinear と同じ見え方)。
// 0 なら一番近い画素の判定をそのまま使う(Point と同じ)。
Shader "Hidden/Iroca/PreviewDiff"
{
    Properties
    {
        _MainTex ("After", 2D) = "white" {}
        _BeforeTex ("Before", 2D) = "white" {}
        _TexSize ("Texture size (w, h)", Vector) = (1, 1, 0, 0)
        _Smooth ("Blend the 4 nearest texels", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src blend", Float) = 5   // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst blend", Float) = 10  // OneMinusSrcAlpha
    }
    SubShader
    {
        Tags { "ForceSupported" = "True" "RenderType" = "Overlay" }
        Lighting Off
        Blend [_SrcBlend] [_DstBlend]
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                float2 clipUV : TEXCOORD1;
            };

            sampler2D _MainTex;
            sampler2D _BeforeTex;
            float4 _TexSize;
            float _Smooth;
            // IMGUI のスクロールビューなどの切り抜き(Unity の GUI のシェーダーと同じ受け取り方)。
            sampler2D _GUIClipTexture;
            uniform float4x4 unity_GUIClipTextureMatrix;

            v2f vert(appdata_t v)
            {
                v2f o;
                float3 eyePos = UnityObjectToViewPos(v.vertex);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.clipUV = mul(unity_GUIClipTextureMatrix, float4(eyePos.xy, 0, 1.0)).xy;
                return o;
            }

            // 読んだ値を 8 ビットの値(0〜255 の整数)へ戻す。
            float3 Bytes(float3 c)
            {
            #if !defined(UNITY_COLORSPACE_GAMMA)
                c = float3(LinearToGammaSpaceExact(c.r), LinearToGammaSpaceExact(c.g), LinearToGammaSpaceExact(c.b));
            #endif
                return round(saturate(c) * 255.0);
            }

            // 画素 t(0 始まりの整数の位置)が変わったか(1)、変わっていないか(0)。画素の中心を読むので補間されない。
            float Changed(float2 t)
            {
                float4 uv = float4((t + 0.5) / _TexSize.xy, 0, 0);
                float3 d = abs(Bytes(tex2Dlod(_BeforeTex, uv).rgb) - Bytes(tex2Dlod(_MainTex, uv).rgb));
                return d.r + d.g + d.b > 10.0 ? 1.0 : 0.0;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 size = _TexSize.xy;
                float2 last = size - 1.0;
                float m;
                if (_Smooth > 0.5)
                {
                    float2 p = i.texcoord * size - 0.5;
                    float2 f = frac(p);
                    float2 t0 = clamp(floor(p), 0.0, last);
                    float2 t1 = clamp(floor(p) + 1.0, 0.0, last);
                    m = lerp(lerp(Changed(t0), Changed(float2(t1.x, t0.y)), f.x),
                             lerp(Changed(float2(t0.x, t1.y)), Changed(t1), f.x), f.y);
                }
                else
                {
                    m = Changed(clamp(floor(i.texcoord * size), 0.0, last));
                }
                float4 col = float4(1.0, 220.0 / 255.0, 0.0, 160.0 / 255.0 * m);
            #if !defined(UNITY_COLORSPACE_GAMMA)
                col.rgb = float3(GammaToLinearSpaceExact(col.r), GammaToLinearSpaceExact(col.g), GammaToLinearSpaceExact(col.b));
            #endif
                col.a *= tex2D(_GUIClipTexture, i.clipUV).a;
                return col;
            }
            ENDCG
        }
    }
}
