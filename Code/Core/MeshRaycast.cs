// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 光線と三角形メッシュの当たり判定と、当たった位置の UV。Scene でモデルをクリックした場所を
    /// プレビューで示すのに使う(<see cref="SceneMeshPicker"/> が Unity のメッシュから詰めて呼ぶ)。
    /// Unity の Mesh・Physics には依存しない(コライダー無しのアバターにも当てるため。単体テストは scripts/unit-run)。
    /// </summary>
    internal static class MeshRaycast
    {
        /// <summary>当たった三角形と位置。位置は (1 - u - v)·a + u·b + v·c。</summary>
        internal struct Hit
        {
            /// <summary>三角形の番号(triangles 配列の 3 つ組の何番目か)。</summary>
            public int triangle;
            /// <summary>光線の媒介変数(origin + t·dir)。dir が単位ベクトルなら距離。</summary>
            public float t;
            public float u;
            public float v;
        }

        /// <summary>
        /// どちらの面に当てるか。描画で消える面(カリング)に当てると、画面では奥に見えている物ではなく
        /// 見えない手前の面を拾う(カメラが服の内側にある・スカートの中を覗く等)。
        /// 表 = 頂点の並びから cross(b − a, c − a) の向く側(Unity の表面・法線と同じ側)。
        /// </summary>
        internal enum Faces
        {
            Both,
            Front,
            Back,
        }

        // 光線と三角形の面がほぼ平行とみなす行列式の大きさ。
        private const float ParallelEpsilon = 1e-12f;
        // 三角形の内側とみなす重心座標の余裕。辺の上をちょうど通る光線(左右対称のモデルの中心線を
        // 真正面から等)が、浮動小数の誤差で隣り合う 2 枚の両方から外れないようにする。
        private const float BaryEpsilon = 1e-5f;

        /// <summary>
        /// origin + t·dir(0 &lt; t &lt; maxT)で一番手前の三角形(faces の面だけ)。当たらなければ false。
        /// </summary>
        public static bool Intersect(Vector3 origin, Vector3 dir, Vector3[] vertices, int[] triangles,
                                     float maxT, Faces faces, out Hit hit)
        {
            hit = default;
            bool found = false;
            float best = maxT;
            int n = vertices.Length;
            int triCount = triangles.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                int ia = triangles[t * 3], ib = triangles[t * 3 + 1], ic = triangles[t * 3 + 2];
                if ((uint)ia >= (uint)n || (uint)ib >= (uint)n || (uint)ic >= (uint)n) continue;
                // Möller–Trumbore
                Vector3 a = vertices[ia];
                Vector3 e1 = vertices[ib] - a, e2 = vertices[ic] - a;
                Vector3 p = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, p);
                if (det > -ParallelEpsilon && det < ParallelEpsilon) continue;
                // det = −dot(dir, cross(e1, e2)): 正なら光線が表面に向かって当たっている
                if (faces == Faces.Front ? det < 0f : faces == Faces.Back && det > 0f) continue;
                float inv = 1f / det;
                Vector3 s = origin - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < -BaryEpsilon || u > 1f + BaryEpsilon) continue;
                Vector3 q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(dir, q) * inv;
                if (v < -BaryEpsilon || u + v > 1f + BaryEpsilon) continue;
                float d = Vector3.Dot(e2, q) * inv;
                if (d <= 0f || d >= best) continue;
                best = d;
                hit = new Hit { triangle = t, t = d, u = u, v = v };
                found = true;
            }
            return found;
        }

        /// <summary>当たった位置の UV(三角形の 3 頂点の UV を重心座標で補間)。</summary>
        public static Vector2 InterpolateUv(Vector2[] uv, int[] triangles, Hit hit)
        {
            Vector2 a = uv[triangles[hit.triangle * 3]];
            Vector2 b = uv[triangles[hit.triangle * 3 + 1]];
            Vector2 c = uv[triangles[hit.triangle * 3 + 2]];
            return a * (1f - hit.u - hit.v) + b * hit.u + c * hit.v;
        }

        /// <summary>
        /// テクスチャ上の位置へ畳む(Tiling で 0..1 の外に出た UV を繰り返しで戻す)。
        /// 戻すのに引いた整数の量を <paramref name="shift"/> に返す(同じ島の頂点も同じだけずらして描くため)。
        /// 0..1 の内側(端の 1 を含む)はそのまま。
        /// </summary>
        public static Vector2 WrapToTexture(Vector2 uv, out Vector2 shift)
        {
            shift = new Vector2(WrapShift(uv.x), WrapShift(uv.y));
            return uv - shift;
        }

        private static float WrapShift(float x) => x >= 0f && x <= 1f ? 0f : Mathf.Floor(x);
    }
}
