// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// Scene でモデルをクリックした場所(テクスチャ上の位置と、その UV の島)。
    /// <see cref="IrocaWindow"/> が作り、<see cref="PreviewView"/> がプレビューに重ねて描く。
    /// </summary>
    internal sealed class SceneClickHighlight {
        /// <summary>この位置を求めたテクスチャ(別のテクスチャを開いたら描かない)。</summary>
        public Texture2D texture;
        /// <summary>クリックした位置の UV(下原点、0..1)。</summary>
        public Vector2 point;
        /// <summary>島の三角形だけを持つ UV 情報(Tiling / Offset と繰り返しの戻し込み済み)。塗りを作るのに使う。</summary>
        public UvChartSource island;
        /// <summary>島の輪郭(2 点で 1 本、UV 下原点)。</summary>
        public Vector2[] outline;
        /// <summary>表示を始めた時刻(EditorApplication.timeSinceStartup)。塗りを薄めていくのに使う。</summary>
        public double shownAt;
        /// <summary>
        /// 島の塗りの下絵(画素が島の内側か。fillW × fillH、行 0 = 下端)。背景で作っておいたもの。null か寸法が
        /// プレビューと違えば、描くときに作る。
        /// </summary>
        public bool[] fillMask;
        public int fillW, fillH;

        /// <summary>
        /// 当たりから作る。scale / offset は当たったマテリアルのメインテクスチャの Tiling / Offset。
        /// 島は右クリックの「この島」と同じまとまり(<see cref="IslandTriangles"/>)。
        /// </summary>
        public static SceneClickHighlight Build(Texture2D texture, SceneMeshPicker.Hit hit, Vector2 scale, Vector2 offset) {
            var h = Compute(texture, hit.renderer != null ? hit.renderer.name : null, hit.uv, hit.submeshTriangles,
                            hit.triangle, hit.hitUv, scale, offset, 0, 0);
            h.shownAt = EditorApplication.timeSinceStartup;
            return h;
        }

        /// <summary>
        /// <see cref="Build"/> の中身のうち、Unity の API に触らない計算(島のチャート分け・輪郭と、fillW × fillH が
        /// 正ならその寸法の塗りの下絵)。背景のスレッドで呼べる(Scene のクリックでメインスレッドを止めない)。
        /// shownAt は呼び出し側がメインスレッドで入れる。
        /// </summary>
        internal static SceneClickHighlight Compute(Texture2D texture, string name, Vector2[] uv, int[] submeshTriangles,
            int triangle, Vector2 hitUv, Vector2 scale, Vector2 offset, int fillW, int fillH) {
            var tiled = new Vector2(hitUv.x * scale.x + offset.x, hitUv.y * scale.y + offset.y);
            Vector2 point = MeshRaycast.WrapToTexture(tiled, out Vector2 shift);
            var island = new UvChartSource {
                name = name,
                uv = uv,
                triangles = IslandTriangles(uv, submeshTriangles, triangle),
                scale = scale,
                // 繰り返しで戻した分(shift)だけ島もずらし、クリック位置と同じ場所に描く。
                offset = offset - shift,
            };
            var h = new SceneClickHighlight {
                texture = texture,
                point = point,
                island = island,
                outline = MeshPartController.ComputeIslandOutline(new[] { island }),
            };
            if (fillW > 0 && fillH > 0 && island.triangles.Length >= 3) {
                h.fillMask = FillMaskOf(island, fillW, fillH);
                h.fillW = fillW;
                h.fillH = fillH;
            }
            return h;
        }

        /// <summary>島の塗りの下絵(w × h の画素が島の内側か)。Unity の API に触らないので背景でも呼べる。</summary>
        internal static unsafe bool[] FillMaskOf(UvChartSource island, int w, int h) {
            // 島の内側の判定は右クリックの「この島」と同じラスタライズ(画素中心が三角形の内側か)
            using var map = UvChartMap.Build(w, h, new[] { island });
            int* raster = map.Raster;
            var mask = new bool[w * h];
            for (int i = 0; i < mask.Length; i++) {
                mask[i] = raster[i] >= 0;
            }
            return mask;
        }

        /// <summary>
        /// 三角形 hitTriangle と同じ UV の島(<see cref="UvChartMap.ComputeCharts"/> と同じ分け方)に属する
        /// 三角形を、triangles と同じ形式(頂点番号 ×3)で返す。右クリックの「この島」と同じまとまりになる。
        /// </summary>
        internal static int[] IslandTriangles(Vector2[] uv, int[] triangles, int hitTriangle) {
            int[] charts = UvChartMap.ComputeCharts(uv, triangles, out _);
            if ((uint)hitTriangle >= (uint)charts.Length) {
                return new int[0];
            }
            int chart = charts[hitTriangle];
            var list = new List<int>();
            for (int t = 0; t < charts.Length; t++) {
                if (charts[t] != chart) {
                    continue;
                }
                list.Add(triangles[t * 3]);
                list.Add(triangles[t * 3 + 1]);
                list.Add(triangles[t * 3 + 2]);
            }
            return list.ToArray();
        }
    }

    internal partial class PreviewView {
        // 塗りは最初だけ濃く出して目を引き、その後は消して輪郭と目印だけを残す(色の確認の邪魔をしない)。
        private const double SceneFillHoldSeconds = 1.0;
        private const double SceneFillFadeSeconds = 1.0;
        private const float SceneFillAlpha = 0.45f;
        // 塗りを薄めている間の描き直しの間隔(30 回/秒)。
        private const double SceneFadeFrameSeconds = 1.0 / 30.0;
        // クリック位置の輪(画面上の大きさで固定。4K の全体表示では 1 テクセルが見えないため)。
        private const float SceneRingRadius = 9f;
        // 拡大中にクリック位置へ寄せるとき、枠の端からこの割合より内側に見えていれば動かさない。
        private const float SceneFocusMargin = 0.1f;

        [System.NonSerialized] private SceneClickHighlight _sceneHighlight;
        // 次の描画で、この UV が見えるようにスクロールする(拡大中だけ)。
        [System.NonSerialized] private Vector2? _pendingFocusUv;
        [System.NonSerialized] private Vector3[] _sceneLineBuffer;
        // 島の塗り(プレビューテクスチャと同じ寸法に描いたもの)。テクスチャを GUI.DrawTexture で重ねると
        // 枠のクリップに確実に従う。拡大するとぼやけるが、くっきりした輪郭を上に描くので場所は読める。
        [System.NonSerialized] private Texture2D _sceneFillTexture;
        [System.NonSerialized] private SceneClickHighlight _sceneFillFor;

        // 背景で求めている Scene クリックの世代。後のクリック・消去・同期の表示が来たら、前の結果は捨てる。
        [System.NonSerialized] private int _sceneHighlightGen;

        /// <summary>Scene でクリックした場所を表示する(拡大中ならそこへスクロールする)。</summary>
        internal void ShowSceneHighlight(SceneClickHighlight highlight) {
            _sceneHighlightGen++;
            _sceneHighlight = highlight;
            _pendingFocusUv = highlight?.point;
        }

        /// <summary>
        /// 島の計算(当たったサブメッシュ全体のチャート分け・輪郭・塗りの下絵)を背景で行い、できたらメインスレッドで
        /// 表示する。以前はクリックの処理の中で同期に求め、塗りは次の描画でチャート分けとラスタライズをもう 1 回していた。
        /// compute には今のプレビューの寸法(塗りの下絵の寸法)を渡す。後のクリック・消去が来たら結果は捨てる。
        /// </summary>
        internal void ShowSceneHighlightLater(System.Func<int, int, SceneClickHighlight> compute) {
            int gen = ++_sceneHighlightGen;
            int fw = previewTexture != null ? previewTexture.width : 0;
            int fh = previewTexture != null ? previewTexture.height : 0;
            var host = _host;
            System.Threading.Tasks.Task.Run(() => compute(fw, fh)).ContinueWith(t => PreviewJobMainThread.Post(() => {
                if (gen != _sceneHighlightGen || host == null) {
                    return;
                }
                if (t.IsFaulted) {
                    Debug.LogException(t.Exception?.InnerException ?? t.Exception);
                    return;
                }
                var h = t.Result;
                if (h == null) {
                    return;
                }
                h.shownAt = EditorApplication.timeSinceStartup;
                ShowSceneHighlight(h);
                host.RequestRepaint();
            }), System.Threading.Tasks.TaskScheduler.Default);
        }

        internal void ClearSceneHighlight() {
            _sceneHighlightGen++;
            _sceneHighlight = null;
            _pendingFocusUv = null;
            ReleaseSceneFill();
        }

        private void ReleaseSceneFill() {
            if (_sceneFillTexture != null) {
                Object.DestroyImmediate(_sceneFillTexture);
            }
            _sceneFillTexture = null;
            _sceneFillFor = null;
        }

        /// <summary>島の塗りのテクスチャ(w×h、島の内側だけ色付き)。作れなければ null。</summary>
        private unsafe Texture2D SceneFillTexture(SceneClickHighlight h, int w, int hgt) {
            if (h.island?.triangles == null || h.island.triangles.Length < 3 || w <= 0 || hgt <= 0) {
                return null;
            }
            if (_sceneFillFor == h && _sceneFillTexture != null
                && _sceneFillTexture.width == w && _sceneFillTexture.height == hgt) {
                return _sceneFillTexture;
            }
            ReleaseSceneFill();
            // 背景で作った下絵が今の寸法なら使う(チャート分けとラスタライズをメインスレッドでやり直さない)。
            bool[] mask = h.fillMask != null && h.fillW == w && h.fillH == hgt
                ? h.fillMask
                : SceneClickHighlight.FillMaskOf(h.island, w, hgt);
            var c = IrocaColors.SceneHighlight;
            var on = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), 255);
            _sceneFillTexture = new Texture2D(w, hgt, TextureFormat.RGBA32, false) {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            // テクスチャの生データへ直接書く(プレビューの寸法の管理配列を作らない)。
            var pixels = (Color32*)NativeArrayUnsafeUtility.GetUnsafePtr(_sceneFillTexture.GetRawTextureData<Color32>());
            int count = w * hgt;
            for (int i = 0; i < count; i++) {
                pixels[i] = mask[i] ? on : default;
            }
            _sceneFillTexture.Apply(false, true);
            _sceneFillFor = h;
            return _sceneFillTexture;
        }

        /// <summary>
        /// 拡大中で、クリック位置が枠の中に見えていなければ、そこが枠の中央に来るようにスクロールする。
        /// BeginScrollView の前に呼ぶ。画像の矩形は前の描画のもの(スクロール内容の座標)。
        /// </summary>
        private void ApplyPendingSceneFocus(bool overflowsFrame, float viewW, float viewH) {
            if (!_pendingFocusUv.HasValue) {
                return;
            }
            var uv = _pendingFocusUv.Value;
            var r = DetailView.lastPreviewRect;
            if (!overflowsFrame || r.width <= 0f || viewW <= 1f || viewH <= 1f) {
                // 全体が見えている(動かす必要が無い)か、まだ一度も描いていない。後者は次の描画で寄せる。
                if (r.width > 0f) {
                    _pendingFocusUv = null;
                }
                return;
            }
            _pendingFocusUv = null;
            Vector2 p = PreviewCoords.UvToScreen(uv, r);
            float mx = viewW * SceneFocusMargin, my = viewH * SceneFocusMargin;
            bool visible = p.x >= _previewScrollPos.x + mx && p.x <= _previewScrollPos.x + viewW - mx
                        && p.y >= _previewScrollPos.y + my && p.y <= _previewScrollPos.y + viewH - my;
            if (visible) {
                return;
            }
            _previewScrollPos = new Vector2(Mathf.Max(0f, p.x - viewW * 0.5f), Mathf.Max(0f, p.y - viewH * 0.5f));
            // 見えている範囲が変わるので拡大表示を作り直させる(パン・ズームと同じ扱い)。
            DetailView.OnViewMoved();
        }

        // 案内の「開く」。ボタン枠を付けると行が miniLabel より高くなりプレビュー枠が跳ねるので、
        // miniLabel と同じ寸法のリンク風の文字にする。
        [System.NonSerialized] private static GUIStyle s_sceneLinkStyle;
        private static GUIStyle SceneLinkStyle {
            get {
                if (s_sceneLinkStyle == null) {
                    s_sceneLinkStyle = new GUIStyle(EditorStyles.miniLabel);
                    var link = EditorStyles.linkLabel;
                    s_sceneLinkStyle.normal.textColor = link.normal.textColor;
                    s_sceneLinkStyle.hover.textColor = link.hover.textColor;
                    s_sceneLinkStyle.active.textColor = link.active.textColor;
                }
                return s_sceneLinkStyle;
            }
        }

        /// <summary>
        /// 操作モード行が空いているときの中身: 別のテクスチャの部分をクリックした案内(と「開く」)か、
        /// まだ一度も使っていない操作の使い方。どれも無ければ空白 1 文字(行の高さを保つ)。
        /// 使い方は、色替えが済んだら右クリック、それまでは Scene のクリック。どちらも一度使えば出ない。
        /// 行はモードが無いときも高さを取っているので、案内を出しても下のプレビューは動かない。
        /// </summary>
        private void DrawIdleRow() {
            var other = _host.SceneOtherTexture;
            if (other != null) {
                GUILayout.Label(
                    TempContent.Of(string.Format(Localization.SceneClickOtherTextureFormat, other.name),
                                   Localization.SceneClickOtherTextureTooltip),
                    // 文の長さだけ取り、「開く」をすぐ後ろに置く(広げると「開く」が行の右端へ離れる)
                    EditorStyles.miniLabel, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(false));
                if (GUILayout.Button(TempContent.Of(Localization.SceneClickOpen, Localization.SceneClickOpenTooltip),
                                     SceneLinkStyle, GUILayout.ExpandWidth(false))) {
                    // テクスチャの切り替えでプレビューの中身が変わるので、この描画の途中ではなく次の更新で行う
                    // (同じフレームの Layout と Repaint で並びが食い違うと IMGUI がエラーを出す)。
                    // delayCall で Unity は例外を受け止めない(投げると、同じ回に積まれた他のツールの処理が飛ぶ)ので、ここで受け止める。
                    var host = _host;
                    EditorApplication.delayCall += () => {
                        try {
                            if (host != null) {
                                host.OpenSceneOtherTexture();
                            }
                        } catch (System.Exception e) { Debug.LogException(e); }
                    };
                }
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                return;
            }
            // 範囲を直す操作(右クリック)はメニューの奥にあり、マスク編集ウィンドウを開かないと案内が無かった。
            // 色替えが済んで「同じ色の別の所まで変わった」と気づく頃に出す。
            if (_host.StepZonesDone && !ContextMenuUsed) {
                GUILayout.Label(TempContent.Of(Localization.ContextMenuHint, Localization.ContextMenuHintTooltip),
                                EditorStyles.miniLabel, GUILayout.MinWidth(0f));
                return;
            }
            bool used = IrocaWindow.SceneClickUsed;
            GUILayout.Label(
                TempContent.Of(used ? " " : Localization.SceneClickHint,
                               used ? Localization.PreviewModeTooltip : Localization.SceneClickHintTooltip),
                EditorStyles.miniLabel, GUILayout.MinWidth(0f));
        }

        private const int SceneRingSegments = 32;
        [System.NonSerialized] private static Vector3[] s_sceneRingPoints;

        private static void DrawSceneRing(Vector2 center, float width, Color color) {
            s_sceneRingPoints ??= new Vector3[SceneRingSegments + 1];
            for (int i = 0; i <= SceneRingSegments; i++) {
                float a = i * (2f * Mathf.PI / SceneRingSegments);
                s_sceneRingPoints[i] = new Vector3(center.x + Mathf.Cos(a) * SceneRingRadius,
                                                   center.y + Mathf.Sin(a) * SceneRingRadius, 0f);
            }
            using (new Handles.DrawingScope(color)) {
                Handles.DrawAAPolyLine(width, s_sceneRingPoints);
            }
        }

        /// <summary>
        /// Scene でクリックした場所を重ねる: 島の塗り(しばらくして消える)・島の輪郭・クリック位置の輪。
        /// 線は Handles、塗りは GUI.DrawTexture で描くので、プレビュー枠の外へははみ出さない(IMGUI のクリップに従う)。
        /// </summary>
        private void DrawSceneHighlightOverlay(Rect previewRect) {
            var h = _sceneHighlight;
            if (h == null || h.texture == null || h.texture != _host.SourceTexture) {
                return;
            }

            GUI.BeginClip(previewRect);
            var local = new Rect(0f, 0f, previewRect.width, previewRect.height);

            double age = EditorApplication.timeSinceStartup - h.shownAt;
            float fade = (float)((age - SceneFillHoldSeconds) / SceneFillFadeSeconds);
            float fillAlpha = SceneFillAlpha * (1f - Mathf.Clamp01(fade));
            if (fillAlpha > 0f && previewTexture != null) {
                var fillTex = SceneFillTexture(h, previewTexture.width, previewTexture.height);
                if (fillTex != null) {
                    var prevColor = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, fillAlpha);
                    GUI.DrawTexture(local, fillTex, ScaleMode.StretchToFill, true);
                    GUI.color = prevColor;
                }
                // 薄めている間は描き直しを続ける(エディタは要求が無いと再描画しない)。以前は毎フレーム本体・別窓・
                // マスク編集の窓をすべて描き直していた。変わるのはプレビューの塗りの濃さだけなので、30 回/秒の予約で
                // プレビューを描いている窓だけを描く(予約は IrocaWindow.TickRepaintWatch が RequestPreviewRepaint で捌く)。
                _host.RequestRepaintAfter(SceneFadeFrameSeconds);
            } else if (_sceneFillTexture != null) {
                ReleaseSceneFill(); // 消し終わったら手放す
            }

            if (h.outline != null && h.outline.Length >= 2) {
                DrawUvSegments(h.outline, local, ref _sceneLineBuffer, IrocaColors.SceneHighlight);
            }

            // 目印は明るい生地でも暗い生地でも沈まないよう、暗い縁取りの上に本体を重ねる(ほかの目印と同じ方針)。
            // 輪は折れ線で描く(Handles.DrawWireDisc の太さ指定は 3D 用で、GUI の座標では崩れる)。
            Vector2 center = PreviewCoords.UvToScreen(h.point, local);
            DrawSceneRing(center, 4f, new Color(0f, 0f, 0f, 0.6f));
            DrawSceneRing(center, 2f, IrocaColors.SceneHighlight);
            EditorGUI.DrawRect(new Rect(center.x - 2f, center.y - 2f, 4f, 4f), new Color(0f, 0f, 0f, 0.6f));
            EditorGUI.DrawRect(new Rect(center.x - 1f, center.y - 1f, 2f, 2f), IrocaColors.SceneHighlight);

            GUI.EndClip();
        }
    }
}
