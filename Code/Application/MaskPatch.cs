// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;

namespace Iroca {
    /// <summary>
    /// 範囲の操作 1 回(右クリックの「ここも塗る / ここは塗らない / この部分だけ塗る」。AI の部分・メッシュの島に当てる)が、
    /// 1 つの層で新しく立てた画素。右クリックの「ここのマスクを消す」が、入れたときの範囲をそっくり消すのに使う。
    /// <para>
    /// 消す範囲を AI に見分け直させると、同じ部分でも半分以上の場合で消し残りが出る(2026-11-12 の計測。84 組中 45 組)。
    /// 1 回の操作が塗る範囲は離れた島や細かい点を含むことが多い(60 件中 44 件)ので、マスクのつながりだけでも決められない。
    /// 塗ったときに何を塗ったかを覚えておけば、推定は要らない。
    /// </para>
    /// <para>
    /// 中身は RLE(<see cref="MaskRle.EncodeBitsNative"/> の形。ヒープの外)で、作った後は変えない。持ち主は
    /// <see cref="MaskWorkspace"/>(編集中のマスクと一緒に Undo の履歴・レシピへ載る)。後の操作やブラシで消された画素も
    /// 入ったままなので、使うときはいまの層と重なる所だけを見る。
    /// </para>
    /// </summary>
    internal sealed unsafe class MaskPatch {
        /// <summary>層のゾーン。null = 全ゾーン共通の除外。</summary>
        public readonly string ZoneId;
        /// <summary>含めるの層か(false = 除外)。</summary>
        public readonly bool Include;
        /// <summary>塗った画素の RLE。参照を 1 つ持つ(<see cref="Release"/> で返す)。</summary>
        public readonly NativeBuffer Rle;

        private string _text;

        /// <param name="rle">塗った画素の RLE。参照を預かる。</param>
        public MaskPatch(string zoneId, bool include, NativeBuffer rle) {
            ZoneId = string.IsNullOrEmpty(zoneId) ? null : zoneId;
            Include = include;
            Rle = rle ?? throw new ArgumentNullException(nameof(rle));
        }

        public int Width => *(int*)(byte*)Rle.Ptr;
        public int Height => *(int*)((byte*)Rle.Ptr + 4);

        public void Release() => Rle.Release();

        /// <summary>同じ層の記録か。</summary>
        public bool IsOf(string zoneId, bool include)
            => Include == include && ZoneId == (string.IsNullOrEmpty(zoneId) ? null : zoneId);

        /// <summary>画素 pixel(y * 幅 + x)を、この操作が塗ったか。</summary>
        public bool Contains(int pixel) {
            byte* b = (byte*)Rle.Ptr;
            bool cur = b[8] != 0;
            uint* run = (uint*)(b + 9);
            long runs = (Rle.Bytes - 9) / 4, pos = 0;
            for (long i = 0; i < runs; i++, cur = !cur) {
                pos += run[i];
                if (pixel < pos) {
                    return cur && pixel >= 0;
                }
            }
            return false;
        }

        /// <summary>塗った画素のマスク(新しいマスク。参照 1)。</summary>
        public NativeBitMask Decode() => MaskRle.DecodeBitsNative(Rle);

        /// <summary>
        /// 塗った画素が、層にまだ 1 つでも立っているか(寸法が違えば false)。全部消された記録は保存しない。
        /// </summary>
        public bool AnyIn(NativeBitMask layer) {
            if (layer == null || layer.Width != Width || layer.Height != Height) {
                return false;
            }
            byte* b = (byte*)Rle.Ptr;
            bool cur = b[8] != 0;
            uint* run = (uint*)(b + 9);
            long runs = (Rle.Bytes - 9) / 4;
            ulong* bits = layer.Bits;
            int pos = 0;
            for (long i = 0; i < runs; i++, cur = !cur) {
                int end = pos + (int)run[i];
                if (cur && AnyBitInRange(bits, pos, end)) {
                    return true;
                }
                pos = end;
            }
            return false;
        }

        // 画素 [start, end) に立っているビットがあるか(語の途中は覆いで、まるごとの語は 0 との比較で)。
        private static bool AnyBitInRange(ulong* bits, int start, int end) {
            if (end <= start) {
                return false;
            }
            int w0 = start >> 6, w1 = (end - 1) >> 6;
            ulong head = ~0UL << (start & 63);
            ulong tail = ~0UL >> (63 - ((end - 1) & 63));
            if (w0 == w1) {
                return (bits[w0] & head & tail) != 0UL;
            }
            if ((bits[w0] & head) != 0UL || (bits[w1] & tail) != 0UL) {
                return true;
            }
            for (int k = w0 + 1; k < w1; k++) {
                if (bits[k] != 0UL) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 保存形式の文字列(<see cref="MaskRle.EncodeBits"/> と同じ。マスクの層と同じ書き方)。中身は変わらないので、
        /// 1 回作ったら使い回す。
        /// </summary>
        public string Text {
            get {
                if (_text == null) {
                    var m = Decode();
                    try { _text = MaskRle.EncodeBits(m); } finally { m.Release(); }
                }
                return _text;
            }
        }

        /// <summary>
        /// 保存形式の文字列から読む。壊れている・寸法が width × height でない・立っている画素が無いなら null
        /// (読めない記録は捨てる。その場所の「ここのマスクを消す」が、つながったかたまりを消すだけになる)。
        /// </summary>
        public static MaskPatch FromText(string zoneId, bool include, string text, int width, int height) {
            if (string.IsNullOrEmpty(text)) {
                return null;
            }
            var m = MaskRle.DecodeBits(text);
            if (m == null) {
                return null;
            }
            try {
                if (m.Width != width || m.Height != height) {
                    return null;
                }
                var rle = MaskRle.EncodeBitsNative(m);
                return rle == null ? null : new MaskPatch(zoneId, include, rle);
            } finally {
                m.Release();
            }
        }

        /// <summary>
        /// 操作の前(before。その層が無かったら null)と後(after)の層から、その操作が新しく立てた画素の記録を作る。
        /// 1 つも立てていなければ null。
        /// </summary>
        public static MaskPatch Painted(string zoneId, bool include, NativeBitMask after, NativeBitMask before) {
            if (after == null) {
                return null;
            }
            NativeBuffer rle;
            if (before == null) {
                rle = MaskRle.EncodeBitsNative(after);
            } else {
                var diff = after.Clone();
                try {
                    ulong* d = diff.Bits, q = before.Bits;
                    for (int k = 0, words = diff.Words; k < words; k++) {
                        d[k] &= ~q[k];
                    }
                    rle = MaskRle.EncodeBitsNative(diff);
                } finally {
                    diff.Release();
                }
            }
            return rle == null ? null : new MaskPatch(zoneId, include, rle);
        }

        /// <summary>同じ中身の RLE の写し(参照 1)。Undo の履歴は、編集中のマスクと塊を共有せずに自分の写しを持つ。</summary>
        public static NativeBuffer CloneRle(NativeBuffer rle) {
            var copy = new NativeBuffer(rle.Bytes, NativeUsage.Mask);
            Buffer.MemoryCopy((void*)rle.Ptr, (void*)copy.Ptr, rle.Bytes, rle.Bytes);
            return copy;
        }

        /// <summary>2 つの RLE の中身が同じか。</summary>
        public static bool SameRle(NativeBuffer a, NativeBuffer b) {
            if (ReferenceEquals(a, b)) {
                return true;
            }
            if (a.Bytes != b.Bytes) {
                return false;
            }
            byte* pa = (byte*)a.Ptr, pb = (byte*)b.Ptr;
            long n = a.Bytes, i = 0;
            for (; i + 8 <= n; i += 8) {
                if (*(ulong*)(pa + i) != *(ulong*)(pb + i)) {
                    return false;
                }
            }
            for (; i < n; i++) {
                if (pa[i] != pb[i]) {
                    return false;
                }
            }
            return true;
        }
    }
}
