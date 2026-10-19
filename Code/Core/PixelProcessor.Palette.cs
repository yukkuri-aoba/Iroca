// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
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
            internal int[] Index;        // 画素 → 番号(長さ len。s_paletteIndexPool から借りる。Release で返す)
            internal float[] H, S, V;    // 番号 → Color.RGBToHSV((Color)色) の結果
            // 番号 → RgbToOklab(byte 版) の L と、C = sqrt(a²+b²)。EnsureOklab で初めて要るときに作る。
            internal float[] OkL, OkC;
            // 色の表のキャッシュ(GetOrBuildPalette)が持つ表。複数の処理から同時に読まれるので、
            // Release で Index をプールへ返さない(キャッシュから外れたら GC に任せる)。
            internal bool Shared;
            private readonly object _oklabLock = new object();

            /// <summary>
            /// OkLab の表を作る(呼び出し側スレッドから、並列ループの外で呼ぶこと)。表は複数の処理から
            /// 同時に使われうるので、作るのは 1 回だけにし、L → C の順に置いて C が見えたら両方揃っているとする。
            /// </summary>
            internal void EnsureOklab(ParallelOptions po)
            {
                if (Volatile.Read(ref OkC) != null) return;
                lock (_oklabLock)
                {
                    if (OkC != null) return;
                    var l = new float[Count];
                    var c = new float[Count];
                    var colors = Colors;
                    ForEachPaletteChunk(Count, po, (k0, k1) =>
                    {
                        for (int k = k0; k < k1; k++)
                        {
                            RgbToOklab(colors[k].r, colors[k].g, colors[k].b, out float L, out float a, out float b);
                            l[k] = L;
                            c[k] = Mathf.Sqrt(a * a + b * b);
                        }
                    });
                    OkL = l;
                    Volatile.Write(ref OkC, c);
                }
            }

            internal void Release()
            {
                if (Shared) return;
                if (Index != null) s_paletteIndexPool.Return(Index);
                Index = null;
            }
        }

        // 色の表を元画素の中身ごとに使い回す(プレビューは同じ元画像を編集のたびに処理するので、毎回
        // 作り直さない)。鍵は寸法と全画素の 64bit ハッシュ。種類が多すぎて作らなかった結果(null)も覚える。
        // 表は色だけで決まり、番号は出力に影響しない(上の注意)ので、使い回しても出力はビット単位で同じ。
        // 2 枠 = プレビューの縮小段とフル解像度段が交互に来ても互いを追い出さない。
        // ただし大きい画像の表(画素 → 番号だけで 4K なら 64MB)は 1 枚までにする。別のテクスチャやシーン反映の
        // 処理が走ると大きい表が 2 枚残り、Unity の Mono はヒープを縮めないので、その分がずっとエディタの
        // メモリになっていた。大きい表を作り直すのは別の画像に切り替わったときだけ。
        private const int PaletteCacheSize = 2;
        private const int PaletteLargeLen = 1 << 20;   // これを超える画素数の表は 1 枚まで(縮小段 512² は小さい側)
        private sealed class PaletteCacheEntry
        {
            internal int W, H;
            internal ulong Hash;
            internal ColorPalette Palette;
        }
        private static readonly PaletteCacheEntry[] s_paletteCache = new PaletteCacheEntry[PaletteCacheSize];
        private static readonly object s_paletteCacheLock = new object();

        /// <summary>色の表をキャッシュから取り出す。無ければ作って入れる(TryBuildPalette と同じく null もありうる)。</summary>
        private static ColorPalette GetOrBuildPalette(Color32[] pixels, int w, int h, ParallelOptions po,
            SubPhaseClock sub = null)
        {
            ulong hash = HashPixels(pixels, w * h, po);
            lock (s_paletteCacheLock)
            {
                for (int k = 0; k < PaletteCacheSize; k++)
                {
                    var e = s_paletteCache[k];
                    if (e == null || e.W != w || e.H != h || e.Hash != hash) continue;
                    for (int m = k; m > 0; m--) s_paletteCache[m] = s_paletteCache[m - 1];
                    s_paletteCache[0] = e;
                    sub?.Mark(SpPaletteHash);   // 当たったときはハッシュを取った時間だけ
                    return e.Palette;
                }
            }
            var built = TryBuildPalette(pixels, w, h, po, sub);
            if (built != null) built.Shared = true;
            lock (s_paletteCacheLock)
            {
                // 新しい表を先頭に置き、残りを古い順に詰める。大きい表どうしは新しい方だけ残す
                // (追い出した表を使用中の処理があっても、Shared なので Index はプールへ返らず壊れない)。
                bool newLarge = w * h > PaletteLargeLen;
                var next = new PaletteCacheEntry[PaletteCacheSize];
                next[0] = new PaletteCacheEntry { W = w, H = h, Hash = hash, Palette = built };
                int filled = 1;
                for (int k = 0; k < PaletteCacheSize && filled < PaletteCacheSize; k++)
                {
                    var e = s_paletteCache[k];
                    if (e == null) continue;
                    if (newLarge && e.W * e.H > PaletteLargeLen && e.Palette != null) continue;
                    next[filled++] = e;
                }
                Array.Copy(next, s_paletteCache, PaletteCacheSize);
            }
            return built;
        }

        /// <summary>
        /// 画素(RGBA)全体の 64bit ハッシュ。塊ごとに並列に混ぜ、塊のハッシュを順に畳む(実行ごとに同じ値)。
        /// 1 画素の違いでも塊のハッシュは必ず変わる(各段が全単射)。
        /// </summary>
        private static ulong HashPixels(Color32[] px, int len, ParallelOptions po)
        {
            const int Chunk = 1 << 16;
            int n = (len + Chunk - 1) / Chunk;
            var part = new ulong[n];
            Parallel.For(0, n, po, c =>
            {
                int i0 = c * Chunk, i1 = Math.Min(len, i0 + Chunk);
                ulong acc = 0x9E3779B97F4A7C15UL * (ulong)(c + 1);
                for (int i = i0; i < i1; i++)
                {
                    Color32 p = px[i];
                    ulong key = (uint)p.r | ((uint)p.g << 8) | ((uint)p.b << 16) | ((uint)p.a << 24);
                    acc ^= key * 0xC2B2AE3D27D4EB4FUL;
                    acc = ((acc << 31) | (acc >> 33)) * 0x9E3779B97F4A7C15UL;
                }
                part[c] = acc;
            });
            ulong hsh = (ulong)len * 0x165667B19E3779F9UL;
            for (int c = 0; c < n; c++)
            {
                hsh ^= part[c] * 0xC2B2AE3D27D4EB4FUL;
                hsh = ((hsh << 27) | (hsh >> 37)) * 0x9E3779B97F4A7C15UL + 0x85EBCA77C2B2AE63UL;
            }
            hsh ^= hsh >> 33; hsh *= 0xFF51AFD7ED558CCDUL;
            hsh ^= hsh >> 33; hsh *= 0xC4CEB9FE1A85EC53UL;
            hsh ^= hsh >> 33;
            return hsh;
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
            // ハッシュ表とスロット → 番号の対応(4K で long 2^21 + int 2^21 = 24MB)は作るたびに確保する。
            // 表はキャッシュから引くので作り直すのは新しい画像のときだけで、使い回しのために常駐させると、
            // Mono では縮まないヒープにその分が残り続ける。
            long[] slots = new long[capacity];
            int[] index = s_paletteIndexPool.Rent(len);
            // 表に渡すまでは、途中で抜けたとき(種類が多すぎた・キャンセル)も借りたものを必ず返す。
            ColorPalette built = null;
            try
            {
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
                    po.CancellationToken.ThrowIfCancellationRequested();
                    return null;
                }

                // 2) 使われたスロットに番号を振り、色と HSV を求める(種類数ぶんだけ)。
                //    slotToId は使われたスロットの位置だけ書いて読むので、0 で埋めなくてよい。
                int count = inserted;
                int[] slotToId = new int[capacity];
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
                var slotToIdL = slotToId;
                Parallel.For(0, h, po, y =>
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++) index[row + x] = slotToIdL[index[row + x]];
                });

                sub?.Mark(SpPaletteRemap);
                built = new ColorPalette { Count = count, Colors = colors, Index = index, H = hh, S = ss, V = vv };
                return built;
            }
            finally
            {
                if (built == null) s_paletteIndexPool.Return(index);
            }
        }

        // ───────── 無彩パスの再着色メモ ─────────
        // 成分別 L マップ(BuildComponentMedianLMap)を使うゾーンでは、再着色色が「色」と「その画素の
        // 成分の地色基準 L」の組で決まるので、色ごとの表を 1 枚では作れない。代わりに、スレッドごとの
        // 小さな直接写像メモで、同じ(色番号, 地色基準 L)の結果を使い回す。テクスチャは成分ごとに
        // 色の種類が少ないので、ほとんどの画素はメモに当たる。
        // 鍵には呼び出し(ゾーン)ごとの世代番号を含める。スレッドプールのスレッドは別の呼び出しの
        // 行も処理するので、世代が違う項目は外れとして扱う(色番号は呼び出しごとに振り直されるため)。
        // 当たった値は同じ入力で RecolorPixel を呼んだ結果そのものなので、出力はビット単位で同じ。
        private const int RecolorMemoSize = 8192;   // 2 のべき
        [ThreadStatic] private static int[] t_memoEpoch;
        [ThreadStatic] private static int[] t_memoColor;
        [ThreadStatic] private static float[] t_memoMid;
        [ThreadStatic] private static float[] t_memoUp;
        [ThreadStatic] private static Color32[] t_memoOut;
        private static int s_recolorMemoEpoch;

        private static int NextRecolorMemoEpoch()
        {
            int e = Interlocked.Increment(ref s_recolorMemoEpoch);
            // 0 は「メモを使わない」の印なので飛ばす(一周するのは 2^32 回後)。
            return e != 0 ? e : Interlocked.Increment(ref s_recolorMemoEpoch);
        }

        private static Color32 RecolorPixelMemo(int epoch, int colorId, float regLmid, float regUp,
            Color32 op, float oV, float alpha, in RecolorParams p)
        {
            int[] ep = t_memoEpoch;
            if (ep == null)
            {
                ep = t_memoEpoch = new int[RecolorMemoSize];
                t_memoColor = new int[RecolorMemoSize];
                t_memoMid = new float[RecolorMemoSize];
                t_memoUp = new float[RecolorMemoSize];
                t_memoOut = new Color32[RecolorMemoSize];
            }
            int slot = (int)(((uint)colorId * 2654435761u) ^ (uint)(int)(regLmid * 1048576f) ^ ((uint)(int)(regUp * 1048576f) * 40503u))
                       & (RecolorMemoSize - 1);
            if (ep[slot] == epoch && t_memoColor[slot] == colorId && t_memoMid[slot] == regLmid && t_memoUp[slot] == regUp)
                return t_memoOut[slot];
            Color32 r = RecolorPixel(op.r, op.g, op.b, oV, alpha, in p, regLmid, regUp);
            ep[slot] = epoch;
            t_memoColor[slot] = colorId;
            t_memoMid[slot] = regLmid;
            t_memoUp[slot] = regUp;
            t_memoOut[slot] = r;
            return r;
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
