// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 作業配列のプールの空きの塊の置き場。塊はただのバイト列なので、型の違うプールどうしでも同じ置き場を使えば、
    /// int のプールが返した 64MB の塊を float のプールがそのまま借りられる。<see cref="WorkPools"/> の 5 つのプールは
    /// 1 つの置き場を共有する(2026-11-08。型ごとに別だった頃は、4096² の 1 回の処理の山で int の空きが 130MB 残ったまま
    /// float の塊を新しく作っていた)。手元に持つ量の上限(<see cref="NativeWorkPool{T}.HoldFactor"/>)も置き場の全体で数える。
    /// 置き場を渡さずに作ったプールは自分だけの置き場を持つ(テストの単体のプールは従来と同じに振る舞う)。
    /// </summary>
    internal sealed class NativeBlockStore {
        internal sealed class Block {
            public readonly long Bytes;
            public readonly NativeBuffer Mem;
            public readonly IntPtr Ptr;
            // 空きのとき: 最後に返したプール(そのプールの FreeBytes に数える)。
            public NativeWorkPoolBase Owner;

            public Block(long bytes) {
                Bytes = bytes;
                Mem = new NativeBuffer(bytes, NativeUsage.Work);
                Ptr = Mem.Ptr;
            }

            public void FreeNow() => Mem.Release();
        }

        internal readonly object Gate = new object();
        // バイト数の区分 → 空きの塊。
        internal readonly Dictionary<long, List<Block>> Free = new Dictionary<long, List<Block>>();
        internal readonly Queue<Block> Quarantine = new Queue<Block>();
        internal long FreeBytes, RentedBytes, PeakRentedBytes;
    }

    /// <summary>型によらない部分(置き場の塊の持ち主として数えるため)。</summary>
    internal abstract class NativeWorkPoolBase {
        // 置き場の空きのうち、このプールが返した塊のバイト数(NativeBlockStore.Gate の中で読み書きする)。
        internal long OwnedFreeBytes;
    }

    /// <summary>
    /// 作業配列のプール。中身は Mono のヒープの外(<see cref="IrocaNativeMemory"/>)に置き、ポインタで貸し出す。
    /// Unity の Mono のヒープは一度広がると OS へ返らないので、フル解像度の作業配列(8K で 1 本 256MB)を
    /// 管理配列で持つと、書き出しなどで一度大きく使っただけでその分がエディタを閉じるまで残る。
    /// ここに置けば <see cref="ReleaseFree"/> で、使っていない塊をすぐ OS へ返せる。
    ///
    /// - 貸し出す長さは要求以上(区分の大きさに切り上げる)。中身は 0 にならない(前の持ち主の値が残る。
    ///   ArrayPool と同じ。借りた側が書いてから読む)。前の持ち主は別の型のプールのこともある。
    /// - 区分はバイト数を 2 のべき乗を 8 つに刻んだ大きさ(新しく確保するときの無駄は最大 1/8)。要求の約 2 倍までの区分の
    ///   空きを、小さい方から使い回し、区分ごとに maxPerClass 個まで手元に残す。空きの塊は <see cref="NativeBlockStore"/> に置く。
    /// - 貸し出し中の塊は解放しない(<see cref="ReleaseFree"/> は空きだけを解放する)。返されたときに残すか解放する。
    /// - 手元に持つ量(置き場の全体の貸し出し中 + 空き)は、直近の貸し出し中の最大量の <see cref="HoldFactor"/> 倍まで。
    ///   新しい塊を作るときに超えるなら、先に空きを大きい順に解放する(区分が合わずに使われない空きが溜まらないように)。
    ///   直近の最大量は <see cref="ReleaseFree"/> で今の貸し出し中の量へ戻す(手を止めたときの縮小。NativeMemoryLifecycle)。
    /// - 塊の持ち主は <see cref="NativeBuffer"/>(用途 Work)。ドメインのアンロードで取りこぼしても、そのファイナライザが解放する。
    /// Rent/Return はどのスレッドからでも呼べる。
    /// </summary>
    internal sealed unsafe class NativeWorkPool<T> : NativeWorkPoolBase where T : unmanaged {
        /// <summary>手元に持つ量の上限: 直近の貸し出し中の最大量の何倍まで持つか。</summary>
        internal const double HoldFactor = 1.5;

        // 検証用(IROCA_POOL_POISON=1)に、返された塊を埋めてから空きへ回すまで寝かせる数。
        private const int QuarantineLength = 8;

        private readonly NativeBlockStore _store;
        private readonly object _gate;
        private readonly int _maxPerClass;
        private readonly Dictionary<IntPtr, NativeBlockStore.Block> _rented = new Dictionary<IntPtr, NativeBlockStore.Block>();
        private long _rentedBytes, _peakRentedBytes;

        public NativeWorkPool(int maxPerClass) : this(maxPerClass, new NativeBlockStore()) { }

        public NativeWorkPool(int maxPerClass, NativeBlockStore store) {
            _maxPerClass = maxPerClass;
            _store = store;
            _gate = store.Gate;
        }

        /// <summary>空きとして手元に残しているバイト数(置き場の空きのうち、このプールが返した塊)。</summary>
        public long FreeBytes {
            get {
                lock (_gate) {
                    return OwnedFreeBytes;
                }
            }
        }

        /// <summary>貸し出し中の塊の数(テスト・計測用)。</summary>
        public int RentedCount {
            get {
                lock (_gate) {
                    return _rented.Count;
                }
            }
        }

        /// <summary>貸し出し中のバイト数。</summary>
        public long RentedBytes {
            get {
                lock (_gate) {
                    return _rentedBytes;
                }
            }
        }

        /// <summary>直近(最後の <see cref="ReleaseFree"/> から)の貸し出し中の最大のバイト数。</summary>
        public long PeakRentedBytes {
            get {
                lock (_gate) {
                    return _peakRentedBytes;
                }
            }
        }

        // 1 オクターブ(2 倍)を刻む区分の数(RoundUp の step = 2^(msb-3))。
        private const int ClassesPerOctave = 8;

        // 区分の大きさ(バイト数): 2 のべき乗を 8 つに刻んだ値へ切り上げる(2^k ちょうどはそのまま)。
        // 要素の大きさが 2 のべき乗なので、要素の数で刻んでいた頃と同じ型の中での区分は変わらない。
        internal static long RoundUp(long n) {
            if (n <= 8) {
                return Math.Max(n, 1);
            }
            int msb = 62;
            while (((ulong)(n - 1) >> msb) == 0) {
                msb--;   // n-1 の最上位ビット
            }
            long step = 1L << Math.Max(0, msb - 3);        // 1 オクターブ (2^msb, 2^(msb+1)] を 8 刻み
            return (n + step - 1) / step * step;
        }

        private static long NextClass(long cap) => RoundUp(cap + 1);

        // 検証用: IROCA_POOL_POISON=1 なら貸す領域を 0xFF で埋める(float は NaN、bool は真)。借りた側が書く前に
        // 読んでいる所があれば出力が変わるので、abcheck・回帰でそれを見つけられる。返された塊は 0xDD で埋め、
        // QuarantineLength 個のあいだ貸さずに寝かせる(返したあとに読む所は出力の違いで、返したものをもう一度返す所は
        // 「このプールが貸した塊ではありません」の例外で見つかる。寝かせないと、同じ番地が別の借り手へ貸された後の
        // 2 回目の返却を見分けられない)。製品では使わない。
        private static readonly bool s_poison = Environment.GetEnvironmentVariable("IROCA_POOL_POISON") == "1";

        public T* Rent(int minLength) {
            if (minLength <= 0) {
                throw new ArgumentOutOfRangeException(nameof(minLength));
            }
            T* p = RentCore(minLength);
            if (s_poison) {
                new Span<byte>(p, minLength * sizeof(T)).Fill(0xFF);
            }
            return p;
        }

        private T* RentCore(int minLength) {
            long need = (long)minLength * sizeof(T);
            // 検証用のはみ出し検出(IrocaNativeMemory.GuardPages)では、要求ちょうどの塊を毎回新しく取る
            // (切り上げた分や、使い回した大きめの塊の余りに、はみ出しが隠れないように)。
            if (IrocaNativeMemory.GuardPages) {
                var exact = new NativeBlockStore.Block(need);
                lock (_gate) {
                    MarkRented(exact);
                }
                return (T*)exact.Ptr;
            }
            long cap = RoundUp(need);
            List<NativeBlockStore.Block> evict = null;
            lock (_gate) {
                // 小さい区分から順に、要求の約 2 倍(1 オクターブ上)までの空きを探す(最初に見つかったのがいちばん小さい)。
                // 外接矩形の大きさで長さが毎回変わる借り物(連結成分のラン・ラベルなど)は、1 つ上の区分までしか
                // 見ないと少しずつ違う区分に新しい塊を作り、空きが溜まっていた(4K で int の空きが 270MB → 210MB)。
                long c = cap;
                for (int tries = 0; tries < ClassesPerOctave + 1; tries++, c = NextClass(c)) {
                    if (!_store.Free.TryGetValue(c, out var list) || list.Count == 0) {
                        continue;
                    }
                    var b = list[list.Count - 1];
                    list.RemoveAt(list.Count - 1);
                    TakeFromFree(b);
                    MarkRented(b);
                    return (T*)b.Ptr;
                }
                // 新しく作る。手元の量(置き場の全体の貸し出し中 + 空き + 新しい塊)が直近の最大量の HoldFactor 倍を
                // 超えるなら、空きを大きい順に手放す(使われない区分の空きが溜まらないように)。
                long rented = _store.RentedBytes, free = _store.FreeBytes;
                long limit = (long)(HoldFactor * Math.Max(_store.PeakRentedBytes, rented + cap));
                if (rented + free + cap > limit) {
                    evict = TakeFreeLargestFirst(rented + free + cap - limit);
                }
            }
            if (evict != null) {
                foreach (var e in evict) {
                    e.FreeNow();
                }
            }
            var nb = new NativeBlockStore.Block(cap);
            lock (_gate) {
                MarkRented(nb);
            }
            return (T*)nb.Ptr;
        }

        // 空きから外す(_gate の中で呼ぶ)。
        private void TakeFromFree(NativeBlockStore.Block b) {
            _store.FreeBytes -= b.Bytes;
            b.Owner.OwnedFreeBytes -= b.Bytes;
            b.Owner = null;
        }

        // 空きへ入れる(_gate の中で呼ぶ)。
        private void PutToFree(NativeBlockStore.Block b) {
            b.Owner = this;
            OwnedFreeBytes += b.Bytes;
            _store.FreeBytes += b.Bytes;
        }

        // 貸し出し中として数える(_gate の中で呼ぶ)。
        private void MarkRented(NativeBlockStore.Block b) {
            _rented.Add(b.Ptr, b);
            _rentedBytes += b.Bytes;
            if (_rentedBytes > _peakRentedBytes) {
                _peakRentedBytes = _rentedBytes;
            }
            _store.RentedBytes += b.Bytes;
            if (_store.RentedBytes > _store.PeakRentedBytes) {
                _store.PeakRentedBytes = _store.RentedBytes;
            }
        }

        // 置き場の空きを大きい順に、合わせて atLeast バイト以上になるまで取り出す(_gate の中で呼ぶ。解放は外で)。
        private List<NativeBlockStore.Block> TakeFreeLargestFirst(long atLeast) {
            var taken = new List<NativeBlockStore.Block>();
            var classes = new List<long>(_store.Free.Keys);
            classes.Sort();
            for (int k = classes.Count - 1; k >= 0 && atLeast > 0; k--) {
                var list = _store.Free[classes[k]];
                while (list.Count > 0 && atLeast > 0) {
                    var b = list[list.Count - 1];
                    list.RemoveAt(list.Count - 1);
                    TakeFromFree(b);
                    atLeast -= b.Bytes;
                    taken.Add(b);
                }
            }
            return taken;
        }

        public void Return(T* p) {
            if (p == null) {
                return;
            }
            NativeBlockStore.Block b;
            // lock の外で解放する塊(解放は OS を呼ぶので、ほかのスレッドの Rent/Return を待たせない)。
            NativeBlockStore.Block drop = null;
            lock (_gate) {
                if (!_rented.TryGetValue((IntPtr)p, out b)) {
                    throw new InvalidOperationException("このプールが貸した塊ではありません");
                }
                // 中断中(ドメインのアンロード)は、並列で使っている途中のスレッドが残っていることがあるので、
                // 貸し出し中のまま残す(解放はアンロードの後のファイナライザ)。
                if (IrocaNativeMemory.CurrentThreadAborting) {
                    return;
                }
                _rented.Remove((IntPtr)p);
                _rentedBytes -= b.Bytes;
                _store.RentedBytes -= b.Bytes;
                // 検証用のはみ出し検出では使い回さず、すぐ解放する(解放後の読み書きも捕まえる)。
                // 2026-11-10 まで、ここで b を null にしてから解放していたので何も解放されず、塊はファイナライザまで残り、
                // 返した後の読み書きも捕まらなかった。
                if (IrocaNativeMemory.GuardPages) {
                    drop = b;
                } else {
                    if (s_poison) {
                        // 返した後に読む所を見つけるため埋め、しばらく貸さない(寝かせている分も手元の量 = 空きに数える。
                        // 貸せないだけで、プールが持っているのは同じ)。
                        new Span<byte>((void*)b.Ptr, checked((int)Math.Min(b.Bytes, int.MaxValue))).Fill(0xDD);
                        _store.Quarantine.Enqueue(b);
                        PutToFree(b);
                        if (_store.Quarantine.Count <= QuarantineLength) {
                            return;
                        }
                        b = _store.Quarantine.Dequeue();
                        TakeFromFree(b);
                    }
                    if (!_store.Free.TryGetValue(b.Bytes, out var list)) {
                        _store.Free[b.Bytes] = list = new List<NativeBlockStore.Block>();
                    }
                    if (list.Count < _maxPerClass) {
                        list.Add(b);
                        PutToFree(b);
                        return;
                    }
                    drop = b;   // 区分ごとに残す数を超えた
                }
            }
            drop?.FreeNow();
        }

        /// <summary>
        /// このプールが返した空きの塊(寝かせている分も)を全部解放する(OS へ戻る)。貸し出し中の塊はそのまま。
        /// 直近の最大量を今の貸し出し中の量へ戻す(置き場の全体の最大量も)。
        /// </summary>
        public void ReleaseFree() {
            List<NativeBlockStore.Block> drop = null;
            lock (_gate) {
                foreach (var list in _store.Free.Values) {
                    for (int i = list.Count - 1; i >= 0; i--) {
                        if (list[i].Owner != this) {
                            continue;
                        }
                        (drop ??= new List<NativeBlockStore.Block>()).Add(list[i]);
                        TakeFromFree(list[i]);
                        list.RemoveAt(i);
                    }
                }
                if (_store.Quarantine.Count > 0) {
                    int n = _store.Quarantine.Count;
                    for (int i = 0; i < n; i++) {
                        var q = _store.Quarantine.Dequeue();
                        if (q.Owner != this) { _store.Quarantine.Enqueue(q); continue; }
                        TakeFromFree(q);
                        (drop ??= new List<NativeBlockStore.Block>()).Add(q);
                    }
                }
                _peakRentedBytes = _rentedBytes;
                _store.PeakRentedBytes = _store.RentedBytes;
            }
            if (drop != null) {
                foreach (var b in drop) {
                    b.FreeNow();
                }
            }
        }
    }
    /// <summary>
    /// 作業配列のプール(型ごとに 1 つ)。PixelProcessor と AI 提案の精密化(SamMaskRefine)が共有し、
    /// <see cref="CoreMemory.ReleaseIdle"/> がまとめて空きを解放する。
    /// </summary>
    internal static class WorkPools {
        // 5 つのプールは空きの塊の置き場を共有する(型が違っても同じ大きさの塊を使い回す。NativeBlockStore)。
        private static readonly NativeBlockStore s_store = new NativeBlockStore();
        internal static readonly NativeWorkPool<float> Float = new NativeWorkPool<float>(maxPerClass: 24, s_store);
        internal static readonly NativeWorkPool<bool> Bool = new NativeWorkPool<bool>(maxPerClass: 12, s_store);
        internal static readonly NativeWorkPool<byte> Byte = new NativeWorkPool<byte>(maxPerClass: 4, s_store);
        internal static readonly NativeWorkPool<Color32> Rgba32 = new NativeWorkPool<Color32>(maxPerClass: 4, s_store);
        internal static readonly NativeWorkPool<int> Int = new NativeWorkPool<int>(maxPerClass: 8, s_store);

        internal static void ReleaseFree() {
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
    internal sealed unsafe class WorkScratch : IDisposable {
        private List<IntPtr> _bools, _ints, _floats, _bytes;

        public bool* Bool(int n, bool clear) {
            bool* p = WorkPools.Bool.Rent(n);
            (_bools ??= new List<IntPtr>(8)).Add((IntPtr)p);
            if (clear) {
                new Span<bool>(p, n).Clear();
            }
            return p;
        }

        public int* Int(int n, bool clear) {
            int* p = WorkPools.Int.Rent(n);
            (_ints ??= new List<IntPtr>(8)).Add((IntPtr)p);
            if (clear) {
                new Span<int>(p, n).Clear();
            }
            return p;
        }

        public float* Float(int n, bool clear) {
            float* p = WorkPools.Float.Rent(n);
            (_floats ??= new List<IntPtr>(4)).Add((IntPtr)p);
            if (clear) {
                new Span<float>(p, n).Clear();
            }
            return p;
        }

        public byte* Byte(int n, bool clear) {
            byte* p = WorkPools.Byte.Rent(n);
            (_bytes ??= new List<IntPtr>(4)).Add((IntPtr)p);
            if (clear) {
                new Span<byte>(p, n).Clear();
            }
            return p;
        }

        // long・double は使う所が少ないので、専用のプールを持たず byte のプールから借りる(どちらも 8 バイト。塊は揃っている)。
        public long* Long(int n, bool clear) => (long*)Byte(checked(n * sizeof(long)), clear);

        public double* Double(int n, bool clear) => (double*)Byte(checked(n * sizeof(double)), clear);

        /// <summary>src の先頭 n 個の写し((bool[])src.Clone() の代わり)。</summary>
        public bool* CopyOf(bool[] src, int n) {
            bool* p = Bool(n, clear: false);
            new ReadOnlySpan<bool>(src, 0, n).CopyTo(new Span<bool>(p, n));
            return p;
        }

        public bool* CopyOf(bool* src, int n) {
            bool* p = Bool(n, clear: false);
            new ReadOnlySpan<bool>(src, n).CopyTo(new Span<bool>(p, n));
            return p;
        }

        public void Dispose() {
            // 中断中(ドメインのアンロード)は、並列で使っている途中のスレッドが残っていることがあるので返さない。
            if (IrocaNativeMemory.CurrentThreadAborting) {
                return;
            }
            if (_bools != null) {
                foreach (var p in _bools) {
                    WorkPools.Bool.Return((bool*)p);
                }
                _bools = null;
            }
            if (_ints != null) {
                foreach (var p in _ints) {
                    WorkPools.Int.Return((int*)p);
                }
                _ints = null;
            }
            if (_floats != null) {
                foreach (var p in _floats) {
                    WorkPools.Float.Return((float*)p);
                }
                _floats = null;
            }
            if (_bytes != null) {
                foreach (var p in _bytes) {
                    WorkPools.Byte.Return((byte*)p);
                }
                _bytes = null;
            }
        }
    }
}
