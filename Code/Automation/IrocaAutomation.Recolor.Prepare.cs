// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    // iroca_recolor のうち、準備(依頼を読み、テクスチャ・登録先・土台を決め、ゾーンとマスクを変える)。
    // 入口(Recolor・RecolorStatus)と依頼の寿命は IrocaAutomation.Recolor.cs。
    public static partial class IrocaAutomation {
        // 依頼を読み、テクスチャ・登録先・土台を決め、ゾーンを変える。エラーなら文言、成功なら null。
        private static string PrepareRecolor(string requestJson, RecolorJob job) {
            var r = job.r;
            RecolorRequest req;
            try { req = JsonUtility.FromJson<RecolorRequest>(requestJson ?? ""); } catch (Exception ex) { return $"request JSON parse failed: {ex.Message}"; }
            if (req == null) {
                return "request JSON is empty.";
            }
            job.req = req;
            r.apply = string.IsNullOrEmpty(req.apply) ? ApplyAvatar : req.apply.Trim().ToLowerInvariant();
            if (r.apply != ApplyAvatar && r.apply != ApplyPng && r.apply != ApplyPreview) {
                return $"unknown apply '{req.apply}' (use 'avatar', 'png' or 'preview').";
            }

            // ── テクスチャ ──
            if (string.IsNullOrWhiteSpace(req.texture)) {
                return "texture is required.";
            }
            var asset = LoadAssetByPath(req.texture);
            var tex = asset as Texture2D ?? (asset is Material m ? MeshUvLocator.MainTexture(m) as Texture2D : null);
            if (tex == null) {
                return $"not a texture or a material with a main texture: {req.texture}";
            }
            var source = MeshUvLocator.EditableSourceOf(tex);
            if (source != null && source != tex) {
                r.warnings.Add($"{AssetDatabase.GetAssetPath(tex)} is an Iroca export; recoloring its source {AssetDatabase.GetAssetPath(source)} instead.");
                tex = source;
            }
            job.tex = tex;
            r.texture = AssetDatabase.GetAssetPath(tex);
            string abs = PhysicalPath(r.texture);
            if (abs == null) {
                return $"texture file not found: {r.texture}";
            }
            // 画素は、画素が無くても断れるもの(出力先・Play 中・登録先・レシピ・プリセット)を確かめた後で読む(下の「原本の画素」)。

            // ── png の出力先(自動調整を待つ前に断れるものは断る) ──
            if (r.apply == ApplyPng) {
                string outError = ResolvePngOutput(req, r.texture, out _);
                if (outError != null) {
                    return outError;
                }
            }

            // ── 登録先と土台 ──
            // 使うレシピ(UI の登録と同じ順。登録先が無い書き出し・比較画像では、このテクスチャのレシピがちょうど 1 つならそれ)。
            // 登録では、ほかのアバターが使っているレシピを流用しない。流用して書き換えると、そのアバターの色も
            // 一緒に変わる(別のシーンにいると気付きにくく、窓を閉じていれば Ctrl+Z でも戻らなかった)。
            IrocaRecipe notShared;
            List<IrocaRecolor> notSharedUsers;
            bool recipeUnreadable;
            if (r.apply == ApplyAvatar) {
                string blocked = AvatarApplyBlocked(EditorApplication.isPlayingOrWillChangePlaymode, NdmfStateForApply);
                if (blocked != null) {
                    return blocked;
                }
                GameObject asked = null;
                if (!string.IsNullOrWhiteSpace(req.avatar)) {
                    asked = FindSceneObject(req.avatar, out string findError);
                    if (asked == null) {
                        return findError;
                    }
                }
                // 登録できるか・登録先・使うレシピは、窓の登録と同じ順・同じ判断(RegistrationService.Prepare)。再生中は上で
                // 断ってあり(NDMF の有無と一緒に文にする)、ゾーンはこの依頼が足す。
                var prep = RegistrationService.Prepare(tex, asked, null, hasEnabledZones: true, playing: false,
                    shareWithOtherAvatars: false);
                switch (prep.refusal) {
                    case RegistrationService.Refusal.NotInScene:
                    case RegistrationService.Refusal.OutsideAvatar:
                        return $"'{req.avatar}' is not inside an avatar (no VRC Avatar Descriptor on it or its parents). "
                            + "Registered there, the recolor would show in the scene preview but not in the upload. "
                            + "Pass the avatar or an object under it.";
                    case RegistrationService.Refusal.Undecided:
                        return "could not decide which object to register on (the texture is used by no "
                            + "scene object, by several avatars, or only by objects outside any avatar). Pass 'avatar' (a scene object path).";
                }
                job.target = prep.target;
                r.avatar = HierarchyPath(job.target.transform);
                job.recipe = prep.recipe;
                notShared = prep.notShared;
                notSharedUsers = prep.notSharedUsers;
                recipeUnreadable = prep.refusal == RegistrationService.Refusal.RecipeUnreadable;
            } else {
                job.recipe = RegistrationService.ChooseRecipe(null, tex, null, shareWithOtherAvatars: false,
                    out notShared, out notSharedUsers);
                recipeUnreadable = RegistrationService.IsUnreadable(job.recipe);
            }
            if (notShared != null) {
                var users = notSharedUsers.ConvertAll(c => HierarchyPath(c.transform));
                users.Sort(StringComparer.Ordinal);
                r.warnings.Add($"the recipe {AssetDatabase.GetAssetPath(notShared)} of this texture is used by "
                    + $"{string.Join(", ", users)}; made a new recipe for this avatar so that their colors do not change "
                    + "(the zones of that recipe are not carried over).");
            }
            // 読めないレシピ(新しい版のいろかで作られた等)は土台にも書き込み先にもしない(UI と同じ)。
            if (recipeUnreadable) {
                return $"the existing recipe cannot be read (made by a newer Iroca?): {AssetDatabase.GetAssetPath(job.recipe)}";
            }

            // 窓の編集内容を土台にするのは、登録では窓が書き込み先と同じレシピを表示しているとき(どちらも無いときを
            // 含む)だけ。同じテクスチャのレシピを複製して色違いにしていると、窓で別のアバターのレシピを開いたまま
            // 頼まれることがあり、書き込み先のレシピがその中身で丸ごと置き換わっていた。
            bool showing = EditingWindows.Current.IsShowing(tex, out var shown);
            bool useWindow = showing && (r.apply != ApplyAvatar || shown == job.recipe);
            var windowState = useWindow ? EditingWindows.Current.EditingStateOf(tex) : null;
            if (r.apply == ApplyAvatar) {
                // 自動調整を待つ間に窓やレシピが編集されたかを、仕上げで見分けるための依頼の時点の中身。
                job.windowJsonAtRequest = windowState != null ? JsonUtility.ToJson(windowState) : null;
                job.recipeJsonAtRequest = job.recipe != null ? job.recipe.sessionJson : null;
            }

            IrocaSessionState state;
            if (!string.IsNullOrWhiteSpace(req.preset)) {
                var preset = ResolvePreset(req.preset);
                if (preset == null) {
                    return $"preset not found / parse failed: {req.preset}";
                }
                state = SessionFromPreset(preset, SessionPersistence.GuidOf(tex), r.warnings);
            } else {
                if (showing && !useWindow) {
                    r.warnings.Add("the Iroca window is showing another recipe of this texture ("
                        + (shown != null ? AssetDatabase.GetAssetPath(shown) : "none") + "), so its edits were not used; "
                        + "started from " + (job.recipe != null ? AssetDatabase.GetAssetPath(job.recipe) : "no zones") + ".");
                }
                state = windowState
                     ?? (job.recipe != null ? RecipeStore.Load(job.recipe) : null)
                     ?? IrocaSessionState.CreateDefault();
            }
            state.zones ??= new List<ColorZone>();
            state.maskState ??= new MaskState();
            job.state = state;

            // ── 原本の画素 ──
            // ここより前の断りは画素を使わない。以前は依頼の頭でデコードしていたので、断る依頼でも、窓が同じテクスチャを
            // 開いていなければ元の画像を丸ごと読んでいた(8K で数百 ms と 256MB)。下のゾーンの変更は、at の画素を読む。
            var kind = ReadWorkingPixels(abs, tex, out job.work, out string openError);
            if (!ExportPipeline.HasPixels(kind)) {
                return UnreadablePixels(kind, openError);
            }
            job.w = job.work.Width;
            job.h = job.work.Height;
            job.workIsFullResolution = IsFullResolution(kind, tex, job.w, job.h);

            // ── ゾーンの変更 ──
            if (req.reset) {
                state.zones.Clear();
                state.maskState = new MaskState();
            }
            foreach (var name in req.remove ?? new List<string>()) {
                int removed = state.zones.RemoveAll(z => z != null && z.name == name);
                if (removed == 0) {
                    r.warnings.Add($"no zone named '{name}' to remove.");
                }
            }
            foreach (var change in req.changes ?? new List<ChangeRequest>()) {
                string err = ApplyChange(state, change, job.work, out var changed);
                if (err != null) {
                    return err;
                }
                if (!job.changedZoneIds.Contains(changed.id)) {
                    job.changedZoneIds.Add(changed.id);
                }
            }
            DropOrphanMasks(state);
            foreach (var z in state.zones) {
                z?.EnsureId();
            }
            if (RecolorInput.CountEnabled(state.zones) == 0) {
                return "no enabled zones (add at least one entry to 'changes').";
            }

            // ── マスク操作(ゾーンの変更の後に確かめる。同じ依頼で作ったゾーンも宛先にできる) ──
            foreach (var maskRequest in req.masks ?? new List<MaskRequest>()) {
                string err = ParseMaskRequest(maskRequest, state, out var edit);
                if (err != null) {
                    return err;
                }
                job.maskEditList.Add(edit);
            }
            return null;
        }

        // マスク操作の依頼を確かめて、当てる形にする。AI・メッシュが使えないことは、待つ前にここで断る。エラーなら文言、よければ null。
        private static string ParseMaskRequest(MaskRequest m, IrocaSessionState state, out AutomationMaskEdits.Edit edit) {
            edit = null;
            if (m == null) {
                return "a mask operation is empty.";
            }
            string opName = (m.op ?? "").Trim().ToLowerInvariant();
            MaskRegionOp op;
            switch (opName) {
                case "paint": op = MaskRegionOp.PaintHere; break;
                case "dont_paint": op = MaskRegionOp.DontPaintHere; break;
                case "only": op = MaskRegionOp.OnlyThisPart; break;
                case "clear": op = MaskRegionOp.ClearHere; break;
                default: return $"unknown mask op '{m.op}' (use 'paint', 'dont_paint', 'only' or 'clear').";
            }
            if (!AutomationOutputRules.TryReadUv(m.at, out Vector2 at)) {
                return $"mask op '{opName}': 'at' must be [u, v] within 0..1 (bottom-left origin).";
            }

            // 「消す」の既定は右クリックと同じ: その場所のマスクを入れた操作の範囲をそっくり消す(AI に見分け直させない)。
            string byName = !string.IsNullOrWhiteSpace(m.by) ? m.by.Trim().ToLowerInvariant()
                : op == MaskRegionOp.ClearHere ? "mask" : "ai";
            AutomationMaskEdits.By by;
            switch (byName) {
                case "ai": by = AutomationMaskEdits.By.Ai; break;
                case "island": by = AutomationMaskEdits.By.Island; break;
                case "other_meshes": by = AutomationMaskEdits.By.OtherMeshes; break;
                case "mask": by = AutomationMaskEdits.By.Mask; break;
                default: return $"unknown mask 'by' '{m.by}' (use 'ai', 'island', 'other_meshes' or 'mask').";
            }
            if (by == AutomationMaskEdits.By.OtherMeshes && op != MaskRegionOp.DontPaintHere) {
                return "by:'other_meshes' works only with op:'dont_paint'.";
            }
            if (by == AutomationMaskEdits.By.Mask && op != MaskRegionOp.ClearHere) {
                return "by:'mask' works only with op:'clear'.";
            }

            string sizeName = string.IsNullOrWhiteSpace(m.size) ? "auto" : m.size.Trim().ToLowerInvariant();
            MaskSuggestGranularity size;
            switch (sizeName) {
                case "auto": size = MaskSuggestGranularity.Auto; break;
                case "fine": size = MaskSuggestGranularity.Fine; break;
                case "coarse": size = MaskSuggestGranularity.Coarse; break;
                default: return $"unknown mask size '{m.size}' (use 'auto', 'fine' or 'coarse').";
            }
            if (by == AutomationMaskEdits.By.Ai && (MaskSuggestBridge.Service == null || MaskSuggestBridge.Service.Phase == MaskSuggestPhase.NoModel)) {
                return "by:'ai' needs Unity Sentis and the AI model, which are not installed. Use by:'island' "
                    + "(the mesh's UV island) instead, or ask the user to install them (see the manual's AI chapter).";
            }

            string zoneName = (m.zone ?? "").Trim();
            ColorZone zone = null;
            if (m.common) {
                if (zoneName.Length > 0) {
                    return "give either 'zone' or common:true, not both.";
                }
                if (op != MaskRegionOp.DontPaintHere && op != MaskRegionOp.ClearHere) {
                    return $"common:true works only with op:'dont_paint' or 'clear' (got '{opName}').";
                }
            } else if (zoneName.Length > 0) {
                zone = state.zones.FirstOrDefault(z => z != null && z.name == zoneName);
                if (zone == null) {
                    return $"no zone named '{zoneName}' (zones: {ZoneNames(state)}).";
                }
            } else {
                var named = state.zones.Where(z => z != null).ToList();
                if (named.Count != 1) {
                    return $"mask op '{opName}': pass 'zone' (zones: {ZoneNames(state)}) or common:true.";
                }
                zone = named[0];
            }
            zone?.EnsureId();
            edit = new AutomationMaskEdits.Edit {
                zoneId = zone?.id,
                zoneName = zone?.name ?? "",
                op = op,
                opName = opName,
                by = by,
                byName = byName,
                size = size,
                uv = at,
            };
            return null;
        }

        private static string ZoneNames(IrocaSessionState state)
            => string.Join(", ", state.zones.Where(z => z != null).Select(z => "'" + z.name + "'"));

        // 1 件の変更をゾーンにする。エラーなら文言、成功なら null。
        internal static unsafe string ApplyChange(IrocaSessionState state, ChangeRequest c, NativePixels work, out ColorZone zone) {
            int w = work.Width, h = work.Height;
            zone = null;
            if (c == null) {
                return "a change is empty.";
            }
            if (string.IsNullOrWhiteSpace(c.to) || !ColorUtility.TryParseHtmlString(c.to.Trim(), out Color to)) {
                return $"change '{c.name}': 'to' must be a color like \"#1A2B5C\" or a color name such as \"navy\" (got '{c.to}').";
            }

            Color from;
            Vector2 uv = new Vector2(-1f, -1f);
            if (c.at != null && c.at.Length >= 2) {
                if (!AutomationOutputRules.TryReadUv(c.at, out uv)) {
                    return $"change '{c.name}': 'at' must be [u, v] within 0..1 (bottom-left origin).";
                }
                int x = Mathf.Clamp((int)(uv.x * w), 0, w - 1);
                int y = Mathf.Clamp((int)(uv.y * h), 0, h - 1);
                var p = work.Ptr[y * w + x];
                if (p.a < PixelProcessor.OpaqueAlphaMin) {
                    return $"change '{c.name}': 'at' points at a transparent pixel.";
                }
                from = new Color(p.r / 255f, p.g / 255f, p.b / 255f, 1f);
            } else if (!string.IsNullOrWhiteSpace(c.from) && ColorUtility.TryParseHtmlString(c.from.Trim(), out from)) {
                from.a = 1f;
            } else {
                return $"change '{c.name}': give 'at' ([u, v]) or 'from' (\"#RRGGBB\").";
            }
            to.a = 1f;

            string name = string.IsNullOrWhiteSpace(c.name) ? null : c.name.Trim();
            zone = name == null ? null : state.zones.FirstOrDefault(z => z != null && z.name == name);
            if (zone == null) {
                // UI でスポイトして新しいゾーンを作ったときと同じ規則(名前の番号・許容範囲)。
                zone = EditingSession.NewZone(state.zones, name);
                state.zones.Add(zone);
            }
            zone.enabled = true;
            // 基準色が変わると、前の場所の調整で導いた値(内部サンプルと、包絡ゲート・彩度天井・穴埋めの余白)は使えない
            // (窓でサンプル色を取り直したときと同じ関数。残すと前の場所の範囲で新しい場所を切る・前の場所も塗る)。
            // 同じ場所を指したまま色だけ頼み直すなら残す(自動調整が基準色を変えていると、同じ場所でも色の比較は違う)。
            bool samePlace = uv.x >= 0f && zone.sampleUV == uv;
            if (!samePlace && zone.sampleColor != from) {
                EditingSession.ClearIslandDerived(zone);
            }
            zone.sampleColor = from;
            zone.sampleColorSet = true;
            zone.targetColor = to;
            zone.targetColorSet = true;
            zone.sampleUV = uv;
            return null;
        }

        // 消したゾーンのマスク(除外・含める)を落とす(残っていても使われないが、レシピに死んだ項目を残さない)。
        private static void DropOrphanMasks(IrocaSessionState state) {
            var ids = new HashSet<string>(state.zones.Where(z => z != null && !string.IsNullOrEmpty(z.id)).Select(z => z.id));
            // どの欄を外すかの規則は Core に 1 つだけ(塗った範囲の記録も一緒に外れる)。
            state.maskState.DropZonesExcept(ids);
        }
    }
}
