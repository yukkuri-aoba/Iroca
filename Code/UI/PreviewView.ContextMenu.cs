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

        // 右クリックで選べる操作(いま直しているゾーンに当てる)。全ゾーン共通にできるのは「塗らない」だけ。
        private static (MaskRegionOp op, string label)[] ContextOps(bool zoneTarget) => zoneTarget
            ? new[]
            {
                (MaskRegionOp.PaintHere, Localization.CtxPaintHere),
                (MaskRegionOp.DontPaintHere, Localization.CtxDontPaintHere),
                (MaskRegionOp.OnlyThisPart, Localization.CtxOnlyThisPart),
            }
            : new[] { (MaskRegionOp.DontPaintHere, Localization.CtxDontPaintHereCommon) };

        // GenericMenu は "/" をサブメニューの区切りとして読むので、名前の "/" は全角に置き換える。
        private static string MenuSafe(string s) => string.IsNullOrEmpty(s) ? s : s.Replace('/', '／');

        // メニューの言葉は「ここも塗る / ここは塗らない / この部分だけ塗る」に揃える(2026-10-06)。
        // 以前は「追加先」「除外」「含める」「AI 提案:」「パーツ:」で、マスクの仕組みを知らないと選べなかった。
        // 宛先は「いま直しているゾーン」(= マスクの編集対象。最後に触ったゾーンへ追従する)。
        private void ShowPreviewContextMenu(Vector2 screenPos, Rect previewRect)
        {
            var maskView = _host._maskView;
            if (maskView == null || _host.SourceTexture == null) return;
            var uv = PreviewCoords.ScreenToUv(screenPos, previewRect);
            var zones = _host.Session?.zones;
            bool hasZone = zones != null && zones.Count > 0;
            string zoneId = maskView.ActiveTargetZoneId();   // null = 全ゾーン共通(ゾーンが無いときも)
            var ops = ContextOps(zoneId != null);
            var menu = new GenericMenu();

            // 見出し: どのゾーンを直すか。
            menu.AddDisabledItem(new GUIContent(
                zoneId != null ? string.Format(Localization.CtxHeaderZoneFormat, MenuSafe(maskView.ActiveTargetName()))
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
                if (EnsureTrueSource(_host.SourceTexture) && _trueSourceW > 0)
                    ai.PrepareSource(_trueSourcePixels, _trueSourceW, _trueSourceH, TrueSourceCacheKey());
                foreach (var (op, label) in ops)
                {
                    var dest = new MaskPaintView.MaskDestination(zoneId, op);
                    menu.AddItem(new GUIContent(label), false, () => RequestAiSuggest(ai, uv, dest));
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
        private void RequestAiSuggest(MaskSuggestController ctl, Vector2 uv, MaskPaintView.MaskDestination dest)
        {
            if (_trueSourcePixels == null)
                EnsureTrueSource(_host.SourceTexture);
            if (_trueSourcePixels == null || _trueSourceW <= 0) return;
            if (!ctl.RequestProposal(uv.x, uv.y, dest, _trueSourcePixels, _trueSourceW, _trueSourceH,
                                     TrueSourceCacheKey()))
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
