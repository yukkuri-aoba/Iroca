// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // PreviewView: プレビュー上の入力処理(ズーム/パン/マスクペイント/FF シード/スポイト)。
    internal partial class PreviewView
    {

        /// <param name="maxZoom">ズーム(previewZoom)の上限。全体表示の倍率込みで Draw が決めたもの。</param>
        private void HandlePreviewGlobalInput(Rect previewRect, float maxZoom)
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
                                newZoom = StepZoom(newZoom, zoomIn, maxZoom);

                            if (previewTexture != null && Mathf.Abs(newZoom - oldZoom) > 0.0001f)
                            {
                                // 全体表示(100%)以下でも、枠の下限高で止まる低いウィンドウでは
                                // ビューポートが画像より小さいことがある。画像が収まるときだけ
                                // スクロールを原点へ戻す(アンカー補正の残差が残ると枠からずれたまま
                                // 直せない)。収まらないときはスクロール位置が正当なのでアンカー補正で
                                // 追従させる(その間はパンも 100% 以下で有効になる)。
                                int panels = PanelCount;
                                float dispW = previewTexture.width * newZoom * _fitScale * panels
                                    + (panels - 1) * IrocaConsts.Preview.PanelSpacing;
                                float dispH = previewTexture.height * newZoom * _fitScale;
                                bool fitsViewport = newZoom <= 1f + ZoomEpsilon &&
                                    dispW <= _detailView.lastViewportW + 1f &&
                                    dispH <= _detailView.lastViewportH + 1f;
                                if (fitsViewport)
                                {
                                    _previewScrollPos = Vector2.zero;
                                }
                                else
                                {
                                    // カーソルの下の点を動かさない。画像は枠の左右中央に置くので
                                    // (Draw 参照)、枠より狭いあいだは左の余白もズームで変わる。
                                    // その差も足さないと、全体表示から拡大した瞬間に余白の分だけ横へ飛ぶ。
                                    var pad = GUI.skin.scrollView.padding;
                                    float oldOffsetX = previewRect.x - pad.left;
                                    float newOffsetX = Mathf.Max(0f,
                                        (_detailView.lastViewportW - pad.horizontal - dispW) * 0.5f);
                                    Vector2 mouseInImage = e.mousePosition - new Vector2(previewRect.x, previewRect.y);
                                    _previewScrollPos += mouseInImage * (newZoom / oldZoom - 1f);
                                    _previewScrollPos.x += newOffsetX - oldOffsetX;
                                    _previewScrollPos.x = Mathf.Max(0f, _previewScrollPos.x);
                                    _previewScrollPos.y = Mathf.Max(0f, _previewScrollPos.y);
                                }
                            }

                            previewZoom = newZoom;
                            _detailView.MarkViewChanged();
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
                        _detailView.MarkViewChanged();
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
                        // サイズは表示倍率(EffectiveZoom)なので、カーソルも同じ大きさで描いて
                        // 「見えている範囲 = 塗られる範囲」を一致させる(従来は半分だった)。
                        float brushPixels = (maskView.brushSize * 2f + 1f) * EffectiveZoom;
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

        // UV の島の輪郭の描画用バッファ(線分の端点をスクリーン座標へ直したもの。毎回確保しない)。
        [System.NonSerialized] private Vector3[] _islandLineBuffer;

        /// <summary>
        /// メッシュの UV の島の輪郭を線で重ねる(マスク欄の「UV の島をプレビューに表示」)。
        /// 右クリックの「メッシュの形で」で当てる範囲がどこまでかを、選ぶ前に見せるため。
        /// テクスチャに焼くとズームで太ったり縮小で途切れたりするので、毎回スクリーン座標の線で描く
        /// (Handles の線は IMGUI のクリップに従うので、プレビュー枠の外へははみ出さない)。
        /// </summary>
        private void DrawMeshIslandOverlay(Rect previewRect)
        {
            if (!MeshPartSection.ShowIslands) return;
            var parts = _host._maskView?.MeshParts;
            var segments = parts != null && parts.HasMesh ? parts.IslandOutline() : null;
            if (segments == null || segments.Length < 2) return;

            if (_islandLineBuffer == null || _islandLineBuffer.Length != segments.Length)
                _islandLineBuffer = new Vector3[segments.Length];
            GUI.BeginClip(previewRect);
            var local = new Rect(0f, 0f, previewRect.width, previewRect.height);
            for (int i = 0; i < segments.Length; i++)
                _islandLineBuffer[i] = PreviewCoords.UvToScreen(segments[i], local);
            using (new Handles.DrawingScope(IrocaColors.MeshIslandLineShadow, Matrix4x4.Translate(new Vector3(1f, 1f, 0f))))
                Handles.DrawLines(_islandLineBuffer);
            using (new Handles.DrawingScope(IrocaColors.MeshIslandLine))
                Handles.DrawLines(_islandLineBuffer);
            GUI.EndClip();
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

        /// <summary>
        /// スポイトで取った色と位置をゾーンへ書く。色か位置のどちらかが変わるなら、書く前に
        /// Undo へ記録する（sampleUV もシリアライズ対象なので、Undo/Redo で色と位置が対で戻る）。
        /// 同じ色を別の場所で拾い直したときも位置は更新する（別の島をクリックしたかもしれない。
        /// 位置は自動調整が AI 提案の証拠を取るアンカー）。
        /// 戻り値 true = プレビューの再生成が要る。色が変わったとき、または位置だけが変わって、その位置が
        /// 選択に効く状態のとき（ColorZone.SelectionUsesSampleUV: 位置を含む連結成分は包絡ゲートで落とさない）。
        /// </summary>
        internal static bool ApplyEyedropperSample(UnityEngine.Object undoHost, ColorZone zone, Color picked, Vector2 uv)
        {
            bool colorChanged = zone.sampleColor != picked || !zone.sampleColorSet;
            if (!colorChanged && zone.sampleUV == uv) return false;
            Undo.RecordObject(undoHost, "Sample Color");
            if (colorChanged)
            {
                zone.sampleColor = picked;
                zone.sampleColorSet = true;
                // 主サンプル変更で陳腐化する内部サンプルを破棄（ColorField と同じ挙動）。
                if (zone.extraSamples != null && zone.extraSamples.Count > 0)
                    zone.extraSamples.Clear();
            }
            zone.sampleUV = uv;
            return colorChanged || zone.SelectionUsesSampleUV;
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
                            if (_host.EyedropperZoneId == IrocaWindow.NewZoneEyedropperId)
                            {
                                // ゾーンがまだ無いときの開始操作: ゾーンを作ってこの色を入れる。
                                // ゾーンリストの変更は次の Layout イベントで適用する（遅延ミューテーション）。
                                _host.RequestNewZoneFromSample(picked, uv);
                                previewDirty = true;
                            }
                            else
                            {
                                // 武装ゾーンは id で解決する（並べ替え・削除で index がずれても正しいゾーンに入る）。
                                var zone = _host.FindZoneById(_host.EyedropperZoneId);
                                if (zone != null && ApplyEyedropperSample(_host, zone, picked, uv))
                                    previewDirty = true;
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
            if (ctl == null) return;
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
