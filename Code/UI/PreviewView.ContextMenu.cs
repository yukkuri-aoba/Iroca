// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // PreviewView: プレビューの右クリックメニュー(AI 提案・メッシュのパーツ・直すゾーンの切り替え)。
    internal partial class PreviewView
    {
        // プレビューの右クリックメニュー。AI 提案とメッシュのパーツ操作をここへ集める
        // (2026-09-30。以前は AI 提案がマスク編集パレットの「モード」で、よく使う操作が奥に隠れていた)。
        //
        // 開くのは ContextClick だけ。Windows は右ボタンの MouseUp の後に、mac は Control+クリックで
        // ContextClick が届く。MouseDown では開かない(両方で開くと 1 回の右クリックで 2 回出る)。
        // 右ボタンは他のどの操作も使っていないので、左ドラッグのパン・ブラシとは干渉しない。
        private void HandlePreviewContextMenu(Rect previewRect)
        {
            var e = Event.current;
            if (e.type != EventType.ContextClick || !previewRect.Contains(e.mousePosition)) return;
            ShowPreviewContextMenu(e.mousePosition, previewRect);
            e.Use();
        }

        private const string ContextMenuUsedPrefKey = IrocaPrefs.ContextMenuUsed;

        /// <summary>
        /// 一度でもプレビューの右クリックメニューを開いたか(操作モード行に使い方の案内を出すかどうか。DrawIdleRow)。
        /// 開けば項目は見えるので、選ばずに閉じても使い方は伝わったとみなす。
        /// </summary>
        internal static bool ContextMenuUsed
        {
            get => s_contextMenuUsed ??= EditorPrefs.GetBool(ContextMenuUsedPrefKey, false);
            set
            {
                if (s_contextMenuUsed == value) return;
                s_contextMenuUsed = value;
                EditorPrefs.SetBool(ContextMenuUsedPrefKey, value);
            }
        }
        // 毎回の描画で EditorPrefs を読まないための控え。
        private static bool? s_contextMenuUsed;

        // 右クリックで選べる操作(宛先のゾーンに当てる。宛先の決め方は ShowPreviewContextMenu)。全ゾーン共通にできるのは「塗らない」だけ。
        private static (MaskRegionOp op, string label)[] ContextOps(bool zoneTarget) => zoneTarget
            ? new[]
            {
                (MaskRegionOp.PaintHere, Localization.CtxPaintHere),
                (MaskRegionOp.DontPaintHere, Localization.CtxDontPaintHere),
                (MaskRegionOp.OnlyThisPart, Localization.CtxOnlyThisPart),
                (MaskRegionOp.ClearHere, Localization.CtxClearHere),
            }
            : new[]
            {
                (MaskRegionOp.DontPaintHere, Localization.CtxDontPaintHereCommon),
                (MaskRegionOp.ClearHere, Localization.CtxClearHereCommon),
            };

        // AI の部分の大きさを、その 1 回だけ変えるサブメニュー。直下の項目はマスク編集の「提案の粒度」(既定: 自動)。
        private static (MaskSuggestGranularity gran, string root)[] GranularitySubmenus() => new[]
        {
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
        private void ShowPreviewContextMenu(Vector2 screenPos, Rect previewRect)
        {
            var maskView = _host._maskView;
            if (maskView == null || _host.SourceTexture == null) return;
            var uv = PreviewCoords.ScreenToUv(screenPos, previewRect);
            var zones = _host.Session?.zones;
            bool hasZone = zones != null && zones.Count > 0;
            string zoneId = maskView.ActiveTargetZoneId();   // null = 全ゾーン共通(ゾーンが無いときも)
            // 「全部のゾーンに共通で直す」を選んでいるときは、選んだとおり共通に当てる。
            var painting = zoneId != null ? PaintingZoneAt(uv) : null;
            bool byPainting = painting != null && painting.id != zoneId;
            if (byPainting) zoneId = painting.id;
            var ops = ContextOps(zoneId != null);
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
            if (ai == null)
            {
                menu.AddDisabledItem(new GUIContent(Localization.CtxAiNoSentis));
            }
            else if (!MaskSuggestSection.ToolReady)
            {
                menu.AddDisabledItem(new GUIContent(Localization.CtxAiNoModel));
            }
            else
            {
                // メニューを読んでいる間に画像の解析を先行させる(準備済みなら no-op)。
                if (EnsureTrueSource(_host.SourceTexture) && TrueSourceW > 0)
                    ai.PrepareSource(TrueSource, TrueSourceCacheKey());
                foreach (var (op, label) in ops)
                {
                    var dest = new MaskPaintView.MaskDestination(zoneId, op);
                    menu.AddItem(new GUIContent(label), false, () => RequestAiSuggest(ai, uv, dest, null));
                }
                // この 1 回だけ大きさを変える。黒い服の上のファスナーのような描き込みは「自動」だと
                // 服全体が取れるので、マスク編集の粒度を切り替えに行かずにここから選べるようにする(2026-10-15)。
                foreach (var (gran, root) in GranularitySubmenus())
                {
                    foreach (var (op, label) in ops)
                    {
                        var dest = new MaskPaintView.MaskDestination(zoneId, op);
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
            if (parts != null && parts.HasMesh)
            {
                string mesh = parts.MeshNameAt(uv.x, uv.y);
                if (mesh == null)
                {
                    menu.AddDisabledItem(new GUIContent(Localization.CtxPartNoIsland));
                }
                else
                {
                    string m = MenuSafe(mesh);
                    foreach (var (op, label) in ops)
                    {
                        var dest = new MaskPaintView.MaskDestination(zoneId, op);
                        menu.AddItem(new GUIContent(meshRoot + string.Format(Localization.CtxMeshIslandFormat, label, m)),
                                     false, () => AddMeshPart(parts, uv, MeshPartController.Region.Island, dest));
                    }
                    if (found.found.Count > 1)
                    {
                        var dest = new MaskPaintView.MaskDestination(zoneId, MaskRegionOp.DontPaintHere);
                        menu.AddItem(new GUIContent(meshRoot + string.Format(Localization.CtxMeshOthersFormat, m)),
                                     false, () => AddMeshPart(parts, uv, MeshPartController.Region.OtherMeshes, dest));
                    }
                    menu.AddSeparator(meshRoot);
                }
            }
            else if (found != null && found.unreadable > 0)
            {
                menu.AddDisabledItem(new GUIContent(string.Format(Localization.CtxPartUnreadableFormat, found.unreadable)));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(Localization.CtxPartNoMesh));
            }

            // メッシュの指定・探し直し(自動で見つからないとき・違うメッシュを拾ったとき)。
            var sel = Selection.activeObject;
            if (sel is GameObject)
                menu.AddItem(new GUIContent(meshRoot + string.Format(Localization.CtxPartUseSelectionFormat, MenuSafe(sel.name))),
                             false, () => UseSelectedMesh(parts, sel));
            else
                menu.AddDisabledItem(new GUIContent(meshRoot + Localization.CtxPartUseSelectionNone));
            menu.AddItem(new GUIContent(meshRoot + Localization.CtxPartResearch), false, () =>
            {
                parts?.Research();
                _host.ShowNotification(new GUIContent(parts != null && parts.HasMesh
                    ? string.Format(Localization.NotifyMeshFoundFormat, parts.Found.found.Count)
                    : Localization.NotifyMeshNotFound));
            });

            if (hasZone)
            {
                menu.AddSeparator("");
                AddTargetSwitchItems(menu, maskView);
            }

            menu.ShowAsContext();
            ContextMenuUsed = true;
        }

        // クリックした所の周り(半径 2 画素。1 画素だけだと、塗られた面の中の細い線や穴に当たって外れる)。
        private const int PaintingProbeRadius = 2;

        /// <summary>
        /// クリックした場所をいま塗っているゾーン(無ければ null)。表示中のプレビューを作った選択(PreviewPipeline.Selection)を
        /// 上のゾーンから順に見る(規則は MaskRegionEdit.PaintingZoneAt)。ソロ表示中は、見えているそのゾーンだけを見る。
        /// 選択がまだ無いゾーン(作り直しの途中など)は塗っていないものとして扱う。
        /// </summary>
        private ColorZone PaintingZoneAt(Vector2 uv)
        {
            var zones = _host.Session?.zones;
            var selection = _pipeline?.Selection;
            if (zones == null || zones.Count == 0 || selection == null) return null;
            if (!EnsureTrueSource(_host.SourceTexture)) return null;
            int w = TrueSourceW, h = TrueSourceH;
            if (w <= 0 || h <= 0) return null;

            int cx = Mathf.Clamp((int)(uv.x * w), 0, w - 1), cy = Mathf.Clamp((int)(uv.y * h), 0, h - 1);
            int x0 = Mathf.Max(0, cx - PaintingProbeRadius), x1 = Mathf.Min(w - 1, cx + PaintingProbeRadius);
            int y0 = Mathf.Max(0, cy - PaintingProbeRadius), y1 = Mathf.Min(h - 1, cy + PaintingProbeRadius);
            var indices = new int[(x1 - x0 + 1) * (y1 - y0 + 1)];
            int n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    indices[n++] = y * w + x;   // 行 0 = 画像の下端(UV と同じ向き)

            var solo = _host.SoloZone;
            var strengths = new float[zones.Count][];
            for (int k = 0; k < zones.Count; k++)
            {
                var z = zones[k];
                if (z == null || !z.enabled || (solo != null && z != solo)) continue;
                var s = new float[n];
                if (selection.TryReadLatest(z.id, w, h, indices, s)) strengths[k] = s;
            }
            int found = MaskRegionEdit.PaintingZoneAt(strengths, n);
            return found >= 0 ? zones[found] : null;
        }

        // 直すゾーンの切り替え(各ゾーン / 全ゾーン共通)。パレットの対象プルダウンと同じ activeMaskTarget を書き換える。
        private void AddTargetSwitchItems(GenericMenu menu, MaskPaintView maskView)
        {
            var zones = _host.Session?.zones;
            if (zones == null) return;
            string root = Localization.CtxChangeZone + "/";
            for (int i = 0; i < zones.Count; i++)
            {
                int index = i;
                string name = string.IsNullOrEmpty(zones[i]?.name) ? Localization.UnnamedZone : zones[i].name;
                // 同名ゾーンがあってもメニュー項目が潰れないよう番号を付ける
                menu.AddItem(new GUIContent(root + (i + 1) + ". " + MenuSafe(name)), maskView.activeMaskTarget == i, () =>
                {
                    maskView.activeMaskTarget = index;
                    maskView.maskDirty = true;
                    _host.RequestRepaint();
                });
            }
            menu.AddSeparator(root);
            menu.AddItem(new GUIContent(root + Localization.CtxCommonTargetItem), maskView.activeMaskTarget < 0, () =>
            {
                maskView.activeMaskTarget = -1;
                maskView.maskDirty = true;
                _host.RequestRepaint();
            });
        }

        // AI 提案の要求。エクスポートと同一の実フル解像度ソースで推論する(プレビュー縮小の影響を受けない)。
        // granularity = null ならマスク編集の「提案の粒度」に従う。
        private void RequestAiSuggest(MaskSuggestController ctl, Vector2 uv, MaskPaintView.MaskDestination dest,
                                      MaskSuggestGranularity? granularity)
        {
            if (TrueSource == null)
                EnsureTrueSource(_host.SourceTexture);
            if (TrueSource == null || TrueSourceW <= 0) return;
            if (!ctl.RequestProposal(uv.x, uv.y, dest, TrueSource, TrueSourceCacheKey(), granularity))
                _host.ShowNotification(new GUIContent(Localization.NotifyAiNotStarted));
            _host.RequestRepaint();
        }

        // メッシュの島(またはクリックしたメッシュ以外)に宛先の操作を当てる。1 回の Undo で戻る。
        private void AddMeshPart(MeshPartController parts, Vector2 uv, MeshPartController.Region kind,
                                 MaskPaintView.MaskDestination dest)
        {
            var maskView = _host._maskView;
            var region = parts.RegionAt(uv.x, uv.y, kind);
            MaskRegionEditResult result = default;
            bool ok = region != null && maskView.ApplyRegion(region, dest, out result);
            _host.ShowNotification(new GUIContent(maskView.DescribeRegionEdit(dest, ok, result)));
        }

        private void UseSelectedMesh(MeshPartController parts, Object obj)
        {
            bool ok = parts != null && parts.UseObject(obj);
            _host.ShowNotification(new GUIContent(ok
                ? string.Format(Localization.NotifyMeshFoundFormat, parts.Found.found.Count)
                : string.Format(Localization.NotifyMeshNotUsableFormat, obj != null ? obj.name : "")));
        }
    }
}
