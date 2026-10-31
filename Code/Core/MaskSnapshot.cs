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
    /// バックグラウンド処理に渡すマスク一式のスナップショット。層(<see cref="NativeBitMask"/>。Mono のヒープの外)を
    /// 参照で共有し、写さない(8K で 1 層 8MB を要求のたびに管理配列へ写していた。計画の B3)。編集側は書く前に
    /// <see cref="NativeBitMask.Writable"/> で自分だけのものにするので、スナップショットの中身は変わらない。
    /// 持ち方は参照の数で、作った側が 1 つ目を持つ。別スレッドの読み手は <see cref="AddRef"/> して持ち、使い終わったら
    /// <see cref="Release"/> する(最後の Release で層の参照を返す。返さないと編集側が毎回写すことになる)。
    /// </summary>
    internal sealed unsafe class MaskSnapshot
    {
        // 1 画素 = 1 bit(true=除外)。画素 idx は語 idx >> 6 の idx & 63 ビット目。層の寸法はどれも width × height。
        public NativeBitMask common;
        public int width;
        public int height;
        public Dictionary<string, NativeBitMask> zones;
        // ゾーン別「含める」マスク(true=強制的に選択へ含める)。key = ColorZone.id。
        // 除外(common/zones)と違い共通版は持たない(「どのゾーンの選択に含めるか」が
        // 定まらないため、含めるは常にゾーン単位)。除外と重なった画素は除外が勝つ。
        public Dictionary<string, NativeBitMask> zoneIncludes;

        private int _refs = 1;

        /// <summary>参照を 1 つ足す。自分が参照を持っている間に呼ぶこと(解放済みなら投げる)。</summary>
        public MaskSnapshot AddRef()
        {
            if (Interlocked.Increment(ref _refs) <= 1)
            {
                Interlocked.Decrement(ref _refs);
                throw new ObjectDisposedException(nameof(MaskSnapshot));
            }
            return this;
        }

        /// <summary>参照を 1 つ返す。最後の参照なら、層の参照を返す。返しすぎは投げる。</summary>
        public void Release()
        {
            int left = Interlocked.Decrement(ref _refs);
            if (left > 0) return;
            if (left < 0)
            {
                Interlocked.Increment(ref _refs);
                throw new InvalidOperationException("MaskSnapshot の参照を返しすぎました");
            }
            common?.Release();
            if (zones != null) foreach (var m in zones.Values) m?.Release();
            if (zoneIncludes != null) foreach (var m in zoneIncludes.Values) m?.Release();
        }

        /// <summary>
        /// len 画素ぶんの 1 画素 1 ビットの配列(全部 0)。自動調整の証拠の詰めた形(<see cref="Pack"/>)が使う。
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

        // bool[] から層を作る(参照 1)。寸法が決まっていない・bool[] が null なら null。
        // bool[] が短ければ足りない画素は false。
        private static NativeBitMask ToLayer(bool[] mask, int width, int height)
        {
            if (mask == null || width <= 0 || height <= 0) return null;
            var layer = NativeBitMask.Allocate(width, height);
            int n = Math.Min(mask.Length, layer.Length);
            ulong* bits = layer.Bits;
            for (int i = 0; i < n; i++)
                if (mask[i]) bits[i >> 6] |= 1UL << (i & 63);
            return layer;
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
                zones = new Dictionary<string, NativeBitMask>()
            };
            snap.common = ToLayer(common, width, height);
            if (zones != null)
            {
                foreach (var kv in zones)
                {
                    var layer = ToLayer(kv.Value, width, height);
                    if (layer == null) continue;
                    if (snap.zones.TryGetValue(kv.Key, out var earlier)) earlier.Release();
                    snap.zones[kv.Key] = layer;
                }
            }
            if (includes != null)
            {
                foreach (var kv in includes)
                {
                    var layer = ToLayer(kv.Value, width, height);
                    if (layer == null) continue;
                    // 全 false かは詰めた後の語で見る(bool[] を先頭から 1 つずつ読み直さない)。
                    if (!layer.Any())
                    {
                        layer.Release();
                        continue;
                    }
                    if (snap.zoneIncludes == null)
                        snap.zoneIncludes = new Dictionary<string, NativeBitMask>();
                    if (snap.zoneIncludes.TryGetValue(kv.Key, out var earlier)) earlier.Release();
                    snap.zoneIncludes[kv.Key] = layer;
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

        /// <summary>層の語の idx 番目ビットを読む(null は false。範囲は呼び出し側が守る)。</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static bool GetBit(ulong* bits, int idx) => bits != null && (bits[idx >> 6] & (1UL << (idx & 63))) != 0UL;
    }
}
