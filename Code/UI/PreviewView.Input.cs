// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // PreviewView: プレビュー上の入力処理(ズーム/パン/マスクペイント/FF シード/スポイト)と、
    // 真ソースの参照(スポイトの画素取得・AI 提案と自動調整が共有するキャッシュキー)。
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
                            // 見えている範囲と倍率が変わるので拡大表示を作り直させる。できるまでは前の切り出しを
                            // 新しい倍率で(元の画像の座標の位置に)描き続ける。
                            _detailView.OnViewMoved();
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
        private void HandlePreviewPanInput(Rect previewRect, bool leftDragPans, Rect beforeRect = default)
        {
            Event e = Event.current;
            if (e == null) return;

            bool isInRect = previewRect.Contains(e.mousePosition);
            // 前後比較の変更前の画像の上。ブラシ・スポイトなどのツールは変更後の画像にしか効かないので、
            // こちらは素の左ドラッグも常にパンにする。
            bool isInBefore = beforeRect.width > 0f && beforeRect.Contains(e.mousePosition);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if ((isInRect && IsPanButton(e, leftDragPans)) || (isInBefore && IsPanButton(e, true)))
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
                        // パンで見えている範囲が変わるので拡大表示を作り直させる。
                        _detailView.OnViewMoved();
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
                    if (beforeRect.width > 0f)
                        EditorGUIUtility.AddCursorRect(beforeRect, MouseCursor.Pan);
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
                        // ストローク終了: 編集中のマスクを _session.maskState に書き戻し、編集を知らせる。
                        maskView.EndStroke();
                        maskView.lastPaintUV = -Vector2.one;
                        _host.Editing.NotifyChanged(EditChange.Masks);
                        e.Use();
                        _host.RequestRepaint();
                    }
                    break;

                case EventType.Repaint:
                    if (maskView.isPainting && isInRect)
                    {
                        // 実際に塗られる円の直径は、全体表示で (2*brushSize+1) 格子セル。拡大するとその倍率だけ
                        // 小さく塗る(MaskPaintView.BrushRadiusPx)ので、画面上の大きさは拡大率に関係なく一定:
                        // 1 セルの画面上のサイズ(EffectiveZoom)÷ 拡大率(previewZoom)。
                        float brushPixels = (maskView.brushSize * 2f + 1f) * EffectiveZoom / Mathf.Max(1f, previewZoom);
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
                        // 変わるときだけ、書く前に Undo へ記録する（EditingSession.SetFloodFillSeed）。
                        var newSeed = PreviewCoords.ScreenToUv(e.mousePosition, previewRect);
                        _host.Editing.SetFloodFillSeed(targetZone, newSeed);
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
                                // 編集の合図はそのときに出る(EditingSession.AddZoneFromSample)。
                                _host.RequestNewZoneFromSample(picked, uv);
                            }
                            else
                            {
                                // 武装ゾーンは id で解決する（並べ替え・削除で index がずれても正しいゾーンに入る）。
                                var zone = _host.FindZoneById(_host.EyedropperZoneId);
                                if (zone != null) _host.Editing.ApplySample(zone, picked, uv);
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
        // TrueSource は GetPixels32 と同じ並びで行 0 = 画像下端。取得不能なら false。
        private bool SampleTrueSourceColor(float u, float v, int srcW, int srcH, out Color color)
        {
            color = Color.white;
            var px = TrueSource;
            if (px == null || srcW <= 0 || srcH <= 0 || px.Length < srcW * srcH) return false;
            PreviewCoords.UvToPixel(u, v, srcW, srcH, out int x, out int y);
            Color32 c = px.Get(y * srcW + x);
            // サンプルカラーはマッチング基準(HSV)に使い α は無関係。スウォッチを不透明にするため a=1。
            color = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
            return true;
        }

        // 埋め込みキャッシュのキー(PreviewPipeline.SourceCacheKey)。
        // internal: 自動調整の証拠要求(IrocaWindow.AutoTune)が AI 提案と同じキーで
        // SetSource するために使う(別キーだとソース切替扱いになり埋め込みを捨てる)。
        internal string TrueSourceCacheKey() => _pipeline?.SourceCacheKey();

        private void PaintAtScreenPos(Vector2 screenPos, Rect previewRect)
        {
            var maskView = _host._maskView;
            // ストローク開始時に1度だけ Unity Undo を登録する。
            maskView.BeginStroke();

            var currentUV = PreviewCoords.ScreenToUv(screenPos, previewRect);

            // ブラシの半径(マスクの画素単位)。ブラシサイズは全体表示のプレビュー格子セル単位で、拡大すると
            // その倍率だけ小さく塗る(画面上の大きさは変わらない。MaskPaintView.BrushRadiusPx)。
            maskView.EnsureMasks();
            int gridW = previewTexture != null ? previewTexture.width : maskView.maskWidth;
            float radiusPx = maskView.BrushRadiusPx(gridW, previewZoom);

            if (maskView.lastPaintUV.x >= 0f)
            {
                // 補間ステップは半径の半分(最小 0.5 画素)。UV 距離は正規化空間なので、長辺の画素数で割る
                // (短辺基準だと縦長/横長テクスチャで長辺方向のスタンプ間隔が空いて点線になる)。
                float dist = Vector2.Distance(maskView.lastPaintUV, currentUV);
                int maskLong = Mathf.Max(1, Mathf.Max(maskView.maskWidth, maskView.maskHeight));
                float step = Mathf.Max(0.5f, radiusPx * 0.5f) / maskLong;
                if (dist > step)
                {
                    int steps = Mathf.CeilToInt(dist / step);
                    for (int i = 1; i < steps; i++)
                    {
                        Vector2 lerped = Vector2.Lerp(maskView.lastPaintUV, currentUV, (float)i / steps);
                        maskView.PaintMask(lerped, radiusPx);
                    }
                }
            }

            maskView.PaintMask(currentUV, radiusPx);
            maskView.lastPaintUV = currentUV;
            // 直接書き込みしたオーバーレイセルを、イベント 1 回分まとめて GPU へ反映。
            maskView.FlushOverlayDirect();
        }
    }
}
