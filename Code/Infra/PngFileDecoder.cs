// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// PNG をファイルから直接、Mono のヒープの外(<see cref="NativePixels"/>)へデコードする。
    /// <para>
    /// Unity の LoadImage は管理配列しか受け取らないので、ファイルの大きさの配列(手元の素材で中央値 5MB・最大 16MB)を
    /// 開くたびに Mono のヒープに作る。Mono のヒープは一度広がると縮まない。ここではファイルを少しずつ読み、行ごとに展開する。
    /// zlib の展開は別のスレッドで、フィルタ戻しと並べて進める(<see cref="InflatedRows"/>。1 枚に 1 本)。
    /// </para>
    /// <para>
    /// 結果は LoadImage と同じ画素(<see cref="WorkingResolution.NativePixelsOf"/> で読んだ値。行 0 = 画像下端)で、手元の
    /// 素材で一致を確かめてある(PngFileDecoderTests)。扱うのは 8bit・16bit の RGB・RGBA・グレー・グレー+α、1〜4bit のグレー、パレット。
    /// それ以外(インターレース・グレーと RGB の透明色)と壊れたファイルは null を返すので、呼ぶ側が LoadImage で読む。
    /// </para>
    /// </summary>
    internal static unsafe class PngFileDecoder {
        private const int ReadBufferBytes = 64 * 1024;
        private const int ColorGray = 0, ColorRgb = 2, ColorPalette = 3, ColorGrayAlpha = 4, ColorRgba = 6;

        /// <summary>
        /// PNG をデコードする(どのスレッドからでもよい)。扱わない形式・壊れたファイルは null。ファイルを開けない・読めないときは
        /// IOException をそのまま投げる(LoadImage の経路の File.ReadAllBytes と同じ)。取り消されたら、次の行へ進む前に画素を手放して
        /// OperationCanceledException を投げる(行ごとに見るので、大きな画像でも待たない)。
        /// </summary>
        public static NativePixels TryDecode(string path, CancellationToken cancel = default) {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferBytes)) {
                try {
                    return Decode(file, cancel, headerOnly: false, out _);
                } catch (EndOfStreamException) {
                    // 長さの足りないファイルも IOException の仲間なので、ここで分ける(開けないときの例外は上へ出す)。
                    return null;
                } catch (InvalidDataException) {
                    return null;   // 壊れたデータ(zlib の展開の失敗も、展開のスレッドがこれに替える)
                } catch (SourceReadFailed e) {
                    // 画素のデータを読む途中でファイルを読めなくなった。包む前の例外を、今までどおりの型で外へ出す。
                    ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                    throw;
                }
            }
        }

        /// <summary>
        /// IDAT を読む途中でファイルの読み込みそのものが失敗した(本当の I/O の失敗)。zlib の展開は壊れたデータでも IOException を
        /// 投げる(Unity の Mono の DeflateStream は "Corrupted data ReadInternal")ので、ファイルから出たものだけをこれで包んで見分ける。
        /// </summary>
        private sealed class SourceReadFailed : IOException {
            public SourceReadFailed(IOException inner) : base(inner.Message, inner) { }
        }

        /// <summary>
        /// 画素の手前までを読んで、<see cref="TryDecode"/> が扱う PNG かを答える(画素の途中で壊れていれば、TryDecode は null を返す)。
        /// 読み始める前に、メインスレッドの LoadImage が要るかを決めるためのもの。開けないときは IOException を投げる。
        /// </summary>
        public static bool CanDecode(string path) {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferBytes)) {
                try {
                    Decode(file, default, headerOnly: true, out bool supported);
                    return supported;
                } catch (EndOfStreamException) { return false; } catch (InvalidDataException) { return false; }
            }
        }

        /// <summary>
        /// PNG の寸法を先頭の IHDR から読む(画素は読まない。どのスレッドからでもよい)。PNG でない・IHDR が先頭に無い・
        /// 短いファイルは false。開けないときは IOException を投げる。
        /// </summary>
        public static bool TryReadSize(string path, out int width, out int height) {
            width = height = 0;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64)) {
                // 署名 8 バイト + 長さ 4 + "IHDR" 4 + 幅 4 + 高さ 4。
                var head = new byte[24];
                try { ReadExactly(file, head, head.Length); } catch (EndOfStreamException) { return false; }
                if (!TextureIO.IsPngSignature(head) || BigEndian(head, 8) != 13 || FourCc(head, 12) != FourCc("IHDR")) {
                    return false;
                }
                uint w = BigEndian(head, 16), h = BigEndian(head, 20);
                if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) {
                    return false;
                }
                width = (int)w;
                height = (int)h;
                return true;
            }
        }

        private static NativePixels Decode(Stream file, CancellationToken cancel, bool headerOnly, out bool supportedHeader) {
            supportedHeader = false;
            var header = new byte[8];
            ReadExactly(file, header, 8);
            if (!TextureIO.IsPngSignature(header)) {
                return null;
            }

            int width = 0, height = 0, bitDepth = 0, colorType = 0;
            Color32[] palette = null;
            bool colorKey = false;
            bool haveHeader = false;
            var chunkHead = new byte[8];
            while (true) {
                ReadExactly(file, chunkHead, 8);
                uint length = BigEndian(chunkHead, 0);
                if (length > int.MaxValue) {
                    return null;
                }
                int type = FourCc(chunkHead, 4);
                if (type == FourCc("IDAT")) {
                    if (!haveHeader) {
                        return null;
                    }
                    if (colorType == ColorPalette && palette == null) {
                        return null;
                    }
                    if (colorKey) {
                        return null;
                    }
                    supportedHeader = true;
                    if (headerOnly) {
                        return null;
                    }
                    return DecodeImage(new IdatStream(file, (int)length), width, height, bitDepth, colorType, palette, cancel);
                }
                if (type == FourCc("IHDR")) {
                    if (length != 13) {
                        return null;
                    }
                    var ihdr = new byte[13];
                    ReadExactly(file, ihdr, 13);
                    width = (int)Math.Min(BigEndian(ihdr, 0), int.MaxValue);
                    height = (int)Math.Min(BigEndian(ihdr, 4), int.MaxValue);
                    bitDepth = ihdr[8];
                    colorType = ihdr[9];
                    int interlace = ihdr[12];
                    if (ihdr[10] != 0 || ihdr[11] != 0 || interlace != 0) {
                        return null;
                    }
                    if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4) {
                        return null;
                    }
                    bool supported;
                    switch (colorType) {
                        case ColorPalette: supported = bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8; break;
                        case ColorGray: supported = bitDepth == 1 || bitDepth == 2 || bitDepth == 4 || bitDepth == 8 || bitDepth == 16; break;
                        case ColorRgb:
                        case ColorGrayAlpha:
                        case ColorRgba: supported = bitDepth == 8 || bitDepth == 16; break;
                        default: supported = false; break;
                    }
                    if (!supported) {
                        return null;
                    }
                    haveHeader = true;
                    Skip(file, 4);
                } else if (type == FourCc("PLTE")) {
                    if (length % 3 != 0 || length > 768) {
                        return null;
                    }
                    var entries = new byte[length];
                    ReadExactly(file, entries, (int)length);
                    palette = new Color32[256];
                    for (int i = 0; i < length / 3; i++) {
                        palette[i] = new Color32(entries[i * 3], entries[i * 3 + 1], entries[i * 3 + 2], 255);
                    }
                    Skip(file, 4);
                } else if (type == FourCc("tRNS")) {
                    if (colorType != ColorPalette || palette == null) {
                        // グレー・RGB の透明色は LoadImage に任せる。
                        colorKey = true;
                        Skip(file, length + 4);
                    } else {
                        if (length > 256) {
                            return null;
                        }
                        var alphas = new byte[length];
                        ReadExactly(file, alphas, (int)length);
                        for (int i = 0; i < length; i++) {
                            palette[i].a = alphas[i];
                        }
                        Skip(file, 4);
                    }
                } else if ((chunkHead[4] & 0x20) == 0) {
                    return null;   // 知らない必須のチャンク(IEND も、IDAT の前に来れば壊れている)
                } else {
                    Skip(file, length + 4);
                }
            }
        }

        private static NativePixels DecodeImage(Stream idat, int width, int height, int bitDepth, int colorType, Color32[] palette,
            CancellationToken cancel) {
            // zlib の先頭 2 バイト。辞書付きは PNG では使えない。
            var zlib = new byte[2];
            ReadExactly(idat, zlib, 2);
            if ((zlib[0] & 0x0F) != 8 || (zlib[1] & 0x20) != 0 || ((zlib[0] << 8) | zlib[1]) % 31 != 0) {
                return null;
            }

            int channels = colorType == ColorRgba ? 4 : colorType == ColorRgb ? 3 : colorType == ColorGrayAlpha ? 2 : 1;
            int pixelBytes = Math.Max(1, channels * bitDepth / 8);
            long rowBytesLong = ((long)width * channels * bitDepth + 7) / 8;
            if (rowBytesLong + 1 > int.MaxValue / 2) {
                return null;
            }
            int rowBytes = (int)rowBytesLong;

            int stride = rowBytes + 1;
            // 前の行の控えは画素数ではなく幅に比例する小さい配列(16384 幅で 65KB)。
            var prev = new byte[stride];
            var pixels = NativePixels.Allocate(width, height);
            bool done = false;
            try {
                using (var rows = new InflatedRows(idat, stride, height)) {
                    for (int y = 0; y < height;) {
                        if (!rows.Next(out byte[] block, out int count)) {
                            return null;
                        }
                        fixed (byte* b = block, pv = prev) {
                            for (int r = 0; r < count; r++, y++) {
                                cancel.ThrowIfCancellationRequested();
                                byte* cur = b + (long)r * stride;
                                byte* prior = r == 0 ? pv : cur - stride;
                                if (!Unfilter(cur[0], cur + 1, prior + 1, rowBytes, pixelBytes)) {
                                    return null;
                                }
                                // PNG は上から、テクスチャは下から。
                                Color32* dst = pixels.Ptr + (long)(height - 1 - y) * width;
                                if (!Convert(cur + 1, dst, width, bitDepth, colorType, palette)) {
                                    return null;
                                }
                            }
                            // 束の最後の行は、次の束の最初の行が使う(束は展開のスレッドへ返すので写しておく)。
                            Buffer.MemoryCopy(b + (long)(count - 1) * stride, pv, stride, stride);
                        }
                        rows.Return();
                    }
                }
                done = true;
                return pixels;
            } finally {
                if (!done) {
                    pixels.Release();
                }
            }
        }

        // PNG のフィルタを戻す(row をその場で)。知らない種類なら false。
        // 速さのため、前の画素の値を変数で持ち回り、Paeth の選択を分岐なしで書く(分岐の予測外れが Mono の JIT では
        // いちばん重かった。4K で 480ms → 240ms)。RGBA・RGB の 8bit(bpp 4・3)は 1 画素の各チャンネルを並べて
        // 計算する。結果は素直な書き方(PngFileDecoderTests の Filtered の逆)と同じ。
        private static bool Unfilter(byte filter, byte* row, byte* prior, int length, int bpp) {
            switch (filter) {
                case 0:
                    return true;
                case 1:
                    for (int k = 0; k < bpp && k < length; k++) {
                        int a = row[k];
                        for (int i = k + bpp; i < length; i += bpp) {
                            a = (byte)(row[i] + a);
                            row[i] = (byte)a;
                        }
                    }
                    return true;
                case 2:
                    UnfilterUp(row, prior, length);
                    return true;
                case 3:
                    for (int k = 0; k < bpp && k < length; k++) {
                        int a = (byte)(row[k] + (prior[k] >> 1));
                        row[k] = (byte)a;
                        for (int i = k + bpp; i < length; i += bpp) {
                            a = (byte)(row[i] + ((a + prior[i]) >> 1));
                            row[i] = (byte)a;
                        }
                    }
                    return true;
                case 4:
                    if (bpp == 4) {
                        UnfilterPaeth4(row, prior, length);
                    } else if (bpp == 3) {
                        UnfilterPaeth3(row, prior, length);
                    } else {
                        UnfilterPaethChannels(row, prior, length, bpp);
                    }
                    return true;
                default:
                    return false;
            }
        }

        // 8 バイトずつ、桁上がりを隣のバイトへ渡さずに足す。
        private static void UnfilterUp(byte* row, byte* prior, int length) {
            const ulong High = 0x8080808080808080UL, Low = 0x7F7F7F7F7F7F7F7FUL;
            int i = 0;
            for (; i + 8 <= length; i += 8) {
                ulong x = *(ulong*)(row + i), y = *(ulong*)(prior + i);
                *(ulong*)(row + i) = ((x & Low) + (y & Low)) ^ ((x ^ y) & High);
            }
            for (; i < length; i++) {
                row[i] = (byte)(row[i] + prior[i]);
            }
        }

        // Paeth の予測(a = 左、b = 上、c = 左上)。pa ≤ pb かつ pa ≤ pc なら a、そうでなく pb ≤ pc なら b、それ以外は c。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Paeth(int a, int b, int c) {
            int pa = b - c, pb = a - c;
            int pc = pa + pb;
            int s = pa >> 31;
            pa = (pa ^ s) - s;
            s = pb >> 31;
            pb = (pb ^ s) - s;
            s = pc >> 31;
            pc = (pc ^ s) - s;
            int notA = ((pb - pa) | (pc - pa)) >> 31;   // -1: pa > pb か pa > pc
            int cOverB = (pc - pb) >> 31;              // -1: pb > pc
            int bc = b ^ ((b ^ c) & cOverB);
            return a ^ ((a ^ bc) & notA);
        }

        private static void UnfilterPaeth4(byte* row, byte* prior, int length) {
            int c0 = prior[0], c1 = prior[1], c2 = prior[2], c3 = prior[3];
            int a0 = (byte)(row[0] + c0), a1 = (byte)(row[1] + c1), a2 = (byte)(row[2] + c2), a3 = (byte)(row[3] + c3);
            row[0] = (byte)a0;
            row[1] = (byte)a1;
            row[2] = (byte)a2;
            row[3] = (byte)a3;
            for (int i = 4; i + 4 <= length; i += 4) {
                int b0 = prior[i], b1 = prior[i + 1], b2 = prior[i + 2], b3 = prior[i + 3];
                a0 = (byte)(row[i] + Paeth(a0, b0, c0));
                a1 = (byte)(row[i + 1] + Paeth(a1, b1, c1));
                a2 = (byte)(row[i + 2] + Paeth(a2, b2, c2));
                a3 = (byte)(row[i + 3] + Paeth(a3, b3, c3));
                row[i] = (byte)a0;
                row[i + 1] = (byte)a1;
                row[i + 2] = (byte)a2;
                row[i + 3] = (byte)a3;
                c0 = b0;
                c1 = b1;
                c2 = b2;
                c3 = b3;
            }
        }

        private static void UnfilterPaeth3(byte* row, byte* prior, int length) {
            int c0 = prior[0], c1 = prior[1], c2 = prior[2];
            int a0 = (byte)(row[0] + c0), a1 = (byte)(row[1] + c1), a2 = (byte)(row[2] + c2);
            row[0] = (byte)a0;
            row[1] = (byte)a1;
            row[2] = (byte)a2;
            for (int i = 3; i + 3 <= length; i += 3) {
                int b0 = prior[i], b1 = prior[i + 1], b2 = prior[i + 2];
                a0 = (byte)(row[i] + Paeth(a0, b0, c0));
                a1 = (byte)(row[i + 1] + Paeth(a1, b1, c1));
                a2 = (byte)(row[i + 2] + Paeth(a2, b2, c2));
                row[i] = (byte)a0;
                row[i + 1] = (byte)a1;
                row[i + 2] = (byte)a2;
                c0 = b0;
                c1 = b1;
                c2 = b2;
            }
        }

        // そのほかの bpp(グレー・16bit・8bit 未満)。チャンネルごとに行をなめる。
        private static void UnfilterPaethChannels(byte* row, byte* prior, int length, int bpp) {
            for (int k = 0; k < bpp && k < length; k++) {
                int c = prior[k];
                int a = (byte)(row[k] + c);
                row[k] = (byte)a;
                for (int i = k + bpp; i < length; i += bpp) {
                    int b = prior[i];
                    a = (byte)(row[i] + Paeth(a, b, c));
                    row[i] = (byte)a;
                    c = b;
                }
            }
        }

        private static bool Convert(byte* src, Color32* dst, int width, int bitDepth, int colorType, Color32[] palette) {
            // 16bit は上位のバイト。Color32 は r, g, b, a の 4 バイトが並ぶ。
            int step = bitDepth == 16 ? 2 : 1;
            byte* d = (byte*)dst;
            switch (colorType) {
                case ColorRgba:
                    if (step == 1) {
                        Buffer.MemoryCopy(src, dst, (long)width * 4, (long)width * 4);
                        return true;
                    }
                    for (int x = 0; x < width; x++, src += 8, d += 4) {
                        d[0] = src[0];
                        d[1] = src[2];
                        d[2] = src[4];
                        d[3] = src[6];
                    }
                    return true;
                case ColorRgb:
                    for (int x = 0; x < width; x++, src += 3 * step, d += 4) {
                        d[0] = src[0];
                        d[1] = src[step];
                        d[2] = src[2 * step];
                        d[3] = 255;
                    }
                    return true;
                case ColorGrayAlpha:
                    for (int x = 0; x < width; x++, src += 2 * step, d += 4) {
                        d[0] = d[1] = d[2] = src[0];
                        d[3] = src[step];
                    }
                    return true;
                case ColorGray:
                    if (bitDepth >= 8) {
                        for (int x = 0; x < width; x++, src += step, d += 4) {
                            d[0] = d[1] = d[2] = src[0];
                            d[3] = 255;
                        }
                        return true;
                    }
                    ConvertLowDepth(src, dst, width, bitDepth, null);
                    return true;
                default:
                    ConvertLowDepth(src, dst, width, bitDepth, palette);
                    return true;
            }
        }

        // 1 画素が 8bit 以下の番号のもの(パレット。palette が null なら番号を 0〜255 へ広げたグレー)。
        private static void ConvertLowDepth(byte* src, Color32* dst, int width, int bitDepth, Color32[] palette) {
            int mask = (1 << bitDepth) - 1;
            fixed (Color32* table = palette) {
                for (int x = 0; x < width; x++) {
                    long bit = (long)x * bitDepth;
                    int shift = 8 - bitDepth - (int)(bit & 7);
                    int value = (src[bit >> 3] >> shift) & mask;
                    if (table != null) {
                        dst[x] = table[value];
                    } else {
                        byte g = (byte)(value * 255 / mask);
                        dst[x] = new Color32(g, g, g, 255);
                    }
                }
            }
        }

        private static void ReadExactly(Stream s, byte[] buffer, int count) {
            int got = 0;
            while (got < count) {
                int n = s.Read(buffer, got, count - got);
                if (n <= 0) {
                    throw new EndOfStreamException();
                }
                got += n;
            }
        }

        private static void Skip(Stream s, long count) {
            if (s.Position + count > s.Length) {
                throw new EndOfStreamException();
            }
            s.Seek(count, SeekOrigin.Current);
        }

        private static uint BigEndian(byte[] b, int at) =>
            (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

        private static int FourCc(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];

        private static int FourCc(string s) => s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3];

        /// <summary>
        /// IDAT の展開(zlib)を別のスレッドで進め、展開した行を束(約 64KB)にして順に渡す。展開とフィルタ戻しが重なるので、
        /// 1 枚にかかる時間は両方の和からおおよそ重い方になる(4K で 500ms → 240ms)。束は 4 つを回して使い、
        /// 管理配列は 1 枚あたり約 256KB(画素数ではなく束の大きさで決まる)。
        /// <para>
        /// 展開のスレッドで出た例外は、受け取る側の <see cref="Next"/> で同じ型のまま投げ直す(1 本のスレッドで読んでいた
        /// ときと同じ扱いになる)。ただし展開の失敗の IOException(壊れたデータ)は InvalidDataException に替える。
        /// データが行の数に足りなければ Next が false。<see cref="Dispose"/> は展開のスレッドが
        /// 抜けるのを待つ(呼び出し側がファイルを閉じる前に必ず抜ける)。
        /// </para>
        /// </summary>
        private sealed class InflatedRows : IDisposable {
            private const int BlockBytes = 64 * 1024;
            private const int BlockCount = 4;

            private readonly Stream _idat;
            private readonly int _stride, _height, _rowsPerBlock;
            private readonly byte[][] _blocks = new byte[BlockCount][];
            private readonly int[] _rows = new int[BlockCount];
            private readonly SemaphoreSlim _filled = new SemaphoreSlim(0);
            private readonly SemaphoreSlim _empty = new SemaphoreSlim(BlockCount);
            private readonly Thread _thread;
            private volatile bool _stop;
            private volatile int _produced;
            private ExceptionDispatchInfo _error;
            private int _consumed;

            public InflatedRows(Stream idat, int stride, int height) {
                _idat = idat;
                _stride = stride;
                _height = height;
                _rowsPerBlock = Math.Max(1, Math.Min(height, BlockBytes / stride));
                for (int i = 0; i < BlockCount; i++) {
                    _blocks[i] = new byte[stride * _rowsPerBlock];
                }
                _thread = new Thread(Run) { IsBackground = true, Name = "Iroca PNG inflate" };
                _thread.Start();
            }

            private void Run() {
                try {
                    using (var inflate = new DeflateStream(_idat, CompressionMode.Decompress, leaveOpen: true)) {
                        for (int y = 0, i = 0; y < _height; i = (i + 1) % BlockCount) {
                            _empty.Wait();
                            if (_stop) {
                                return;
                            }
                            int rows = Math.Min(_rowsPerBlock, _height - y);
                            if (!ReadFully(inflate, _blocks[i], rows * _stride)) {
                                return;
                            }
                            _rows[i] = rows;
                            y += rows;
                            _produced++;
                            _filled.Release();
                        }
                    }
                } catch (IOException e) when (!(e is EndOfStreamException) && !(e is SourceReadFailed)) {
                    // ファイルからでない IOException は展開の失敗(壊れたデータ)。Unity の Mono の DeflateStream は
                    // IOException("Corrupted data ReadInternal") を投げるので、壊れたファイルとして扱えるように替える
                    // (そのままだと「ファイルを読めない」として外へ出て、LoadImage と取り込み済みへ回らない)。
                    _error = ExceptionDispatchInfo.Capture(new InvalidDataException(e.Message, e));
                } catch (Exception e) {
                    _error = ExceptionDispatchInfo.Capture(e);
                } finally {
                    // 途中で抜けたときも、待っている受け手を起こす(_produced が増えていないので終わりと分かる)。
                    _filled.Release();
                }
            }

            private static bool ReadFully(Stream s, byte[] buffer, int count) {
                int got = 0;
                while (got < count) {
                    int n = s.Read(buffer, got, count - got);
                    if (n <= 0) {
                        return false;
                    }
                    got += n;
                }
                return true;
            }

            /// <summary>次の束(rows 行)。データが尽きたら false、展開で例外が出ていればそれを投げる。</summary>
            public bool Next(out byte[] block, out int rows) {
                _filled.Wait();
                if (_consumed >= _produced) {
                    block = null;
                    rows = 0;
                    _error?.Throw();
                    return false;
                }
                int i = _consumed % BlockCount;
                block = _blocks[i];
                rows = _rows[i];
                return true;
            }

            /// <summary>Next で受け取った束を使い終えた。</summary>
            public void Return() {
                _consumed++;
                _empty.Release();
            }

            public void Dispose() {
                _stop = true;
                _empty.Release();
                _thread.Join();
                _filled.Dispose();
                _empty.Dispose();
            }
        }

        // 続いた IDAT チャンクの中身だけを読むストリーム(間の CRC とチャンクの見出しを飛ばす)。
        private sealed class IdatStream : Stream {
            private readonly Stream _file;
            private int _left;
            private bool _ended;
            private readonly byte[] _head = new byte[8];

            public IdatStream(Stream file, int firstChunkLength) {
                _file = file;
                _left = firstChunkLength;
            }

            public override int Read(byte[] buffer, int offset, int count) {
                try {
                    while (_left == 0) {
                        if (_ended) {
                            return 0;
                        }
                        Skip(_file, 4);   // CRC
                        ReadExactly(_file, _head, 8);
                        if (FourCc(_head, 4) != FourCc("IDAT")) { _ended = true; return 0; }
                        uint length = BigEndian(_head, 0);
                        if (length > int.MaxValue) {
                            throw new InvalidDataException("IDAT too long");
                        }
                        _left = (int)length;
                    }
                    int n = _file.Read(buffer, offset, Math.Min(count, _left));
                    if (n <= 0) {
                        throw new EndOfStreamException();
                    }
                    _left -= n;
                    return n;
                } catch (IOException e) when (!(e is EndOfStreamException)) {
                    // ファイルの読み込みの失敗。展開(DeflateStream)の失敗と見分けられるように包む(TryDecode が元の型に戻す)。
                    throw new SourceReadFailed(e);
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
