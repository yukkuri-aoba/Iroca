// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    public static partial class IrocaAutomation
    {
        /// <summary><see cref="Recolor"/> の依頼(JSON)。</summary>
        [Serializable]
        internal class RecolorRequest
        {
            // テクスチャかマテリアル(メインテクスチャ)のアセットパス
            public string texture = "";
            public List<ChangeRequest> changes = new List<ChangeRequest>();
            // 消すゾーンの名前
            public List<string> remove = new List<string>();
            // true なら、今あるゾーンとマスクを捨ててから changes を足す
            public bool reset;
            // 土台にするプリセット(ファイルパス・名前・インライン JSON)。指定すると今の編集内容の代わりにこれを土台にする
            public string preset = "";
            // "avatar"(アバターに非破壊で登録) | "png"(画像に書き出す) | "preview"(何も保存せず比較画像だけ)
            public string apply = ApplyAvatar;
            // 登録先のシーンのオブジェクト(既定: そのテクスチャを使っているレンダラーの共通の親)
            public string avatar = "";
            // png の出力先(既定: 元と同じフォルダの <名前>_recolored.png)
            public string output = "";
        }

        [Serializable]
        internal class ChangeRequest
        {
            // ゾーンの名前。今あるゾーンと同じ名前なら、そのゾーンを置き換える(頼み直し)
            public string name = "";
            // スポイトする位置 [u, v](左下原点、0..1)。iroca_inspect の uv をそのまま渡せる
            public float[] at;
            // at が無いときの、変えたい色("#RRGGBB"。Unity の色名 "navy" なども読める)
            public string from = "";
            // 変更先の色("#RRGGBB"。Unity の色名 "navy" なども読める)
            public string to = "";
        }

        [Serializable]
        internal class RecolorTextureResult
        {
            public bool ok;
            public string error = "";
            public string texture = "";
            public string apply = "";
            public string avatar = "";      // 登録したオブジェクト(階層のパス)
            public string recipe = "";      // 書いたレシピ
            public string output = "";      // 書き出した PNG
            public string preview = "";     // 比較画像(左=前 / 中=後 / 右=変わった画素をマゼンタ)
            public List<ZoneInfo> zones = new List<ZoneInfo>();
            public float changedFraction;
            public float largestComponentFraction;
            public int changeComponentCount;
            public List<string> warnings = new List<string>();
            public string note = "";
        }

        [Serializable]
        internal class ZoneInfo
        {
            public string name = "";
            public string from = "";
            public string to = "";
            public float[] at;
            public float tolerance;
            public bool enabled;
        }

        internal const string ApplyAvatar = "avatar";
        internal const string ApplyPng = "png";
        internal const string ApplyPreview = "preview";

        // UI で新しく作ったゾーンの初期値と同じ(IrocaWindow.NewZoneInitialTolerance)。
        private const float NewZoneTolerance = ZonesJsonDefaults.Tolerance;

        /// <summary>
        /// 色替えを足して、アバターに登録する(既定)・PNG に書き出す・比較画像だけ作る。
        /// 土台は、そのテクスチャを開いているいろかの窓の編集内容 → 登録先のレシピ → プロジェクトに 1 つだけのレシピ
        /// → 空、の順(preset を渡せばそれ)。changes の 1 件が 1 ゾーンで、UI でスポイトして新しいゾーンを作ったのと同じ
        /// (末尾に足す=優先度は最も低い)。同じ名前のゾーンがあれば、その場所で置き換える。
        /// </summary>
        public static string Recolor(string requestJson)
        {
            var r = new RecolorTextureResult();
            try
            {
                RecolorRequest req;
                try { req = JsonUtility.FromJson<RecolorRequest>(requestJson ?? ""); }
                catch (Exception ex) { return FinishRecolor(r, $"request JSON parse failed: {ex.Message}"); }
                if (req == null) return FinishRecolor(r, "request JSON is empty.");
                r.apply = string.IsNullOrEmpty(req.apply) ? ApplyAvatar : req.apply.Trim().ToLowerInvariant();
                if (r.apply != ApplyAvatar && r.apply != ApplyPng && r.apply != ApplyPreview)
                    return FinishRecolor(r, $"unknown apply '{req.apply}' (use 'avatar', 'png' or 'preview').");

                // ── テクスチャ ──
                if (string.IsNullOrWhiteSpace(req.texture)) return FinishRecolor(r, "texture is required.");
                var asset = LoadAssetByPath(req.texture);
                var tex = asset as Texture2D ?? (asset is Material m ? MeshUvLocator.MainTexture(m) as Texture2D : null);
                if (tex == null) return FinishRecolor(r, $"not a texture or a material with a main texture: {req.texture}");
                var source = MeshUvLocator.EditableSourceOf(tex);
                if (source != null && source != tex)
                {
                    r.warnings.Add($"{AssetDatabase.GetAssetPath(tex)} is an Iroca export; recoloring its source {AssetDatabase.GetAssetPath(source)} instead.");
                    tex = source;
                }
                r.texture = AssetDatabase.GetAssetPath(tex);
                string abs = PhysicalPath(r.texture);
                if (abs == null) return FinishRecolor(r, $"texture file not found: {r.texture}");
                if (ExportPipeline.ReadSourcePixels(abs, tex, out Color32[] work, out int w, out int h, workingSize: true)
                        == ExportPipeline.SourceKind.Unavailable)
                    return FinishRecolor(r, "could not read the pixels (enable Read/Write in the import settings, or use a PNG/JPG source).");

                // ── 登録先と土台 ──
                GameObject target = null;
                IrocaRecipe recipe = null;
                if (r.apply == ApplyAvatar)
                {
                    if (!string.IsNullOrWhiteSpace(req.avatar))
                    {
                        target = FindSceneObject(req.avatar, out string findError);
                        if (target == null) return FinishRecolor(r, findError);
                    }
                    else
                    {
                        target = RecipeRegistration.SuggestTarget(tex, null);
                        if (target == null)
                            return FinishRecolor(r, "could not decide which object to register on (the texture is used by no "
                                + "scene object, or by several avatars). Pass 'avatar' (a scene object path).");
                    }
                    r.avatar = HierarchyPath(target.transform);
                    recipe = RecipeRegistration.ExistingFor(target, tex);
                }
                if (recipe == null)
                {
                    var found = RecipeStore.FindForTexture(tex);
                    if (found.Count == 1) recipe = found[0];
                }
                // 読めないレシピ(新しい版のいろかで作られた等)は土台にも書き込み先にもしない(UI と同じ)。
                if (recipe != null && !string.IsNullOrEmpty(recipe.sessionJson) && RecipeStore.Load(recipe) == null)
                    return FinishRecolor(r, $"the existing recipe cannot be read (made by a newer Iroca?): {AssetDatabase.GetAssetPath(recipe)}");

                IrocaSessionState state;
                if (!string.IsNullOrWhiteSpace(req.preset))
                {
                    var preset = ResolvePreset(req.preset);
                    if (preset == null) return FinishRecolor(r, $"preset not found / parse failed: {req.preset}");
                    state = SessionFromPreset(preset);
                }
                else
                {
                    state = IrocaWindow.EditingStateOf(tex)
                         ?? (recipe != null ? RecipeStore.Load(recipe) : null)
                         ?? IrocaSessionState.CreateDefault();
                }
                state.zones ??= new List<ColorZone>();
                state.maskState ??= new MaskState();

                // ── ゾーンの変更 ──
                if (req.reset)
                {
                    state.zones.Clear();
                    state.maskState = new MaskState();
                }
                foreach (var name in req.remove ?? new List<string>())
                {
                    int removed = state.zones.RemoveAll(z => z != null && z.name == name);
                    if (removed == 0) r.warnings.Add($"no zone named '{name}' to remove.");
                }
                foreach (var change in req.changes ?? new List<ChangeRequest>())
                {
                    string err = ApplyChange(state, change, work, w, h);
                    if (err != null) return FinishRecolor(r, err);
                }
                DropOrphanMasks(state);
                foreach (var z in state.zones) if (z != null) { z.EnsureId(); z.UpdateCacheIfNeeded(); }
                if (SessionRecolor.CountEnabled(state.zones) == 0)
                    return FinishRecolor(r, "no enabled zones (add at least one entry to 'changes').");

                // ── 比較画像(作業解像度。登録・プレビューで見えるものと同じ) ──
                var after = (Color32[])work.Clone();
                SessionRecolor.Apply(after, w, h, state);
                var metrics = RecolorPreview.ComputeMetrics(work, after, w, h);
                r.changedFraction = metrics.changedFraction;
                r.largestComponentFraction = metrics.largestComponentFraction;
                r.changeComponentCount = metrics.componentCount;
                r.preview = WriteRecolorPanel(work, after, w, h, r.texture, r.warnings);
                foreach (var z in state.zones)
                {
                    if (z == null) continue;
                    r.zones.Add(new ZoneInfo
                    {
                        name = z.name,
                        from = "#" + ColorUtility.ToHtmlStringRGB(z.sampleColor),
                        to = "#" + ColorUtility.ToHtmlStringRGB(z.targetColor),
                        at = z.sampleUV.x >= 0f ? new[] { z.sampleUV.x, z.sampleUV.y } : new float[0],
                        tolerance = z.tolerance,
                        enabled = z.enabled,
                    });
                }

                // ── 反映 ──
                if (r.apply == ApplyPng)
                {
                    string outPath = string.IsNullOrWhiteSpace(req.output) ? ExportPipeline.BatchOutputPath(r.texture) : req.output;
                    var exported = RunRecolorCore(r.texture, state, outPath, r.warnings, imported: tex, panelNextToOutput: false);
                    if (!exported.ok) return FinishRecolor(r, exported.error);
                    r.output = exported.output;
                }
                else if (r.apply == ApplyAvatar)
                {
                    if (recipe == null) recipe = RecipeStore.Create(tex, state);
                    else RecipeStore.Save(recipe, state);
                    r.recipe = AssetDatabase.GetAssetPath(recipe);

                    // UI の登録と同じ: 書き出した画像を差しているマテリアルを元へ戻し、コンポーネントに足す(Undo 1 回)。
                    Undo.IncrementCurrentGroup();
                    int undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName(UndoName);
                    var exports = RecipeRegistration.ExportsOf(tex);
                    var exportUsers = RecipeRegistration.MaterialsUsing(target, exports);
                    int switched = RecipeRegistration.SwitchToSource(exportUsers, tex, exports);
                    if (switched > 0) r.warnings.Add($"switched {switched} material(s) from the exported image back to the source texture.");
                    RecipeRegistration.Attach(target, recipe);
                    // Attach が付ける名前(Iroca: Register Recolor)ではなく、外からの色替えだと分かる名前にする。
                    Undo.SetCurrentGroupName(UndoName);
                    Undo.CollapseUndoOperations(undoGroup);

                    IrocaWindow.ReloadFromRecipe(recipe, UndoName);
                }
                return FinishRecolor(r, null);
            }
            catch (Exception ex)
            {
                return FinishRecolor(r, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private const string UndoName = "Iroca: Recolor (automation)";

        // 1 件の変更をゾーンにする。エラーなら文言、成功なら null。
        private static string ApplyChange(IrocaSessionState state, ChangeRequest c, Color32[] work, int w, int h)
        {
            if (c == null) return "a change is empty.";
            if (string.IsNullOrWhiteSpace(c.to) || !ColorUtility.TryParseHtmlString(c.to.Trim(), out Color to))
                return $"change '{c.name}': 'to' must be a color like \"#1A2B5C\" or a color name such as \"navy\" (got '{c.to}').";

            Color from;
            Vector2 uv = new Vector2(-1f, -1f);
            if (c.at != null && c.at.Length >= 2)
            {
                if (c.at[0] < 0f || c.at[0] > 1f || c.at[1] < 0f || c.at[1] > 1f)
                    return $"change '{c.name}': 'at' must be [u, v] within 0..1 (bottom-left origin).";
                uv = new Vector2(c.at[0], c.at[1]);
                int x = Mathf.Clamp((int)(uv.x * w), 0, w - 1);
                int y = Mathf.Clamp((int)(uv.y * h), 0, h - 1);
                var p = work[y * w + x];
                if (p.a < 128) return $"change '{c.name}': 'at' points at a transparent pixel.";
                from = new Color(p.r / 255f, p.g / 255f, p.b / 255f, 1f);
            }
            else if (!string.IsNullOrWhiteSpace(c.from) && ColorUtility.TryParseHtmlString(c.from.Trim(), out from))
            {
                from.a = 1f;
            }
            else return $"change '{c.name}': give 'at' ([u, v]) or 'from' (\"#RRGGBB\").";
            to.a = 1f;

            string name = string.IsNullOrWhiteSpace(c.name) ? NextZoneName(state.zones) : c.name.Trim();
            var zone = state.zones.FirstOrDefault(z => z != null && z.name == name);
            if (zone == null)
            {
                // UI でスポイトして新しいゾーンを作ったときと同じ(IrocaWindow.CreateNewZone)。
                zone = new ColorZone { name = name, tolerance = NewZoneTolerance };
                zone.EnsureId();
                state.zones.Add(zone);
            }
            zone.enabled = true;
            zone.sampleColor = from;
            zone.sampleColorSet = true;
            zone.targetColor = to;
            zone.targetColorSet = true;
            zone.sampleUV = uv;
            return null;
        }

        // 既存と重ならない最小の番号の名前(UI の NextZoneName と同じ規則)。
        private static string NextZoneName(List<ColorZone> zones)
        {
            var used = new HashSet<string>(zones.Where(z => z != null && !string.IsNullOrEmpty(z.name)).Select(z => z.name));
            for (int n = 1; ; n++)
            {
                string candidate = string.Format(Localization.NewZoneNameFormat, n);
                if (!used.Contains(candidate)) return candidate;
            }
        }

        // 消したゾーンのマスク(除外・含める)を落とす(残っていても使われないが、レシピに死んだ項目を残さない)。
        private static void DropOrphanMasks(IrocaSessionState state)
        {
            var ids = new HashSet<string>(state.zones.Where(z => z != null && !string.IsNullOrEmpty(z.id)).Select(z => z.id));
            state.maskState.zones?.RemoveAll(e => e == null || !ids.Contains(e.zoneId));
            state.maskState.zoneIncludes?.RemoveAll(e => e == null || !ids.Contains(e.zoneId));
        }

        private static string WriteRecolorPanel(Color32[] before, Color32[] after, int w, int h, string texturePath, List<string> warnings)
        {
            try
            {
                var panel = RecolorPreview.BuildComparisonPanel(before, after, w, h, RecolorPreview.DefaultMaxTile, out int pw, out int ph);
                string stem = Path.GetFileNameWithoutExtension(texturePath);
                string guid = AssetDatabase.AssetPathToGUID(texturePath);
                string file = Path.Combine(McpDir, "preview",
                    $"{stem}_{(string.IsNullOrEmpty(guid) ? "x" : guid.Substring(0, 8))}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                AtomicFile.WriteAllBytes(file, ExportPipeline.EncodePng(panel, pw, ph));
                return file;
            }
            catch (Exception ex)
            {
                warnings.Add($"preview failed: {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }

        private static string FinishRecolor(RecolorTextureResult r, string error)
        {
            r.ok = string.IsNullOrEmpty(error);
            r.error = error ?? "";
            if (r.ok)
            {
                r.note = "Open 'preview' to check the result: left=before, middle=after, right=changed pixels (magenta). "
                    + "Magenta outside the intended part means over-selection; missing magenta inside it means it was missed. "
                    + "To retry a part, send a change with the same 'name' (it replaces that zone).";
                if (r.apply == ApplyAvatar)
                    r.note += " The recolor is registered non-destructively and shows in the Scene view (NDMF preview); "
                        + "use iroca_open to fine-tune it in the Iroca window.";
                else if (r.apply == ApplyPreview)
                    r.note += " Nothing was saved (apply='preview').";
            }
            return JsonUtility.ToJson(r);
        }
    }
}
