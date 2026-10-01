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
        // 注: 作業用マスク(MaskPaintView.exclusionMask / zoneMasks)や保存形式(MaskFileStore)は
        //     bool[] / RLE のまま。ここはスレッドへ渡すスナップショットの内部表現のみを packed 化する。
        public ulong[] common;
        public int width;
        public int height;
        public Dictionary<string, ulong[]> zones;
        // ゾーン別「含める」マスク(true=強制的に選択へ含める)。key = ColorZone.id。
        // 除外(common/zones)と違い共通版は持たない(「どのゾーンの選択に含めるか」が
        // 定まらないため、含めるは常にゾーン単位)。除外と重なった画素は除外が勝つ。
        public Dictionary<string, ulong[]> zoneIncludes;

        /// <summary>bool[](true=除外)を 1bit/画素の ulong[] にパックする。null は null を返す。</summary>
        public static ulong[] Pack(bool[] mask)
        {
            if (mask == null) return null;
            var packed = new ulong[(mask.Length + 63) >> 6];
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) packed[i >> 6] |= 1UL << (i & 63);
            return packed;
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
                    if (kv.Value == null || !AnyTrue(kv.Value)) continue;
                    if (snap.zoneIncludes == null)
                        snap.zoneIncludes = new Dictionary<string, ulong[]>();
                    snap.zoneIncludes[kv.Key] = Pack(kv.Value);
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
