// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Iroca
{
    public static partial class IrocaAutomation
    {
        /// <summary>主な色の数の既定と上限。</summary>
        private const int DefaultInspectColors = 8;
        private const int MaxInspectColors = 16;
        // 印付きの縮小画像の長辺。
        private const int InspectThumbnailMaxSide = 512;

        [Serializable]
        internal class InspectResult
        {
            public bool ok;
            public string error = "";
            // "scene" | "object" | "texture"
            public string kind = "";
            public string target = "";
            public string version = "";
            public List<ObjectInfo> objects = new List<ObjectInfo>();
            public List<TextureInfo> textures = new List<TextureInfo>();
            public List<string> warnings = new List<string>();
            public string note = "";
        }

        [Serializable]
        internal class ObjectInfo
        {
            public string path = "";
            public bool isAvatar;
            public int rendererCount;
            public int textureCount;
        }

        [Serializable]
        internal class TextureInfo
        {
            public string path = "";
            public int width;              // 元ファイルの寸法
            public int height;
            public int workWidth;          // 色替え・解析に使う作業解像度(colors を出したときだけ)
            public int workHeight;
            public List<string> materials = new List<string>();
            public List<string> renderers = new List<string>();
            public List<string> recipes = new List<string>();
            // Iroca の書き出し物なら、その元のテクスチャ(色替えは元に対して行う)。元そのものなら空。
            public string editableSource = "";
            public string thumbnail = "";
            public List<ColorInfo> colors = new List<ColorInfo>();
        }

        [Serializable]
        internal class ColorInfo
        {
            public string hex = "";
            public float[] rgb;            // 0..1(sRGB)
            public float coverage;         // 不透明な画素に占める割合
            public float[] uv;             // 代表点 [u, v](左下原点)。iroca_recolor の at にそのまま渡せる
            public float regionShare;      // その色のうち、代表点のある塊の割合(小さい = あちこちに散っている色)
        }

        /// <summary>
        /// 色替えの対象を調べる。<paramref name="target"/> は次のどれか:
        ///   空 = 開いているシーンの、レンダラーを持つルート(アバターの一覧)。
        ///   シーンの GameObject(階層のパス "Avatar/Body"・名前・"#インスタンス ID")= その下で使っているテクスチャの一覧。
        ///   テクスチャ・マテリアルのアセットパス = そのテクスチャ(マテリアルならメインテクスチャ)の主な色と代表点、
        ///   印付きの縮小画像(thumbnail)。
        /// </summary>
        public static string Inspect(string target, int maxColors = DefaultInspectColors)
        {
            var r = new InspectResult { target = target ?? "", version = ExtractVersion() };
            try
            {
                maxColors = Mathf.Clamp(maxColors <= 0 ? DefaultInspectColors : maxColors, 1, MaxInspectColors);
                if (string.IsNullOrWhiteSpace(target))
                {
                    InspectScene(r);
                    return Finish(r);
                }

                var asset = LoadAssetByPath(target);
                if (asset is Material mat)
                {
                    var main = MeshUvLocator.MainTexture(mat) as Texture2D;
                    if (main == null) return Finish(r, $"material has no main texture: {target}");
                    InspectTexture(r, main, maxColors);
                    return Finish(r);
                }
                if (asset is Texture2D tex)
                {
                    InspectTexture(r, tex, maxColors);
                    return Finish(r);
                }
                if (asset != null)
                    return Finish(r, $"unsupported asset type {asset.GetType().Name}: {target} (pass a texture, a material or a scene object)");

                var go = FindSceneObject(target, out string findError);
                if (go == null) return Finish(r, findError);
                InspectObject(r, go);
                return Finish(r);
            }
            catch (Exception ex)
            {
                return Finish(r, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string Finish(InspectResult r, string error = null)
        {
            r.ok = string.IsNullOrEmpty(error);
            r.error = error ?? "";
            if (r.ok && string.IsNullOrEmpty(r.note))
            {
                r.note = r.kind == "texture"
                    ? "Open 'thumbnail' to see the texture with a ring at each color's 'uv'. "
                      + "Pass the uv of the part you want to change as 'at' to iroca_recolor."
                    : "Call iroca_inspect with a texture path from 'textures' to get its main colors and click positions.";
            }
            return JsonUtility.ToJson(r);
        }

        private static void InspectScene(InspectResult r)
        {
            r.kind = "scene";
            foreach (var root in SceneRoots())
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) continue;
                r.objects.Add(new ObjectInfo
                {
                    path = root.name,
                    isAvatar = HasComponentNamed(root, "VRCAvatarDescriptor"),
                    rendererCount = renderers.Length,
                    textureCount = MainTexturesOf(renderers).Count,
                });
            }
        }

        private static void InspectObject(InspectResult r, GameObject go)
        {
            r.kind = "object";
            r.target = HierarchyPath(go.transform);
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (var kv in MainTexturesOf(renderers))
            {
                var tex = kv.Key;
                var info = NewTextureInfo(tex);
                foreach (var (renderer, material) in kv.Value)
                {
                    string rp = HierarchyPath(renderer.transform);
                    if (!info.renderers.Contains(rp)) info.renderers.Add(rp);
                    string mp = MaterialLabel(material);
                    if (!info.materials.Contains(mp)) info.materials.Add(mp);
                }
                r.textures.Add(info);
            }
            if (r.textures.Count == 0) r.warnings.Add("no renderer with a main texture under this object.");
        }

        private static void InspectTexture(InspectResult r, Texture2D tex, int maxColors)
        {
            r.kind = "texture";
            var info = NewTextureInfo(tex);
            r.target = info.path;
            r.textures.Add(info);

            // シーンでこのテクスチャをメインに使っているマテリアル・レンダラー
            foreach (var kv in MainTexturesOf(SceneRenderers()))
            {
                if (kv.Key != tex) continue;
                foreach (var (renderer, material) in kv.Value)
                {
                    string rp = HierarchyPath(renderer.transform);
                    if (!info.renderers.Contains(rp)) info.renderers.Add(rp);
                    string mp = MaterialLabel(material);
                    if (!info.materials.Contains(mp)) info.materials.Add(mp);
                }
            }
            if (!string.IsNullOrEmpty(info.editableSource))
                r.warnings.Add($"this texture is an Iroca export; recolor its source instead: {info.editableSource}");

            string abs = PhysicalPath(info.path);
            if (abs == null) { r.warnings.Add("texture file not found; colors are not available."); return; }
            var kind = ExportPipeline.ReadSourcePixels(abs, tex, out Color32[] pixels, out int w, out int h, workingSize: true);
            if (kind == ExportPipeline.SourceKind.Unavailable)
            {
                r.warnings.Add("could not read the pixels (enable Read/Write in the import settings, or use a PNG/JPG source).");
                return;
            }
            info.workWidth = w;
            info.workHeight = h;

            var entries = TextureColorSummary.Summarize(pixels, w, h, maxColors);
            foreach (var e in entries)
            {
                info.colors.Add(new ColorInfo
                {
                    hex = "#" + ColorUtility.ToHtmlStringRGB(e.color),
                    rgb = new[] { e.color.r / 255f, e.color.g / 255f, e.color.b / 255f },
                    coverage = e.fraction,
                    uv = new[] { e.uv.x, e.uv.y },
                    regionShare = e.regionShare,
                });
            }
            info.thumbnail = WriteInspectThumbnail(pixels, w, h, entries, info.path, r.warnings);
        }

        private static TextureInfo NewTextureInfo(Texture2D tex)
        {
            string path = AssetDatabase.GetAssetPath(tex);
            var info = new TextureInfo { path = path, width = tex.width, height = tex.height };
            if (AssetImporter.GetAtPath(path) is TextureImporter ti)
            {
                ti.GetSourceTextureWidthAndHeight(out int sw, out int sh);
                if (sw > 0 && sh > 0) { info.width = sw; info.height = sh; }
            }
            var source = MeshUvLocator.EditableSourceOf(tex);
            if (source != null && source != tex) info.editableSource = AssetDatabase.GetAssetPath(source);
            foreach (var recipe in RecipeStore.FindForTexture(source ?? tex))
                info.recipes.Add(AssetDatabase.GetAssetPath(recipe));
            return info;
        }

        // 縮小画像に、各色の代表点の輪(外側が黒・内側が白)を描いて UserSettings/Iroca/mcp/inspect に書く。
        // Assets の外なので取り込まれない。失敗しても調べた結果は返す(warnings に残す)。
        private static string WriteInspectThumbnail(Color32[] pixels, int w, int h,
            List<TextureColorSummary.Entry> entries, string texturePath, List<string> warnings)
        {
            try
            {
                TextureColorSummary.DownscaleTo(pixels, w, h, InspectThumbnailMaxSide, out var small, out int tw, out int th);
                var img = ReferenceEquals(small, pixels) ? (Color32[])pixels.Clone() : small;
                foreach (var e in entries)
                {
                    int cx = Mathf.Clamp((int)(e.uv.x * tw), 0, tw - 1);
                    int cy = Mathf.Clamp((int)(e.uv.y * th), 0, th - 1);
                    DrawRing(img, tw, th, cx, cy, 6, new Color32(0, 0, 0, 255));
                    DrawRing(img, tw, th, cx, cy, 5, new Color32(255, 255, 255, 255));
                    DrawRing(img, tw, th, cx, cy, 4, new Color32(0, 0, 0, 255));
                }
                string stem = Path.GetFileNameWithoutExtension(texturePath);
                string guid = AssetDatabase.AssetPathToGUID(texturePath);
                string file = Path.Combine(McpDir, "inspect",
                    $"{stem}_{(string.IsNullOrEmpty(guid) ? "x" : guid.Substring(0, 8))}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                AtomicFile.WriteAllBytes(file, ExportPipeline.EncodePng(img, tw, th));
                return file;
            }
            catch (Exception ex)
            {
                warnings.Add($"thumbnail failed: {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }

        private static void DrawRing(Color32[] img, int w, int h, int cx, int cy, int radius, Color32 c)
        {
            for (int d = -radius; d <= radius; d++)
            {
                Plot(img, w, h, cx + d, cy - radius, c);
                Plot(img, w, h, cx + d, cy + radius, c);
                Plot(img, w, h, cx - radius, cy + d, c);
                Plot(img, w, h, cx + radius, cy + d, c);
            }
        }

        private static void Plot(Color32[] img, int w, int h, int x, int y, Color32 c)
        {
            if (x >= 0 && x < w && y >= 0 && y < h) img[y * w + x] = c;
        }

        // ─── 対象の解決 ───

        // アセットパス(Assets/・Packages/ 始まり、またはプロジェクト内の絶対パス)ならそのアセット。違えば null。
        private static UnityEngine.Object LoadAssetByPath(string target)
        {
            string rel = target.Replace('\\', '/');
            if (Path.IsPathRooted(rel))
            {
                rel = PathUtils.ToAssetsRelativeOrNull(rel);
                if (rel == null) return null;
            }
            if (!rel.StartsWith("Assets/", StringComparison.Ordinal) && !rel.StartsWith("Packages/", StringComparison.Ordinal))
                return null;
            return AssetDatabase.LoadMainAssetAtPath(rel);
        }

        // "#123"・"123"(インスタンス ID)、"Root/Child"(階層のパス)、名前(シーン全体で 1 つだけのとき)。
        private static GameObject FindSceneObject(string target, out string error)
        {
            error = "";
            string t = target.Trim();
            string idText = t.StartsWith("#", StringComparison.Ordinal) ? t.Substring(1) : t;
            if (int.TryParse(idText, out int id))
            {
                var obj = EditorUtility.InstanceIDToObject(id);
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go != null && RecipeRegistration.IsSceneObject(go)) return go;
                if (t.StartsWith("#", StringComparison.Ordinal)) { error = $"no scene object with instance id {id}"; return null; }
            }

            string[] parts = t.Trim('/').Split('/');
            foreach (var root in SceneRoots())
            {
                if (root.name != parts[0]) continue;
                var cur = root.transform;
                for (int i = 1; i < parts.Length && cur != null; i++) cur = cur.Find(parts[i]);
                if (cur != null) return cur.gameObject;
            }

            if (parts.Length == 1)
            {
                var hits = new List<GameObject>();
                foreach (var root in SceneRoots())
                    foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                        if (tr.name == t) hits.Add(tr.gameObject);
                if (hits.Count == 1) return hits[0];
                if (hits.Count > 1)
                {
                    error = $"'{t}' matches {hits.Count} objects; pass a hierarchy path: "
                          + string.Join(", ", hits.Take(5).Select(g => HierarchyPath(g.transform)));
                    return null;
                }
            }
            error = $"not found: '{t}' (pass a scene object path, a texture or material asset path, or nothing to list the scene)";
            return null;
        }

        private static IEnumerable<GameObject> SceneRoots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) yield return root;
            }
        }

        private static IEnumerable<Renderer> SceneRenderers()
        {
            foreach (var root in SceneRoots())
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    yield return r;
        }

        // メインテクスチャごとに、それを使うレンダラーとマテリアル(テクスチャは最初に見つかった順)。
        private static List<KeyValuePair<Texture2D, List<(Renderer, Material)>>> MainTexturesOf(IEnumerable<Renderer> renderers)
        {
            var result = new List<KeyValuePair<Texture2D, List<(Renderer, Material)>>>();
            var map = new Dictionary<Texture2D, List<(Renderer, Material)>>();
            foreach (var r in renderers)
                foreach (var m in r.sharedMaterials)
                {
                    if (!(MeshUvLocator.MainTexture(m) is Texture2D tex)) continue;
                    if (!map.TryGetValue(tex, out var list))
                    {
                        map[tex] = list = new List<(Renderer, Material)>();
                        result.Add(new KeyValuePair<Texture2D, List<(Renderer, Material)>>(tex, list));
                    }
                    list.Add((r, m));
                }
            return result;
        }

        private static bool HasComponentNamed(GameObject go, string typeName)
        {
            foreach (var c in go.GetComponents<Component>())
                if (c != null && c.GetType().Name == typeName) return true;
            return false;
        }

        private static string MaterialLabel(Material m)
        {
            string p = AssetDatabase.GetAssetPath(m);
            return string.IsNullOrEmpty(p) ? m.name : p;
        }

        private static string HierarchyPath(Transform t)
        {
            var names = new List<string>();
            for (var cur = t; cur != null; cur = cur.parent) names.Add(cur.name);
            names.Reverse();
            return string.Join("/", names);
        }

        // アセットパスの実ファイル(Packages/ は FileUtil で解決)。プロジェクトの外は null。
        private static string PhysicalPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string abs = assetPath.StartsWith("Packages/", StringComparison.Ordinal)
                ? Path.GetFullPath(FileUtil.GetPhysicalPath(assetPath))
                : ToProjectAbsolute(assetPath);
            return abs != null && File.Exists(abs) ? abs : null;
        }
    }
}
