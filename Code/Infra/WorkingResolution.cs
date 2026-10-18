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
    /// 編集の作業解像度。プレビュー・スポイト・AI 提案・自動調整・非破壊ビルド(NDMF)は、既定で元ファイルを
    /// 取り込み後の寸法(インポート設定の Max Size で縮んだ大きさ)へ縮めた画素で処理する。ゲームに出るのは
    /// その寸法なので見た目はほぼ変わらず、元ファイルが大きいときのメモリ(8K で 6GB ほど)を使わずに済む。
    /// PNG の書き出し(単体・一括)は、この設定に関係なく元ファイルの解像度で処理する。
    /// 「元の解像度で編集」(<see cref="AtSourceResolution"/>)をオンにすると、作業解像度も元ファイルの
    /// 解像度になる(2026-10-18 より前の動き)。
    /// マスクの土台は設定に関係なく元ファイルの寸法(書き出しで塗った境目が粗くならないように)。
    /// 処理側はマスクと画素の寸法が違っても対応する画素を引く(PixelProcessor.BuildMaskIndexMap)。
    /// 縮小は面積平均。プレビューと非破壊ビルドが同じ縮小・同じ処理を通るので、両者の結果は一致する。
    /// </summary>
    internal static class WorkingResolution
    {
        private const string AtSourcePrefKey = "Iroca.EditAtSourceResolution";
        private static bool? s_atSource;

        /// <summary><see cref="AtSourceResolution"/> が変わったとき(メインスレッド)。</summary>
        public static event Action Changed;

        /// <summary>元ファイルの解像度で編集するか。この PC のいろか全体の設定(既定はオフ)。</summary>
        public static bool AtSourceResolution
        {
            get => s_atSource ??= EditorPrefs.GetBool(AtSourcePrefKey, false);
            set
            {
                if (value == AtSourceResolution) return;
                s_atSource = value;
                EditorPrefs.SetBool(AtSourcePrefKey, value);
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// 元ファイル(fileW×fileH)を編集する寸法。取り込み済みテクスチャが無ければ元ファイルのまま。
        /// </summary>
        public static void Size(int fileW, int fileH, Texture2D imported, out int w, out int h)
        {
            if (imported == null) { w = fileW; h = fileH; return; }
            Size(fileW, fileH, imported.width, imported.height, AtSourceResolution, out w, out h);
        }

        /// <summary>
        /// 寸法の規則。取り込みで小さくなったときは取り込み後の寸法そのもの(2 のべき乗へ丸める取り込みで
        /// 縦横比が変わっても従う。ゲームに出るのもその寸法)。取り込みで大きくなるときは元ファイルのまま
        /// (引き伸ばさない)。
        /// </summary>
        internal static void Size(int fileW, int fileH, int importW, int importH, bool atSource, out int w, out int h)
        {
            w = fileW;
            h = fileH;
            if (atSource || importW <= 0 || importH <= 0) return;
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

        // ───────────── 面積平均の縮小 ─────────────

        /// <summary>
        /// 面積平均で縮小する(整数倍ならボックスフィルタと同じ)。拡大方向のときは最近傍。
        /// 行 0 = 画像下端の並び(Unity の画素順)のまま扱う。
        /// </summary>
        internal static Color32[] ResizeArea(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int y0 = SrcRow0(y, sh, dh), y1 = SrcRow1(y, y0, sh, dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = (int)((long)x * sw / dw);
                    int x1 = Math.Max(x0 + 1, (int)(((long)x + 1) * sw / dw));
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * sw;
                        for (int xx = x0; xx < x1; xx++)
                        {
                            var c = src[row + xx];
                            r += c.r; g += c.g; b += c.b; a += c.a; n++;
                        }
                    }
                    dst[y * dw + x] = Average(r, g, b, a, n);
                }
            }
            return dst;
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
        internal static Color32[] ResizeArea(NativeArray<byte> src, Layout layout, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
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
                        dst[y * dw + x] = Average(r, g, b, a, n);
                    }
                });
            }
            return dst;
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
