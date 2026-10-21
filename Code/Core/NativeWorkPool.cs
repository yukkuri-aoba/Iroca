// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 作業配列のプール。中身は Mono のヒープの外(<see cref="IrocaNativeMemory"/>)に置き、ポインタで貸し出す。
    /// Unity の Mono のヒープは一度広がると OS へ返らないので、フル解像度の作業配列(8K で 1 本 256MB)を
    /// 管理配列で持つと、書き出しなどで一度大きく使っただけでその分がエディタを閉じるまで残る。
    /// ここに置けば <see cref="ReleaseFree"/> で、使っていない塊をすぐ OS へ返せる。
    ///
    /// - 貸し出す長さは要求以上(区分の大きさに切り上げる)。中身は 0 にならない(前の持ち主の値が残る。
    ///   ArrayPool と同じ。借りた側が書いてから読む)。
    /// - 区分は 2 のべき乗を 8 つに刻んだ大きさ(新しく確保するときの無駄は最大 1/8)。要求の約 2 倍までの区分の空きを、
    ///   小さい方から使い回し、区分ごとに maxPerClass 個まで手元に残す。
    /// - 貸し出し中の塊は解放しない(<see cref="ReleaseFree"/> は空きだけを解放する)。返されたときに残すか解放する。
    /// - 塊ごとにファイナライザを持つので、ドメインのアンロードで取りこぼしても最後に解放される。
    /// Rent/Return はどのスレッドからでも呼べる。
    /// </summary>
    internal sealed unsafe class NativeWorkPool<T> where T : unmanaged
    {
        private sealed class Block
        {
            public readonly int Capacity;
            public readonly long Bytes;
            private IntPtr _ptr;
            public IntPtr Ptr => _ptr;

            public Block(int capacity)
            {
                Capacity = capacity;
                Bytes = (long)capacity * sizeof(T);
                _ptr = IrocaNativeMemory.Alloc(Bytes);
            }

            ~Block() => Free();

            public void Free()
            {
                IntPtr p = System.Threading.Interlocked.Exchange(ref _ptr, IntPtr.Zero);
                if (p != IntPtr.Zero) IrocaNativeMemory.Free(p, Bytes);
            }

            public void FreeNow()
            {
                Free();
                GC.SuppressFinalize(this);
            }
        }

        private readonly object _gate = new object();
        private readonly int _maxPerClass;
        private readonly Dictionary<int, List<Block>> _free = new Dictionary<int, List<Block>>();
        private readonly Dictionary<IntPtr, Block> _rented = new Dictionary<IntPtr, Block>();
        private long _freeBytes;

        public NativeWorkPool(int maxPerClass)
        {
            _maxPerClass = maxPerClass;
        }

        /// <summary>空きとして手元に残しているバイト数。</summary>
        public long FreeBytes { get { lock (_gate) return _freeBytes; } }

        /// <summary>貸し出し中の塊の数(テスト・計測用)。</summary>
        public int RentedCount { get { lock (_gate) return _rented.Count; } }

        // 1 オクターブ(2 倍)を刻む区分の数(RoundUp の step = 2^(msb-3))。
        private const int ClassesPerOctave = 8;

        // 区分の大きさ: 2 のべき乗を 8 つに刻んだ値へ切り上げる(2^k ちょうどはそのまま)。
        internal static int RoundUp(int n)
        {
            if (n <= 8) return Math.Max(n, 1);
            int msb = 31;
            while (((uint)(n - 1) >> msb) == 0) msb--;   // n-1 の最上位ビット
            int step = 1 << Math.Max(0, msb - 3);         // 1 オクターブ (2^msb, 2^(msb+1)] を 8 刻み
            long cap = ((long)n + step - 1) / step * step;
            return cap > int.MaxValue ? n : (int)cap;
        }

        private static int NextClass(int cap) => RoundUp(cap + 1);

        // 検証用: IROCA_POOL_POISON=1 なら貸す領域を 0xFF で埋める(float は NaN、bool は真)。借りた側が書く前に
        // 読んでいる所があれば出力が変わるので、abcheck・回帰でそれを見つけられる。製品では使わない。
        private static readonly bool s_poison = Environment.GetEnvironmentVariable("IROCA_POOL_POISON") == "1";

        public T* Rent(int minLength)
        {
            if (minLength <= 0) throw new ArgumentOutOfRangeException(nameof(minLength));
            T* p = RentCore(minLength);
            if (s_poison) new Span<byte>(p, minLength * sizeof(T)).Fill(0xFF);
            return p;
        }

        private T* RentCore(int minLength)
        {
            int cap = RoundUp(minLength);
            lock (_gate)
            {
                // 小さい区分から順に、要求の約 2 倍(1 オクターブ上)までの空きを探す(最初に見つかったのがいちばん小さい)。
                // 外接矩形の大きさで長さが毎回変わる借り物(連結成分のラン・ラベルなど)は、1 つ上の区分までしか
                // 見ないと少しずつ違う区分に新しい塊を作り、空きが溜まっていた(4K で int の空きが 270MB → 210MB)。
                int c = cap;
                for (int tries = 0; tries < ClassesPerOctave + 1; tries++, c = NextClass(c))
                {
                    if (!_free.TryGetValue(c, out var list) || list.Count == 0) continue;
                    var b = list[list.Count - 1];
                    list.RemoveAt(list.Count - 1);
                    _freeBytes -= b.Bytes;
                    _rented.Add(b.Ptr, b);
                    return (T*)b.Ptr;
                }
            }
            var nb = new Block(cap);
            lock (_gate) _rented.Add(nb.Ptr, nb);
            return (T*)nb.Ptr;
        }

        public void Return(T* p)
        {
            if (p == null) return;
            Block b;
            lock (_gate)
            {
                if (!_rented.TryGetValue((IntPtr)p, out b))
                    throw new InvalidOperationException("このプールが貸した塊ではありません");
                // 中断中(ドメインのアンロード)は、並列で使っている途中のスレッドが残っていることがあるので、
                // 貸し出し中のまま残す(解放はファイナライザ)。
                if (IrocaNativeMemory.CurrentThreadAborting) return;
                _rented.Remove((IntPtr)p);
                if (!_free.TryGetValue(b.Capacity, out var list)) _free[b.Capacity] = list = new List<Block>();
                if (list.Count < _maxPerClass)
                {
                    list.Add(b);
                    _freeBytes += b.Bytes;
                    return;
                }
            }
            b.FreeNow();
        }

        /// <summary>空きの塊を全部解放する(OS へ戻る)。貸し出し中の塊はそのまま。</summary>
        public void ReleaseFree()
        {
            List<Block> drop = null;
            lock (_gate)
            {
                foreach (var list in _free.Values)
                {
                    if (list.Count == 0) continue;
                    (drop ??= new List<Block>()).AddRange(list);
                    list.Clear();
                }
                _freeBytes = 0;
            }
            if (drop != null)
                foreach (var b in drop) b.FreeNow();
        }
    }
    /// <summary>
    /// 作業配列のプール(型ごとに 1 つ)。PixelProcessor と AI 提案の精密化(SamMaskRefine)が共有し、
    /// <see cref="PixelProcessor.ReleaseWorkBuffers"/> がまとめて空きを解放する。
    /// </summary>
    internal static class WorkPools
    {
        internal static readonly NativeWorkPool<float> Float = new NativeWorkPool<float>(maxPerClass: 24);
        internal static readonly NativeWorkPool<bool> Bool = new NativeWorkPool<bool>(maxPerClass: 12);
        internal static readonly NativeWorkPool<byte> Byte = new NativeWorkPool<byte>(maxPerClass: 4);
        internal static readonly NativeWorkPool<Color32> Rgba32 = new NativeWorkPool<Color32>(maxPerClass: 4);
        internal static readonly NativeWorkPool<int> Int = new NativeWorkPool<int>(maxPerClass: 8);

        internal static void ReleaseFree()
        {
            Float.ReleaseFree();
            Bool.ReleaseFree();
            Byte.ReleaseFree();
            Rgba32.ReleaseFree();
            Int.ReleaseFree();
        }
    }

    /// <summary>
    /// 1 つの処理の中で借りる作業配列をまとめて持ち、Dispose でまとめて返す(<c>using var scratch = new WorkScratch();</c>)。
    /// 途中で抜けても(キャンセル)返る。返すのは借りたスレッドだけで使うこと(並列ループの中から借りない)。
    /// 中身は前の持ち主の値が残る。<c>new T[n]</c> と同じ 0 埋めが要るときは clear: true で借りる。
    /// </summary>
    internal sealed unsafe class WorkScratch : IDisposable
    {
        private List<IntPtr> _bools, _ints;

        public bool* Bool(int n, bool clear)
        {
            bool* p = WorkPools.Bool.Rent(n);
            (_bools ??= new List<IntPtr>(8)).Add((IntPtr)p);
            if (clear) new Span<bool>(p, n).Clear();
            return p;
        }

        public int* Int(int n, bool clear)
        {
            int* p = WorkPools.Int.Rent(n);
            (_ints ??= new List<IntPtr>(8)).Add((IntPtr)p);
            if (clear) new Span<int>(p, n).Clear();
            return p;
        }

        /// <summary>src の先頭 n 個の写し((bool[])src.Clone() の代わり)。</summary>
        public bool* CopyOf(bool[] src, int n)
        {
            bool* p = Bool(n, clear: false);
            new ReadOnlySpan<bool>(src, 0, n).CopyTo(new Span<bool>(p, n));
            return p;
        }

        public void Dispose()
        {
            // 中断中(ドメインのアンロード)は、並列で使っている途中のスレッドが残っていることがあるので返さない。
            if (IrocaNativeMemory.CurrentThreadAborting) return;
            if (_bools != null) { foreach (var p in _bools) WorkPools.Bool.Return((bool*)p); _bools = null; }
            if (_ints != null) { foreach (var p in _ints) WorkPools.Int.Return((int*)p); _ints = null; }
        }
    }
}
