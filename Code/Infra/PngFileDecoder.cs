// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// PNG をファイルから直接、Mono のヒープの外(<see cref="NativePixels"/>)へデコードする。
    /// <para>
    /// Unity の LoadImage は管理配列しか受け取らないので、ファイルの大きさの配列(手元の素材で中央値 5MB・最大 16MB)を
    /// 開くたびに Mono のヒープに作る。Mono のヒープは一度広がると縮まない。ここではファイルを少しずつ読み、行ごとに展開する。
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
                try { return Decode(file, cancel, headerOnly: false, out _); }
                // 長さの足りないファイルも IOException の仲間なので、ここで分ける(開けないときの例外は上へ出す)。
                catch (EndOfStreamException) { return null; } catch (InvalidDataException) { return null; }
            }
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

            // 行の控えは画素数ではなく幅に比例する小さい配列(16384 幅で 65KB × 2)。
            var cur = new byte[rowBytes + 1];
            var prev = new byte[rowBytes + 1];
            var pixels = NativePixels.Allocate(width, height);
            bool done = false;
            try {
                using (var inflate = new DeflateStream(idat, CompressionMode.Decompress, leaveOpen: true)) {
                    for (int y = 0; y < height; y++) {
                        cancel.ThrowIfCancellationRequested();
                        if (!ReadRow(inflate, cur)) {
                            return null;
                        }
                        fixed (byte* c = cur, p = prev) {
                            if (!Unfilter(c[0], c + 1, p + 1, rowBytes, pixelBytes)) {
                                return null;
                            }
                            // PNG は上から、テクスチャは下から。
                            Color32* dst = pixels.Ptr + (long)(height - 1 - y) * width;
                            if (!Convert(c + 1, dst, width, bitDepth, colorType, palette)) {
                                return null;
                            }
                        }
                        var swap = cur; cur = prev; prev = swap;
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

        private static bool ReadRow(Stream inflate, byte[] row) {
            int got = 0;
            while (got < row.Length) {
                int n = inflate.Read(row, got, row.Length - got);
                if (n <= 0) {
                    return false;
                }
                got += n;
            }
            return true;
        }

        // PNG のフィルタを戻す(row をその場で)。知らない種類なら false。
        private static bool Unfilter(byte filter, byte* row, byte* prior, int length, int bpp) {
            switch (filter) {
                case 0:
                    return true;
                case 1:
                    for (int i = bpp; i < length; i++) {
                        row[i] = (byte)(row[i] + row[i - bpp]);
                    }
                    return true;
                case 2:
                    for (int i = 0; i < length; i++) {
                        row[i] = (byte)(row[i] + prior[i]);
                    }
                    return true;
                case 3:
                    for (int i = 0; i < bpp && i < length; i++) {
                        row[i] = (byte)(row[i] + (prior[i] >> 1));
                    }
                    for (int i = bpp; i < length; i++) {
                        row[i] = (byte)(row[i] + ((row[i - bpp] + prior[i]) >> 1));
                    }
                    return true;
                case 4:
                    for (int i = 0; i < bpp && i < length; i++) {
                        row[i] = (byte)(row[i] + prior[i]);
                    }
                    for (int i = bpp; i < length; i++) {
                        int a = row[i - bpp], b = prior[i], c = prior[i - bpp];
                        int pa = b - c, pb = a - c;
                        int pc = pa + pb;
                        if (pa < 0) {
                            pa = -pa;
                        }
                        if (pb < 0) {
                            pb = -pb;
                        }
                        if (pc < 0) {
                            pc = -pc;
                        }
                        row[i] = (byte)(row[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                    }
                    return true;
                default:
                    return false;
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
