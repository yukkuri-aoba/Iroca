// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Iroca {
    /// <summary>
    /// ネイティブメモリに置いた float の並び(選択キャッシュの強さ)。中身の出し入れは配列・ポインタとの一括コピーだけ。
    /// 持ち方は <see cref="NativeBuffer"/> と同じ参照の数: 作った側が 1 つ目を持ち、写している間の読み手は
    /// <see cref="AddRef"/> して持ち、<see cref="Release"/> で返す。返し忘れは NativeBuffer のファイナライザが解放する。
    /// </summary>
    internal sealed unsafe class NativeFloatBuffer : IDisposable {
        private readonly NativeBuffer _mem;
        public int Length { get; }

        // 大きい一括コピーは帯に分けて並列にする(4K の 64MB を 1 本で写すと数十 ms かかる)。
        private const int CopyChunk = 1 << 18;

        public NativeFloatBuffer(int length) {
            if (length <= 0) {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            Length = length;
            _mem = new NativeBuffer((long)length * sizeof(float), NativeUsage.Selection);
        }

        /// <summary>いまの参照の数(持ち主が「自分だけが持っているか」を見るため)。</summary>
        public int RefCount => _mem.RefCount;

        /// <summary>参照を 1 つ足す(解放済みなら投げる)。</summary>
        public NativeFloatBuffer AddRef() {
            _mem.AddRef();
            return this;
        }

        /// <summary>参照を 1 つ返す。最後の参照なら解放する。返しすぎは投げる。</summary>
        public void Release() => _mem.Release();

        /// <summary>持ち主が 1 つのときの後始末(<see cref="Release"/> と同じ)。</summary>
        public void Dispose() => Release();

        public void CopyFrom(float[] src, int count) {
            if (count > src.Length) {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            fixed (float* p = src) {
                Copy(p, count, toNative: true);
            }
        }

        public void CopyTo(float[] dst, int count) {
            if (count > dst.Length) {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            fixed (float* p = dst) {
                Copy(p, count, toNative: false);
            }
        }

        // 長さ count 以上の領域との出し入れ(作業配列のプールが貸すポインタ)。
        public void CopyFrom(float* src, int count) => Copy(src, count, toNative: true);

        public void CopyTo(float* dst, int count) => Copy(dst, count, toNative: false);

        /// <summary>
        /// src(長さ srcLength)のうち、indices の位置の値だけを先頭から順に写す(このバッファの長さは indices の数以上)。
        /// 範囲外の添字は例外。
        /// </summary>
        public void GatherFrom(float* src, long srcLength, int[] indices) {
            float* mine = (float*)_mem.Ptr;
            if (mine == null) {
                throw new ObjectDisposedException(nameof(NativeFloatBuffer));
            }
            if (indices.Length > Length) {
                throw new ArgumentOutOfRangeException(nameof(indices));
            }
            for (int i = 0; i < indices.Length; i++) {
                int k = indices[i];
                if ((uint)k >= (ulong)srcLength) {
                    throw new ArgumentOutOfRangeException(nameof(indices));
                }
                mine[i] = src[k];
            }
        }

        // 少数の画素だけを読む(右クリックの宛先を決める 5×5 画素など)。範囲外は例外。
        public void ReadAt(int[] indices, float[] dst) {
            float* mine = (float*)_mem.Ptr;
            if (mine == null) {
                throw new ObjectDisposedException(nameof(NativeFloatBuffer));
            }
            if (dst.Length < indices.Length) {
                throw new ArgumentOutOfRangeException(nameof(dst));
            }
            for (int i = 0; i < indices.Length; i++) {
                int k = indices[i];
                if ((uint)k >= (uint)Length) {
                    throw new ArgumentOutOfRangeException(nameof(indices));
                }
                dst[i] = mine[k];
            }
        }

        private void Copy(float* other, int count, bool toNative) {
            float* mine = (float*)_mem.Ptr;
            if (mine == null) {
                throw new ObjectDisposedException(nameof(NativeFloatBuffer));
            }
            if (count < 0 || count > Length) {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            int chunks = (count + CopyChunk - 1) / CopyChunk;
            Parallel.For(0, chunks, CoreParallelism.Options(), c => {
                int s0 = c * CopyChunk;
                long bytes = (long)Math.Min(CopyChunk, count - s0) * sizeof(float);
                if (toNative) {
                    Buffer.MemoryCopy(other + s0, mine + s0, bytes, bytes);
                } else {
                    Buffer.MemoryCopy(mine + s0, other + s0, bytes, bytes);
                }
            });
        }

    }
}
