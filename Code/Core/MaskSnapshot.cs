// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// バックグラウンド処理に渡すためのマスク一式のイミュータブルスナップショット。
    /// 配列は deep clone 済み（呼び出し側の書き換えと競合しない）。
    /// </summary>
    internal class MaskSnapshot
    {
        // 1 画素 = 1 bit のビットパック表現(true=除外)。bool[] を deep clone すると 4K で 16.7MB/枚に
        // なりプレビュー再生成・ペイントのたびに GC を圧迫するため、スナップショットは 1/8 サイズの
        // ulong[] で保持する。idx 番目の画素は (arr[idx>>6] >> (idx&63)) & 1。範囲は width*height。
        // 注: 編集中のマスク(MaskWorkspace)は同じ並びのヒープの外のマスク(NativeBitMask)、保存形式(MaskFileStore)は RLE。
        //     ここはスレッドへ渡すスナップショットの表現(編集中のマスクから語ごとに写す)。
        public ulong[] common;
        public int width;
        public int height;
        public Dictionary<string, ulong[]> zones;
        // ゾーン別「含める」マスク(true=強制的に選択へ含める)。key = ColorZone.id。
        // 除外(common/zones)と違い共通版は持たない(「どのゾーンの選択に含めるか」が
        // 定まらないため、含めるは常にゾーン単位)。除外と重なった画素は除外が勝つ。
        public Dictionary<string, ulong[]> zoneIncludes;

        /// <summary>
        /// len 画素ぶんの 1 画素 1 ビットの配列(全部 0)。詰めた形の配列はここでだけ作る(保存形式からの直接の展開
        /// <see cref="MaskRle.DecodePacked"/> も使う。ヒープの外の NativeBitMask へ置き換えるときの 1 か所。計画の 2-3)。
        /// </summary>
        internal static ulong[] NewBits(int len) => new ulong[(len + 63) >> 6];

        /// <summary>bool[](true=除外)を 1bit/画素の ulong[] にパックする。null は null を返す。</summary>
        public static ulong[] Pack(bool[] mask)
        {
            if (mask == null) return null;
            var packed = NewBits(mask.Length);
            int words = packed.Length;
            // 64 画素 = 1 語ごとに独立なので、大きいマスクは語の範囲に分けて並列に詰める(どの語も同じ値になる)。
            // プレビューは生成のたびにメインスレッドで全マスクを詰める(BuildSnapshot)ので、ここで待つ時間が
            // そのまま操作の遅れになる(4K で 1 枚 1677 万画素)。
            int dop = mask.Length >= PackParallelMinPixels ? PixelProcessor.GetMaxParallelism() : 1;
            if (dop <= 1)
            {
                PackWords(mask, packed, 0, words);
                return packed;
            }
            int chunk = (words + dop - 1) / dop;
            Parallel.For(0, dop, new ParallelOptions { MaxDegreeOfParallelism = dop }, p =>
            {
                int w0 = p * chunk, w1 = Math.Min(words, w0 + chunk);
                if (w0 < w1) PackWords(mask, packed, w0, w1);
            });
            return packed;
        }

        // これ未満の画素数のマスクは並列にしない(スレッドの起動の方が高い)。
        private const int PackParallelMinPixels = 1 << 18;

        // 語 [w0, w1) を詰める。語 wi は画素 wi*64 .. wi*64+63 で、画素 i は語の (i & 63) ビット目。
        private static void PackWords(bool[] mask, ulong[] packed, int w0, int w1)
        {
            int n = mask.Length;
            for (int wi = w0; wi < w1; wi++)
            {
                int i0 = wi << 6, i1 = Math.Min(n, i0 + 64);
                ulong v = 0UL;
                for (int i = i0; i < i1; i++)
                    if (mask[i]) v |= 1UL << (i - i0);
                packed[wi] = v;
            }
        }

        // 詰めたマスクに立っているビットがあるか(null は false)。
        internal static bool AnyBitSet(ulong[] packed)
        {
            if (packed == null) return false;
            for (int i = 0; i < packed.Length; i++) if (packed[i] != 0UL) return true;
            return false;
        }

        /// <summary>
        /// 作業用の bool[] マスク一式からスナップショットを組み立てる。編集画面(MaskPaintView)と、
        /// 保存済みマスクから作り直す経路(<see cref="MaskStateCodec.ToSnapshot"/>)が同じ規則を
        /// 通るよう、組み立てはここにだけ書く。
        /// 全 false の含めるマスクは載せない。載せると処理側が空の includedPx 展開(全画素ループ)を
        /// 毎回行い、選択キャッシュキーも「含めるなし」と別になってしまう(出力は同じなのにミスが増える)。
        /// </summary>
        public static MaskSnapshot FromBuffers(int width, int height, bool[] common,
            IEnumerable<KeyValuePair<string, bool[]>> zones,
            IEnumerable<KeyValuePair<string, bool[]>> includes)
        {
            var snap = new MaskSnapshot
            {
                width = width,
                height = height,
                zones = new Dictionary<string, ulong[]>()
            };
            snap.common = Pack(common);   // Pack(null) は null
            if (zones != null)
            {
                foreach (var kv in zones)
                {
                    if (kv.Value == null) continue;
                    snap.zones[kv.Key] = Pack(kv.Value);
                }
            }
            if (includes != null)
            {
                foreach (var kv in includes)
                {
                    if (kv.Value == null) continue;
                    // 全 false かは詰めた後の語で見る(bool[] を先頭から 1 つずつ読み直さない)。
                    var packed = Pack(kv.Value);
                    if (!AnyBitSet(packed)) continue;
                    if (snap.zoneIncludes == null)
                        snap.zoneIncludes = new Dictionary<string, ulong[]>();
                    snap.zoneIncludes[kv.Key] = packed;
                }
            }
            return snap;
        }

        /// <summary>true の画素が 1 つでもあるか(null は false)。</summary>
        public static bool AnyTrue(bool[] mask)
        {
            if (mask == null) return false;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) return true;
            return false;
        }

        /// <summary>パック済みマスクの idx 番目ビットを読む(null・範囲外は false)。</summary>
        public static bool GetBit(ulong[] packed, int idx)
        {
            if (packed == null) return false;
            int word = idx >> 6;
            if (word < 0 || word >= packed.Length) return false;
            return (packed[word] & (1UL << (idx & 63))) != 0UL;
        }
    }
}
