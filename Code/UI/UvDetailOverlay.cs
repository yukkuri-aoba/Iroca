// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 開発者向けの UV の詳しい表示(2026-11-12 ユーザー要望)。「UV の島をプレビューに表示」の輪郭に加えて、
    /// 島ごとの塗り分け・三角形の線・UV の重なり・カーソルの下のメッシュ名を出す。
    /// ウィンドウのタブの「⋮」の「開発者向けの設定を表示」が ON で、島の表示も ON のときだけ効く。
    ///
    /// 10 万ポリゴン級のメッシュの三角形を描き直しのたびに線で描くとプレビューが重くなるので、塗り・線・重なりは
    /// 背景のスレッドで 1 枚の画像(長辺 MaxSide 以下)に焼き、描くときはそれを重ねるだけにする。焼き直すのは
    /// メッシュ・寸法・表示する層が変わったときだけ。拡大すると画像の画素が見えるが、開発用の表示なので受け入れる。
    /// 画素の大きさのバッファはヒープの外(NativeBuffer)に置く。
    /// </summary>
    internal sealed unsafe class UvDetailOverlay {
        // 焼く画像の長辺。4K のテクスチャでも 2048 で島・三角形の見分けはつき、作業は 16MB ほどで済む。
        private const int MaxSide = 2048;
        // 島の塗りの不透明度(下のプレビューが透けて見える程度)。
        private const byte FillAlpha = 90;
        // 重なりの縞の不透明度と、縞の太さ(画素)。
        private const byte OverlapAlpha = 170;
        private const int OverlapStripe = 4;
        // 三角形の線の不透明度。
        private const byte WireAlpha = 150;
        // 島の色を色相環で散らす歩幅(黄金比の端数。隣の番号の島が似た色にならない)。
        private const float HueStep = 0.618034f;

        [System.Flags]
        private enum Layers {
            None = 0,
            Fill = 1,
            Wire = 2,
            Overlap = 4,
        }

        // ── 表示の設定(PC ごと) ──
        // プレビューの描画のたびに読むので、値は控えておく(EditorPrefs を毎回引かない。書くのはここだけ)。
        private static bool? s_showFill, s_showWire, s_showOverlap, s_showHoverInfo;

        public static bool ShowFill {
            get => s_showFill ??= EditorPrefs.GetBool(IrocaPrefs.UvDetailFill, false);
            set {
                s_showFill = value;
                EditorPrefs.SetBool(IrocaPrefs.UvDetailFill, value);
            }
        }
        public static bool ShowWire {
            get => s_showWire ??= EditorPrefs.GetBool(IrocaPrefs.UvDetailWire, false);
            set {
                s_showWire = value;
                EditorPrefs.SetBool(IrocaPrefs.UvDetailWire, value);
            }
        }
        public static bool ShowOverlap {
            get => s_showOverlap ??= EditorPrefs.GetBool(IrocaPrefs.UvDetailOverlap, false);
            set {
                s_showOverlap = value;
                EditorPrefs.SetBool(IrocaPrefs.UvDetailOverlap, value);
            }
        }
        public static bool ShowHoverInfo {
            get => s_showHoverInfo ??= EditorPrefs.GetBool(IrocaPrefs.UvDetailHoverInfo, false);
            set {
                s_showHoverInfo = value;
                EditorPrefs.SetBool(IrocaPrefs.UvDetailHoverInfo, value);
            }
        }

        /// <summary>詳しい表示が効く状態か(開発者向けの設定と、島の表示が ON)。</summary>
        public static bool Available => IrocaWindow.ShowDeveloperSettings && MeshPartSection.ShowIslands;

        /// <summary>カーソルの下の情報を出すか(マウスの動きで描き直す必要があるか)。</summary>
        public static bool HoverActive => Available && ShowHoverInfo;

        private static Layers WantedLayers() {
            if (!Available) {
                return Layers.None;
            }
            var l = Layers.None;
            if (ShowFill) {
                l |= Layers.Fill;
            }
            if (ShowWire) {
                l |= Layers.Wire;
            }
            if (ShowOverlap) {
                l |= Layers.Overlap;
            }
            return l;
        }

        // 焼いた結果。chart と count はカーソルの下の情報に使う(画像を作った後も持つ)。
        private sealed class Baked {
            internal int w, h;
            internal Layers layers;
            internal NativeBuffer rgba;       // Color32 × w×h(テクスチャへ写したら手放す)
            internal NativeBuffer chart;      // int × w×h。島の通し番号(-1 = 島の外)
            internal NativeBuffer count;      // byte × w×h。その画素に乗る三角形の数(重なり。255 で止める)
            internal int[] chartSource;       // 島の通し番号 → メッシュ(ソース)の番号
            internal int[] chartLocal;        // 島の通し番号 → そのメッシュの中での島の番号

            internal void Release() {
                rgba?.Release();
                chart?.Release();
                count?.Release();
                rgba = chart = count = null;
            }
        }

        private Texture2D _texture;
        private Baked _shown;                 // いま表示している焼き(テクスチャと対)
        private object _shownFor;             // 焼いたときのメッシュの結果(同じものなら焼き直さない)
        private Task<Baked> _task;
        private object _taskFor;
        private int _taskW, _taskH;
        private Layers _taskLayers;
        private CancellationTokenSource _cts;

        /// <summary>
        /// 必要なら焼き直しを始め、焼けていれば重ねて描く。Repaint のときに呼ぶ。焼いている間は true を返す
        /// (呼び出し側が描き直しを頼む。背景のスレッドからはウィンドウを描き直せないため)。
        /// </summary>
        public bool Draw(Rect previewRect, MeshUvLocator.Result found, int texW, int texH) {
            var layers = WantedLayers();
            bool hover = HoverActive;
            if ((layers == Layers.None && !hover) || found == null || found.found.Count == 0 || texW <= 0 || texH <= 0) {
                if (layers == Layers.None && !hover) {
                    Release();   // 使わない間は持たない
                }
                return false;
            }
            Fit(texW, texH, out int w, out int h);

            bool upToDate = _shown != null && ReferenceEquals(_shownFor, found)
                            && _shown.w == w && _shown.h == h && _shown.layers == layers;
            if (!upToDate) {
                bool taskMatches = _task != null && ReferenceEquals(_taskFor, found)
                                   && _taskW == w && _taskH == h && _taskLayers == layers;
                if (!taskMatches) {
                    StartBake(found, w, h, layers);
                }
                if (_task.IsCompleted) {
                    TakeBake();
                }
            }

            if (_texture != null && _shown != null && layers != Layers.None) {
                GUI.DrawTexture(previewRect, _texture, ScaleMode.StretchToFill, true);
            }
            return _task != null && !_task.IsCompleted;
        }

        /// <summary>カーソルの下の島の情報を、カーソルのそばに出す。Repaint のときに、ほかの目印の後で呼ぶ。</summary>
        public void DrawHoverInfo(Rect previewRect, MeshUvLocator.Result found, Vector2 mouse) {
            if (!HoverActive || _shown == null || found == null || !previewRect.Contains(mouse)) {
                return;
            }
            var uv = PreviewCoords.ScreenToUv(mouse, previewRect);
            PreviewCoords.UvToPixel(uv.x, uv.y, _shown.w, _shown.h, out int x, out int y);
            int i = y * _shown.w + x;
            int c = ((int*)_shown.chart.Ptr)[i];
            int n = ((byte*)_shown.count.Ptr)[i];
            string text;
            if (c < 0 || c >= _shown.chartSource.Length) {
                text = Localization.UvDetailHoverNone;
            } else {
                int s = _shown.chartSource[c];
                string mesh = s >= 0 && s < found.found.Count ? found.found[s].source.name : "?";
                text = string.Format(Localization.UvDetailHoverFormat, mesh, _shown.chartLocal[c] + 1, n);
            }
            var content = TempContent.Of(text);
            var style = EditorStyles.helpBox;
            var size = style.CalcSize(content);
            // カーソルの右下に置き、枠からはみ出すなら左・上へ回す。
            var r = new Rect(mouse.x + 14f, mouse.y + 14f, size.x, size.y);
            if (r.xMax > previewRect.xMax) {
                r.x = mouse.x - 14f - size.x;
            }
            if (r.yMax > previewRect.yMax) {
                r.y = mouse.y - 14f - size.y;
            }
            EditorGUI.DrawRect(r, IrocaColors.WindowBackground);
            GUI.Label(r, content, style);
        }

        /// <summary>焼いたもの・作業中のものを手放す。</summary>
        public void Release() {
            _cts?.Cancel();
            _cts = null;
            DiscardTask();
            _shown?.Release();
            _shown = null;
            _shownFor = null;
            TextureSlot.Release(ref _texture);
        }

        private static void Fit(int texW, int texH, out int w, out int h) {
            float s = Mathf.Min(1f, (float)MaxSide / Mathf.Max(texW, texH));
            w = Mathf.Max(1, Mathf.RoundToInt(texW * s));
            h = Mathf.Max(1, Mathf.RoundToInt(texH * s));
        }

        private void StartBake(MeshUvLocator.Result found, int w, int h, Layers layers) {
            _cts?.Cancel();
            DiscardTask();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var sources = found.Sources();
            _taskFor = found;
            _taskW = w;
            _taskH = h;
            _taskLayers = layers;
            NativeMemoryLifecycle.WorkStarted();
            _task = Task.Run(() => {
                try {
                    return Bake(sources, w, h, layers, token);
                } finally {
                    NativeMemoryLifecycle.WorkEnded();
                }
            });
        }

        // 焼き終わったものを引き取り、テクスチャへ写す(メインスレッド)。失敗・取り消しなら何も出さない。
        private void TakeBake() {
            var task = _task;
            object forResult = _taskFor;
            _task = null;
            _taskFor = null;
            if (task.Status != TaskStatus.RanToCompletion || task.Result == null) {
                if (task.IsFaulted) {
                    Debug.LogWarning($"[Iroca] UV detail overlay failed: {task.Exception?.GetBaseException().Message}");
                }
                return;
            }
            var baked = task.Result;
            _shown?.Release();
            _shown = baked;
            _shownFor = forResult;
            TextureSlot.Resize(ref _texture, baked.w, baked.h, FilterMode.Point);
            var dst = _texture.GetRawTextureData<Color32>();
            long bytes = (long)baked.w * baked.h * 4;
            System.Buffer.MemoryCopy((void*)baked.rgba.Ptr,
                Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(dst), bytes, bytes);
            _texture.Apply(false, false);
            baked.rgba.Release();
            baked.rgba = null;
        }

        // 使わない焼きを捨てる(作っている途中のものは止められないので、終わってから手放す)。
        private void DiscardTask() {
            var task = _task;
            _task = null;
            _taskFor = null;
            task?.ContinueWith(t => {
                if (t.Status == TaskStatus.RanToCompletion) {
                    t.Result?.Release();
                }
            }, TaskScheduler.Default);
        }

        // ── 焼く(背景のスレッド。Unity の API は使わない) ──

        private static Baked Bake(List<UvChartSource> sources, int w, int h, Layers layers, CancellationToken token) {
            long n = (long)w * h;
            var baked = new Baked {
                w = w, h = h, layers = layers,
                rgba = new NativeBuffer(n * 4, NativeUsage.Other),
                chart = new NativeBuffer(n * 4, NativeUsage.Other),
                count = new NativeBuffer(n, NativeUsage.Other),
            };
            try {
                var rgba = (Color32*)baked.rgba.Ptr;
                var chart = (int*)baked.chart.Ptr;
                var count = (byte*)baked.count.Ptr;
                new System.Span<Color32>(rgba, (int)n).Clear();
                new System.Span<int>(chart, (int)n).Fill(-1);
                new System.Span<byte>(count, (int)n).Clear();

                var chartSource = new List<int>();
                var chartLocal = new List<int>();
                for (int s = 0; s < sources.Count; s++) {
                    var src = sources[s];
                    if (src?.uv == null || src.triangles == null || src.triangles.Length < 3) {
                        continue;
                    }
                    int[] local = UvChartMap.ComputeCharts(src.uv, src.triangles, out int chartCount);
                    int baseId = chartSource.Count;
                    for (int c = 0; c < chartCount; c++) {
                        chartSource.Add(s);
                        chartLocal.Add(c);
                    }
                    int triCount = src.triangles.Length / 3;
                    for (int t = 0; t < triCount; t++) {
                        if ((t & 1023) == 0) {
                            token.ThrowIfCancellationRequested();
                        }
                        if (!TriangleCorners(src, t, w, h, out var a, out var b, out var c3)) {
                            continue;
                        }
                        Rasterize(chart, count, w, h, a, b, c3, baseId + local[t]);
                    }
                }
                baked.chartSource = chartSource.ToArray();
                baked.chartLocal = chartLocal.ToArray();

                // 塗りと重なり。
                for (long i = 0; i < n; i++) {
                    int c = chart[i];
                    if (c < 0) {
                        continue;
                    }
                    int x = (int)(i % w), y = (int)(i / w);
                    if ((layers & Layers.Overlap) != 0 && count[i] >= 2) {
                        rgba[i] = ((x + y) / OverlapStripe) % 2 == 0
                            ? new Color32(255, 0, 255, OverlapAlpha)
                            : new Color32(80, 0, 80, OverlapAlpha);
                    } else if ((layers & Layers.Fill) != 0) {
                        rgba[i] = ChartColor(c);
                    }
                }

                // 三角形の線(塗りの上)。
                if ((layers & Layers.Wire) != 0) {
                    var wire = new Color32(255, 255, 255, WireAlpha);
                    foreach (var src in sources) {
                        if (src?.uv == null || src.triangles == null) {
                            continue;
                        }
                        int triCount = src.triangles.Length / 3;
                        for (int t = 0; t < triCount; t++) {
                            if ((t & 1023) == 0) {
                                token.ThrowIfCancellationRequested();
                            }
                            if (!TriangleCorners(src, t, w, h, out var a, out var b, out var c3)) {
                                continue;
                            }
                            Line(rgba, w, h, a, b, wire);
                            Line(rgba, w, h, b, c3, wire);
                            Line(rgba, w, h, c3, a, wire);
                        }
                    }
                }
                return baked;
            } catch {
                baked.Release();
                throw;
            }
        }

        private static Color32 ChartColor(int chart) {
            float hue = (chart * HueStep) % 1f;
            Color c = Color.HSVToRGB(hue, 0.75f, 1f);
            return new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), FillAlpha);
        }

        // 三角形の 3 頂点を画像の画素座標(下原点。Tiling / Offset を掛けた UV × 寸法)にする。添字が壊れていれば false。
        private static bool TriangleCorners(UvChartSource src, int t, int w, int h, out Vector2 a, out Vector2 b, out Vector2 c) {
            int ia = src.triangles[t * 3], ib = src.triangles[t * 3 + 1], ic = src.triangles[t * 3 + 2];
            var uv = src.uv;
            if ((uint)ia >= (uint)uv.Length || (uint)ib >= (uint)uv.Length || (uint)ic >= (uint)uv.Length) {
                a = b = c = default;
                return false;
            }
            a = ToPixel(src, uv[ia], w, h);
            b = ToPixel(src, uv[ib], w, h);
            c = ToPixel(src, uv[ic], w, h);
            return true;
        }

        private static Vector2 ToPixel(UvChartSource s, Vector2 uv, int w, int h)
            => new Vector2((uv.x * s.scale.x + s.offset.x) * w, (uv.y * s.scale.y + s.offset.y) * h);

        // 画素の中心が三角形の内側にある画素に、島の番号を書き、乗っている三角形の数を数える。
        // 向きはどちらでもよい(面積の符号でそろえる)。辺の上にちょうど中心が乗る画素は両側で数えうるが、まれなので受け入れる。
        private static void Rasterize(int* chart, byte* count, int w, int h, Vector2 a, Vector2 b, Vector2 c, int id) {
            float area = Edge(a, b, c);
            if (Mathf.Abs(area) < 1e-6f) {
                return;   // つぶれた三角形
            }
            float sign = area > 0f ? 1f : -1f;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            int x1 = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            int y1 = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
            for (int y = y0; y <= y1; y++) {
                for (int x = x0; x <= x1; x++) {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    if (Edge(a, b, p) * sign < 0f || Edge(b, c, p) * sign < 0f || Edge(c, a, p) * sign < 0f) {
                        continue;
                    }
                    long i = (long)y * w + x;
                    chart[i] = id;
                    if (count[i] < byte.MaxValue) {
                        count[i]++;
                    }
                }
            }
        }

        private static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        // 画像の中だけに線を引く(Bresenham)。
        private static void Line(Color32* rgba, int w, int h, Vector2 from, Vector2 to, Color32 color) {
            int x0 = Mathf.FloorToInt(from.x), y0 = Mathf.FloorToInt(from.y);
            int x1 = Mathf.FloorToInt(to.x), y1 = Mathf.FloorToInt(to.y);
            // 画像から大きく外れた線(0..1 の外の UV)は描かない(長い線を 1 画素ずつ辿らない)。
            if ((x0 < 0 && x1 < 0) || (y0 < 0 && y1 < 0) || (x0 >= w && x1 >= w) || (y0 >= h && y1 >= h)) {
                return;
            }
            int dx = System.Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -System.Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            int guard = dx - dy + 1;
            while (guard-- > 0) {
                if ((uint)x0 < (uint)w && (uint)y0 < (uint)h) {
                    rgba[(long)y0 * w + x0] = color;
                }
                if (x0 == x1 && y0 == y1) {
                    break;
                }
                int e2 = 2 * err;
                if (e2 >= dy) {
                    err += dy;
                    x0 += sx;
                }
                if (e2 <= dx) {
                    err += dx;
                    y0 += sy;
                }
            }
        }
    }
}
