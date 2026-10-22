// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Iroca
{
    /// <summary>
    /// 編集中のテクスチャを使うマテリアルの「色の設定」を読む。いろかが変えるのはテクスチャだけだが、
    /// アバター上の色は、シェーダーがテクスチャに掛ける設定(メインカラー・色調補正・重ね描きなど)でも変わる。
    /// ここはその設定を見つけるだけで、プレビューに掛けるのは <see cref="AppearanceRenderer"/>。
    ///
    /// シェーダーはプロパティの有無で見分ける。名前で見分けると、ロックした Poiyomi のように
    /// シェーダー名が変わるものを取りこぼす。lilToon・Poiyomi 以外は、メインテクスチャに
    /// メインカラーを掛けるもの(標準・URP・VRChat の Mobile 系・MToon など)として扱う。
    /// </summary>
    internal static class MaterialColorSettings
    {
        internal enum Family { Generic, LilToon, Poiyomi }

        /// <summary>テクスチャがマテリアルのどの欄で使われているか。</summary>
        internal enum Slot { Main, Layer2nd, Layer3rd }

        /// <summary>見つけた色の設定。</summary>
        [System.Flags]
        internal enum Effect
        {
            None = 0,
            /// <summary>色の掛け算(メインテクスチャならメインカラー、重ね描きの層ならその層の色)。</summary>
            Tint = 1 << 0,
            /// <summary>lilToon の色調補正(色相・彩度・明度・ガンマ)。</summary>
            ToneCorrection = 1 << 1,
            /// <summary>lilToon のグラデーションマップ。</summary>
            Gradation = 1 << 2,
            /// <summary>グラデーションマップの強さが 1(色を変えても見た目にはほとんど出ない)。</summary>
            GradationFull = 1 << 3,
            /// <summary>lilToon の 2nd / 3rd を UV0 で重ねる(メインの上に重ねて描く)。</summary>
            Layer2nd = 1 << 4,
            Layer3rd = 1 << 5,
            /// <summary>UV0 以外(UV1〜3・MatCap)で重ねる層。テクスチャの上の位置が決まらないのでプレビューに入れない。</summary>
            LayerOtherUv = 1 << 6,
            /// <summary>Poiyomi の Color Adjust(色相・彩度・明るさ・ガンマなど)。</summary>
            PoiColorAdjust = 1 << 7,
            /// <summary>Poiyomi のデカール。</summary>
            PoiDecal = 1 << 8,
            /// <summary>Poiyomi の RGBA Color Masking。</summary>
            PoiRgbMask = 1 << 9,
        }

        /// <summary>1 つのマテリアルの読み取り結果。</summary>
        internal sealed class Report
        {
            public Material material;
            public Family family;
            public Slot slot;
            /// <summary>掛け算の色(マテリアルに保存されている値。HDR なら 1 を超える)。</summary>
            public Color tint = Color.white;
            public Effect effects;
            /// <summary>
            /// 色・設定の要約。同じ値なら同じ見た目になる(重複をまとめる・設定が変わったかを見るのに使う)。
            /// </summary>
            public string signature;

            /// <summary>見た目がテクスチャのままでない(何か色の設定がある)か。</summary>
            public bool AffectsColor => effects != Effect.None;
        }

        // 2nd・3rd の層を見るプロパティ(lilToon)。
        private const string Use2nd = "_UseMain2ndTex", Use3rd = "_UseMain3rdTex";
        private const string Tex2nd = "_Main2ndTex", Tex3rd = "_Main3rdTex";
        private const string Uv2nd = "_Main2ndTex_UVMode", Uv3rd = "_Main3rdTex_UVMode";
        private const string Color2nd = "_Color2nd", Color3rd = "_Color3rd";

        // Poiyomi のデカールは 4 枚。
        private static readonly string[] PoiDecalToggles = { "_DecalEnabled", "_DecalEnabled1", "_DecalEnabled2", "_DecalEnabled3" };

        // 色が白とみなす幅(8 bit の半段)。
        private const float WhiteEpsilon = 0.5f / 255f;

        /// <summary>
        /// マテリアルが targets のどれかを使っていれば、その欄と色の設定を読む。使っていなければ null。
        /// メインテクスチャと重ね描きの層の両方で使っていれば、メインとして読む。
        /// メイン・2nd・3rd 以外の欄(輪郭線・影色・発光など)だけで使っていれば null(色替えはそのまま付いていく)。
        /// </summary>
        public static Report Analyze(Material mat, HashSet<Texture> targets)
        {
            if (mat == null || mat.shader == null || targets == null) return null;
            var family = FamilyOf(mat);
            Slot? slot = null;
            string main = MainTextureProperty(mat);
            if (main != null && targets.Contains(mat.GetTexture(main))) slot = Slot.Main;
            else if (family == Family.LilToon && UsesLayer(mat, Use2nd, Tex2nd, targets)) slot = Slot.Layer2nd;
            else if (family == Family.LilToon && UsesLayer(mat, Use3rd, Tex3rd, targets)) slot = Slot.Layer3rd;
            if (slot == null) return null;

            var r = new Report { material = mat, family = family, slot = slot.Value };
            switch (r.slot)
            {
                case Slot.Main:
                    r.tint = MainColor(mat);
                    if (!IsWhite(r.tint)) r.effects |= Effect.Tint;
                    if (family == Family.LilToon) ReadLilToonMain(mat, r);
                    else if (family == Family.Poiyomi) ReadPoiyomiMain(mat, r);
                    break;
                case Slot.Layer2nd:
                    r.tint = mat.HasProperty(Color2nd) ? mat.GetColor(Color2nd) : Color.white;
                    if (!IsWhite(r.tint)) r.effects |= Effect.Tint;
                    break;
                case Slot.Layer3rd:
                    r.tint = mat.HasProperty(Color3rd) ? mat.GetColor(Color3rd) : Color.white;
                    if (!IsWhite(r.tint)) r.effects |= Effect.Tint;
                    break;
            }
            r.signature = Signature(mat, r);
            return r;
        }

        /// <summary>シェーダーの系統。lilToon は版のプロパティ、Poiyomi は Thry のエディタの印で見分ける。</summary>
        internal static Family FamilyOf(Material mat)
        {
            if (mat.HasProperty("_lilToonVersion")) return Family.LilToon;
            if (mat.HasProperty("shader_is_using_thry_editor") || mat.HasProperty("_MainColorAdjustToggle")
                || mat.shader.name.IndexOf("poiyomi", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return Family.Poiyomi;
            return Family.Generic;
        }

        /// <summary>
        /// メインテクスチャのプロパティ名。シェーダーが [MainTexture] を付けていればそれ、無ければ
        /// _MainTex・_BaseMap・_BaseColorMap(標準・URP・HDRP)の順。無ければ null。
        /// </summary>
        internal static string MainTextureProperty(Material mat)
            => FlaggedProperty(mat.shader, ShaderPropertyFlags.MainTexture, ShaderPropertyType.Texture)
               ?? FirstExisting(mat, "_MainTex", "_BaseMap", "_BaseColorMap");

        /// <summary>
        /// メインカラー(テクスチャに掛ける色)。シェーダーが [MainColor] を付けていればそれ、無ければ
        /// _Color・_BaseColor の順(Unity の Material.color と同じ決め方に URP の名前を足したもの)。無ければ白。
        /// </summary>
        internal static Color MainColor(Material mat)
        {
            string p = FlaggedProperty(mat.shader, ShaderPropertyFlags.MainColor, ShaderPropertyType.Color)
                       ?? FirstExisting(mat, "_Color", "_BaseColor");
            return p != null ? mat.GetColor(p) : Color.white;
        }

        private static void ReadLilToonMain(Material mat, Report r)
        {
            if (mat.HasProperty("_MainTexHSVG"))
            {
                var hsvg = mat.GetVector("_MainTexHSVG");
                if (!Near(hsvg.x, 0f) || !Near(hsvg.y, 1f) || !Near(hsvg.z, 1f) || !Near(hsvg.w, 1f))
                    r.effects |= Effect.ToneCorrection;
            }
            if (mat.HasProperty("_MainGradationStrength") && mat.HasProperty("_MainGradationTex"))
            {
                float s = mat.GetFloat("_MainGradationStrength");
                if (s > 0f && mat.GetTexture("_MainGradationTex") != null)
                {
                    r.effects |= Effect.Gradation;
                    if (s >= 0.99f) r.effects |= Effect.GradationFull;
                }
            }
            // UV0 で重ねる層はメインテクスチャの上の位置が決まる(プレビューに入れられる)。UV1〜3・MatCap は決まらない。
            if (LayerOn(mat, Use2nd)) r.effects |= LayerUvMode(mat, Uv2nd) == 0 ? Effect.Layer2nd : Effect.LayerOtherUv;
            if (LayerOn(mat, Use3rd)) r.effects |= LayerUvMode(mat, Uv3rd) == 0 ? Effect.Layer3rd : Effect.LayerOtherUv;
        }

        private static void ReadPoiyomiMain(Material mat, Report r)
        {
            if (FloatOn(mat, "_MainColorAdjustToggle") && PoiyomiAdjusts(mat))
                r.effects |= Effect.PoiColorAdjust;
            foreach (var t in PoiDecalToggles)
                if (FloatOn(mat, t)) { r.effects |= Effect.PoiDecal; break; }
            if (FloatOn(mat, "_RGBMaskEnabled")) r.effects |= Effect.PoiRgbMask;
        }

        // Color Adjust を入れていても、どの値も既定のままなら見た目は変わらない。
        private static bool PoiyomiAdjusts(Material mat)
        {
            if (FloatOn(mat, "_MainHueShiftToggle") && !Near(Get(mat, "_MainHueShift", 0f), 0f)) return true;
            if (!Near(Get(mat, "_Saturation", 0f), 0f)) return true;
            if (!Near(Get(mat, "_MainBrightness", 0f), 0f)) return true;
            if (!Near(Get(mat, "_MainGamma", 1f), 1f)) return true;
            if (FloatOn(mat, "_ColorGradingToggle") && Get(mat, "_MainGradationStrength", 0f) > 0f) return true;
            if (mat.HasProperty("_MainTintColor") && mat.GetColor("_MainTintColor").a > 0f) return true;
            return false;
        }

        /// <summary>
        /// 見た目を決める値の要約。メインカラーと、lilToon なら焼き込みのシェーダー
        /// (<see cref="AppearanceRenderer"/>)へ写すプロパティの値すべて。
        /// </summary>
        private static string Signature(Material mat, Report r)
        {
            var sb = new StringBuilder();
            sb.Append((int)r.family).Append('|').Append((int)r.slot).Append('|').Append((int)r.effects).Append('|');
            Append(sb, r.tint);
            if (r.slot == Slot.Main && r.family == Family.LilToon)
            {
                var baker = AppearanceRenderer.LilToonBaker;
                if (baker != null)
                {
                    int n = baker.GetPropertyCount();
                    for (int i = 0; i < n; i++)
                    {
                        string name = baker.GetPropertyName(i);
                        if (name == "_MainTex" || !mat.HasProperty(name)) continue;
                        sb.Append('|');
                        switch (baker.GetPropertyType(i))
                        {
                            case ShaderPropertyType.Color: Append(sb, mat.GetColor(name)); break;
                            case ShaderPropertyType.Vector: Append(sb, mat.GetVector(name)); break;
                            case ShaderPropertyType.Int: sb.Append(mat.GetInteger(name)); break;
                            case ShaderPropertyType.Texture:
                                var t = mat.GetTexture(name);
                                sb.Append(t != null ? t.GetInstanceID() : 0);
                                Append(sb, mat.GetTextureScale(name));
                                Append(sb, mat.GetTextureOffset(name));
                                break;
                            default: sb.Append(mat.GetFloat(name).ToString("R")); break;
                        }
                    }
                }
            }
            return sb.ToString();
        }

        // ── 編集中のテクスチャを使うマテリアルを集める ─────────────────────────

        // 素材フォルダから読むマテリアルの上限(依存で絞った後)。
        private const int MaxProjectMaterials = 32;

        /// <summary>
        /// テクスチャ(と、いろかで書き出したもの)を使うマテリアルを読む。開いているシーンの Renderer を先に見て、
        /// 1 つも無ければテクスチャと同じ素材フォルダのマテリアルを見る(<see cref="MeshUvLocator"/> と同じ範囲)。
        /// 使っている Renderer の数の多い順。
        /// </summary>
        public static List<Report> FindForTexture(Texture2D texture)
            => texture != null ? FindForTexture(texture, MeshUvLocator.TargetTextures(texture), out _) : new List<Report>();

        /// <summary>
        /// <see cref="FindForTexture(Texture2D)"/> の、探すテクスチャの組(<see cref="MeshUvLocator.TargetTextures"/>)を
        /// 呼び出し側が持つ版。inScene は開いているシーンで見つけたか(素材フォルダから探したなら false)。
        /// </summary>
        public static List<Report> FindForTexture(Texture2D texture, HashSet<Texture> targets, out bool inScene)
        {
            var result = new List<Report>();
            inScene = false;
            if (texture == null || targets == null) return result;

            var users = new Dictionary<Material, int>();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (EditorUtility.IsPersistent(r)) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null) users[m] = users.TryGetValue(m, out int c) ? c + 1 : 1;
            }
            foreach (var kv in users)
            {
                var rep = Analyze(kv.Key, targets);
                if (rep != null) result.Add(rep);
            }
            if (result.Count > 0)
            {
                result.Sort((a, b) => users[b.material].CompareTo(users[a.material]));
                inScene = true;
                return result;
            }

            foreach (var m in MaterialsUsing(MeshUvLocator.AssetRoot(AssetDatabase.GetAssetPath(texture)), targets))
            {
                var rep = Analyze(m, targets);
                if (rep != null) result.Add(rep);
            }
            return result;
        }

        /// <summary>素材フォルダ(root)のマテリアルのうち、targets のどれかに依存するもの(読み込む前に依存で絞る)。</summary>
        private static IEnumerable<Material> MaterialsUsing(string root, HashSet<Texture> targets)
        {
            if (root == null) yield break;
            var targetPaths = new HashSet<string>();
            foreach (var t in targets)
            {
                string p = t != null ? AssetDatabase.GetAssetPath(t) : null;
                if (!string.IsNullOrEmpty(p)) targetPaths.Add(p);
            }
            if (targetPaths.Count == 0) yield break;
            int found = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                bool uses = false;
                foreach (var dep in AssetDatabase.GetDependencies(path, false))
                    if (targetPaths.Contains(dep)) { uses = true; break; }
                if (!uses) continue;
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) continue;
                yield return m;
                if (++found >= MaxProjectMaterials) yield break;
            }
        }

        // ── 小さな道具 ─────────────────────────────────────────────

        private static bool UsesLayer(Material mat, string use, string tex, HashSet<Texture> targets)
            => LayerOn(mat, use) && mat.HasProperty(tex) && targets.Contains(mat.GetTexture(tex));

        private static bool LayerOn(Material mat, string use) => FloatOn(mat, use);

        private static int LayerUvMode(Material mat, string prop)
            => mat.HasProperty(prop) ? Mathf.RoundToInt(mat.GetFloat(prop)) : 0;

        private static bool FloatOn(Material mat, string prop) => mat.HasProperty(prop) && mat.GetFloat(prop) > 0.5f;

        private static float Get(Material mat, string prop, float fallback)
            => mat.HasProperty(prop) ? mat.GetFloat(prop) : fallback;

        private static bool Near(float a, float b) => Mathf.Abs(a - b) <= 1e-4f;

        private static bool IsWhite(Color c)
            => Mathf.Abs(c.r - 1f) <= WhiteEpsilon && Mathf.Abs(c.g - 1f) <= WhiteEpsilon && Mathf.Abs(c.b - 1f) <= WhiteEpsilon;

        private static string FlaggedProperty(Shader shader, ShaderPropertyFlags flag, ShaderPropertyType type)
        {
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
                if ((shader.GetPropertyFlags(i) & flag) != 0 && shader.GetPropertyType(i) == type)
                    return shader.GetPropertyName(i);
            return null;
        }

        private static string FirstExisting(Material mat, params string[] names)
        {
            foreach (var n in names)
                if (mat.HasProperty(n)) return n;
            return null;
        }

        private static void Append(StringBuilder sb, Color c)
            => sb.Append(c.r.ToString("R")).Append(',').Append(c.g.ToString("R")).Append(',')
                 .Append(c.b.ToString("R")).Append(',').Append(c.a.ToString("R"));

        private static void Append(StringBuilder sb, Vector4 v)
            => sb.Append(v.x.ToString("R")).Append(',').Append(v.y.ToString("R")).Append(',')
                 .Append(v.z.ToString("R")).Append(',').Append(v.w.ToString("R"));

        private static void Append(StringBuilder sb, Vector2 v)
            => sb.Append(',').Append(v.x.ToString("R")).Append(',').Append(v.y.ToString("R"));
    }
}
