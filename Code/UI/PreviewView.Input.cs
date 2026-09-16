// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // PreviewView: プレビュー上の入力処理(ズーム/パン/マスクペイント/FF シード/スポイト)。
    internal partial class PreviewView
    {

        private void HandlePreviewGlobalInput(Rect previewRect, float scale)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            // Esc でプレビュー上の一時モード（スポイト／シード指定／マスクのブラシ／AI 提案）を
            // 解除する。以前は各モードのボタンを「もう一度押す」以外に抜ける手段が無く、
            // そのボタンが設定列のスクロール外や別ウィンドウにあると解除できなかった。
            //
            // GetTypeForControl を通さず生の type を見るのは、Passive な controlId では
            // キーイベントが Ignore に落ちるため。e.Use() 後は下の switch が Used を見て
            // 何もしない（control ID の消費順は変えないので IMGUI の整合は保たれる）。
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape
                && _host.ClearPreviewInteractionModes())
            {
                e.Use();
            }

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.ScrollWheel:
                    if (isInRect && e.control && Mathf.Abs(e.delta.y) > ZoomEpsilon)
                    {
                        // スクロール量を蓄積し、閾値ぶんたまるごとに 1 ストップだけ進める。
                        // 拡大↔縮小で向きが変わったら貯金をリセットし、逆方向の繰り越しが
                        // 残って一瞬反対に動くのを防ぐ（即応性を保つ）。
                        if (_zoomScrollAccum != 0f && Mathf.Sign(e.delta.y) != Mathf.Sign(_zoomScrollAccum))
                            _zoomScrollAccum = 0f;
                        _zoomScrollAccum += e.delta.y;

                        // 閾値を超えたぶんのストップ数。端数は次イベントへ繰り越す。
                        int steps = (int)(_zoomScrollAccum / ZoomScrollStepThreshold);
                        if (steps != 0)
                        {
                            _zoomScrollAccum -= steps * ZoomScrollStepThreshold;

                            float oldZoom = previewZoom;
                            // 上スクロール(delta.y<0 ⇒ steps<0)で拡大。
                            bool zoomIn = steps < 0;
                            int count = Mathf.Abs(steps);
                            float newZoom = oldZoom;
                            for (int i = 0; i < count; i++)
                                newZoom = StepZoom(newZoom, zoomIn, ComputeMaxZoom(scale));

                            if (previewTexture != null && Mathf.Abs(newZoom - oldZoom) > 0.0001f)
                            {
                                // 等倍(100%)以下でも、動的高さ調整でビューポートが画像より
                                // 小さいことがある。画像が収まるときだけスクロールを原点へ戻す
                                // (アンカー補正の残差が残ると枠からずれたまま直せない)。
                                // 収まらないときはスクロール位置が正当なのでアンカー補正で
                                // 追従させる(その間はパンも等倍以下で有効になる)。
                                int panels = (comparisonMode && rawPreviewTexture != null) ? 2 : 1;
                                float dispW = previewTexture.width * newZoom * panels
                                    + (panels - 1) * IrocaConsts.Preview.PanelSpacing;
                                float dispH = previewTexture.height * newZoom;
                                bool fitsViewport = newZoom <= 1f + ZoomEpsilon &&
                                    dispW <= _detailView.lastViewportW + 1f &&
                                    dispH <= _detailView.lastViewportH + 1f;
                                if (fitsViewport)
                                {
                                    _previewScrollPos = Vector2.zero;
                                }
                                else
                                {
                                    Vector2 mouseInImage = e.mousePosition - new Vector2(previewRect.x, previewRect.y);
                                    _previewScrollPos += mouseInImage * (newZoom / oldZoom - 1f);
                                    _previewScrollPos.x = Mathf.Max(0f, _previewScrollPos.x);
                                    _previewScrollPos.y = Mathf.Max(0f, _previewScrollPos.y);
                                }
                            }

                            previewZoom = newZoom;
                            _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                            // ズーム比が変わるとピクセル/ソース比も変わるため、
                            // 古い詳細プレビューは整合しなくなる。破棄して再生成を待つ。
                            _detailView.InvalidateDisplay();
                            _host.RequestRepaint();
                        }

                        // ストップが動かなくてもイベントは消費し、外側スクロールビューが
                        // 動かないようにする（蓄積中も含め Ctrl+スクロールはここで完結）。
                        e.Use();
                    }
                    break;

            }
        }

        /// <summary>
        /// プレビューの表示位置を掴んで動かす。
        /// </summary>
        /// <param name="leftDragPans">
        /// 素の左ドラッグをパンに使ってよいか。ブラシ・スポイト・シード指定が左クリックを
        /// 使っている間は false。false でも中ボタンドラッグと Alt+左ドラッグは常にパンになる
        /// （塗っている最中でも表示を動かせるように。2026-09-11 の UX 見直し）。
        /// </param>
        private void HandlePreviewPanInput(Rect previewRect, bool leftDragPans)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (isInRect && IsPanButton(e, leftDragPans))
                    {
                        GUIUtility.hotControl = controlId;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId)
                    {
                        // 「掴んで動かす」操作なので、ドラッグ量と画像の移動量を 1:1 にする。
                        // _previewScrollPos はコンテンツ座標(=ズーム後の displayW/H 空間)で持ち、
                        // この単位は画面ピクセルと等価。よって e.delta(画面 px)をそのまま引けば、
                        // カーソル下に掴んだ点が常にカーソルへ追従する（ズーム倍率に依らず一定）。
                        // 以前は e.delta に previewZoom を掛けて「1 ストロークで全幅横断」を狙って
                        // いたが、高ズームほど画像がカーソルの何倍も飛んで感度が高すぎたため廃止。
                        _previewScrollPos -= e.delta;
                        _previewScrollPos.x = Mathf.Max(0f, _previewScrollPos.x);
                        _previewScrollPos.y = Mathf.Max(0f, _previewScrollPos.y);
                        _detailView.lastDetailDirtyTime = EditorApplication.timeSinceStartup;
                        // パンで詳細クロップ位置が変わるので古い詳細プレビューを破棄。
                        _detailView.InvalidateDisplay();
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;

                case EventType.Repaint:
                    // 「掴めるカーソル」を出すのは素の左ドラッグがパンのときだけ。中ボタン・
                    // Alt は補助操作なので、ブラシのカーソル表示を上書きしない。
                    if (isInRect && leftDragPans)
                        EditorGUIUtility.AddCursorRect(previewRect, MouseCursor.Pan);
                    break;
            }
        }

        /// <summary>
        /// このマウス押下をパンの開始として受けるか。
        /// 中ボタンと Alt+左はどのツールとも衝突しないので常に受ける。
        /// </summary>
        private static bool IsPanButton(Event e, bool leftDragPans)
        {
            if (e.button == 2) return true;   // 中ボタンドラッグ
            if (e.button != 0) return false;
            if (e.alt) return true;           // Alt+左ドラッグ（Unity 慣習に合わせる）
            return leftDragPans;
        }

        private void HandlePreviewPaintInput(Rect previewRect)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            var maskView = _host._maskView;

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    // Alt+左ドラッグはパンに譲る（塗っている最中でも表示を動かせるように）。
                    // 譲らないと、後段のパン処理へイベントが届く前にここで塗ってしまう。
                    if (e.button == 0 && isInRect && !e.alt)
                    {
                        GUIUtility.hotControl = controlId;
                        maskView.isPainting = true;
                        maskView._maskStrokeStarted = false;
                        maskView.lastPaintUV = -Vector2.one;
                        PaintAtScreenPos(e.mousePosition, previewRect);
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (maskView.isPainting && GUIUtility.hotControl == controlId)
                    {
                        PaintAtScreenPos(e.mousePosition, previewRect);
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0 && GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        maskView.isPainting = false;
                        // ストローク終了: bool[] バッファを _session.maskState に書き戻す。
                        maskView.EndStroke();
                        maskView.lastPaintUV = -Vector2.one;
                        previewDirty = true;
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.Repaint:
                    if (maskView.isPainting && isInRect)
                    {
                        // 実際に塗られる円は直径 (2*brushSize+1) 格子セル。1 セルの画面上の
                        // サイズは previewZoom なので、カーソルも同じ大きさで描いて
                        // 「見えている範囲 = 塗られる範囲」を一致させる(従来は半分だった)。
                        float brushPixels = (maskView.brushSize * 2f + 1f) * previewZoom;
                        // カーソル色は「いま塗ろうとしているマスクの色」を映す（オーバーレイと
                        // 同じ 赤=除外 / 緑=含める）。消しゴムはどちらの種類でも「取り除く」操作
                        // なので中立の白にする。以前は塗る/消すの別を色で表していたため、含める
                        // マスクを塗っている最中に赤いカーソルが出て意味が食い違っていた
                        // （2026-09-11 の UX 見直し）。
                        var cursorColor = maskView.brushEraseMode
                            ? IrocaColors.BrushCursorErase
                            : (maskView.editIncludeLayer
                                ? IrocaColors.BrushCursorInclude
                                : IrocaColors.BrushCursorExclude);
                        float r = brushPixels * 0.5f;
                        EditorGUI.DrawRect(
                            new Rect(e.mousePosition.x - r, e.mousePosition.y - r, r * 2f, r * 2f),
                            cursorColor);
                    }
                    break;
            }
        }

        /// <param name="armed">
        /// ゾーンカードの「指定」ボタンで武装しているか。true なら対象ゾーンが一意に決まり、
        /// 素のクリックでシードを置いて一発で武装解除する（スポイトと同じ one-shot）。
        /// false のときは従来どおり Shift+クリックで、対象は「マスク編集対象のゾーン、
        /// 無ければ先頭の該当ゾーン」という暗黙の選び方になる。
        /// </param>
        private void HandleFloodFillSeedInput(Rect previewRect, bool armed)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            var zones = _host.Session.zones;
            ColorZone targetZone = null;

            if (armed)
            {
                // 武装したゾーンへ確実に入れる（enabled は問わない。ユーザーがそのゾーンを
                // 名指ししているので、無効でも値は記録しておき、有効化した時点で効く）。
                var z = _host.FindZoneById(_host.SeedPickZoneId);
                if (z != null && z.mode == SelectionMode.ColorPick && z.useFloodFill)
                    targetZone = z;
            }
            else if (zones != null)
            {
                int activeZoneIndex = _host._maskView.activeMaskTarget;
                if (activeZoneIndex >= 0 && activeZoneIndex < zones.Count)
                {
                    var activeZone = zones[activeZoneIndex];
                    if (activeZone.enabled && activeZone.mode == SelectionMode.ColorPick && activeZone.useFloodFill)
                        targetZone = activeZone;
                }
                if (targetZone == null)
                {
                    for (int i = 0; i < zones.Count; i++)
                    {
                        var zone = zones[i];
                        if (zone == null || !zone.enabled
                            || zone.mode != SelectionMode.ColorPick || !zone.useFloodFill) continue;
                        targetZone = zone;
                        break;
                    }
                }
            }

            bool hasFloodFill = targetZone != null;

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    // 武装中は素のクリック。武装していないときは、自動アンカリングが既定なので
                    // 通常クリックをパン/検分に温存し、Shift+クリックでのみシードを置く。
                    bool accept = hasFloodFill && e.button == 0 && isInRect && !e.control && !e.alt
                                  && (armed ? !e.shift : e.shift);
                    if (accept)
                    {
                        // 更新の直前に Undo を登録する（記録されるのは更新前の seedUV）。
                        var newSeed = PreviewCoords.ScreenToUv(e.mousePosition, previewRect);
                        if (targetZone.seedUV != newSeed)
                        {
                            Undo.RecordObject(_host, "Set Flood Fill Seed");
                            targetZone.seedUV = newSeed;
                            previewDirty = true;
                        }
                        if (armed)
                        {
                            // 一発で武装解除（同じ位置を選び直したときも解除する）。
                            // ★ここで hotControl を取ってはいけない★ — 武装が解けた次のイベントでは
                            // このハンドラが呼ばれないことがあり（AI 提案が武装していると呼ばれない）、
                            // MouseUp で解放できずに残る。残った hotControl は以降のマウス入力を
                            // すべて Ignore に落とすため、ウィンドウが固まったように見える。
                            // クリック 1 回で完結する操作なのでマウスを掴み続ける必要もない。
                            _host.SeedPickZoneId = null;
                        }
                        else
                        {
                            // Shift+クリック経路は武装が続く＝次のイベントでも必ずここへ来るので、
                            // 従来どおり掴んで MouseUp で解放する。
                            GUIUtility.hotControl = controlId;
                        }
                        // 値が変わらなくてもイベントは消費する。消さないと後段のパンが
                        // 掴んでしまい、シードを置いたつもりの操作が表示移動になる。
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;

                case EventType.Repaint:
                    if (hasFloodFill && isInRect && (armed || e.shift))
                        EditorGUIUtility.AddCursorRect(previewRect, MouseCursor.Link);
                    break;
            }
        }

        /// <summary>
        /// 連続領域モードのシード位置を十字で描く。色はゾーン識別色（マスクオーバーレイと
        /// 同じ黄金比生成）にして、複数ゾーンがシードを持っていても対応が分かるようにする。
        /// 以前は全ゾーン同じ黄色で、どのシードがどのゾーンのものか読み取れなかった。
        /// </summary>
        private void DrawFloodFillSeedOverlay(Rect previewRect)
        {
            var zones = _host.Session.zones;
            if (zones == null) return;
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                if (z == null || !z.enabled || z.mode != SelectionMode.ColorPick || !z.useFloodFill) continue;
                if (z.seedUV.x < 0f) continue;

                var sp = PreviewCoords.UvToScreen(z.seedUV, previewRect);
                DrawMarkerCross(sp.x, sp.y, ZoneMarkerColor(i));
            }
        }

        /// <summary>
        /// スポイトで色を取った位置を菱形で描く。自動調整はこの位置に AI マスク提案をかけて
        /// 証拠にするため、「どこを取ったか」が見えないと結果の当たり外れを説明できなかった。
        /// </summary>
        private void DrawSampleUvOverlay(Rect previewRect)
        {
            var zones = _host.Session.zones;
            if (zones == null) return;
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                if (z == null || !z.enabled || !z.HasSampleUV) continue;

                var sp = PreviewCoords.UvToScreen(z.sampleUV, previewRect);
                DrawMarkerDiamond(sp.x, sp.y, ZoneMarkerColor(i));
            }
        }

        /// <summary>ゾーン識別色（マスクオーバーレイと共通）を不透明寄りの Color で返す。</summary>
        private static Color ZoneMarkerColor(int zoneIndex)
        {
            Color32 c = MaskPaintView.OverlayColorForZone(zoneIndex);
            return new Color(c.r / 255f, c.g / 255f, c.b / 255f, 0.95f);
        }

        // マーカーは明るい生地でも暗い生地でも沈まないよう、暗い縁取りの上に本体を重ねる
        // （AI 提案の反映待ちドットと同じ方針）。
        private static void DrawMarkerCross(float sx, float sy, Color color)
        {
            const float armLen = 7f;
            const float thickness = 2f;
            var outline = new Color(0f, 0f, 0f, 0.55f);
            EditorGUI.DrawRect(new Rect(sx - armLen - 1f, sy - thickness * 0.5f - 1f, (armLen + 1f) * 2f, thickness + 2f), outline);
            EditorGUI.DrawRect(new Rect(sx - thickness * 0.5f - 1f, sy - armLen - 1f, thickness + 2f, (armLen + 1f) * 2f), outline);
            EditorGUI.DrawRect(new Rect(sx - armLen, sy - thickness * 0.5f, armLen * 2f, thickness), color);
            EditorGUI.DrawRect(new Rect(sx - thickness * 0.5f, sy - armLen, thickness, armLen * 2f), color);
        }

        // 菱形は「幅を変えながら積む横線」で描く（IMGUI に多角形塗りが無いため）。
        // 十字（シード）と形で区別が付けば十分なので 5px 相当の粗さで足りる。
        private static void DrawMarkerDiamond(float sx, float sy, Color color)
        {
            const int half = 5;
            var outline = new Color(0f, 0f, 0f, 0.55f);
            for (int dy = -half - 1; dy <= half + 1; dy++)
            {
                int w = (half + 1) - Mathf.Abs(dy);
                if (w <= 0) continue;
                EditorGUI.DrawRect(new Rect(sx - w, sy + dy, w * 2f, 1f), outline);
            }
            for (int dy = -half + 1; dy <= half - 1; dy++)
            {
                int w = (half - 1) - Mathf.Abs(dy);
                if (w <= 0) continue;
                EditorGUI.DrawRect(new Rect(sx - w, sy + dy, w * 2f, 1f), color);
            }
        }

        // プレビュー上のクリックで、武装中ゾーンのサンプルカラーを実テクスチャ画素から取得する。
        // 一発取得したら自動で武装解除する（one-shot）。マスクペイント中は呼ばれない。
        private void HandleEyedropperInput(Rect previewRect, int srcW, int srcH)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    // 素のクリックのみ受ける（修飾キー付きは別操作なので拾わない）。
                    if (e.button == 0 && isInRect && !e.shift && !e.control && !e.alt)
                    {
                        var uv = PreviewCoords.ScreenToUv(e.mousePosition, previewRect);
                        if (SampleTrueSourceColor(uv.x, uv.y, srcW, srcH, out Color picked))
                        {
                            // 武装ゾーンは id で解決する（並べ替え・削除で index がずれても正しいゾーンに入る）。
                            var zone = _host.FindZoneById(_host.EyedropperZoneId);
                            if (zone != null)
                            {
                                if (zone.sampleColor != picked || !zone.sampleColorSet)
                                {
                                    Undo.RecordObject(_host, "Sample Color");
                                    zone.sampleColor = picked;
                                    zone.sampleColorSet = true;
                                    // 主サンプル変更で陳腐化する内部サンプルを破棄（ColorField と同じ挙動）。
                                    if (zone.extraSamples != null && zone.extraSamples.Count > 0)
                                        zone.extraSamples.Clear();
                                    previewDirty = true;
                                }
                                // スポイト位置は自動調整の証拠アンカー（AI 提案をかける位置）。同じ色を
                                // 拾い直したときも位置は更新する（別の島をクリックしたかもしれない）。
                                zone.sampleUV = uv;
                                // Undo で色が巻き戻ったときに「この位置はもう古い」と判定するため、
                                // 位置と対になる色を控える。
                                _host.RememberSampleUvColor(zone);
                            }
                            // one-shot: 取得したら武装解除。
                            // ★hotControl は取らない★ — 武装解除後は次のイベントでこのハンドラが
                            // 呼ばれないため、MouseUp で解放できずに残る。残った hotControl は以降の
                            // マウス入力を Ignore に落とす。これまでは、呼ばれなくなったぶん後続の
                            // ハンドラへ同じ ID が回り、そちらの MouseUp が偶然解放していただけで、
                            // 呼び出し順を変えると壊れる作りだった（2026-09-11）。
                            // クリック 1 回で完結する操作なのでマウスを掴む必要もない。
                            _host.EyedropperZoneId = null;
                            e.Use();
                            _host.RequestRepaint();
                        }
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;

                case EventType.Repaint:
                    if (isInRect)
                        EditorGUIUtility.AddCursorRect(previewRect, MouseCursor.Link);
                    break;
            }
        }

        // u,v(0-1, v は下端=0)を実フル解像度ソース画素にマップして色を返す。
        // _trueSourcePixels は GetPixels32 由来で行 0 = 画像下端。取得不能なら false。
        private bool SampleTrueSourceColor(float u, float v, int srcW, int srcH, out Color color)
        {
            color = Color.white;
            var px = _trueSourcePixels;
            if (px == null || srcW <= 0 || srcH <= 0 || px.Length < srcW * srcH) return false;
            PreviewCoords.UvToPixel(u, v, srcW, srcH, out int x, out int y);
            Color32 c = px[y * srcW + x];
            // サンプルカラーはマッチング基準(HSV)に使い α は無関係。スウォッチを不透明にするため a=1。
            color = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
            return true;
        }

        // AI 提案の反映待ちクリック位置に目印を描く。推論が追いつくまで「押したのに未反映」の
        // 時間があるため、受理済みクリックを可視化して二度押し・押し忘れの混乱を防ぐ。
        // 先頭(処理中)は明るく、待ちの分は薄く描いて進行が分かるようにする。
        private void DrawAiSuggestPendingOverlay(Rect previewRect)
        {
            var maskView = _host._maskView;
            var ctl = maskView != null ? maskView.SuggestControllerIfCreated : null;
            if (ctl == null || !ctl.Active) return;
            var clicks = ctl.PendingClicks;
            if (clicks == null || clicks.Count == 0) return;

            for (int i = 0; i < clicks.Count; i++)
            {
                var sp = PreviewCoords.UvToScreen(clicks[i], previewRect);
                float sx = sp.x, sy = sp.y;
                float alpha = i == 0 ? 0.95f : 0.55f;
                const float half = 4f;
                // 明るい生地でも暗い生地でも沈まないよう、暗い縁取りの上に明色ドットを重ねる
                EditorGUI.DrawRect(
                    new Rect(sx - half - 1f, sy - half - 1f, (half + 1f) * 2f, (half + 1f) * 2f),
                    new Color(0f, 0f, 0f, alpha * 0.6f));
                EditorGUI.DrawRect(
                    new Rect(sx - half, sy - half, half * 2f, half * 2f),
                    new Color(0.3f, 0.85f, 1f, alpha));
            }
        }

        // クリック位置を UV(下原点)に変換してコントローラへ渡す。実際の推論・提案表示は
        // MaskSuggestController + Sentis サービス側が担い、ここは入力の横取りだけを行う。
        //
        // 受けるのは**右クリック**(mac は Control+クリックでも可)。左ボタンを取ると
        // AI モード中はパンが完全に止まり、推論の待ち時間に画像を動かして次の対象を
        // 探すことすらできなくなる。右へ寄せることで、左ドラッグのパンを AI モード中も
        // そのまま残せる。
        private void HandleAiSuggestInput(Rect previewRect, int srcW, int srcH)
        {
            var maskView = _host._maskView;
            var ctl = maskView != null ? maskView.SuggestControllerIfCreated : null;
            if (ctl == null || !ctl.Active) return;

            var e = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            bool isInRect = previewRect.Contains(e.mousePosition);

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 1 && isInRect && !e.alt)
                    {
                        RequestAiSuggestAt(ctl, e.mousePosition, previewRect);
                        _aiSuggestRightPressHandled = true;
                        GUIUtility.hotControl = controlId;
                        e.Use();
                        _host.RequestRepaint();
                    }
                    else if (e.button != 1)
                    {
                        // 右ボタン以外の押下が入った = 前の右クリックの ContextClick は
                        // もう来ない。ここで下ろしておかないと、mac の Control+クリック
                        // (MouseDown は左 → ContextClick)が抑止側と誤認されて 1 回空振る。
                        _aiSuggestRightPressHandled = false;
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;

                case EventType.ContextClick:
                    // 右クリックは MouseDown で受け済みなので、ここは menu 抑止のためだけに
                    // 消費する。逆に MouseDown を伴わず ContextClick だけが来る環境
                    // (mac の Control+クリック)では、これが唯一の受け口になる。
                    if (isInRect)
                    {
                        if (!_aiSuggestRightPressHandled)
                        {
                            RequestAiSuggestAt(ctl, e.mousePosition, previewRect);
                            _host.RequestRepaint();
                        }
                        _aiSuggestRightPressHandled = false;
                        e.Use();
                    }
                    break;

                case EventType.Layout:
                    // クリックを待たず、AI モードでいる間にソース画像の解析を先行させる。
                    // 初回はモデルのロードと推論カーネルのコンパイルで時間がかかるため、
                    // クリック後に始めると押しても無反応な時間が生まれる(同一ソースなら no-op)。
                    // EnsureTrueSource はテクスチャが変わったときだけ読み直す(通常はキャッシュ)。
                    if (EnsureTrueSource(_host.SourceTexture) && _trueSourceW > 0)
                        ctl.PrepareSource(_trueSourcePixels, _trueSourceW, _trueSourceH,
                                          TrueSourceCacheKey());
                    break;
            }
        }

        // 画面座標を UV(下原点)へ直して提案を要求する。MouseDown / ContextClick の
        // 2 経路から呼ばれるため切り出してある。
        private void RequestAiSuggestAt(MaskSuggestController ctl, Vector2 screenPos, Rect previewRect)
        {
            var uv = PreviewCoords.ScreenToUv(screenPos, previewRect);
            // エクスポートと同一の実フル解像度ソースで推論する(プレビュー縮小の影響を受けない)
            if (_trueSourcePixels == null)
                EnsureTrueSource(_host.SourceTexture);
            if (_trueSourcePixels != null && _trueSourceW > 0)
                ctl.OnPreviewClick(uv.x, uv.y, _trueSourcePixels, _trueSourceW, _trueSourceH,
                                   TrueSourceCacheKey());
        }

        // 埋め込みキャッシュのキー。テクスチャの中身が変わったら別キーになるよう
        // アセットパス + ファイル更新時刻 + 実寸で構成する。
        // internal: 自動調整の証拠要求(IrocaWindow.AutoTune)が AI 提案と同じキーで
        // SetSource するために使う(別キーだとソース切替扱いになり埋め込みを捨てる)。
        internal string TrueSourceCacheKey()
        {
            string path = _trueSourceFor != null ? AssetDatabase.GetAssetPath(_trueSourceFor) : null;
            long ticks = 0;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                ticks = System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            return $"{path}|{ticks}|{_trueSourceW}x{_trueSourceH}";
        }

        private void PaintAtScreenPos(Vector2 screenPos, Rect previewRect)
        {
            var maskView = _host._maskView;
            // ストローク開始時に1度だけ Unity Undo を登録する。
            maskView.BeginStroke();

            var currentUV = PreviewCoords.ScreenToUv(screenPos, previewRect);

            // 塗り格子 = 表示プレビューの画素格子。オーバーレイ/プロキシ処理が最近傍で
            // 代表点を読む単位と一致させるため、previewTexture の実寸を渡す(WYSIWYG)。
            maskView.EnsureMasks();
            int gridW = previewTexture != null ? previewTexture.width : maskView.maskWidth;
            int gridH = previewTexture != null ? previewTexture.height : maskView.maskHeight;

            if (maskView.lastPaintUV.x >= 0f)
            {
                float dist = Vector2.Distance(maskView.lastPaintUV, currentUV);
                // 補間ステップは格子の長辺基準。UV 距離は正規化空間なので、短辺基準だと
                // 縦長/横長テクスチャで長辺方向のスタンプ間隔がセルを跨いで点線になる。
                int gridLong = Mathf.Max(1, Mathf.Max(gridW, gridH));
                float step = 1f / gridLong;
                if (dist > step)
                {
                    int steps = Mathf.CeilToInt(dist / step);
                    for (int i = 1; i < steps; i++)
                    {
                        Vector2 lerped = Vector2.Lerp(maskView.lastPaintUV, currentUV, (float)i / steps);
                        maskView.PaintMask(lerped, gridW, gridH);
                    }
                }
            }

            maskView.PaintMask(currentUV, gridW, gridH);
            maskView.lastPaintUV = currentUV;
            // 直接書き込みしたオーバーレイセルを、イベント 1 回分まとめて GPU へ反映。
            maskView.FlushOverlayDirect();
        }
    }
}
