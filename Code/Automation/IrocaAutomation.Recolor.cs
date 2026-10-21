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
            // png の出力先(Assets/ の下の .png。既定: 元と同じフォルダの <名前>_recolored.png)
            public string output = "";
            // true なら、png の出力先に既にあるファイルを置き換える(既定は断る)
            public bool overwrite;
            // true(既定)なら、changes で作った・置き換えたゾーンに UI と同じ自動調整をかける
            public bool autoTune = true;
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
            // 自動調整のかけ方("evidence" / "conventional" / "skipped: 理由")。この依頼で調整しなかったゾーンは空
            public string autoTune = "";
        }

        /// <summary>自動調整を待っている間の途中経過(<see cref="Recolor"/> / <see cref="RecolorStatus"/> が返す)。</summary>
        [Serializable]
        internal class RecolorPending
        {
            public bool ok = true;
            public bool pending = true;
            public string message = "";
            public int tunedZones;
            public int zonesToTune;
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
            CancelRecolorJob();
            var job = new RecolorJob();
            try
            {
                string error = PrepareRecolor(requestJson, job);
                if (error != null) return FinishRecolor(job.r, error);

                if (job.req.autoTune && job.changedZoneIds.Count > 0)
                {
                    job.tune = new AutomationAutoTune(job.state, job.tex, job.work, job.w, job.h, job.changedZoneIds, job.r.warnings);
                    job.tune.Advance();
                    if (!job.tune.Done)
                    {
                        s_job = job;
                        EditorApplication.update -= PumpRecolorJob;
                        EditorApplication.update += PumpRecolorJob;
                        return PendingJson(job);
                    }
                }
                return CompleteRecolor(job);
            }
            catch (Exception ex)
            {
                job.tune?.Cancel();
                return FinishRecolor(job.r, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 自動調整を待っている <see cref="Recolor"/> の続き。進めて、終わっていれば最後の結果(Recolor と同じ形)、
        /// まだなら途中経過(pending)を返す。MCP の問い合わせ直し(action=status)から呼ぶ。
        /// </summary>
        public static string RecolorStatus()
        {
            var job = s_job;
            if (job == null)
                return FinishRecolor(new RecolorTextureResult(), "no recolor is in progress.");
            try
            {
                PumpRecolorJob();
                if (job.finalJson == null) return PendingJson(job);
                s_job = null;
                return job.finalJson;
            }
            catch (Exception ex)
            {
                CancelRecolorJob();
                return FinishRecolor(job.r, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // 自動調整を待っている依頼(同時に 1 つ。新しい依頼が来たら前のものは捨てる)。
        private static RecolorJob s_job;

        private sealed class RecolorJob
        {
            public RecolorRequest req;
            public readonly RecolorTextureResult r = new RecolorTextureResult();
            public Texture2D tex;
            public Color32[] work;
            public int w, h;
            // 作業解像度で読んだ画素が、書き出しで読む元の解像度の画素と同じか(書き出しで読み直さなくてよいか)。
            public bool workIsFullResolution;
            public GameObject target;
            // png の出力先(絶対パス。依頼の時点で確かめてある)
            public string outAbs;
            public IrocaRecipe recipe;
            public IrocaSessionState state;
            public readonly List<string> changedZoneIds = new List<string>();
            public AutomationAutoTune tune;
            // 終わった結果。問い合わせが来なくても update で仕上げまで進めて、ここに置いておく。
            public string finalJson;
        }

        // update と問い合わせから呼ぶ。自動調整を進め、終わったら仕上げ(比較画像・登録・書き出し)まで行う。
        private static void PumpRecolorJob()
        {
            var job = s_job;
            if (job == null) { EditorApplication.update -= PumpRecolorJob; return; }
            if (job.finalJson != null) return;
            try
            {
                job.tune.Advance();
                if (!job.tune.Done) return;
                job.finalJson = CompleteRecolor(job);
            }
            catch (Exception ex)
            {
                job.tune?.Cancel();
                job.finalJson = FinishRecolor(job.r, $"{ex.GetType().Name}: {ex.Message}");
            }
            EditorApplication.update -= PumpRecolorJob;
        }

        private static void CancelRecolorJob()
        {
            s_job?.tune?.Cancel();
            s_job = null;
            EditorApplication.update -= PumpRecolorJob;
        }

        private static string PendingJson(RecolorJob job)
        {
            var p = new RecolorPending
            {
                tunedZones = job.tune.Finished,
                zonesToTune = job.tune.Total,
                message = "Auto-tuning the zones (the first time on a texture, the AI prepares the image, which can take a while). "
                    + "Call again to get the result.",
            };
            return JsonUtility.ToJson(p);
        }

        // 依頼を読み、テクスチャ・登録先・土台を決め、ゾーンを変える。エラーなら文言、成功なら null。
        private static string PrepareRecolor(string requestJson, RecolorJob job)
        {
            var r = job.r;
            RecolorRequest req;
            try { req = JsonUtility.FromJson<RecolorRequest>(requestJson ?? ""); }
            catch (Exception ex) { return $"request JSON parse failed: {ex.Message}"; }
            if (req == null) return "request JSON is empty.";
            job.req = req;
            r.apply = string.IsNullOrEmpty(req.apply) ? ApplyAvatar : req.apply.Trim().ToLowerInvariant();
            if (r.apply != ApplyAvatar && r.apply != ApplyPng && r.apply != ApplyPreview)
                return $"unknown apply '{req.apply}' (use 'avatar', 'png' or 'preview').";

            // ── テクスチャ ──
            if (string.IsNullOrWhiteSpace(req.texture)) return "texture is required.";
            var asset = LoadAssetByPath(req.texture);
            var tex = asset as Texture2D ?? (asset is Material m ? MeshUvLocator.MainTexture(m) as Texture2D : null);
            if (tex == null) return $"not a texture or a material with a main texture: {req.texture}";
            var source = MeshUvLocator.EditableSourceOf(tex);
            if (source != null && source != tex)
            {
                r.warnings.Add($"{AssetDatabase.GetAssetPath(tex)} is an Iroca export; recoloring its source {AssetDatabase.GetAssetPath(source)} instead.");
                tex = source;
            }
            job.tex = tex;
            r.texture = AssetDatabase.GetAssetPath(tex);
            string abs = PhysicalPath(r.texture);
            if (abs == null) return $"texture file not found: {r.texture}";
            var kind = ExportPipeline.ReadSourcePixels(abs, tex, out job.work, out job.w, out job.h, workingSize: true);
            if (kind == ExportPipeline.SourceKind.Unavailable)
                return "could not read the pixels (enable Read/Write in the import settings, or use a PNG/JPG source).";
            job.workIsFullResolution = IsFullResolution(kind, tex, job.w, job.h);

            // ── png の出力先(自動調整を待つ前に断れるものは断る) ──
            if (r.apply == ApplyPng)
            {
                string outError = ResolvePngOutput(req, r.texture, out job.outAbs);
                if (outError != null) return outError;
            }

            // ── 登録先と土台 ──
            if (r.apply == ApplyAvatar)
            {
                string blocked = AvatarApplyBlocked(EditorApplication.isPlayingOrWillChangePlaymode, NdmfSupport.State);
                if (blocked != null) return blocked;
                if (!string.IsNullOrWhiteSpace(req.avatar))
                {
                    job.target = FindSceneObject(req.avatar, out string findError);
                    if (job.target == null) return findError;
                }
                else
                {
                    job.target = RecipeRegistration.SuggestTarget(tex, null);
                    if (job.target == null)
                        return "could not decide which object to register on (the texture is used by no "
                            + "scene object, or by several avatars). Pass 'avatar' (a scene object path).";
                }
                r.avatar = HierarchyPath(job.target.transform);
                job.recipe = RecipeRegistration.ExistingFor(job.target, tex);
            }
            if (job.recipe == null)
            {
                var found = RecipeStore.FindForTexture(tex);
                if (found.Count == 1) job.recipe = found[0];
            }
            // 読めないレシピ(新しい版のいろかで作られた等)は土台にも書き込み先にもしない(UI と同じ)。
            if (job.recipe != null && !string.IsNullOrEmpty(job.recipe.sessionJson) && RecipeStore.Load(job.recipe) == null)
                return $"the existing recipe cannot be read (made by a newer Iroca?): {AssetDatabase.GetAssetPath(job.recipe)}";

            IrocaSessionState state;
            if (!string.IsNullOrWhiteSpace(req.preset))
            {
                var preset = ResolvePreset(req.preset);
                if (preset == null) return $"preset not found / parse failed: {req.preset}";
                state = SessionFromPreset(preset);
            }
            else
            {
                state = IrocaWindow.EditingStateOf(tex)
                     ?? (job.recipe != null ? RecipeStore.Load(job.recipe) : null)
                     ?? IrocaSessionState.CreateDefault();
            }
            state.zones ??= new List<ColorZone>();
            state.maskState ??= new MaskState();
            job.state = state;

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
                string err = ApplyChange(state, change, job.work, job.w, job.h, out var changed);
                if (err != null) return err;
                if (!job.changedZoneIds.Contains(changed.id)) job.changedZoneIds.Add(changed.id);
            }
            DropOrphanMasks(state);
            foreach (var z in state.zones) if (z != null) { z.EnsureId(); z.UpdateCacheIfNeeded(); }
            if (SessionRecolor.CountEnabled(state.zones) == 0)
                return "no enabled zones (add at least one entry to 'changes').";
            return null;
        }

        // 比較画像を作り、登録・書き出しまで行って、最後の結果を返す。
        private static string CompleteRecolor(RecolorJob job)
        {
            var r = job.r;
            var state = job.state;
            var tex = job.tex;
            foreach (var z in state.zones) if (z != null) z.UpdateCacheIfNeeded();

            // ── 比較画像(作業解像度。登録・プレビューで見えるものと同じ) ──
            var after = (Color32[])job.work.Clone();
            SessionRecolor.Apply(after, job.w, job.h, state);
            var metrics = RecolorPreview.ComputeMetrics(job.work, after, job.w, job.h);
            r.changedFraction = metrics.changedFraction;
            r.largestComponentFraction = metrics.largestComponentFraction;
            r.changeComponentCount = metrics.componentCount;
            r.preview = WriteRecolorPanel(job.work, after, job.w, job.h, r.texture, r.warnings);
            foreach (var z in state.zones)
            {
                if (z == null) continue;
                string how = "";
                if (job.tune != null && job.tune.How.TryGetValue(z.name, out var h)) how = h;
                else if (job.changedZoneIds.Contains(z.id) && !job.req.autoTune) how = "skipped: autoTune=false";
                r.zones.Add(new ZoneInfo
                {
                    name = z.name,
                    from = "#" + ColorUtility.ToHtmlStringRGB(z.sampleColor),
                    to = "#" + ColorUtility.ToHtmlStringRGB(z.targetColor),
                    at = z.sampleUV.x >= 0f ? new[] { z.sampleUV.x, z.sampleUV.y } : new float[0],
                    tolerance = z.tolerance,
                    enabled = z.enabled,
                    autoTune = how,
                });
            }

            // ── 反映 ──
            if (r.apply == ApplyPng)
            {
                // 自動調整を待つ間に、出力先へ別の書き出しがされていることがある。書く直前にもう一度確かめる。
                string outError = ResolvePngOutput(job.req, r.texture, out string outPath);
                if (outError != null) return FinishRecolor(r, outError);
                if (job.workIsFullResolution)
                {
                    // 作業解像度の画素が元の解像度の画素そのものなら、比較画像のために色替えした after が書き出す画素
                    // (同じ画素に同じ編集状態で SessionRecolor.Apply)。読み込みと色替えをもう一度しない。
                    string error = WriteRecoloredPng(after, job.w, job.h, PhysicalPath(r.texture), outPath, out string outAbs);
                    if (error != null) return FinishRecolor(r, error);
                    r.output = outAbs;
                }
                else
                {
                    // 取り込みで縮めて編集していたときは、UI の書き出しと同じく元の解像度で読み直して色替えする。
                    // 変化の指標は作業解像度で数えてあるので、ここでは数えない。
                    var exported = RunRecolorCore(r.texture, state, outPath, r.warnings, imported: tex,
                        panelNextToOutput: false, metrics: false);
                    if (!exported.ok) return FinishRecolor(r, exported.error);
                    r.output = exported.output;
                }
            }
            else if (r.apply == ApplyAvatar)
            {
                // 自動調整を待つ間に Play に入っていたら登録しない(依頼の時点と同じ判定)。
                string blocked = AvatarApplyBlocked(EditorApplication.isPlayingOrWillChangePlaymode, NdmfSupport.State);
                if (blocked != null) return FinishRecolor(r, blocked);
                var recipe = job.recipe;
                if (recipe == null) recipe = RecipeStore.Create(tex, state);
                else RecipeStore.Save(recipe, state);
                r.recipe = AssetDatabase.GetAssetPath(recipe);

                // UI の登録と同じ: 書き出した画像を差しているマテリアルを元へ戻し、コンポーネントに足す(Undo 1 回)。
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(UndoName);
                var exports = RecipeRegistration.ExportsOf(tex);
                var exportUsers = RecipeRegistration.MaterialsUsing(job.target, exports);
                int switched = RecipeRegistration.SwitchToSource(exportUsers, tex, exports);
                if (switched > 0) r.warnings.Add($"switched {switched} material(s) from the exported image back to the source texture.");
                RecipeRegistration.Attach(job.target, recipe);
                // Attach が付ける名前(Iroca: Register Recolor)ではなく、外からの色替えだと分かる名前にする。
                Undo.SetCurrentGroupName(UndoName);
                Undo.CollapseUndoOperations(undoGroup);

                IrocaWindow.ReloadFromRecipe(recipe, UndoName);
            }
            return FinishRecolor(r, null);
        }

        private const string UndoName = "Iroca: Recolor (automation)";

        /// <summary>
        /// apply='avatar'(アバターへの登録)ができない理由。できるなら null。
        /// Play 中に付けたコンポーネントは Play を止めると消え、書き出し画像から元へ戻したマテリアル(アセット)だけが
        /// 残る。NDMF が使えないと、登録しても色替えされず、アップロードはいろかの安全装置が止める(窓の登録は
        /// 警告を出すが、AI への結果には出せない)。どちらも登録しないで断り、できることを案内する。
        /// </summary>
        internal static string AvatarApplyBlocked(bool playing, NdmfSupportState ndmf)
        {
            if (playing)
                return "apply='avatar' does not work in Play mode (a registration made now is lost when Play stops). "
                    + "Ask the user to stop Play mode, then call again. apply='preview' and 'png' work in Play mode.";
            if (ndmf == NdmfSupportState.Supported) return null;
            string reason = ndmf == NdmfSupportState.TooOld ? "the installed NDMF is too old for this Iroca"
                : ndmf == NdmfSupportState.TooNew ? "the installed NDMF is newer than this Iroca supports"
                : "NDMF is not installed";
            return $"apply='avatar' needs NDMF to recolor the avatar non-destructively, but {reason} "
                + "(the recolor would not show, and the upload would be stopped). Use apply='png' to write a recolored "
                + "image instead, or ask the user to install a supported NDMF (the Iroca installer adds it).";
        }

        /// <summary>
        /// 作業解像度で読んだ画素(<see cref="ExportPipeline.ReadSourcePixels"/> の workingSize: true)が、書き出しで読む
        /// 元の解像度の画素(workingSize: false)と同じになるか。取り込み済みテクスチャから読んだとき(PSD・TGA など)は
        /// どちらも同じ画素を読む。ファイルから読んだときは、寸法が元のファイルと同じなら縮めていない
        /// (<see cref="WorkingResolution.PixelsOf"/> は寸法が同じならデコードした画素をそのまま返す)。
        /// </summary>
        internal static bool IsFullResolution(ExportPipeline.SourceKind kind, Texture2D tex, int w, int h)
        {
            if (kind == ExportPipeline.SourceKind.ImportedTexture) return true;
            if (kind != ExportPipeline.SourceKind.File) return false;
            if (!(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) is TextureImporter ti)) return false;
            ti.GetSourceTextureWidthAndHeight(out int fw, out int fh);
            return fw == w && fh == h;
        }

        /// <summary>
        /// apply='png' の出力先を決めて確かめる。エラーなら文言、よければ null と出力先の絶対パス。
        /// 書くのは Assets/ の下の .png だけ(Packages/・Library/・ProjectSettings/ などへは書かない)。
        /// 既にファイルがあれば、依頼に overwrite が無い限り断る。既定の出力先は「適用して保存」の既定と同じ名前なので、
        /// 前に書き出してマテリアルに差している画像を黙って置き換えると、それを使うアバターの見た目まで変わる
        /// (UI の書き出しは同じ場面で確認を出す)。元テクスチャ自身は overwrite があっても断る。
        /// </summary>
        private static string ResolvePngOutput(RecolorRequest req, string texturePath, out string outAbs)
        {
            bool given = !string.IsNullOrWhiteSpace(req.output);
            string path = given ? req.output.Trim() : ExportPipeline.BatchOutputPath(texturePath);
            outAbs = ResolveOutputPath(path);
            string rel = outAbs != null ? PathUtils.ToAssetsRelativeOrNull(outAbs) : null;
            if (rel == null)
            {
                outAbs = null;
                return given
                    ? $"'output' must be a .png path under Assets/: {req.output}"
                    : $"the default output ({path}) is outside Assets/; pass 'output' (a .png path under Assets/).";
            }
            string srcAbs = PhysicalPath(texturePath);
            if (srcAbs != null && string.Equals(outAbs, srcAbs, StringComparison.OrdinalIgnoreCase))
                return "output must differ from the source texture "
                     + $"(recoloring in place would destroy the original irreversibly): {rel}";
            if (!req.overwrite && File.Exists(outAbs))
                return $"{rel} already exists (materials or avatars may use it). Ask the user before replacing it, "
                     + "then call again with overwrite:true; or pass another 'output'.";
            return null;
        }

        // 1 件の変更をゾーンにする。エラーなら文言、成功なら null。
        private static string ApplyChange(IrocaSessionState state, ChangeRequest c, Color32[] work, int w, int h, out ColorZone zone)
        {
            zone = null;
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
            zone = state.zones.FirstOrDefault(z => z != null && z.name == name);
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
