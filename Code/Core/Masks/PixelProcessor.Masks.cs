// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // マスクの契約(除外・含める)。製品の約束で、選択の作りを替えても変えない。選択の強度に除外を掛け直し
    // (ApplyMaskContract)、含めるを足す(ApplyInclude)。再着色の段も、除外の画素を読まないためにここの判定を使う。
    // 除外は 3 か所で効く: 照合(除外の画素に 0 を書く)、連結成分の直前(掛け直す。線の向こうの成分を切る壁)、
    // 最後(ApplyMaskContract。ぼかしのはみ出しを消す)。閉領域の復帰は除外を通れる(2026-11-11 に壁を足した)。
    internal static unsafe partial class PixelProcessor {
        /// <summary>
        /// マスクの契約(製品の約束)の 1 つ目: 選択の強度に、除外マスクを掛け直す(ぼかしが除外の画素へはみ出すことがある)。
        /// ProcessPixelsArrayCore から中身を書き換えずに切り出したもの(0.4.0 の計画の 3-7a)。含めるマスクは
        /// <see cref="ApplyInclude"/> が後で足す。
        /// </summary>
        private static void ApplyMaskContract(RunContext run, ColorZone zone, float* strength, ulong* zoneMask) {
            int w = run.W, h = run.H, len = run.Len;
            var po = run.Po;
            ulong* commonMask = run.CommonBits;
            int maskW = run.MaskW, maskH = run.MaskH;
            int[] maskColOf = run.MaskColOf, maskRowBase = run.MaskRowBase;
            var debug = run.Debug;
            var _sub = run.Sub;

            // 3. 除外マスクを再適用：ブラーが除外ピクセルにはみ出す可能性がある
            if (commonMask != null || zoneMask != null) {
                ReapplyExclusion(strength, w, h, maskColOf, maskRowBase, maskW, maskH, commonMask, zoneMask, po);
                debug?.RecordStage(zone.id, DebugStages.MaskReapply, ToManaged(strength, len), w, h);
            }
            _sub.Mark(SpBlurReapply);
        }

        /// <summary>
        /// マスクの契約の 2 つ目: 含めるマスクの画素を全強度(1)で選択へ足す。includedPx には除外優先が焼き込み済みなので
        /// 「除外が勝つ」。後段のぼかしを通らないので、マスク解像度のハードエッジになる(除外マスクと同じ WYSIWYG 契約)。
        /// 再着色では、含める前の強さを読む段(無彩の固め・デコンタミ・基準色・彩度の分位・領域 L)の後に呼ぶ
        /// (RecolorStatsStage)。それらの段が含める前と同じ強さを読むので、統計は含める前と同じ画素から決まり、範囲の外の
        /// 色が動かない。含める前に強さを 1 にしていたころは、色でもう選ばれていた画素まで統計から外す必要があり
        /// (外さないと、足した画素が統計を汚す)、範囲が塗れていた画素の大半を覆うと基準が残りわずかの画素から決まって、
        /// 範囲の外まで色がずれた(2026-11-22)。
        /// デコンタミが AA 縁として分解した含める画素は、その印(aaMask。null 可)を下ろす(全強度で塗る画素なので、
        /// 縁の合成をすると、範囲の中に元の背景色の混ざった輪が残る)。
        /// </summary>
        /// <param name="includedBox">含める画素の外接矩形(includedPx を展開したときに取ったもの。含める画素はこの外に無い)。</param>
        private static void ApplyInclude(RunContext run, ColorZone zone, float* strength, bool* includedPx,
            in SelectionCache.PostBox includedBox, bool* aaMask) {
            if (includedPx == null) {
                return;
            }
            ForceIncluded(strength, includedPx, aaMask, run.W, includedBox, run.Po);
            run.Debug?.RecordStage(zone.id, DebugStages.MaskReapply, ToManaged(strength, run.Len), run.W, run.H);
        }

        /// <summary>
        /// マスク(除外・含める)の判定表を作る。作業座標の列 x → マスクの列(mx)と、行 y → マスクの行の先頭(my·maskW)。
        /// 作業画像(w×h)はテクスチャ全体で、マスクの解像度は
        /// テクスチャと違ってよい(整数の拡縮で対応するマスク画素へ、端は切り詰め)。
        /// 以前は画素ごとにこの割り算をしていた(同じ式なので判定は同じ)。
        /// </summary>
        private static void BuildMaskIndexMap(int w, int h,
            int maskW, int maskH, out int[] colOf, out int[] rowBase) {
            colOf = new int[w];
            for (int x = 0; x < w; x++) {
                colOf[x] = Mathf.Clamp(x * maskW / w, 0, maskW - 1);
            }
            rowBase = new int[h];
            for (int y = 0; y < h; y++) {
                rowBase[y] = Mathf.Clamp(y * maskH / h, 0, maskH - 1) * maskW;
            }
        }

        /// <summary>作業座標 (x, y) が除外(共通 ∪ ゾーン)か。表は BuildMaskIndexMap(無ければ除外なし)。</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsExcludedAt(int[] colOf, int[] rowBase, int x, int y,
            ulong* commonMask, ulong* zoneMask) {
            if (colOf == null) {
                return false;
            }
            int idx = rowBase[y] + colOf[x];
            return MaskSnapshot.GetBit(commonMask, idx) || MaskSnapshot.GetBit(zoneMask, idx);
        }

        /// <summary>
        /// 作業座標 (x, y) がゾーンの含めるマスク(1=強制的に選択へ含める)に入るか。座標の対応は
        /// 除外と同じ表(BuildMaskIndexMap。無ければ含めるなし)。除外優先は呼び出し側が組む。
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsIncludedAt(int[] colOf, int[] rowBase, int x, int y, ulong* includeMask)
            => colOf != null && MaskSnapshot.GetBit(includeMask, rowBase[y] + colOf[x]);

        /// <summary>
        /// 除外(共通 ∪ ゾーン)の画素の strength を 0 にする。表は BuildMaskIndexMap(無ければ何もしない)。
        /// 除外の立っていないマスクの行に当たる作業行は読まない(選択キャッシュのヒットでも毎回走るため)。
        /// </summary>
        private static void ReapplyExclusion(float* strength, int w, int h, int[] colOf, int[] rowBase,
            int maskW, int maskH, ulong* commonMask, ulong* zoneMask, ParallelOptions po) {
            if (colOf == null) {
                return;
            }
            var rows = new bool[maskH];
            int words = MaskWordCount(maskW, maskH);
            for (int wi = 0; wi < words; wi++) {
                if ((MaskWord(commonMask, wi) | MaskWord(zoneMask, wi)) != 0UL) {
                    MarkMaskRows(rows, wi, maskW, maskH);
                }
            }
            Parallel.For(0, h, po, y => {
                if (!rows[rowBase[y] / maskW]) {
                    return;
                }
                int rowOff = y * w;
                for (int x = 0; x < w; x++) {
                    if (IsExcludedAt(colOf, rowBase, x, y, commonMask, zoneMask)) {
                        strength[rowOff + x] = 0f;
                    }
                }
            });
        }

        private static int MaskWordCount(int maskW, int maskH) => (int)(((long)maskW * maskH + 63) >> 6);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static ulong MaskWord(ulong* packed, int wi) => packed != null ? packed[wi] : 0UL;

        // 語 wi(画素 wi*64 .. wi*64+63)が触れるマスクの行に印をつける(語の中のどこが立っているかは見ない。
        // 印が多すぎても、印のある行は画素ごとに読み直すので結果は変わらない)。
        private static void MarkMaskRows(bool[] rows, int wi, int maskW, int maskH) {
            long first = (long)wi << 6;
            int r0 = (int)(first / maskW);
            int r1 = (int)Math.Min(maskH - 1, (first + 63) / maskW);
            for (int r = r0; r <= r1; r++) {
                rows[r] = true;
            }
        }

        /// <summary>
        /// 矩形 [x0, x1] × [y0, y1](両端含む)の除外フラグ(IsExcludedAt)を dst に書く。範囲外は触らない。
        /// デコンタミ・混色帯・無彩フチ消しの 3 か所が、それぞれ自分の読む範囲だけをこれで埋める。
        /// </summary>
        private static void FillExcludedRect(bool* dst, int w, int x0, int y0, int x1, int y1,
            int[] colOf, int[] rowBase, ulong* commonMask, ulong* zoneMask, ParallelOptions po) {
            Parallel.For(y0, y1 + 1, po, y => {
                int rowOff = y * w;
                for (int x = x0; x <= x1; x++) {
                    dst[rowOff + x] = IsExcludedAt(colOf, rowBase, x, y, commonMask, zoneMask);
                }
            });
        }

        /// <summary>
        /// 含める画素(included[i] が true)の strength を 1(full strength)にし、aaMask(null 可)の印を下ろす。
        /// 他の画素は触らない。
        /// 読むのは含める画素の外接矩形 box の中だけ(外に含める画素は無いので、全画面を読むのと同じ結果。
        /// 含めるマスクのあるゾーンでは、色だけ変えた 1 手でも毎回ここを通る)。box が空なら何もしない。
        /// </summary>
        private static void ForceIncluded(float* strength, bool* included, bool* aaMask, int w,
            in SelectionCache.PostBox box, ParallelOptions po) {
            if (!box.Has) {
                return;
            }
            int x0 = box.MinX, x1 = box.MaxX;
            Parallel.For(box.MinY, box.MaxY + 1, po, y => {
                int rowOff = y * w;
                for (int x = x0; x <= x1; x++) {
                    int i = rowOff + x;
                    if (included[i]) {
                        strength[i] = 1f;
                        if (aaMask != null) {
                            aaMask[i] = false;
                        }
                    }
                }
            });
        }
    }
}
