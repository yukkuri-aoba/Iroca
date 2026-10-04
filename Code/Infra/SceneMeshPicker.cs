// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// Scene ビューの光線が、いま画面に映っているどのメッシュのどの三角形に当たるかを調べる。
    /// アバターは普通コライダーを持たないので Physics は使わず、メッシュへ直接当てる
    /// (<see cref="MeshRaycast"/>)。SkinnedMeshRenderer はポーズ・ブレンドシェイプ込みの形
    /// (BakeMesh)に当てる。元のメッシュのままだと、ポーズを付けたアバターでは外れる。
    /// </summary>
    internal static class SceneMeshPicker
    {
        internal sealed class Hit
        {
            public Renderer renderer;
            /// <summary>当たったサブメッシュのマテリアル(無ければ null)。</summary>
            public Material material;
            public int submesh;
            /// <summary>サブメッシュ内の三角形の番号。</summary>
            public int triangle;
            /// <summary>メッシュの UV0(頂点ごと)。</summary>
            public Vector2[] uv;
            /// <summary>当たったサブメッシュの三角形(頂点番号 ×3)。</summary>
            public int[] submeshTriangles;
            /// <summary>当たった位置の UV0(Tiling / Offset を掛ける前)。</summary>
            public Vector2 hitUv;
            public float distance;
        }

        // SkinnedMeshRenderer の bounds は作者が決めた localBounds 由来で、ポーズ次第で実際の形より
        // 小さいことがある。絞り込みで取りこぼさないよう、大きさの割合と固定量の両方で広げて使う。
        private const float SkinnedBoundsGrowRatio = 0.25f;
        private const float SkinnedBoundsGrowMeters = 0.05f;

        private static readonly Vector3[] s_corners = new Vector3[8];

        /// <summary>一番手前で当たったメッシュ。何にも当たらなければ null。</summary>
        public static Hit Pick(Ray ray)
        {
            var candidates = new List<(Renderer r, float enter)>();
            foreach (var r in StageUtility.GetCurrentStageHandle().FindComponentsOfType<Renderer>())
            {
                if (!IsPickable(r)) continue;
                float enter;
                if (r is SkinnedMeshRenderer smr)
                {
                    if (!SkinnedBounds(smr, out var b))
                    {
                        candidates.Add((r, 0f)); // 範囲が分からないものは必ず調べる
                        continue;
                    }
                    b.Expand(b.size * SkinnedBoundsGrowRatio + Vector3.one * SkinnedBoundsGrowMeters);
                    if (!b.IntersectRay(ray, out enter)) continue;
                }
                else if (!r.bounds.IntersectRay(ray, out enter))
                {
                    continue;
                }
                candidates.Add((r, enter));
            }
            // 手前の bounds から調べ、見つけた当たりより奥から始まる bounds は調べない。
            candidates.Sort((x, y) => x.enter.CompareTo(y.enter));

            Hit best = null;
            Mesh bakeMesh = null;
            try
            {
                foreach (var (r, enter) in candidates)
                {
                    if (best != null && enter > best.distance) break;
                    var h = PickRenderer(r, ray, best != null ? best.distance : float.PositiveInfinity, ref bakeMesh);
                    if (h != null) best = h;
                }
            }
            finally
            {
                if (bakeMesh != null) Object.DestroyImmediate(bakeMesh);
            }
            return best;
        }

        /// <summary>
        /// SkinnedMeshRenderer の今の範囲(rootBone 基準の localBounds を、いまのボーンの位置で世界へ)。
        /// Renderer.bounds は次に描画されるまで更新されないので、ボーンを動かした直後のクリックでは
        /// 古い位置を返し、絞り込みで取りこぼす。updateWhenOffscreen の物は localBounds を使わないので false。
        /// </summary>
        private static bool SkinnedBounds(SkinnedMeshRenderer smr, out Bounds bounds)
        {
            bounds = default;
            if (smr.updateWhenOffscreen) return false;
            var local = smr.localBounds;
            var toWorld = (smr.rootBone != null ? smr.rootBone : smr.transform).localToWorldMatrix;
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
                s_corners[i] = toWorld.MultiplyPoint3x4(corner);
            }
            bounds = new Bounds(s_corners[0], Vector3.zero);
            for (int i = 1; i < 8; i++) bounds.Encapsulate(s_corners[i]);
            return true;
        }

        /// <summary>
        /// 当てる対象か。非表示・無効のもの、NDMF などが作るプレビュー用の複製(保存されない隠し
        /// オブジェクト。マテリアルが元と別物)は除く。Scene の目のアイコンで隠したものも、見えない
        /// ので当てない。
        /// </summary>
        private static bool IsPickable(Renderer r)
        {
            if (r == null || !r.enabled) return false;
            var go = r.gameObject;
            if (!go.activeInHierarchy) return false;
            if ((go.hideFlags & (HideFlags.DontSave | HideFlags.HideInHierarchy)) != 0) return false;
            if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) return false;
            return !SceneVisibilityManager.instance.IsHidden(go);
        }

        // カリングの設定を持つプロパティ(lilToon・Poiyomi・URP は _Cull、HDRP は _CullMode)。
        // 値は UnityEngine.Rendering.CullMode と同じ(0 = 両面を描く、1 = 表を消す、2 = 裏を消す)。
        private static readonly string[] CullProperties = { "_Cull", "_CullMode" };

        /// <summary>
        /// このマテリアルで画面に映る面。設定が分からないシェーダーは裏を消す(ShaderLab の既定)とみなす。
        /// 光線はローカル座標で当てるので、拡縮が負(左右反転)の Renderer でも同じ判定でよい
        /// (Unity は反転した物のカリングを裏返して描くので、ローカルで見た表がそのまま表に映る)。
        /// </summary>
        private static MeshRaycast.Faces FacesOf(Material mat)
        {
            if (mat != null)
            {
                foreach (var p in CullProperties)
                {
                    if (!mat.HasProperty(p)) continue;
                    switch (Mathf.RoundToInt(mat.GetFloat(p)))
                    {
                        case 0: return MeshRaycast.Faces.Both;
                        case 1: return MeshRaycast.Faces.Back;
                        default: return MeshRaycast.Faces.Front;
                    }
                }
            }
            return MeshRaycast.Faces.Front;
        }

        private static Hit PickRenderer(Renderer r, Ray ray, float maxDistance, ref Mesh bakeMesh)
        {
            Mesh mesh;
            Vector3[] vertices;
            try
            {
                if (r is SkinnedMeshRenderer smr)
                {
                    mesh = smr.sharedMesh;
                    if (mesh == null) return null;
                    if (bakeMesh == null) bakeMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    // useScale = true の頂点は Renderer のローカル座標(localToWorldMatrix で世界へ)。
                    // false だと拡縮を除いた座標になり、拡縮のある Renderer で位置がずれる(実測で確認)。
                    smr.BakeMesh(bakeMesh, true);
                    vertices = bakeMesh.vertices;
                }
                else
                {
                    mesh = r.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null;
                    if (mesh == null) return null;
                    vertices = mesh.vertices;
                }
            }
            catch (System.Exception)
            {
                return null; // 読めないメッシュ(Read/Write 無効の扱いは Unity の版で違う)
            }
            if (vertices == null || vertices.Length == 0) return null;

            // 頂点を世界へ動かす代わりに、光線を Renderer のローカルへ移す。方向は正規化し直さないので、
            // 媒介変数 t はそのまま世界の距離(ray.direction は単位ベクトル)。
            var toLocal = r.transform.worldToLocalMatrix;
            Vector3 origin = toLocal.MultiplyPoint3x4(ray.origin);
            Vector3 dir = toLocal.MultiplyVector(ray.direction);

            var mats = r.sharedMaterials;
            bool found = false;
            MeshRaycast.Hit bestHit = default;
            int bestSubmesh = -1;
            int[] bestTris = null;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                int[] tris;
                try { tris = mesh.GetTriangles(s); }
                catch (System.Exception) { break; }
                var faces = FacesOf(s < mats.Length ? mats[s] : null);
                if (!MeshRaycast.Intersect(origin, dir, vertices, tris, found ? bestHit.t : maxDistance, faces, out var h))
                    continue;
                found = true;
                bestHit = h;
                bestSubmesh = s;
                bestTris = tris;
            }
            if (!found) return null;

            var uvList = new List<Vector2>();
            try { mesh.GetUVs(0, uvList); }
            catch (System.Exception) { return null; }
            if (uvList.Count != mesh.vertexCount) return null;
            var uv = uvList.ToArray();
            return new Hit
            {
                renderer = r,
                material = bestSubmesh < mats.Length ? mats[bestSubmesh] : null,
                submesh = bestSubmesh,
                triangle = bestHit.triangle,
                uv = uv,
                submeshTriangles = bestTris,
                hitUv = MeshRaycast.InterpolateUv(uv, bestTris, bestHit),
                distance = bestHit.t,
            };
        }
    }
}
