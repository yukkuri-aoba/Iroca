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

        /// <summary>bool[](true=除外)を 1bit/画素の ulong[] にパックする。null は null を返す。</summary>
        public static ulong[] Pack(bool[] mask)
        {
            if (mask == null) return null;
            var packed = new ulong[(mask.Length + 63) >> 6];
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) packed[i >> 6] |= 1UL << (i & 63);
            return packed;
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
