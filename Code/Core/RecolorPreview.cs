// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 再着色の before/after を並べた「比較パネル画像」と、変化量メトリクスを生成する純ユーティリティ。
    ///
    /// 目的: ヘッドレス自動化(<see cref="IrocaAutomation"/>)経由で AI エージェントが色替えを実行するとき、
    /// エージェントは結果テクスチャを直接は「見られない」。そこで
    ///   ・比較パネル PNG（左=変換前 / 中=変換後 / 右=変化画素をマゼンタ表示）
    ///   ・変化量の数値（変化率・最大連結成分の占有率＝過検出/散在の兆候）
    /// を返し、エージェントがパネルを Read で開いて目視し、数値で裏取りできるようにする。
    ///
    /// 再着色アルゴリズム本体（<see cref="PixelProcessor"/>）には一切関与しない診断用の後段生成。
    /// UnityEditor 非依存(Color32 配列と System のみ)なので headless ハーネスでも実行・検証できる。
    /// </summary>
    internal static class RecolorPreview
    {
        /// <summary>比較パネル各タイルの最大辺(px)。これを超える入力はアスペクト比維持で縮小する。</summary>
        public const int DefaultMaxTile = 512;

        /// <summary>タイル間の仕切り(px)。</summary>
        public const int PanelGutter = 4;

        /// <summary>変化量メトリクス。過検出(散在)や変換漏れの兆候を数値で表す。</summary>
        public struct ChangeMetrics
        {
            public int totalPixels;
            public int changedPixels;
            public float changedFraction;      // changedPixels / totalPixels
            // 変化領域のバウンディングボックス(px)。変化ゼロなら全て 0。
            public int bboxX, bboxY, bboxW, bboxH;
            // 変化画素のうち最大 4-連結成分が占める割合(0..1)。1 に近い＝まとまった 1 領域(健全)、
            // 小さい＝散在(過検出/ノイズの兆候)。
            public float largestComponentFraction;
            public int componentCount;         // 4-連結成分数
        }

        // 変化判定: RGBA のいずれかが異なれば変化とみなす(α は再着色で不変前提だが安全側で含める)。
        private static bool Changed(Color32 a, Color32 b) =>
            a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a;

        /// <summary>before/after を突き合わせて変化量メトリクスを算出する。</summary>
        public static ChangeMetrics ComputeMetrics(Color32[] before, Color32[] after, int w, int h)
        {
            var m = new ChangeMetrics { totalPixels = Math.Max(0, w * h) };
            if (before == null || after == null || before.Length != after.Length ||
                before.Length != w * h || w <= 0 || h <= 0)
                return m;

            var changed = new bool[w * h];
            int minX = w, minY = h, maxX = -1, maxY = -1, cnt = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (!Changed(before[i], after[i])) continue;
                    changed[i] = true; cnt++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            m.changedPixels = cnt;
            m.changedFraction = m.totalPixels > 0 ? (float)cnt / m.totalPixels : 0f;
            if (cnt > 0)
            {
                m.bboxX = minX; m.bboxY = minY;
                m.bboxW = maxX - minX + 1; m.bboxH = maxY - minY + 1;
                LargestComponent(changed, w, h, cnt, out int largest, out int comps);
                m.largestComponentFraction = (float)largest / cnt;
                m.componentCount = comps;
            }
            return m;
        }

        // 4-連結成分の最大サイズと個数を求める。走査中に changed を消費(visited 兼用)するので破壊的。
        // スタックは changed 画素数で上限が付く(各画素は高々 1 回 push される)。
        private static void LargestComponent(bool[] changed, int w, int h, int changedCount,
            out int largest, out int componentCount)
        {
            largest = 0; componentCount = 0;
            var stack = new int[changedCount];
            int n = changed.Length;
            for (int start = 0; start < n; start++)
            {
                if (!changed[start]) continue;
                componentCount++;
                int sp = 0, size = 0;
                stack[sp++] = start; changed[start] = false;
                while (sp > 0)
                {
                    int p = stack[--sp]; size++;
                    int px = p % w;
                    if (px > 0)     { int q = p - 1; if (changed[q]) { changed[q] = false; stack[sp++] = q; } }
                    if (px < w - 1) { int q = p + 1; if (changed[q]) { changed[q] = false; stack[sp++] = q; } }
                    if (p >= w)     { int q = p - w; if (changed[q]) { changed[q] = false; stack[sp++] = q; } }
                    if (p < n - w)  { int q = p + w; if (changed[q]) { changed[q] = false; stack[sp++] = q; } }
                }
                if (size > largest) largest = size;
            }
        }

        /// <summary>入力(w×h)をアスペクト比維持で maxTile 以内へ縮小したときのタイル寸法を求める(拡大はしない)。</summary>
        public static void ComputeTileSize(int w, int h, int maxTile, out int tileW, out int tileH)
        {
            if (w <= 0 || h <= 0) { tileW = 1; tileH = 1; return; }
            int m = Math.Max(w, h);
            if (m <= maxTile) { tileW = w; tileH = h; return; }
            double s = (double)maxTile / m;
            tileW = Math.Max(1, (int)Math.Round(w * s));
            tileH = Math.Max(1, (int)Math.Round(h * s));
        }

        /// <summary>
        /// 比較パネル画像を Color32 配列で組み立てる。横並びで [変換前 | 変換後 | 変化マップ] の 3 タイル。
        /// 変化マップは変換前のグレースケール(暗)にマゼンタで変化画素を重ね、過検出を目視しやすくする。
        /// 返り値は <paramref name="panelW"/>×<paramref name="panelH"/> の行優先(GetPixels32 と同じ下→上)配列。
        /// </summary>
        public static Color32[] BuildComparisonPanel(Color32[] before, Color32[] after, int w, int h,
            int maxTile, out int panelW, out int panelH)
        {
            ComputeTileSize(w, h, maxTile, out int tw, out int th);
            var tileBefore = DownscaleBox(before, w, h, tw, th);
            var tileAfter = DownscaleBox(after, w, h, tw, th);
            var tileChange = BuildChangeTile(before, after, w, h, tw, th);

            panelW = tw * 3 + PanelGutter * 2;
            panelH = th;
            var panel = new Color32[panelW * panelH];
            var gutter = new Color32(32, 32, 32, 255);
            for (int i = 0; i < panel.Length; i++) panel[i] = gutter;

            Blit(panel, panelW, tileBefore, tw, th, 0);
            Blit(panel, panelW, tileAfter, tw, th, tw + PanelGutter);
            Blit(panel, panelW, tileChange, tw, th, tw * 2 + PanelGutter * 2);
            return panel;
        }

        // src(w×h)を tw×th へボックス平均で縮小する。等倍(tw==w,th==h)なら実質コピー。
        private static Color32[] DownscaleBox(Color32[] src, int w, int h, int tw, int th)
        {
            var dst = new Color32[tw * th];
            if (src == null || src.Length != w * h) return dst;
            for (int dy = 0; dy < th; dy++)
            {
                int sy0 = (int)((long)dy * h / th);
                int sy1 = (int)((long)(dy + 1) * h / th);
                if (sy1 <= sy0) sy1 = sy0 + 1;
                for (int dx = 0; dx < tw; dx++)
                {
                    int sx0 = (int)((long)dx * w / tw);
                    int sx1 = (int)((long)(dx + 1) * w / tw);
                    if (sx1 <= sx0) sx1 = sx0 + 1;
                    long r = 0, g = 0, b = 0, a = 0; int n = 0;
                    for (int sy = sy0; sy < sy1 && sy < h; sy++)
                    {
                        int row = sy * w;
                        for (int sx = sx0; sx < sx1 && sx < w; sx++)
                        {
                            var c = src[row + sx];
                            r += c.r; g += c.g; b += c.b; a += c.a; n++;
                        }
                    }
                    dst[dy * tw + dx] = n > 0
                        ? new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n))
                        : new Color32(0, 0, 0, 255);
                }
            }
            return dst;
        }

        // 変化マップタイル。縮小ブロック内に 1 つでも変化画素があればマゼンタ(=変化を潰さない max プーリング)、
        // なければ変換前の輝度を暗く落としたグレー。
        private static Color32[] BuildChangeTile(Color32[] before, Color32[] after, int w, int h, int tw, int th)
        {
            var dst = new Color32[tw * th];
            var hot = new Color32(255, 0, 255, 255);
            for (int dy = 0; dy < th; dy++)
            {
                int sy0 = (int)((long)dy * h / th);
                int sy1 = (int)((long)(dy + 1) * h / th);
                if (sy1 <= sy0) sy1 = sy0 + 1;
                for (int dx = 0; dx < tw; dx++)
                {
                    int sx0 = (int)((long)dx * w / tw);
                    int sx1 = (int)((long)(dx + 1) * w / tw);
                    if (sx1 <= sx0) sx1 = sx0 + 1;
                    bool any = false; long lum = 0; int n = 0;
                    for (int sy = sy0; sy < sy1 && sy < h; sy++)
                    {
                        int row = sy * w;
                        for (int sx = sx0; sx < sx1 && sx < w; sx++)
                        {
                            int i = row + sx;
                            if (Changed(before[i], after[i])) any = true;
                            var c = before[i];
                            lum += (long)(c.r * 0.299f + c.g * 0.587f + c.b * 0.114f);
                            n++;
                        }
                    }
                    if (any) dst[dy * tw + dx] = hot;
                    else
                    {
                        byte g = (byte)(n > 0 ? lum / n * 35 / 100 : 0);
                        dst[dy * tw + dx] = new Color32(g, g, g, 255);
                    }
                }
            }
            return dst;
        }

        // タイルをパネルの水平オフセット xOff へ貼り込む(行優先)。
        private static void Blit(Color32[] dst, int dstW, Color32[] tile, int tw, int th, int xOff)
        {
            for (int y = 0; y < th; y++)
                Array.Copy(tile, y * tw, dst, y * dstW + xOff, tw);
        }
    }
}
