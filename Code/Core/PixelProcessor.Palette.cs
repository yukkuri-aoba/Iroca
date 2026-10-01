// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    internal static partial class PixelProcessor
    {
        /// <summary>
        /// 元画素の「色の種類」の表。アバター用テクスチャは塗りが平坦で、色(RGBA)の種類が画素数の
        /// 0〜1% 程度しかない(4096² の実テクスチャ 10 枚で 1,369〜187,588 種)。色だけで決まる計算
        /// (HSV・ColorPick のマッチ・再着色)を色ごとに 1 回だけ行い、画素へは番号で配る。
        ///
        /// 同じ関数に同じ入力を与えるだけなので、画素ごとに計算した場合と結果はビット単位で同じ。
        /// 番号の振り方はハッシュ表への挿入順で実行ごとに変わりうるが、番号ごとの値は色だけで
        /// 決まるので出力には影響しない。**番号順に浮動小数を足し込むような使い方はしないこと**
        /// (足す順序が実行ごとに変わり、結果が揺れる)。
        ///
        /// 色の種類が多い画像(写真調・JPEG ノイズ)では表を引く手間が計算の節約を上回るので、
        /// 画素数の 1/<see cref="PaletteMaxColorsDivisor"/> を超えた時点で作るのをやめ、
        /// 呼び出し側は画素ごとの計算に戻る。
        /// </summary>
        private sealed class ColorPalette
        {
            internal int Count;
            internal Color32[] Colors;   // 番号 → 色
            internal int[] Index;        // 画素 → 番号(長さ len。s_intPool から借りる。Release で返す)
            internal float[] H, S, V;    // 番号 → Color.RGBToHSV((Color)色) の結果

            internal void Release()
            {
                if (Index != null) s_intPool.Return(Index);
                Index = null;
            }
        }

        // 色の種類の上限 = min(画素数 / これ, PaletteMaxColorsCap)。超えたら表を作らない。
        private const int PaletteMaxColorsDivisor = 4;
        private const int PaletteMaxColorsCap = 1 << 20;

        /// <summary>
        /// 元画素から色の表を作る。種類が上限を超えた・キャンセルされたときは null(呼び出し側は
        /// 画素ごとの計算に戻る)。
        /// </summary>
        private static ColorPalette TryBuildPalette(Color32[] pixels, int w, int h, ParallelOptions po,
            SubPhaseClock sub = null)
        {
            int len = w * h;
            int maxColors = Math.Min(PaletteMaxColorsCap, Math.Max(256, len / PaletteMaxColorsDivisor));
            // 開番地法のハッシュ表。容量は上限の 2 倍以上(充填率 1/2 以下)にして探索を必ず終わらせる。
            int bits = 1;
            while ((1 << bits) < maxColors * 2) bits++;
            int capacity = 1 << bits;
            int mask = capacity - 1;
            int shift = 32 - bits;
            // 0 = 空き、それ以外 = RGBA の 32bit 値 + 1。
            var slots = new long[capacity];
            int[] index = s_intPool.Rent(len);
            int inserted = 0;
            int overflow = 0;

            // 1) 画素ごとに表の位置(スロット)を求めて index に書く。隣の画素と同じ色が続くことが
            //    多いので、直前の色と同じなら表を引かない。
            Parallel.For(0, h, po, (y, state) =>
            {
                if (Volatile.Read(ref overflow) != 0) { state.Stop(); return; }
                int row = y * w;
                uint lastKey = 0;
                int lastSlot = -1;
                for (int x = 0; x < w; x++)
                {
                    Color32 c = pixels[row + x];
                    uint key = (uint)c.r | ((uint)c.g << 8) | ((uint)c.b << 16) | ((uint)c.a << 24);
                    if (lastSlot >= 0 && key == lastKey) { index[row + x] = lastSlot; continue; }
                    long want = (long)key + 1;
                    int slot = (int)((key * 2654435761u) >> shift);
                    while (true)
                    {
                        long cur = Volatile.Read(ref slots[slot]);
                        if (cur == want) break;
                        if (cur == 0)
                        {
                            long prev = Interlocked.CompareExchange(ref slots[slot], want, 0);
                            if (prev == 0)
                            {
                                if (Interlocked.Increment(ref inserted) > maxColors)
                                {
                                    Volatile.Write(ref overflow, 1);
                                    state.Stop();
                                    return;
                                }
                                break;
                            }
                            if (prev == want) break;
                        }
                        slot = (slot + 1) & mask;
                    }
                    index[row + x] = slot;
                    lastKey = key;
                    lastSlot = slot;
                }
            });
            sub?.Mark(SpPaletteHash);   // 種類が多すぎて作るのをやめた分もここに計上する
            if (overflow != 0 || po.CancellationToken.IsCancellationRequested)
            {
                s_intPool.Return(index);
                po.CancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            // 2) 使われたスロットに番号を振り、色と HSV を求める(種類数ぶんだけ)。
            int count = inserted;
            var slotToId = new int[capacity];
            var colors = new Color32[count];
            int n = 0;
            for (int s = 0; s < capacity; s++)
            {
                long v = slots[s];
                if (v == 0) continue;
                uint key = (uint)(v - 1);
                slotToId[s] = n;
                colors[n++] = new Color32((byte)key, (byte)(key >> 8), (byte)(key >> 16), (byte)(key >> 24));
            }
            var hh = new float[count];
            var ss = new float[count];
            var vv = new float[count];
            ForEachPaletteChunk(count, po, (k0, k1) =>
            {
                for (int k = k0; k < k1; k++)
                    Color.RGBToHSV((Color)colors[k], out hh[k], out ss[k], out vv[k]);
            });

            sub?.Mark(SpPaletteIds);
            // 3) スロット番号を色番号へ置き換える。
            Parallel.For(0, h, po, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++) index[row + x] = slotToId[index[row + x]];
            });

            sub?.Mark(SpPaletteRemap);
            return new ColorPalette { Count = count, Colors = colors, Index = index, H = hh, S = ss, V = vv };
        }

        /// <summary>色番号 [0,count) を固定幅のチャンクに分けて並列に処理する。</summary>
        private static void ForEachPaletteChunk(int count, ParallelOptions po, Action<int, int> body)
        {
            const int Chunk = 4096;
            int chunks = (count + Chunk - 1) / Chunk;
            Parallel.For(0, chunks, po, c =>
            {
                int k0 = c * Chunk;
                body(k0, Math.Min(count, k0 + Chunk));
            });
        }
    }
}
