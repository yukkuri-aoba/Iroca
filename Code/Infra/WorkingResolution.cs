// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 編集の作業解像度。プレビュー・スポイト・AI 提案・自動調整・非破壊ビルド(NDMF)は、元ファイルの解像度の
    /// 画素で処理する。PNG の書き出し(単体・一括)と同じ解像度で処理するので、どの経路でも色替えの中身は同じになる
    /// (色替えは画素ごとに非線形なので、縮めてから処理すると、処理してから縮めたものと色がずれる。細かい模様の布で
    /// OkLab の差が 0.01 前後、金の小物で 0.05 前後あった。2026-10-25)。
    /// ただし元ファイルの辺が <see cref="MaxSourceSide"/> を超えるときは、メモリ(16K で 25GB ほど)が足りなく
    /// なるので、取り込み後の寸法(インポート設定の Max Size で縮んだ大きさ)へ縮めて処理する。その間は書き出しと
    /// 色が少し違い得ることを、ウィンドウの案内(IrocaWindow.DrawLargeSourceNotice)が知らせる。
    /// 2026-10-18〜25 は既定で取り込み後の寸法で処理し、「元の解像度で編集」で元の解像度に切り替えていた。
    /// プレビューの拡大表示は、処理した結果を取り込み後の寸法(<see cref="DisplaySize"/>)へ縮めて映す
    /// (ゲームに出るのはその寸法。非破壊ビルドも同じ縮小を通る)。
    /// マスクの土台は元ファイルの寸法。処理側はマスクと画素の寸法が違っても対応する画素を引く
    /// (PixelProcessor.BuildMaskIndexMap)。縮小は面積平均。
    /// </summary>
    internal static class WorkingResolution
    {
        /// <summary>元ファイルのまま処理する辺の上限(画素)。これを超える元ファイルは取り込み後の寸法で処理する。</summary>
        public const int MaxSourceSide = 8192;

        /// <summary>
        /// 元ファイル(fileW×fileH)を編集する寸法。取り込み済みテクスチャが無ければ元ファイルのまま。
        /// </summary>
        public static void Size(int fileW, int fileH, Texture2D imported, out int w, out int h)
        {
            if (imported == null) { w = fileW; h = fileH; return; }
            Size(fileW, fileH, imported.width, imported.height, out w, out h);
        }

        /// <summary>元ファイルが大きすぎて、元の解像度では処理しないか(<see cref="MaxSourceSide"/> を超える辺がある)。</summary>
        public static bool ExceedsLimit(int fileW, int fileH) => fileW > MaxSourceSide || fileH > MaxSourceSide;

        /// <summary>
        /// 寸法の規則。元ファイルの辺が <see cref="MaxSourceSide"/> 以下なら元ファイルのまま。超えるときは、取り込みで
        /// 小さくなっていれば取り込み後の寸法そのもの(2 のべき乗へ丸める取り込みで縦横比が変わっても従う。ゲームに
        /// 出るのもその寸法)。取り込みで小さくならないなら元ファイルのまま(引き伸ばさない)。
        /// </summary>
        internal static void Size(int fileW, int fileH, int importW, int importH, out int w, out int h)
        {
            w = fileW;
            h = fileH;
            if (!ExceedsLimit(fileW, fileH)) return;
            ShrinkToImport(fileW, fileH, importW, importH, ref w, ref h);
        }

        /// <summary>
        /// 作業解像度(workW×workH)で処理した結果を、プレビューの拡大表示に映す寸法。取り込みで小さくなるなら
        /// 取り込み後の寸法(ゲームに出る寸法)、そうでなければ作業解像度のまま。
        /// </summary>
        internal static void DisplaySize(int workW, int workH, int importW, int importH, out int w, out int h)
        {
            w = workW;
            h = workH;
            ShrinkToImport(workW, workH, importW, importH, ref w, ref h);
        }

        private static void ShrinkToImport(int fileW, int fileH, int importW, int importH, ref int w, ref int h)
        {
            if (importW <= 0 || importH <= 0) return;
            if ((long)importW * importH < (long)fileW * fileH)
            {
                w = importW;
                h = importH;
            }
        }

        /// <summary>
        /// デコードした元ファイルから作業解像度の画素を取る(メインスレッド)。縮めるときは元の画素を
        /// マネージドの配列へ丸ごと写さずに読む。8K なら 256MB の一時配列を Mono のヒープに作らない
        /// (Mono のヒープは一度広がると Unity を閉じるまで縮まない)。
        /// </summary>
        public static Color32[] PixelsOf(Texture2D decoded, Texture2D imported, out int w, out int h)
        {
            int fw = decoded.width, fh = decoded.height;
            Size(fw, fh, imported, out w, out h);
            if (w == fw && h == fh) return decoded.GetPixels32();
            if (TryLayout(decoded.format, out var layout))
                return ResizeArea(decoded.GetPixelData<byte>(0), layout, fw, fh, w, h);
            return ResizeArea(decoded.GetPixels32(), fw, fh, w, h);
        }

        /// <summary>
        /// <see cref="PixelsOf"/> と同じ画素を、Mono のヒープの外(<see cref="NativePixels"/>。参照 1 で返す)に作る
        /// (メインスレッド)。プレビューの原本はエディタを開いている間ずっと持つので、管理配列にすると閉じた後も
        /// その分(4K で 64MB、8K で 256MB)がエディタのメモリに残る。8bit の非圧縮形式はテクスチャの生データから
        /// 直接書き、管理配列の一時配列も作らない(それ以外の形式は GetPixels32 を経由する)。
        /// </summary>
        public static unsafe NativePixels NativePixelsOf(Texture2D decoded, Texture2D imported)
        {
            int fw = decoded.width, fh = decoded.height;
            Size(fw, fh, imported, out int w, out int h);
            if (!TryLayout(decoded.format, out var layout))
                return NativePixels.CopyOf(PixelsOf(decoded, imported, out w, out h), w, h);
            var raw = decoded.GetPixelData<byte>(0);
            var dst = NativePixels.Allocate(w, h);
            try
            {
                if (w == fw && h == fh) ConvertLayout(raw, layout, fw, fh, dst.Ptr);
                else ResizeArea(raw, layout, fw, fh, w, h, dst.Ptr);
            }
            catch
            {
                dst.Release();
                throw;
            }
            return dst;
        }

        // 縮めないときの写し(GetPixels32 と同じ値。RGBA32 はそのまま写す)。
        private static unsafe void ConvertLayout(NativeArray<byte> src, Layout layout, int w, int h, Color32* dst)
        {
            byte* s = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(src);
            long n = (long)w * h;
            if (layout.bpp == 4 && layout.r == 0 && layout.g == 1 && layout.b == 2 && layout.a == 3)
            {
                Buffer.MemoryCopy(s, dst, n * 4, n * 4);
                return;
            }
            int bpp = layout.bpp, ro = layout.r, go = layout.g, bo = layout.b, ao = layout.a;
            // 呼び出し元(メインスレッド)は返るまで待つので、テクスチャのデータはこの間ずっと読める。
            Parallel.For(0, h, y =>
            {
                byte* row = s + (long)y * w * bpp;
                Color32* d = dst + (long)y * w;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + x * bpp;
                    d[x] = new Color32(p[ro], p[go], p[bo], ao >= 0 ? p[ao] : (byte)255);
                }
            });
        }

        // ───────────── 面積平均の縮小 ─────────────

        /// <summary>
        /// 面積平均で縮小する(整数倍ならボックスフィルタと同じ)。拡大方向のときは最近傍。
        /// 行 0 = 画像下端の並び(Unity の画素順)のまま扱う。
        /// </summary>
        internal static unsafe Color32[] ResizeArea(Color32[] src, int sw, int sh, int dw, int dh)
        {
            if (src.Length < (long)sw * sh) throw new ArgumentException($"src.Length({src.Length}) < {sw}x{sh}", nameof(src));
            var dst = new Color32[dw * dh];
            fixed (Color32* s = src)
            fixed (Color32* d = dst)
                ResizeArea(s, sw, sh, dw, dh, d);
            return dst;
        }

        /// <summary>
        /// 上と同じ縮小を、ポインタの画素(sw*sh)から呼び出し側が用意した dw*dh 画素の領域へ書く(非破壊ビルドが
        /// Mono のヒープの外の画素からテクスチャのデータへ直接書く)。出力の行ごとに並列(結果は行の順に依らない)。
        /// 呼び出し側は返るまで両方の領域を保つこと。
        /// </summary>
        internal static unsafe void ResizeArea(Color32* src, int sw, int sh, int dw, int dh, Color32* dst)
        {
            Parallel.For(0, dh, y =>
            {
                int y0 = SrcRow0(y, sh, dh), y1 = SrcRow1(y, y0, sh, dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = (int)((long)x * sw / dw);
                    int x1 = Math.Max(x0 + 1, (int)(((long)x + 1) * sw / dw));
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        Color32* row = src + (long)yy * sw;
                        for (int xx = x0; xx < x1; xx++)
                        {
                            var c = row[xx];
                            r += c.r; g += c.g; b += c.b; a += c.a; n++;
                        }
                    }
                    dst[(long)y * dw + x] = Average(r, g, b, a, n);
                }
            });
        }

        // GetPixelData<byte> の 1 画素の並び(a < 0 は α なし = 255。GetPixels32 と同じ値になる)。
        internal struct Layout
        {
            public int bpp, r, g, b, a;
        }

        // 8bit の非圧縮形式だけ。それ以外(16bit の PNG など)は GetPixels32 に任せる。
        internal static bool TryLayout(TextureFormat f, out Layout l)
        {
            switch (f)
            {
                case TextureFormat.RGBA32: l = new Layout { bpp = 4, r = 0, g = 1, b = 2, a = 3 }; return true;
                case TextureFormat.ARGB32: l = new Layout { bpp = 4, a = 0, r = 1, g = 2, b = 3 }; return true;
                case TextureFormat.BGRA32: l = new Layout { bpp = 4, b = 0, g = 1, r = 2, a = 3 }; return true;
                case TextureFormat.RGB24:  l = new Layout { bpp = 3, r = 0, g = 1, b = 2, a = -1 }; return true;
                default: l = default; return false;
            }
        }

        // 一度にマネージドへ写す元の行の量の目安(バイト)。
        private const int BandBytes = 16 << 20;

        /// <summary>
        /// <see cref="ResizeArea(Color32[], int, int, int, int)"/> と同じ縮小を、テクスチャの生データ
        /// (<paramref name="layout"/> の並び)から行う。元の行を帯ごとにマネージドの作業配列へ写し、
        /// 出力の行ごとに並列で平均する(結果は行の順に依らない)。NativeArray はメインスレッドでだけ読む。
        /// </summary>
        internal static unsafe Color32[] ResizeArea(NativeArray<byte> src, Layout layout, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            fixed (Color32* d = dst) ResizeArea(src, layout, sw, sh, dw, dh, d);
            return dst;
        }

        // 上の縮小を、呼び出し側が用意した dw*dh 画素の領域へ書く(プレビューの原本をネイティブへ直接作る)。
        private static unsafe void ResizeArea(NativeArray<byte> src, Layout layout, int sw, int sh, int dw, int dh,
            Color32* dst)
        {
            int rowBytes = sw * layout.bpp;
            int rowsPerDst = (sh + dh - 1) / dh + 1;
            int bandDst = Math.Max(1, Math.Min(dh, BandBytes / Math.Max(1, rowsPerDst * rowBytes)));
            int stripRows = Math.Min(sh, (int)((long)bandDst * sh / dh) + 2);
            var strip = new byte[(long)stripRows * rowBytes];
            for (int yb = 0; yb < dh; yb += bandDst)
            {
                int ye = Math.Min(dh, yb + bandDst);
                int sy0 = SrcRow0(yb, sh, dh);
                int sy1 = SrcRow1(ye - 1, SrcRow0(ye - 1, sh, dh), sh, dh);
                NativeArray<byte>.Copy(src, sy0 * rowBytes, strip, 0, (sy1 - sy0) * rowBytes);
                Parallel.For(yb, ye, y =>
                {
                    int bpp = layout.bpp, ro = layout.r, go = layout.g, bo = layout.b, ao = layout.a;
                    int y0 = SrcRow0(y, sh, dh), y1 = SrcRow1(y, y0, sh, dh);
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = (int)((long)x * sw / dw);
                        int x1 = Math.Max(x0 + 1, (int)(((long)x + 1) * sw / dw));
                        int r = 0, g = 0, b = 0, a = 0, n = 0;
                        for (int yy = y0; yy < y1; yy++)
                        {
                            int row = (yy - sy0) * rowBytes;
                            for (int xx = x0; xx < x1; xx++)
                            {
                                int o = row + xx * bpp;
                                r += strip[o + ro];
                                g += strip[o + go];
                                b += strip[o + bo];
                                a += ao >= 0 ? strip[o + ao] : 255;
                                n++;
                            }
                        }
                        dst[(long)y * dw + x] = Average(r, g, b, a, n);
                    }
                });
            }
        }

        // 出力の行 y が読む元の行 [y0, y1)。
        private static int SrcRow0(int y, int sh, int dh) => (int)((long)y * sh / dh);
        private static int SrcRow1(int y, int y0, int sh, int dh) => Math.Max(y0 + 1, (int)(((long)y + 1) * sh / dh));

        private static Color32 Average(int r, int g, int b, int a, int n)
        {
            int half = n / 2;
            return new Color32(
                (byte)((r + half) / n), (byte)((g + half) / n),
                (byte)((b + half) / n), (byte)((a + half) / n));
        }
    }
}
