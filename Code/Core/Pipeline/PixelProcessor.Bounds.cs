// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // 強度の範囲(バウンディングボックス)。選択の後段と再着色の段が、走査を選択の画素の周りに限るために使う。
    internal static unsafe partial class PixelProcessor {
        /// <summary>
        /// strength &gt; thr の画素を囲むバウンディングボックス(min/max)を求める。px を渡すと、不透明(α&gt;=128)な画素だけを
        /// 数える(連結成分アンカリング・成分別 L マップが「連結の述語」と同じ条件で使う)。px が null なら強度だけを見る
        /// (後段パス(穴埋め/境界回復/ブラー)と再着色のループを、実マッチ範囲に限るために使う)。
        /// 行ごとに独立に求めた min/max をマージするだけなので、逐次走査と結果は同一(順序非依存)。
        /// 4K では全画素走査なので、単スレッドのままだと範囲の算出自体が無視できない。
        /// </summary>
        /// <returns>該当画素が 1 つ以上あれば true(false のとき範囲は空で、後段は何もしない)。</returns>
        private static bool TryComputeMatchedBBox(float* strength, Color32* px, int w, int h, float thr,
            out int minX, out int minY, out int maxX, out int maxY, CancellationToken ct = default)
            => TryComputeMatchedBBox(strength, px, w, h, thr, FullBox(w, h),
                out minX, out minY, out maxX, out maxY, ct);

        /// <summary>画像全体を表す範囲。</summary>
        private static SelectionCache.PostBox FullBox(int w, int h) =>
            new SelectionCache.PostBox { Has = true, MinX = 0, MinY = 0, MaxX = w - 1, MaxY = h - 1 };

        /// <summary>2 つの範囲を囲む範囲(片方が空ならもう片方)。</summary>
        private static SelectionCache.PostBox UnionBox(in SelectionCache.PostBox a, in SelectionCache.PostBox b) {
            if (!a.Has) {
                return b;
            }
            if (!b.Has) {
                return a;
            }
            return new SelectionCache.PostBox {
                Has = true,
                MinX = Math.Min(a.MinX, b.MinX),
                MinY = Math.Min(a.MinY, b.MinY),
                MaxX = Math.Max(a.MaxX, b.MaxX),
                MaxY = Math.Max(a.MaxY, b.MaxY),
            };
        }

        /// <summary>
        /// <see cref="TryComputeMatchedBBox(float*, Color32*, int, int, float, out int, out int, out int, out int, CancellationToken)"/> の、
        /// 走査を <paramref name="scan"/> の中に限る版。呼び出し側は「該当する画素は scan の外に無い」と分かっているときに
        /// 渡す(マスクの契約の後の <see cref="ZoneState.Scan"/>)。その前提のもとで、画像全体を走査した結果と同じ。
        /// scan が空(Has = false)なら走査せずに false。
        /// </summary>
        private static bool TryComputeMatchedBBox(float* strength, Color32* px, int w, int h, float thr,
            in SelectionCache.PostBox scan,
            out int minX, out int minY, out int maxX, out int maxY, CancellationToken ct = default) {
            int lminX = w, lmaxX = -1, lminY = h, lmaxY = -1;
            if (!scan.Has) {
                minX = lminX; minY = lminY; maxX = lmaxX; maxY = lmaxY;
                return false;
            }
            int sx0 = scan.MinX, sx1 = scan.MaxX;
            object gate = new object();
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            Parallel.For(scan.MinY, scan.MaxY + 1, po,
                () => (minX: w, maxX: -1, minY: h, maxY: -1),
                (y, _, loc) => {
                    // 行の左端から最初の該当まで、右端から最後の該当までだけ読む(間は範囲に影響しない)。
                    // α を見るのは強度が該当した画素だけ(px が null なら見ない)。
                    int rb = y * w;
                    int x0 = sx0;
                    while (x0 <= sx1 && !(strength[rb + x0] > thr && (px == null || px[rb + x0].a >= OpaqueAlphaMin))) {
                        x0++;
                    }
                    if (x0 > sx1) {
                        return loc;
                    }
                    int x1 = sx1;
                    while (!(strength[rb + x1] > thr && (px == null || px[rb + x1].a >= OpaqueAlphaMin))) {
                        x1--;
                    }
                    if (x0 < loc.minX) {
                        loc.minX = x0;
                    }
                    if (x1 > loc.maxX) {
                        loc.maxX = x1;
                    }
                    if (y < loc.minY) {
                        loc.minY = y;
                    }
                    if (y > loc.maxY) {
                        loc.maxY = y;
                    }
                    return loc;
                },
                loc => {
                    lock (gate) {
                        if (loc.minX < lminX) {
                            lminX = loc.minX;
                        }
                        if (loc.maxX > lmaxX) {
                            lmaxX = loc.maxX;
                        }
                        if (loc.minY < lminY) {
                            lminY = loc.minY;
                        }
                        if (loc.maxY > lmaxY) {
                            lmaxY = loc.maxY;
                        }
                    }
                });
            minX = lminX; minY = lminY; maxX = lmaxX; maxY = lmaxY;
            return lmaxX >= 0;
        }

        /// <summary>強度だけを見る版(α を見ない)。画像全体を走査する。</summary>
        private static bool TryComputeStrengthBBox(float* strength, int w, int h, float thr,
            out int minX, out int minY, out int maxX, out int maxY, CancellationToken ct = default)
            => TryComputeMatchedBBox(strength, null, w, h, thr, FullBox(w, h), out minX, out minY, out maxX, out maxY, ct);

        /// <summary>強度だけを見る版(α を見ない)の、走査を <paramref name="scan"/> の中に限る版。</summary>
        private static bool TryComputeStrengthBBox(float* strength, int w, int h, float thr,
            in SelectionCache.PostBox scan,
            out int minX, out int minY, out int maxX, out int maxY, CancellationToken ct = default)
            => TryComputeMatchedBBox(strength, null, w, h, thr, scan, out minX, out minY, out maxX, out maxY, ct);
    }
}
