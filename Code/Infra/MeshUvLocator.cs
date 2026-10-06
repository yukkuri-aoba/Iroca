// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 編集中のテクスチャを使っているメッシュを探し、UV チャート地図(<see cref="UvChartMap"/>)の入力を作る。
    /// 見つからなければ null を返し、呼び出し側は今まで通り(メッシュ情報なし)で動く。
    ///
    /// 自動で探す順:
    ///   1. 開いているシーンの Renderer
    ///   2. テクスチャと同じ素材フォルダ(Assets/&lt;作者やモデル名&gt;/ 配下)の Prefab
    /// マテリアルの対応は「メインテクスチャがこのテクスチャ」か「このテクスチャを Iroca で書き出したもの
    /// (&lt;元の名前&gt;_recolored*.png、同じフォルダ)」。後者は、書き出し後にマテリアルを差し替えたあと
    /// 元のテクスチャを開き直したときに、メッシュを見失わないため。
    /// たどれないとき(FBX の既定マテリアルのまま等)は <see cref="FromObject"/> で手動指定する。
    /// </summary>
    internal static class MeshUvLocator
    {
        /// <summary>メインテクスチャとして見るプロパティ(lilToon / 標準 / URP / HDRP)。</summary>
        private static readonly string[] MainTextureProperties = { "_MainTex", "_BaseMap", "_BaseColorMap" };

        internal sealed class Found
        {
            public UvChartSource source;
            /// <summary>シーン上のパスや Prefab のパスと Renderer 名(表示・報告用)。</summary>
            public string origin;
            public Renderer renderer;
            public int submesh;
            /// <summary>マテリアルがこのテクスチャ(か書き出し物)を参照していたか。手動指定で参照が無いものは false。</summary>
            public bool matchedTexture;
        }

        internal sealed class Result
        {
            public readonly List<Found> found = new List<Found>();
            /// <summary>UV か三角形を読めなかったサブメッシュの数(Read/Write 無効など)。</summary>
            public int unreadable;
            /// <summary>どこで見つけたか("scene" / "project" / "manual")。</summary>
            public string via;

            public List<UvChartSource> Sources()
            {
                var list = new List<UvChartSource>(found.Count);
                foreach (var f in found) list.Add(f.source);
                return list;
            }
        }

        /// <summary>
        /// シーン → 同じ素材フォルダの Prefab の順に探す。使っているメッシュが無ければ null。
        /// あったが 1 つも読めなければ found が空で unreadable &gt; 0 の結果を返す(案内を出すため)。
        /// </summary>
        public static Result FindForTexture(Texture2D texture)
        {
            if (texture == null) return null;
            var targets = TargetTextures(texture);

            var scene = new Result { via = "scene" };
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (EditorUtility.IsPersistent(r)) continue;
                Collect(r, targets, scene, requireMatch: true, originPrefix: ScenePath(r.transform));
            }
            if (scene.found.Count > 0) return Dedup(scene);

            var project = new Result { via = "project" };
            string root = AssetRoot(AssetDatabase.GetAssetPath(texture));
            if (root != null)
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { root }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (go == null) continue;
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        Collect(r, targets, project, requireMatch: true, originPrefix: path + ":" + r.name);
                }
            }
            if (project.found.Count > 0) return Dedup(project);
            // 使っているメッシュはあったが読めなかった(Read/Write 無効など)。found は空で、件数だけ返す
            int unreadable = scene.unreadable + project.unreadable;
            return unreadable > 0 ? new Result { via = scene.unreadable > 0 ? "scene" : "project", unreadable = unreadable } : null;
        }

        /// <summary>
        /// 手動指定: シーンの GameObject・Prefab・FBX(モデル)の配下の Renderer から集める。
        /// 対応の強い順に 3 段で探す: (1) マテリアルがこのテクスチャ(か書き出し物)を参照、
        /// (2) 同じ名前のテクスチャを参照(FBX 付属のマテリアルが別フォルダの同名画像を指している場合)、
        /// (3) どれも無ければ全サブメッシュを matchedTexture = false で返す。
        /// </summary>
        public static Result FromObject(Object obj, Texture2D texture)
        {
            var go = obj as GameObject;
            if (go == null && obj != null)
                go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(obj));
            if (go == null) return null;
            var targets = texture != null ? TargetTextures(texture) : new HashSet<Texture>();
            string prefix = EditorUtility.IsPersistent(go) ? AssetDatabase.GetAssetPath(go) + ":" : "";

            var matched = new Result { via = "manual" };
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                Collect(r, targets, matched, requireMatch: true, originPrefix: prefix + ScenePath(r.transform));
            if (matched.found.Count > 0) return Dedup(matched);

            if (texture != null)
            {
                var byName = new Result { via = "manual" };
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    Collect(r, targets, byName, requireMatch: true, originPrefix: prefix + ScenePath(r.transform),
                            nameStem: texture.name);
                if (byName.found.Count > 0) return Dedup(byName);
            }

            var all = new Result { via = "manual" };
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                Collect(r, targets, all, requireMatch: false, originPrefix: prefix + ScenePath(r.transform));
            return all.found.Count > 0 || all.unreadable > 0 ? Dedup(all) : null;
        }

        /// <summary>このテクスチャ自身と、同じフォルダにある Iroca の書き出し物(&lt;名前&gt;_recolored*)。</summary>
        internal static HashSet<Texture> TargetTextures(Texture2D texture)
        {
            var set = new HashSet<Texture> { texture };
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return set;
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string stem = Path.GetFileNameWithoutExtension(path) + PathUtils.RecoloredSuffix;
            if (string.IsNullOrEmpty(dir)) return set;
            foreach (var guid in AssetDatabase.FindAssets(stem + " t:Texture2D", new[] { dir }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetDirectoryName(p)?.Replace('\\', '/') != dir) continue;
                if (!Path.GetFileNameWithoutExtension(p).StartsWith(stem, System.StringComparison.Ordinal)) continue;
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                if (t != null) set.Add(t);
            }
            return set;
        }

        /// <summary>
        /// テクスチャの「素材フォルダ」= Assets/ 直下の 1 階層目(Assets/AVATAR_A/Materials/... なら Assets/AVATAR_A)。
        /// アバター素材は普通この単位で入っているので、プロジェクト全体を走査せずに済む。
        /// </summary>
        internal static string AssetRoot(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var parts = assetPath.Replace('\\', '/').Split('/');
            if (parts.Length < 3 || parts[0] != "Assets") return null;
            return parts[0] + "/" + parts[1];
        }

        private static void Collect(Renderer r, HashSet<Texture> targets, Result result, bool requireMatch, string originPrefix,
                                    string nameStem = null)
        {
            Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                : r.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null;
            if (mesh == null) return;
            var mats = r.sharedMaterials;
            int n = Mathf.Min(mats.Length, mesh.subMeshCount);
            for (int s = 0; s < n; s++)
            {
                var mat = mats[s];
                bool match = MainTextureOf(mat, targets, nameStem, out string prop);
                if (requireMatch && !match) continue;
                var src = Read(mesh, s);
                if (src == null) { result.unreadable++; continue; }
                src.name = r.name + (mesh.subMeshCount > 1 ? "[" + s + "]" : "");
                if (match && mat != null)
                {
                    src.scale = mat.GetTextureScale(prop);
                    src.offset = mat.GetTextureOffset(prop);
                }
                result.found.Add(new Found
                {
                    source = src, origin = originPrefix, renderer = r, submesh = s, matchedTexture = match,
                });
            }
        }

        /// <summary>マテリアルのメインテクスチャが targets のどれかなら true(そのプロパティ名も返す)。</summary>
        internal static bool UsesTexture(Material mat, HashSet<Texture> targets, out string property)
            => MainTextureOf(mat, targets, null, out property);

        /// <summary>マテリアルのメインテクスチャ(<see cref="MainTextureProperties"/> の最初に見つかったもの)。無ければ null。</summary>
        internal static Texture MainTexture(Material mat)
        {
            if (mat == null) return null;
            foreach (var p in MainTextureProperties)
            {
                if (!mat.HasProperty(p)) continue;
                var t = mat.GetTexture(p);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// いろかで開くテクスチャ。Iroca の書き出し物(&lt;名前&gt;_recolored*、同じフォルダ)なら元の &lt;名前&gt; を返す
        /// (書き出し物を開いて色を変えると二重に塗ることになる。<see cref="TargetTextures"/> の逆向き)。
        /// 画像のアセットでなければ null。
        /// </summary>
        internal static Texture2D EditableSourceOf(Texture tex)
        {
            var tex2d = tex as Texture2D;
            if (tex2d == null) return null;
            string path = AssetDatabase.GetAssetPath(tex2d);
            if (string.IsNullOrEmpty(path)) return null;
            string name = Path.GetFileNameWithoutExtension(path);
            int cut = name.IndexOf(PathUtils.RecoloredSuffix, System.StringComparison.Ordinal);
            if (cut <= 0) return tex2d;
            string stem = name.Substring(0, cut);
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir)) return tex2d;
            foreach (var guid in AssetDatabase.FindAssets(stem + " t:Texture2D", new[] { dir }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetDirectoryName(p)?.Replace('\\', '/') != dir) continue;
                if (Path.GetFileNameWithoutExtension(p) != stem) continue;
                var original = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                if (original != null) return original;
            }
            return tex2d;
        }

        /// <summary>
        /// メインテクスチャが targets のどれかなら true。nameStem を渡すと、名前が同じ
        /// (か &lt;名前&gt;_recolored で始まる)テクスチャも対応とみなす(手動指定の 2 段目だけで使う)。
        /// </summary>
        private static bool MainTextureOf(Material mat, HashSet<Texture> targets, string nameStem, out string property)
        {
            property = null;
            if (mat == null) return false;
            foreach (var p in MainTextureProperties)
            {
                if (!mat.HasProperty(p)) continue;
                var t = mat.GetTexture(p);
                if (t == null) continue;
                bool hit = targets.Contains(t)
                    || (nameStem != null && (t.name == nameStem
                        || t.name.StartsWith(nameStem + PathUtils.RecoloredSuffix, System.StringComparison.Ordinal)));
                if (hit) { property = p; return true; }
            }
            return false;
        }

        /// <summary>UV0 と三角形を読む。読めなければ null(Read/Write 無効の扱いは Unity の版で違うので例外で判定する)。</summary>
        private static UvChartSource Read(Mesh mesh, int submesh)
        {
            try
            {
                var uv = new List<Vector2>();
                mesh.GetUVs(0, uv);
                if (uv.Count == 0) return null;
                var tris = mesh.GetTriangles(submesh);
                if (tris == null || tris.Length < 3) return null;
                return new UvChartSource { uv = uv.ToArray(), triangles = tris };
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>同じメッシュ・同じサブメッシュが複数の Renderer から見つかったら 1 つにする(UV は同じ)。</summary>
        private static Result Dedup(Result r)
        {
            var seen = new HashSet<(int, int)>();
            var outR = new Result { via = r.via, unreadable = r.unreadable };
            foreach (var f in r.found)
            {
                Mesh mesh = f.renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : f.renderer.TryGetComponent(out MeshFilter mf) ? mf.sharedMesh : null;
                if (mesh == null || seen.Add((mesh.GetInstanceID(), f.submesh))) outR.found.Add(f);
            }
            return outR;
        }

        private static string ScenePath(Transform t)
        {
            var names = new List<string>();
            for (; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
