// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    // PreviewView: プレビューの右クリックメニュー(AI 提案・メッシュのパーツ・直すゾーンの切り替え)。
    internal partial class PreviewView {
        // プレビューの右クリックメニュー。AI 提案とメッシュのパーツ操作をここへ集める
        // (2026-09-30。以前は AI 提案がマスク編集パレットの「モード」で、よく使う操作が奥に隠れていた)。
        //
        // 開くのは ContextClick だけ。Windows は右ボタンの MouseUp の後に、mac は Control+クリックで
        // ContextClick が届く。MouseDown では開かない(両方で開くと 1 回の右クリックで 2 回出る)。
        // 右ボタンは他のどの操作も使っていないので、左ドラッグのパン・ブラシとは干渉しない。
        private void HandlePreviewContextMenu(Rect previewRect) {
            var e = Event.current;
            if (e.type != EventType.ContextClick || !previewRect.Contains(e.mousePosition)) {
                return;
            }
            // 拡大中は画像が枠より大きい。枠の外でも座標はスクロールの中へ写されるので、見えていない場所のメニューを開かないようにする。
            if (_previewFrameScreenRect.width > 0f
                && !_previewFrameScreenRect.Contains(GUIUtility.GUIToScreenPoint(e.mousePosition))) {
                return;
            }
            // 書き出し・自動調整は始めたときの編集内容で動いていて、途中のマスクの変更は結果に入らない。
            if (_host.IsJobBlockingUI) {
                return;
            }
            ShowPreviewContextMenu(e.mousePosition, previewRect);
            e.Use();
        }

        // プレビューの枠の見えている範囲(画面座標。前の Repaint のもの)。
        [System.NonSerialized] private Rect _previewFrameScreenRect;

        private const string ContextMenuUsedPrefKey = IrocaPrefs.ContextMenuUsed;

        /// <summary>
        /// 一度でもプレビューの右クリックメニューを開いたか(操作モード行に使い方の案内を出すかどうか。DrawIdleRow)。
        /// 開けば項目は見えるので、選ばずに閉じても使い方は伝わったとみなす。
        /// </summary>
        internal static bool ContextMenuUsed {
            get => s_contextMenuUsed ??= EditorPrefs.GetBool(ContextMenuUsedPrefKey, false);
            set {
                if (s_contextMenuUsed == value) {
                    return;
                }
                s_contextMenuUsed = value;
                EditorPrefs.SetBool(ContextMenuUsedPrefKey, value);
            }
        }
        // 毎回の描画で EditorPrefs を読まないための控え。
        private static bool? s_contextMenuUsed;

        // 右クリックで選べる操作(宛先のゾーンに当てる。宛先の決め方は ShowPreviewContextMenu)。全ゾーン共通にできるのは
        // 「塗らない」と、それを消すことだけ。clearLabel が null なら「消す」は出さない(消す物が無い)。
        private static List<(MaskRegionOp op, string label)> ContextOps(bool zoneTarget, string clearLabel) {
            var ops = zoneTarget
                ? new List<(MaskRegionOp, string)> {
                    (MaskRegionOp.PaintHere, Localization.CtxPaintHere),
                    (MaskRegionOp.DontPaintHere, Localization.CtxDontPaintHere),
                    (MaskRegionOp.OnlyThisPart, Localization.CtxOnlyThisPart),
                }
                : new List<(MaskRegionOp, string)> { (MaskRegionOp.DontPaintHere, Localization.CtxDontPaintHereCommon) };
            if (clearLabel != null) {
                ops.Add((MaskRegionOp.ClearHere, clearLabel));
            }
            return ops;
        }

        // 「ここのマスクを消す」の宛先と項目の文字。宛先はマスクがあるゾーン(MaskPaintView.ClearTargetZoneIdAt)で、
        // 見出しのゾーンと違うときは項目にゾーンの名前を出す。マスクが 1 枚も無ければ項目を出さない
        // (選んでも「消すマスクはありません」となるだけ)。radius = クリックの狙いの外れを許す幅(マスクの画素)。
        private (string zoneId, string label) ClearItem(MaskPaintView maskView, Vector2 uv, string activeZoneId, string zoneId,
                                                        int radius) {
            var workspace = maskView.Workspace;
            if (zoneId == null) {
                return (null, workspace.Common != null ? Localization.CtxClearHereCommon : null);
            }
            if (!workspace.HasLayers) {
                return (zoneId, null);
            }
            string target = maskView.ClearTargetZoneIdAt(uv, activeZoneId, zoneId, radius);
            if (target == zoneId) {
                return (zoneId, Localization.CtxClearHere);
            }
            var zone = _host.Session?.zones?.Find(z => z != null && z.id == target);
            string name = string.IsNullOrEmpty(zone?.name) ? Localization.UnnamedZone : zone.name;
            return (target, string.Format(Localization.CtxClearHereZoneFormat, MenuSafe(name)));
        }

        // AI の部分の大きさを、その 1 回だけ変えるサブメニュー。直下の項目はマスク編集の「提案の粒度」(既定: 自動)。
        private static (MaskSuggestGranularity gran, string root)[] GranularitySubmenus() => new[] {
            (MaskSuggestGranularity.Fine, Localization.CtxAiFineRoot),
            (MaskSuggestGranularity.Coarse, Localization.CtxAiCoarseRoot),
        };

        // GenericMenu は "/" をサブメニューの区切りとして読むので、名前の "/" は全角に置き換える。
        private static string MenuSafe(string s) => string.IsNullOrEmpty(s) ? s : s.Replace('/', '／');

        // メニューの言葉は「ここも塗る / ここは塗らない / この部分だけ塗る」に揃える(2026-10-06)。
        // 以前は「追加先」「除外」「含める」「AI 提案:」「パーツ:」で、マスクの仕組みを知らないと選べなかった。
        // 宛先は、クリックした場所をいま塗っているゾーン(PaintingZoneIdAt)。塗られていない所では「いま直している
        // ゾーン」(= マスクの編集対象。最後に触ったゾーンへ追従する)。以前は後者だけで決めていたので、別のゾーンが
        // 塗っている所に「ここは塗らない」を当てても見た目が変わらなかった(MaskRegionEdit.PaintingZoneAt)。
        // 塗っているゾーンに当てても「いま直しているゾーン」は変えない。外した後のその場所は塗られていない所に
        // なるので、続けて「ここも塗る」を選べば、いま直しているゾーンに入る(別のゾーンへ付け替える 2 手)。
        private void ShowPreviewContextMenu(Vector2 screenPos, Rect previewRect) {
            var maskView = _host._maskView;
            if (maskView == null || _host.SourceTexture == null) {
                return;
            }
            var uv = PreviewCoords.ScreenToUv(screenPos, previewRect);
            var zones = _host.Session?.zones;
            bool hasZone = zones != null && zones.Count > 0;
            string activeZoneId = maskView.ActiveTargetZoneId();   // null = 全ゾーン共通(ゾーンが無いときも)
            string zoneId = activeZoneId;
            // 「全部のゾーンに共通で直す」を選んでいるときは、選んだとおり共通に当てる。
            var painting = zoneId != null ? PaintingZoneAt(uv) : null;
            bool byPainting = painting != null && painting.id != zoneId;
            if (byPainting) {
                zoneId = painting.id;
            }
            // 縮小表示では画面の 1 画素がマスクの何画素にもなるので、マスクを探す範囲は表示の倍率から決める。
            int clearRadius = MaskRegionEdit.ClearProbeRadiusFor(
                previewRect.width > 0f ? maskView.maskWidth / previewRect.width : 0f);
            var (clearZoneId, clearLabel) = ClearItem(maskView, uv, activeZoneId, zoneId, clearRadius);
            var ops = ContextOps(zoneId != null, clearLabel);
            string targetZoneId = zoneId;
            MaskPaintView.MaskDestination Dest(MaskRegionOp op) =>
                new MaskPaintView.MaskDestination(op == MaskRegionOp.ClearHere ? clearZoneId : targetZoneId, op);
            var menu = new GenericMenu();

            // 見出し: どのゾーンを直すか。いま直しているゾーンと違うときは、なぜそのゾーンかを添える。
            menu.AddDisabledItem(new GUIContent(
                byPainting ? string.Format(Localization.CtxHeaderPaintingZoneFormat,
                                           MenuSafe(string.IsNullOrEmpty(painting.name) ? Localization.UnnamedZone : painting.name))
                : zoneId != null ? string.Format(Localization.CtxHeaderZoneFormat, MenuSafe(maskView.ActiveTargetName()))
                : hasZone ? Localization.CtxHeaderCommon
                : Localization.CtxHeaderNoZone));
            menu.AddSeparator("");

            // AI が判定した部分に当てる。推論は非同期で、結果は選んだ時点の宛先へ入る。
            var ai = maskView.SuggestController;
            bool aiReady = ai != null && MaskSuggestSection.ToolReady;
            if (ai == null) {
                menu.AddDisabledItem(new GUIContent(Localization.CtxAiNoSentis));
            } else if (!aiReady) {
                menu.AddDisabledItem(new GUIContent(Localization.CtxAiNoModel));
            } else {
                // メニューを読んでいる間に画像の解析を先行させる(準備済みなら no-op)。
                if (EnsureTrueSource(_host.SourceTexture) && TrueSourceW > 0) {
                    ai.PrepareSource(TrueSource, TrueSourceCacheKey());
                }
                foreach (var (op, label) in ops) {
                    if (op == MaskRegionOp.ClearHere) {
                        continue;   // 消すのは下の項目(AI を使わない)
                    }
                    var dest = Dest(op);
                    menu.AddItem(new GUIContent(label), false, () => RequestAiSuggest(ai, uv, dest, null));
                }
            }
            // ここのマスクを消す: その場所を塗った操作(右クリックの 1 回)が塗った範囲をそっくり消す。AI には見分けさせない
            // (2026-11-12。AI に見分け直させると、塗ったときの範囲と合わずに縁や離れた所に細かい消し残りが出た。何を塗ったかは
            // マスクと一緒に覚えている。MaskPatch)。ブラシで塗った所・記録が無い所は、つながっているひとかたまり。AI が無くても使える。
            // 一部だけを消すのは、下のサブメニュー(AI の部分・メッシュの島)。
            if (clearLabel != null) {
                menu.AddItem(new GUIContent(clearLabel), false, () => ClearMaskHere(maskView, uv, clearZoneId, clearRadius));
            }
            if (aiReady) {
                // この 1 回だけ大きさを変える。黒い服の上のファスナーのような描き込みは「自動」だと
                // 服全体が取れるので、マスク編集の粒度を切り替えに行かずにここから選べるようにする(2026-10-15)。
                foreach (var (gran, root) in GranularitySubmenus()) {
                    foreach (var (op, label) in ops) {
                        var dest = Dest(op);
                        menu.AddItem(new GUIContent(root + "/" + label), false, () => RequestAiSuggest(ai, uv, dest, gran));
                    }
                }
            }
            menu.AddSeparator("");

            // メッシュの形(UV の島)で当てる。初回はここでメッシュを探す(テクスチャ単位でキャッシュ)。
            // 使う人の多くはメッシュの作りを知らないので、AI の項目より下のサブメニューに置く。
            string meshRoot = Localization.CtxMeshRoot + "/";
            var parts = maskView.MeshParts;
            var found = parts?.Found;
            if (parts != null && parts.HasMesh) {
                string mesh = parts.MeshNameAt(uv.x, uv.y);
                if (mesh == null) {
                    menu.AddDisabledItem(new GUIContent(Localization.CtxPartNoIsland));
                } else {
                    string m = MenuSafe(mesh);
                    foreach (var (op, label) in ops) {
                        var dest = Dest(op);
                        menu.AddItem(new GUIContent(meshRoot + string.Format(Localization.CtxMeshIslandFormat, label, m)),
                                     false, () => AddMeshPart(parts, uv, MeshPartController.Region.Island, dest));
                    }
                    if (found.found.Count > 1) {
                        var dest = new MaskPaintView.MaskDestination(zoneId, MaskRegionOp.DontPaintHere);
                        menu.AddItem(new GUIContent(meshRoot + string.Format(Localization.CtxMeshOthersFormat, m)),
                                     false, () => AddMeshPart(parts, uv, MeshPartController.Region.OtherMeshes, dest));
                    }
                    menu.AddSeparator(meshRoot);
                }
            } else if (found != null && found.unreadable > 0) {
                menu.AddDisabledItem(new GUIContent(MenuSafe(string.Format(Localization.CtxPartUnreadableFormat, found.unreadable))));
            } else {
                menu.AddDisabledItem(new GUIContent(Localization.CtxPartNoMesh));
            }

            // メッシュの指定・探し直し(自動で見つからないとき・違うメッシュを拾ったとき)。
            var sel = Selection.activeObject;
            if (sel is GameObject) {
                menu.AddItem(new GUIContent(meshRoot + string.Format(Localization.CtxPartUseSelectionFormat, MenuSafe(sel.name))),
                             false, () => UseSelectedMesh(parts, sel));
            } else {
                menu.AddDisabledItem(new GUIContent(meshRoot + Localization.CtxPartUseSelectionNone));
            }
            menu.AddItem(new GUIContent(meshRoot + Localization.CtxPartResearch), false, () => {
                parts?.Research();
                _host.ShowNotification(new GUIContent(parts != null && parts.HasMesh
                    ? string.Format(Localization.NotifyMeshFoundFormat, parts.Found.found.Count)
                    : Localization.NotifyMeshNotFound));
            });

            if (hasZone) {
                menu.AddSeparator("");
                AddTargetSwitchItems(menu, maskView);
            }

            menu.ShowAsContext();
            ContextMenuUsed = true;
        }

        /// <summary>
        /// クリックした場所をいま塗っているゾーン(無ければ null)。表示中のプレビューを作った選択(PreviewPipeline.Selection)を
        /// 上のゾーンから順に見る(規則は MaskRegionEdit.PaintingZoneAt)。ソロ表示中は、見えているそのゾーンだけを見る。
        /// 選択がまだ無いゾーン(作り直しの途中など)は塗っていないものとして扱う。
        /// </summary>
        private ColorZone PaintingZoneAt(Vector2 uv) {
            var zones = _host.Session?.zones;
            var selection = _pipeline?.Selection;
            if (zones == null || zones.Count == 0 || selection == null) {
                return null;
            }
            if (!EnsureTrueSource(_host.SourceTexture)) {
                return null;
            }
            int w = TrueSourceW, h = TrueSourceH;
            if (w <= 0 || h <= 0) {
                return null;
            }

            // クリックした所の周り(MaskRegionEdit.PaintedProbeRadius。行 0 = 画像の下端で、UV と同じ向き)
            var indices = MaskRegionEdit.PaintedProbeIndices(uv.x, uv.y, w, h);
            int n = indices.Length;

            var solo = _host.SoloZone;
            var strengths = new float[zones.Count][];
            for (int k = 0; k < zones.Count; k++) {
                var z = zones[k];
                if (z == null || !z.enabled || (solo != null && z != solo)) {
                    continue;
                }
                var s = new float[n];
                if (selection.TryReadLatest(z.id, w, h, indices, s)) {
                    strengths[k] = s;
                }
            }
            int found = MaskRegionEdit.PaintingZoneAt(strengths, n);
            return found >= 0 ? zones[found] : null;
        }

        /// <summary>
        /// 「ここも塗る」(粒度 自動)の提案の範囲 proposal(元の画像の寸法・下原点)を、押した所 uv の色に近い画素へ絞るかを決めて絞る
        /// (<see cref="PaintHereNarrowing.Narrow"/>。自動化・ハーネスと同じ規則)。宛先のゾーンの選択の強さは、表示中のプレビューを
        /// 作った選択の控え(<see cref="PaintingZoneAt"/> と同じ読み方)から区切って読み、今のマスク masks の含める・除外を重ねる
        /// (<see cref="PaintHereNarrowing.WithMaskContract"/>。自動化・ハーネスの SimulateSelection と同じ「塗れている」になる)。
        /// 読めないとき(作り直しの途中・ゾーンが無い・寸法が違う)は絞らない。絞ったら新しい範囲を返す(owned = true。呼び出し側が
        /// 放す)。MaskSuggestController が当てる直前に呼ぶ。masks は呼び出し側が持ち、この呼び出しの間だけ読む。
        /// </summary>
        internal NativeBools NarrowPaintHere(NativeBools proposal, Vector2 uv, MaskPaintView.MaskDestination dest,
                                             MaskSuggestGranularity granularity, MaskSnapshot masks, out bool owned,
                                             out PaintHereNarrowing.Outcome outcome) {
            owned = false;
            outcome = default;
            bool sizeAuto = granularity == MaskSuggestGranularity.Auto;
            if (proposal == null || !PaintHereNarrowing.Applies(dest.op, sizeAuto)) {
                return proposal;
            }
            var selection = _pipeline?.Selection;
            string zoneId = dest.zoneId;
            if (selection == null || string.IsNullOrEmpty(zoneId) || !EnsureTrueSource(_host.SourceTexture)
                || TrueSource == null) {
                return proposal;
            }
            int w = proposal.Width, h = proposal.Height;
            PaintHereNarrowing.StrengthReader cached = (indices, strength, count)
                => selection.TryReadLatest(zoneId, w, h, indices, strength);
            var read = PaintHereNarrowing.WithMaskContract(cached, masks, zoneId, w, h);
            return PaintHereNarrowing.Narrow(proposal, TrueSource, uv.x, uv.y, dest.op, sizeAuto, read, out owned, out outcome);
        }

        // 直すゾーンの切り替え(各ゾーン / 全ゾーン共通)。パレットの対象プルダウンと同じ activeMaskTarget を書き換える。
        private void AddTargetSwitchItems(GenericMenu menu, MaskPaintView maskView) {
            var zones = _host.Session?.zones;
            if (zones == null) {
                return;
            }
            string root = Localization.CtxChangeZone + "/";
            for (int i = 0; i < zones.Count; i++) {
                int index = i;
                string name = string.IsNullOrEmpty(zones[i]?.name) ? Localization.UnnamedZone : zones[i].name;
                // 同名ゾーンがあってもメニュー項目が潰れないよう番号を付ける
                menu.AddItem(new GUIContent(root + (i + 1) + ". " + MenuSafe(name)), maskView.activeMaskTarget == i, () => {
                    maskView.activeMaskTarget = index;
                    maskView.maskDirty = true;
                    _host.RequestRepaint();
                });
            }
            menu.AddSeparator(root);
            menu.AddItem(new GUIContent(root + Localization.CtxCommonTargetItem), maskView.activeMaskTarget < 0, () => {
                maskView.activeMaskTarget = -1;
                maskView.maskDirty = true;
                _host.RequestRepaint();
            });
        }

        /// <summary>
        /// スポイトのボタンを押したとき(<see cref="IrocaWindow.WarmAiForPick"/>)、AI のモデルの読み込みと元の画像の埋め込みの計算を
        /// 先に始める。クリックの後の自動調整は同じ元(同じ鍵)の埋め込みを使い回す。モデルの読み込みは同期で重い(初回は進捗バーを
        /// 出して数秒止まる)ので、押したボタンの描き直しを先に済ませ、次のエディタの更新で始める(delayCall はテストランナーの
        /// 数フレームでは回らなかったので、毎回回る update を 1 回だけ使う。RequestNewZoneFromSample と同じ)。そのときもう武装を
        /// 解いていた・テクスチャが替わった・窓を閉じた・AI が使えない(モデルが無い・エラー中)なら何もしない。
        /// </summary>
        internal void WarmAiSource(Texture2D texture) {
            EditorApplication.CallbackFunction once = null;
            once = () => {
                EditorApplication.update -= once;
                var host = _host;
                if (host == null || texture == null || host.SourceTexture != texture
                    || string.IsNullOrEmpty(host.EyedropperZoneId)) {
                    return;
                }
                var svc = MaskSuggestBridge.Service;
                if (svc == null || svc.Phase == MaskSuggestPhase.NoModel || svc.Phase == MaskSuggestPhase.Error) {
                    return;
                }
                if (!EnsureTrueSource(texture) || TrueSource == null || TrueSourceW <= 0) {
                    return;
                }
                if (svc.Phase == MaskSuggestPhase.NotLoaded && !svc.TryEnsureModels()) {
                    return;
                }
                // 待機中だけ元を渡す(ほかの要求の処理中・計算中なら何もしない。規則は AiSuggestBroker.Prepare)。
                AiSuggestBroker.Prepare(TrueSourceCacheKey(), TrueSource);
            };
            EditorApplication.update += once;
        }

        // AI 提案の要求。エクスポートと同一の実フル解像度ソースで推論する(プレビュー縮小の影響を受けない)。
        // granularity = null ならマスク編集の「提案の粒度」に従う。
        private void RequestAiSuggest(MaskSuggestController ctl, Vector2 uv, MaskPaintView.MaskDestination dest,
                                      MaskSuggestGranularity? granularity) {
            if (TrueSource == null) {
                EnsureTrueSource(_host.SourceTexture);
            }
            if (TrueSource == null || TrueSourceW <= 0) {
                return;
            }
            if (!ctl.RequestProposal(uv.x, uv.y, dest, TrueSource, TrueSourceCacheKey(), granularity)) {
                _host.ShowNotification(new GUIContent(Localization.NotifyAiNotStarted));
            }
            _host.RequestRepaint();
        }

        // クリックした所のマスクを消す(その場で終わる。1 回の Undo で戻る。範囲の決め方は MaskPaintView.ClearMaskAt)。
        private void ClearMaskHere(MaskPaintView maskView, Vector2 uv, string zoneId, int radius) {
            bool ok = maskView.ClearMaskAt(uv, zoneId, radius, out var result);
            var dest = new MaskPaintView.MaskDestination(zoneId, MaskRegionOp.ClearHere);
            _host.ShowNotification(new GUIContent(maskView.DescribeRegionEdit(dest, ok, result)));
        }

        // メッシュの島(またはクリックしたメッシュ以外)に宛先の操作を当てる。1 回の Undo で戻る。
        private void AddMeshPart(MeshPartController parts, Vector2 uv, MeshPartController.Region kind,
                                 MaskPaintView.MaskDestination dest) {
            var maskView = _host._maskView;
            var region = parts.RegionAt(uv.x, uv.y, kind);
            MaskRegionEditResult result = default;
            bool ok;
            try {
                ok = region != null && maskView.ApplyRegion(region, dest, out result);
            } finally {
                region?.Release();
            }
            _host.ShowNotification(new GUIContent(maskView.DescribeRegionEdit(dest, ok, result)));
        }

        private void UseSelectedMesh(MeshPartController parts, Object obj) {
            bool ok = parts != null && parts.UseObject(obj);
            _host.ShowNotification(new GUIContent(ok
                ? string.Format(Localization.NotifyMeshFoundFormat, parts.Found.found.Count)
                : string.Format(Localization.NotifyMeshNotUsableFormat, obj != null ? obj.name : "")));
        }
    }
}
