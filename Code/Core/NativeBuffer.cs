// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Iroca
{
    /// <summary>ヒープの外のメモリの用途(数え上げの区分。<see cref="IrocaNativeMemory"/>)。</summary>
    internal enum NativeUsage
    {
        /// <summary>作業配列のプール(<see cref="NativeWorkPool{T}"/>)。</summary>
        Work,
        /// <summary>画素(<see cref="NativePixels"/>。プレビューの原本・出力、非破壊ビルド・書き出しの画素)。</summary>
        Pixels,
        /// <summary>色の表(画素 → 番号・番号 → HSV)。</summary>
        Palette,
        /// <summary>選択キャッシュの強さ。</summary>
        Selection,
        /// <summary>1 画素 1 ビットのマスク。</summary>
        Mask,
        /// <summary>そのほか。</summary>
        Other,
    }

    /// <summary>
    /// ヒープの外(<see cref="IrocaNativeMemory"/>)に確保した 1 つの塊の持ち主。いろかのヒープの外のメモリは
    /// どれもこの上に作る(作業配列のプールの塊・画素・色の表・選択キャッシュ)。決まりはここにだけ書く:
    /// <list type="bullet">
    /// <item>持ち方は参照の数。作った側が 1 つ目を持ち、ほかの持ち主・別スレッドの読み手は <see cref="AddRef"/> して持ち、
    ///   使い終わったら <see cref="Release"/> する。持ち主が 1 つだけなら <see cref="Dispose"/>(= Release)でよい。</item>
    /// <item>返しすぎ・解放後の <see cref="AddRef"/> は、いつも投げる(黙って無視しない)。</item>
    /// <item>中断中(ドメインのアンロードでスレッドが止められている最中。<see cref="IrocaNativeMemory.CurrentThreadAborting"/>)の
    ///   最後の Release は解放しない。並列で読み書きしている途中のスレッドが残っていることがあるので、static の一覧に入れて
    ///   アンロードまで持ち、アンロードの後で走るファイナライザに解放させる(手放すと、アンロードより前に GC が回収して
    ///   解放してしまうことがある)。</item>
    /// <item>取りこぼし(Release し忘れ)はファイナライザが解放し、用途つきで数える
    ///   (<see cref="IrocaNativeMemory.FinalizedCount"/>。テストで 0 であることを確かめる)。</item>
    /// </list>
    /// </summary>
    internal sealed class NativeBuffer : IDisposable
    {
        // 塊そのもの。SafeHandle のファイナライザは、持ち主のファイナライザより後に走る(中身を使う側が先に片づく)。
        private sealed class Block : SafeHandle
        {
            internal readonly long Bytes;
            internal readonly NativeUsage Usage;
            // 持ち主が Release で解放した(false のまま解放されたらファイナライザ = 取りこぼし)。
            internal volatile bool Released;

            internal Block(long bytes, NativeUsage usage) : base(IntPtr.Zero, ownsHandle: true)
            {
                Bytes = bytes;
                Usage = usage;
                SetHandle(IrocaNativeMemory.Alloc(bytes, usage));
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle()
            {
                if (!Released) IrocaNativeMemory.NoteFinalized(Usage);
                IrocaNativeMemory.Free(handle, Bytes, Usage);
                return true;
            }
        }

        // 中断中に最後の参照が返された塊(アンロードまで持つ)。
        private static readonly List<NativeBuffer> s_keptUntilUnload = new List<NativeBuffer>();

        private readonly Block _block;
        private IntPtr _ptr;
        private int _refs = 1;

        /// <summary>bytes バイトを確保する(中身は不定)。参照 1 で返る。</summary>
        public NativeBuffer(long bytes, NativeUsage usage)
        {
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            _block = new Block(bytes, usage);
            _ptr = _block.DangerousGetHandle();
        }

        /// <summary>バイト数。</summary>
        public long Bytes => _block.Bytes;

        /// <summary>用途。</summary>
        public NativeUsage Usage => _block.Usage;

        /// <summary>先頭の番地。解放後は IntPtr.Zero。</summary>
        public IntPtr Ptr => _ptr;

        /// <summary>いまの参照の数(テストと、「自分だけが持っているか」を見る持ち主のため)。</summary>
        public int RefCount => Volatile.Read(ref _refs);

        /// <summary>参照を 1 つ足す。自分が参照を持っている間に呼ぶこと(解放済みなら投げる)。</summary>
        public NativeBuffer AddRef()
        {
            if (Interlocked.Increment(ref _refs) <= 1)
            {
                Interlocked.Decrement(ref _refs);
                throw new ObjectDisposedException(nameof(NativeBuffer), $"解放済みの塊({Usage})に参照を足そうとしました");
            }
            return this;
        }

        /// <summary>参照を 1 つ返す。最後の参照ならすぐ解放する(どのスレッドからでもよい)。返しすぎは投げる。</summary>
        public void Release()
        {
            int left = Interlocked.Decrement(ref _refs);
            if (left > 0) return;
            if (left < 0)
            {
                Interlocked.Increment(ref _refs);
                throw new InvalidOperationException($"塊({Usage})の参照を返しすぎました");
            }
            if (IrocaNativeMemory.CurrentThreadAborting)
            {
                // 解放はアンロードの後のファイナライザ。取りこぼしではないので数えない。
                _block.Released = true;
                lock (s_keptUntilUnload) s_keptUntilUnload.Add(this);
                return;
            }
            _ptr = IntPtr.Zero;
            _block.Released = true;
            _block.Dispose();
        }

        /// <summary>持ち主が 1 つのときの後始末(<see cref="Release"/> と同じ)。</summary>
        public void Dispose() => Release();
    }
}
