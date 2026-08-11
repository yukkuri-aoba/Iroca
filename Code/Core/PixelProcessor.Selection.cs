// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    // PixelProcessor: 選択リファイン後段(ボックス和/ブラー/bbox/穴埋め/境界回復/緩和マッチ)。
    internal static partial class PixelProcessor
    {
        /// <summary>
        /// 分離型ボックス和フィルタ。各ピクセル位置で (2r+1)×(2r+1) 窓内の合計を dst に書き込む
        /// （境界はゼロ拡張：画像外の寄与を 0 として無視）。スライディングウィンドウで O(N) で計算。
        /// 内部 temp バッファは ArrayPool から借用・返却するのでヒープアロケーションなし。
        /// dst は呼び出し元が事前に確保すること（ArrayPool.Rent 推奨）。
        ///
        /// bbox 未指定(boxMaxX&lt;0)なら全画素。指定時は dst をその矩形内だけ埋める(GaussianBlur と
        /// 同じ形)。src は矩形を r だけ広げた範囲 [boxMinX-r, boxMaxX+r]×[boxMinY-r, boxMaxY+r] が
        /// 正しく用意されていれば足り、その外は読まれない。
        /// 矩形指定時はスライディング和の開始位置が変わるため、加算順序が全画素版と一致するのは
        /// **src が整数値(0..255 のバイト値 / 0-1 マスク)で窓和が float の整数精度に収まる**用途に限る
        /// (現在の呼び出し元はすべてこれを満たす)。その場合は丸め差が原理的に生じず出力ビット不変。
        /// </summary>
        private static void BoxFilterSum(float[] src, float[] dst, int w, int h, int r, CancellationToken ct = default,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1)
        {
            int len = w * h;
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }
            float[] temp = s_floatPool.Rent(len);
            var filterPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try
            {
                // 水平パス。垂直パスが読む行は [boxMinY-r, boxMaxY+r] なので、その行範囲だけ作る。
                int hMinY = Mathf.Max(0, boxMinY - r);
                int hMaxY = Mathf.Min(h - 1, boxMaxY + r);
                Parallel.For(hMinY, hMaxY + 1, filterPo, y =>
                {
                    int rowOff = y * w;
                    float sum = 0f;
                    int initBeg = Mathf.Max(0, boxMinX - r);
                    int initEnd = Mathf.Min(w - 1, boxMinX + r);
                    for (int k = initBeg; k <= initEnd; k++) sum += src[rowOff + k];
                    temp[rowOff + boxMinX] = sum;
                    for (int x = boxMinX + 1; x <= boxMaxX; x++)
                    {
                        int subIdx = x - 1 - r;
                        int addIdx = x + r;
                        if (subIdx >= 0) sum -= src[rowOff + subIdx];
                        if (addIdx < w) sum += src[rowOff + addIdx];
                        temp[rowOff + x] = sum;
                    }
                });

                // 垂直パス。1 反復 = 1 列の縦走査だと、4K ではストライド 16KB でキャッシュラインの
                // 1/16 しか使えない。VBlock 列ぶんの走査和をまとめて持ち行方向に進むことで、1 行の
                // アクセスが連続 16 要素(=1 キャッシュライン)になる。各列の加算順序は従来と同一
                // (初期窓を昇順に加算 → 行ごとに sub → add)なので出力ビット不変。
                const int VBlock = 16;
                int blockCount = (boxMaxX - boxMinX + VBlock) / VBlock;
                Parallel.For(0, blockCount, filterPo, bi =>
                {
                    int xs = boxMinX + bi * VBlock;
                    int xe = Mathf.Min(xs + VBlock - 1, boxMaxX);
                    var sums = new float[VBlock];
                    int initBeg = Mathf.Max(0, boxMinY - r);
                    int initEnd = Mathf.Min(h - 1, boxMinY + r);
                    for (int k = initBeg; k <= initEnd; k++)
                    {
                        int krb = k * w;
                        for (int x = xs; x <= xe; x++) sums[x - xs] += temp[krb + x];
                    }
                    int drb = boxMinY * w;
                    for (int x = xs; x <= xe; x++) dst[drb + x] = sums[x - xs];
                    for (int y = boxMinY + 1; y <= boxMaxY; y++)
                    {
                        int subIdx = y - 1 - r;
                        int addIdx = y + r;
                        if (subIdx >= 0)
                        {
                            int srb = subIdx * w;
                            for (int x = xs; x <= xe; x++) sums[x - xs] -= temp[srb + x];
                        }
                        if (addIdx < h)
                        {
                            int arb = addIdx * w;
                            for (int x = xs; x <= xe; x++) sums[x - xs] += temp[arb + x];
                        }
                        int yrb = y * w;
                        for (int x = xs; x <= xe; x++) dst[yrb + x] = sums[x - xs];
                    }
                });
            }
            finally
            {
                s_floatPool.Return(temp);
            }
        }

        /// <summary>
        /// ガウシアンブラーを src に適用して dst に書き込む。
        /// 内部 temp バッファは ArrayPool から借用・返却するのでヒープアロケーションなし。
        /// dst は呼び出し元が事前に確保すること（ArrayPool.Rent 推奨）。
        /// radius &lt; 1 のとき何もしない（dst は未定義のまま）。
        /// 戻り値: ブラー処理を行った場合 true、スキップした場合 false。
        /// </summary>
        private static bool GaussianBlur(float[] src, float[] dst, int w, int h, float sigma,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1,
            CancellationToken ct = default)
        {
            int radius = Mathf.CeilToInt(sigma * 2.5f);
            if (radius < 1) return false;

            // 1D カーネルを構築
            float[] kernel = new float[radius * 2 + 1];
            float kernelSum = 0f;
            for (int i = -radius; i <= radius; i++)
            {
                kernel[i + radius] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                kernelSum += kernel[i + radius];
            }
            for (int i = 0; i < kernel.Length; i++)
                kernel[i] /= kernelSum;

            int len = w * h;
            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけブラーする(P2-7)。矩形は
            // 呼び出し側が「src の非0 が矩形より radius 以上内側」を保証するため、矩形外の真のブラー出力は
            // 常に 0。dst を全クリアしておけば矩形内だけ計算しても全画素ブラーと出力ビット一致する。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            float[] temp = s_floatPool.Rent(len);
            var gaussPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try
            {
                Array.Clear(dst, 0, len);   // 矩形外は 0(全画素ブラーの src=0 領域と一致)
                // 垂直パスが読む行は [boxMinY-radius, boxMaxY+radius] なので、水平パスはその行範囲ぶん
                // 上下に広げて temp を用意する(列は矩形のまま。垂直は矩形列しか読まない)。
                int hMinY = Mathf.Max(0, boxMinY - radius);
                int hMaxY = Mathf.Min(h - 1, boxMaxY + radius);
                // 水平パス
                Parallel.For(hMinY, hMaxY + 1, gaussPo, y =>
                {
                    int rb = y * w;
                    for (int x = boxMinX; x <= boxMaxX; x++)
                    {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int nx = Mathf.Clamp(x + k, 0, w - 1);
                            val += src[rb + nx] * kernel[k + radius];
                        }
                        temp[rb + x] = val;
                    }
                });

                // 垂直パス
                Parallel.For(boxMinY, boxMaxY + 1, gaussPo, y =>
                {
                    int rb = y * w;
                    for (int x = boxMinX; x <= boxMaxX; x++)
                    {
                        float val = 0f;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int ny = Mathf.Clamp(y + k, 0, h - 1);
                            val += temp[ny * w + x] * kernel[k + radius];
                        }
                        dst[rb + x] = val;
                    }
                });
            }
            finally
            {
                s_floatPool.Return(temp);
            }
            return true;
        }

        private static void ConstrainBlur(float[] blurred, float[] original, int w, int h, int radius, CancellationToken ct = default)
        {
            // マッチがなかった領域へのブラーの流出を防止。
            // original > 0 を float マスクに変換して BoxFilterSum に流すことで
            // 近傍チェックを O(N·r²) から O(N) に削減。
            int len = w * h;
            float[]? mask = null, neighborSum = null;
            try
            {
                mask = s_floatPool.Rent(len);
                neighborSum = s_floatPool.Rent(len);
                // 行並列(要素独立・自 index 書き込みのみ=出力ビット不変)。per-index デリゲートを避ける。
                var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
                var maskL = mask;
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        maskL[i] = original[i] > 0f ? 1f : 0f;
                    }
                });

                BoxFilterSum(mask, neighborSum, w, h, radius, ct);

                var neighborSumL = neighborSum;
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        if (original[i] > 0f) continue; // already matched
                        if (neighborSumL[i] <= 0f)
                            blurred[i] = 0f;
                    }
                });
            }
            finally
            {
                if (neighborSum != null) s_floatPool.Return(neighborSum);
                if (mask        != null) s_floatPool.Return(mask);
            }
        }

        /// <summary>
        /// strength &gt; thr かつ α&gt;=128 の画素を囲むバウンディングボックスを求める。
        /// 連結成分系(連結成分アンカリング / 成分別 L マップ)が「連結予測子」と同じ条件で使う共通版。
        /// 行ごとに独立に求めた min/max をマージするだけなので、逐次走査と結果は同一(順序非依存)。
        /// </summary>
        /// <returns>該当画素が 1 つ以上あれば true。</returns>
        private static bool TryComputeMatchedBBox(float[] strength, Color32[] px, int w, int h, float thr,
            out int minX, out int minY, out int maxX, out int maxY, CancellationToken ct = default)
        {
            int lminX = w, lmaxX = -1, lminY = h, lmaxY = -1;
            object gate = new object();
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            Parallel.For(0, h, po,
                () => (minX: w, maxX: -1, minY: h, maxY: -1),
                (y, _, loc) =>
                {
                    int rb = y * w;
                    for (int x = 0; x < w; x++)
                        if (strength[rb + x] > thr && px[rb + x].a >= 128)
                        {
                            if (x < loc.minX) loc.minX = x;
                            if (x > loc.maxX) loc.maxX = x;
                            if (y < loc.minY) loc.minY = y;
                            if (y > loc.maxY) loc.maxY = y;
                        }
                    return loc;
                },
                loc =>
                {
                    lock (gate)
                    {
                        if (loc.minX < lminX) lminX = loc.minX;
                        if (loc.maxX > lmaxX) lmaxX = loc.maxX;
                        if (loc.minY < lminY) lminY = loc.minY;
                        if (loc.maxY > lmaxY) lmaxY = loc.maxY;
                    }
                });
            minX = lminX; minY = lminY; maxX = lmaxX; maxY = lmaxY;
            return lmaxX >= 0;
        }

        /// <summary>
        /// strength &gt; thr の画素を囲むバウンディングボックス(min/max)を求める。
        /// 後段パス(穴埋め/境界回復/ブラー)を実マッチ範囲＋余白に限定し、全画素走査を避けるために使う。
        /// </summary>
        /// <returns>マッチ画素が 1 つ以上あれば true(false のとき bbox は空で、後段パスは no-op)。</returns>
        private static bool TryComputeStrengthBBox(float[] strength, int w, int h, float thr,
            out int minX, out int minY, out int maxX, out int maxY, CancellationToken ct = default)
        {
            // 行ごとに独立に求めた min/max をマージするだけなので順序非依存(逐次走査と同一結果)。
            // 4K では全画素走査なので、単スレッドのままだと後段 bbox の算出自体が無視できない。
            int lminX = w, lmaxX = -1, lminY = h, lmaxY = -1;
            object gate = new object();
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            Parallel.For(0, h, po,
                () => (minX: w, maxX: -1, minY: h, maxY: -1),
                (y, _, loc) =>
                {
                    int rb = y * w;
                    int rowMinX = -1, rowMaxX = -1;
                    for (int x = 0; x < w; x++)
                    {
                        if (strength[rb + x] > thr)
                        {
                            if (rowMinX < 0) rowMinX = x;
                            rowMaxX = x;
                        }
                    }
                    if (rowMaxX >= 0)
                    {
                        if (rowMinX < loc.minX) loc.minX = rowMinX;
                        if (rowMaxX > loc.maxX) loc.maxX = rowMaxX;
                        if (y < loc.minY) loc.minY = y;
                        if (y > loc.maxY) loc.maxY = y;
                    }
                    return loc;
                },
                loc =>
                {
                    lock (gate)
                    {
                        if (loc.minX < lminX) lminX = loc.minX;
                        if (loc.maxX > lmaxX) lmaxX = loc.maxX;
                        if (loc.minY < lminY) lminY = loc.minY;
                        if (loc.maxY > lmaxY) lmaxY = loc.maxY;
                    }
                });
            minX = lminX; minY = lminY; maxX = lmaxX; maxY = lmaxY;
            return lmaxX >= 0;
        }

        /// <summary>
        /// 形態学的フィル：ゼロ強度のピクセルがマッチした隣接ピクセルの多数派に囲まれていれば
        /// 最小隣接強度で埋める。
        /// これにより、satConfidenceゲートを通過するに低い彩度を持つアンチエイリアス処理された
        /// 端のピクセルが原因の孤立した1-2pxドットアーティファクトを除去します。
        /// パス数と最小隣接数はアドバンスモードで調整可能。
        /// </summary>
        /// <remarks>
        /// ダブルバッファリング方式: パスごとの配列クローンを避け、事前確保した
        /// バッファを読み書きで swap することで大テクスチャでのメモリコピーを削減。
        /// </remarks>
        private static void FillSmallHoles(float[] strength, int w, int h,
            int passes = 3, int minNeighbors = 4, bool[] allowedMask = null,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1,
            CancellationToken ct = default)
        {
            if (passes <= 0) return;
            // minNeighbors<=0 だと「matched>=0 かつ total>=0」が常に真になり、relaxed 許可領域
            // 全体が毎パス埋まる（プリセット JSON 由来の 0 で到達し得た。レビュー §4 低）。
            // 8 近傍のうち最低 1 つは一致していることを要求し、上限も近傍数で頭打ちにする。
            minNeighbors = Mathf.Clamp(minNeighbors, 1, 8);

            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけ近傍走査する(P2-7)。
            // 矩形外は呼び出し側が「処理前後とも 0」を保証するので走査を省いても出力ビット不変。
            // Array.Copy(全画素)は安価なので残し、近傍を読む重いループだけを矩形に絞る。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            int len = w * h;
            float[] buffer = s_floatPool.Rent(len);
            var fillPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try
            {
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(read, write, len);

                Parallel.For(boxMinY, boxMaxY + 1, fillPo, y =>
                {
                    for (int x = boxMinX; x <= boxMaxX; x++)
                    {
                        int idx = y * w + x;
                        if (read[idx] > 0f) continue;
                        // relaxed ゲート: 画素自身が relaxed マッチを通る色でなければ埋めない。
                        // マッチ領域に囲まれただけの背景グレー/白を full strength に塗らないことで、
                        // 薄いロゴ周辺のフリンジ(白/灰ノイズ)を構造的に防ぐ。
                        if (allowedMask != null && !allowedMask[idx]) continue;

                        int matched = 0;
                        int total = 0;
                        float minNeighbour = 1f;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                total++;
                                float ns = read[ny * w + nx];
                                if (ns > 0f)
                                {
                                    matched++;
                                    if (ns < minNeighbour) minNeighbour = ns;
                                }
                            }
                        }

                        // 最小隣接数以上のマッチした隣接ピクセルがあれば埋める
                        if (matched >= minNeighbors && total >= minNeighbors)
                            write[idx] = minNeighbour;
                    }
                });

                // read/write を入れ替え
                var tmp = read;
                read = write;
                write = tmp;
            }

            // 最新結果が呼び出し元の strength 配列に入るように調整
            if (!ReferenceEquals(read, strength))
                System.Array.Copy(read, strength, len);
            } // end try
            finally
            {
                s_floatPool.Return(buffer);
            }
        }

        // 彩度天井ゲート(グレーモード専用)のコア近接保護半径。この px 以内に低彩度コアが
        // ある高彩度画素は「素材自身の装飾/陰影」(クリーム素材のピンク縫い取りや濃い目の
        // シェーディング帯、三角の頂点ハイライト等)とみなして除去しない。手描きテクスチャの
        // 陰影グラデ帯・先端の装飾はコアの地色から数 px〜10px 規模で連続するため、帯を覆える
        // 幅(実測で GT 高彩度画素の 99%+ をカバー)に置く。独立した別素材はコアから
        // 数十〜数百 px 離れているため、この半径では保護されない。
        private const int ChromaCeilProtectRadius = 12;

        /// <summary>
        /// 彩度天井ゲート(グレーモード専用・コア近接保護付き)を post-match に適用する。
        /// 無彩/微 tint サンプルの彩度包絡(ceil=max(sS*ChromaCeilSampleFrac, ChromaCeilAbs))を
        /// 超える高彩度画素のうち、低彩度コア(strength>0.05 かつ pS&lt;ceil)から
        /// ChromaCeilProtectRadius px より遠いものを strength(と matchConf)から除去する。
        /// 白い布をスポイトしたときクリーム色のワンピース等の別素材が巻き込まれるのを防ぐ。
        /// 素材自身の高彩度ディテール(三角のピンク角・濃い陰影帯等)は低彩度コアに隣接する
        /// ため保護される。除去は部分強度への格下げでなく 0 へのハード除去とする
        /// (部分強度は出力を中途半端な明るさにし、陰影相関 form_fidelity を壊すことが判明)。
        /// matchConf も除去しないと flood fill が「確信コアを含む成分」として別素材を保持する。
        /// 保護はコアからの 4 近傍 dilation を 1 回限りで行い、保護の連鎖はしない。
        /// </summary>
        private static void ApplyChromaCeilingGate(
            float[] strength, float[] matchConf, float[] pixS,
            float sS, float sV, float chromaThreshold,
            int w, int h, CancellationToken ct = default)
        {
            // グレーモード判定は ColorZone.MatchOneSample / GetRelaxedMatchStrength と共有ヘルパー。
            float effectiveChromaThreshold = ColorZone.GrayModeEffectiveChromaThreshold(sV, chromaThreshold);
            if (sS > effectiveChromaThreshold) return;   // 有彩サンプル=グレーモードではない

            float satCeil = Mathf.Max(sS * ColorZone.ChromaCeilSampleFrac, ColorZone.ChromaCeilAbs);

            int len = w * h;
            const float matchThr = ColorZone.MatchStrengthFloor;  // コア判定の strength 床(NeutralReject と同じ)
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            bool[] cur = s_boolPool.Rent(len);
            bool[] nxt = s_boolPool.Rent(len);
            try
            {
                // 低彩度コア: 選択済み かつ 彩度が天井未満。同時に保護候補(高彩度画素)の有無を調べる。
                bool anyHigh = false;
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    bool localHigh = false;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        cur[i] = strength[i] > matchThr && pixS[i] < satCeil;
                        if (pixS[i] >= satCeil) localHigh = true;
                    }
                    if (localHigh) anyHigh = true;
                });
                if (!anyHigh) return;

                // 低彩度コアから 4 近傍 dilation を ProtectRadius 回(保護領域を広げる)。画像端外は false。
                for (int it = 0; it < ChromaCeilProtectRadius; it++)
                {
                    var curL = cur; var nxtL = nxt;
                    Parallel.For(0, h, po, y =>
                    {
                        int rowOff = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = rowOff + x;
                            bool on = curL[i]
                                || (x > 0 && curL[i - 1])
                                || (x < w - 1 && curL[i + 1])
                                || (y > 0 && curL[i - w])
                                || (y < h - 1 && curL[i + w]);
                            nxtL[i] = on;
                        }
                    });
                    (cur, nxt) = (nxt, cur);
                }

                // 保護領域外の高彩度選択画素を除去(別素材)。matchConf も除去しないと
                // flood fill が「確信コアを含む成分」として別素材を保持してしまう。
                var prot = cur;
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        if (!prot[i] && pixS[i] >= satCeil && strength[i] > 0f)
                        {
                            strength[i] = 0f;
                            if (matchConf != null) matchConf[i] = 0f;
                        }
                    }
                });
            }
            finally
            {
                s_boolPool.Return(cur);
                s_boolPool.Return(nxt);
            }
        }

        // 中性ツヤ復帰(グレーモード専用)の開領域伝播の収束上限。前方/後方ラスタ走査の対で
        // 伝播させるため、素直な UV レイアウトなら数回で収束する(細い渦巻き状の通路だけが
        // 多くの反復を要するが、テクスチャのパディングはそうならない)。到達しきらなかった
        // 場合は「開領域が未確定=復帰しない」側に倒れるので安全。
        private const int EnclosedNeutralMaxSweeps = 24;

        /// <summary>
        /// 彩度整合ゲートが落とした「素材自身の純白ツヤ」を空間的に復帰させる(グレーモード専用)。
        ///
        /// 彩度整合ゲート(ColorZone.Match の床ゲート)は、サンプルより著しく中性な画素=純白の
        /// UV パディングを別マテリアルとみなし距離加算で落とす。これは実測で有効(Feina では
        /// パディングへの巻き込みが false positive の 0.0〜0.9% に収まる)一方、生成り/オフホワイトの
        /// 布のように **素材自身のツヤが純白へ脱彩する** 場合、そのハイライトも同じ色になるため
        /// 巻き添えで落ちる(実測: 白衣装の recall 0.665)。両者は色が完全に同一なので、色空間の
        /// どのしきい値でも分離できない。
        ///
        /// 唯一残る非対称性は空間にある。パディングは画像端まで繋がった開領域だが、素材のツヤは
        /// 選択済みの地色に囲まれた閉領域である。そこで未選択画素を画像端から辿り、届かなかった
        /// 閉領域のうち「ゲートが落としたはずの画素」だけを戻す。ApplyChromaCeilingGate
        /// (コア近接保護付きの空間ゲート)の鏡像で、判定は連結性とサンプル相対の色量のみ=
        /// 特定キャラ・色・座標には依存しない。
        ///
        /// 復帰対象は「ゲートが無ければマッチしていたはず」の画素に限る:
        ///   ・未選択(strength &lt;= matchThr)
        ///   ・彩度がゲートの床未満(= ゲートがペナルティを課した画素)
        ///   ・グレーモードの素の RGB 距離が tolerance 以内(= 距離加算だけが棄却の理由だった)
        /// 部分強度でなく full strength で戻す(部分強度は出力を中途半端な明るさにして陰影相関を
        /// 壊す、という ApplyChromaCeilingGate と同じ理由)。
        ///
        /// 連結性は大域演算なので **フル画像経路でのみ**呼ぶこと(呼び出し側で担保)。部分クロップで
        /// 走らせるとクロップ境界に接した閉領域が開領域と誤判定される。
        /// </summary>
        private static void RecoverEnclosedNeutral(
            float[] strength, float[] matchConf, float[] pixS, Color32[] pixels,
            Color sampleColor, float tolerance, float sS, float sV, float chromaThreshold,
            int w, int h, CancellationToken ct = default)
        {
            // グレーモード判定は ColorZone.MatchOneSample / ApplyChromaCeilingGate と共有ヘルパー。
            float effectiveChromaThreshold = ColorZone.GrayModeEffectiveChromaThreshold(sV, chromaThreshold);
            if (sS > effectiveChromaThreshold) return;   // 有彩サンプル=グレーモードではない
            // 彩度整合ゲートが作動しないサンプル(真の無彩)では打ち消す対象が無い。
            if (sS <= ColorZone.ChromaGateActivateSat) return;
            // 暗いサンプルではゲート重みがフェードし、距離指標も pS 側へ lerp されるため
            // 素の RGB 距離では「ゲートが無ければマッチしたか」を再現できない。対象外。
            if (sV < ColorZone.GrayModeDarkSampleValue) return;

            float satFloor = Mathf.Min(sS * ColorZone.ChromaGateFloorFrac, ColorZone.ChromaGateFloorCap);
            float sr = sampleColor.r, sg = sampleColor.g, sb = sampleColor.b;

            int len = w * h;
            const float matchThr = ColorZone.MatchStrengthFloor;   // 未選択判定(ApplyChromaCeilingGate と同じ床)
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            bool[] free = s_boolPool.Rent(len);   // 未選択=開領域を辿れる画素
            bool[] open = s_boolPool.Rent(len);   // 画像端から到達できた未選択画素
            try
            {
                bool anyCandidate = false;
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    bool local = false;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        bool f = strength[i] <= matchThr;
                        free[i] = f;
                        open[i] = false;
                        if (f && pixS[i] < satFloor) local = true;
                    }
                    if (local) anyCandidate = true;
                });
                if (!anyCandidate) return;

                // 画像端の未選択画素を種に、未選択画素だけを 4 近傍で伝播させる。前方(左/上から)と
                // 後方(右/下から)のラスタ走査を交互に回すと、単純な dilation を距離ぶん繰り返すより
                // 桁違いに速く収束する。
                bool changed = true;
                for (int sweep = 0; sweep < EnclosedNeutralMaxSweeps && changed; sweep++)
                {
                    ct.ThrowIfCancellationRequested();
                    changed = false;
                    for (int y = 0; y < h; y++)
                    {
                        int rowOff = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = rowOff + x;
                            if (!free[i] || open[i]) continue;
                            if (y == 0 || x == 0 || open[i - 1] || open[i - w])
                            {
                                open[i] = true;
                                changed = true;
                            }
                        }
                    }
                    for (int y = h - 1; y >= 0; y--)
                    {
                        int rowOff = y * w;
                        for (int x = w - 1; x >= 0; x--)
                        {
                            int i = rowOff + x;
                            if (!free[i] || open[i]) continue;
                            if (y == h - 1 || x == w - 1 || open[i + 1] || open[i + w])
                            {
                                open[i] = true;
                                changed = true;
                            }
                        }
                    }
                }

                // 閉領域(画像端から到達できなかった未選択画素)のうち、ゲートが落としたはずの
                // 中性画素を full strength で戻す。
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        if (!free[i] || open[i]) continue;
                        if (pixS[i] >= satFloor) continue;
                        Color32 c = pixels[i];
                        float dr = c.r / 255f - sr, dg = c.g / 255f - sg, db = c.b / 255f - sb;
                        float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * ColorZone.InvSqrt3;
                        if (rgbDist > tolerance) continue;   // ゲート以外の理由で外れていた画素
                        strength[i] = 1f;
                        if (matchConf != null) matchConf[i] = 1f;
                    }
                });
            }
            finally
            {
                s_boolPool.Return(free);
                s_boolPool.Return(open);
            }
        }

        /// <summary>
        /// 境界復元：少なくとも1つのマッチしたピクセルに隣接するマッチしないピクセルについて
        /// 元の固定低彩度閾値（satMin=0.02, satRamp=0.08）を使用してカラーマッチを再評価します。
        /// これにより、厳格な動的satMinが拒否したアンチエイリアス端のピクセルに対して
        /// 正しい段階的な強度値が得られます。各パスはマッチした境界からさらに1ピクセル
        /// 外側への復元を拡張します。隣接要件により、内部領域でのAO/影のにじみを防ぎます。
        /// </summary>
        private static void RecoverBoundaryEdges(
            float[] strength, int w, int h,
            float[] pixH, float[] pixS, float[] pixV,
            Color sampleColor, float tolerance,
            float edgeSoftness, float valueWeight, float satDistWeight,
            float relaxedSatMin, float relaxedSatRamp, float shadowForgivenessSatMin, int passes,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1,
            Color32[] originalPixels = null, float chromaConfidence = 1f, float chromaThreshold = 0.05f,
            CancellationToken ct = default)
        {
            if (passes <= 0) return;

            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけ走査する(P2-7、出力ビット不変)。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            float sH, sS, sV;
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);
            // relaxed ゲートの RGB ブレンド用 sample RGB(0..1)。
            float rcSampR = sampleColor.r, rcSampG = sampleColor.g, rcSampB = sampleColor.b;

            int len = w * h;
            float[] buffer = s_floatPool.Rent(len);
            var recoverPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            try
            {
            float[] read = strength;
            float[] write = buffer;

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(read, write, len);

                Parallel.For(boxMinY, boxMaxY + 1, recoverPo, y =>
                {
                    for (int x = boxMinX; x <= boxMaxX; x++)
                    {
                        int idx = y * w + x;
                        if (read[idx] > 0f) continue;

                        // Check if adjacent to at least one matched pixel
                        bool hasMatchedNeighbor = false;
                        for (int dy = -1; dy <= 1 && !hasMatchedNeighbor; dy++)
                            for (int dx = -1; dx <= 1 && !hasMatchedNeighbor; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                                if (read[ny * w + nx] > 0f) hasMatchedNeighbor = true;
                            }

                        if (!hasMatchedNeighbor) continue;

                        // Re-evaluate this pixel with the relaxed fixed saturation threshold
                        float rpR = 0f, rpG = 0f, rpB = 0f;
                        if (originalPixels != null)
                        {
                            Color32 rop = originalPixels[idx];
                            rpR = rop.r / 255f; rpG = rop.g / 255f; rpB = rop.b / 255f;
                        }
                        float relaxed = GetRelaxedMatchStrength(
                            pixH[idx], pixS[idx], pixV[idx],
                            sH, sS, sV, tolerance, edgeSoftness, valueWeight,
                            satDistWeight, relaxedSatMin, relaxedSatRamp, shadowForgivenessSatMin,
                            rpR, rpG, rpB, rcSampR, rcSampG, rcSampB, chromaConfidence, chromaThreshold);
                        if (relaxed > 0f)
                            write[idx] = relaxed;
                    }
                });

                var tmp = read;
                read = write;
                write = tmp;
            }

            if (!ReferenceEquals(read, strength))
                System.Array.Copy(read, strength, len);
            } // end try
            finally
            {
                s_floatPool.Return(buffer);
            }
        }

        /// <summary>
        /// 緩和された彩度閾値を使用したカラーマッチ。
        /// すでにマッチした領域に隣接する境界ピクセルにのみ使用されます。
        /// アドバンスモードでrelaxedSatMin/relaxedSatRamp/satDistWeightを調整可能。
        /// </summary>
        /// <remarks>
        /// FIX: satConfidence による強度ダンピングを廃止。境界復元はすでにマッチ済みピクセルに
        /// 隣接するピクセルのみが対象なので、「微妙にマッチさせる」より「マッチさせるなら全強度で」
        /// の方が見た目が綺麗。低 satConfidence (例: 0.3) を掛けると AA 縁が部分的に元色を残し、
        /// 白装飾の周囲などにピンク/赤の残留ピクセルが見える原因になっていた。
        /// 純白装飾はそのまま残すため、relaxedSatMin による「最低彩度ゲート」だけは保持する。
        /// relaxedSatRamp は引数互換のため残置（未使用）。
        /// </remarks>
        private static float GetRelaxedMatchStrength(
            float pH, float pS, float pV,
            float sH, float sS, float sV,
            float tolerance, float edgeSoftness, float valueWeight,
            float satDistWeight, float relaxedSatMin, float relaxedSatRamp, float shadowForgivenessSatMin,
            float pR = 0f, float pG = 0f, float pB = 0f,
            float sR = 0f, float sG = 0f, float sB = 0f, float chromaConfidence = 1f,
            float chromaThreshold = 0.05f)
        {
            // ColorZone.MatchOneSample と共有ヘルパーによる動的しきい値（暗いサンプルほど範囲拡大）。
            // 上端は zone.chromaThreshold(ユーザー可変)を使う。以前は既定値 0.05 を焼き込んでいたため、
            // ユーザーが chromaThreshold を変えると主経路と穴埋め/境界回復でグレーモード判定が食い違っていた。
            float effectiveChromaThreshold = ColorZone.GrayModeEffectiveChromaThreshold(sV, chromaThreshold);

            // 純白装飾はそのまま残す: relaxedSatMin 未満は弾く（ハードゲート）
            // ただしサンプル自体が高彩度の場合のみ適用（暗サンプルの低彩度ピクセルは通過させる）
            if (pS < relaxedSatMin && sS > effectiveChromaThreshold) return 0f;

            // サンプルが暗い型（指定出来ない彩度）の場合: 動的頃値を使って判定
            if (sS <= effectiveChromaThreshold)
            {
                // 主経路(GetColorMatchScores グレーモード)と同じ RGB 距離で判定する。
                // 旧実装は値距離 |pV-sV| のみで、明るいサンプルでは色に関係なく「明るい」だけで一致
                // したため、対象と同じくらい明るい隣接の有彩色(布地の縁など)まで境界回復/穴埋めが
                // 拾い、再着色色が対象の元領域を超えてはみ出していた。RGB 距離なら主経路と同じく
                // 色の遠い隣接色(距離>tol)を拒否し、対象自身の AA 縁(地色寄りの混色)だけを回復する。
                // 暗サンプルでは Lerp で pS へ収束=従来同等。
                float dr = pR - sR, dg = pG - sG, db = pB - sB;
                float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * ColorZone.InvSqrt3;
                float darknessFactor = Mathf.Clamp01((ColorZone.GrayModeDarkSampleValue - sV) / ColorZone.GrayModeDarkSampleValue);
                float effectiveDist = Mathf.Lerp(rgbDist, pS, darknessFactor);
                // 輝度盲対策: 純黒サンプルで純白まで距離0マッチするのを防ぐ。ヘッドルーム超えの
                // 明るさに輝度超過ペナルティを加える。ColorZone.MatchOneSample のグレーモードと同期。
                float lumExcess = Mathf.Max(0f, (pV - sV) - ColorZone.GrayHighlightHeadroom);
                effectiveDist += lumExcess * ColorZone.GrayLumExcessWeight;
                // 彩度整合ゲート(主経路 MatchOneSample と共有ヘルパー)。サンプルが微小な tint を
                // 持つとき、それより著しく中性寄りの候補(純白 UV 背景等)を距離加算でソフト排除する。
                // 明るい tint 付きサンプル(生成りの布地など)では値距離だと純白(pV≈sV)が一致するため、
                // ここでも必要。
                effectiveDist += ColorZone.GrayChromaGatePenalty(sS, sV, pS, tolerance);
                // 彩度天井ゲート(主経路 GetColorMatchScores のグレーモードと同期): 無彩/微 tint
                // 素材の彩度包絡を超える高彩度画素(染められた別素材)に距離を加算する。
                // 穴埋め/境界回復が主経路で弾かれた別素材を復元してしまわないよう同じゲートを課す。
                {
                    float ceilWeight = Mathf.Clamp01(sV / ColorZone.GrayModeDarkSampleValue);
                    float satCeil = Mathf.Max(sS * ColorZone.ChromaCeilSampleFrac, ColorZone.ChromaCeilAbs);
                    float overS = Mathf.Clamp01((pS - satCeil) / Mathf.Max(satCeil, 1e-4f));
                    effectiveDist += overS * ColorZone.ChromaCeilPenalty * tolerance * ceilWeight;
                }
                if (effectiveDist >= tolerance) return 0f;
                float sr = tolerance * edgeSoftness;
                float hr = tolerance - sr;
                if (sr < 0.0001f || effectiveDist <= hr) return 1f;
                return 1f - (effectiveDist - hr) / sr;
            }

            float hDist = Mathf.Abs(pH - sH);
            if (hDist > 0.5f) hDist = 1f - hDist;

            // 境界ピクセルはAAブレンディングから低下した彩度を持つと予想される
            float sDist = Mathf.Abs(pS - sS);
            float vDist = Mathf.Abs(pV - sV);
            float sRatio = (sS > 0.01f) ? Mathf.Clamp01(pS / sS) : 1f;
            float dist = hDist + sDist * satDistWeight + vDist * valueWeight * (1f - sRatio);

            // 低彩度サンプル(白/灰)では HSV 距離が hue 支配になり、同色相だが彩度だけが大きく違う別色
            // (オフホワイトの地の隣にある鮮やかな布地など)を弾けず、境界回復が別色を周囲へ大量にスピル
            // させる。
            // プライマリ(CalculateHybridDistance)と同じ RGB 距離ブレンドで整合させる:
            // dist = lerp(rgbDist, hsvDist, chromaConfidence)。有彩サンプルは cc≈1 で従来式と一致。
            if (chromaConfidence < 0.999f)
            {
                float dr = pR - sR, dg = pG - sG, db = pB - sB;
                float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * ColorZone.InvSqrt3;
                dist = rgbDist * (1f - chromaConfidence) + dist * chromaConfidence;
            }

            // シャドウ（暗い色）の境界距離許容は廃止（プライマリ CalculateHybridDistance と同期）。
            // 緩和マッチ(穴埋め/境界回復)で near-black の別マテリアルを tolerance 内へ逆送し
            // 巻き込みを広げる経路だったため除去する。

            if (dist >= tolerance) return 0f;

            float softRange = tolerance * edgeSoftness;
            float hardRange = tolerance - softRange;

            float strength;
            if (softRange < 0.0001f)
                strength = 1f;
            else if (dist <= hardRange)
                strength = 1f;
            else
                strength = 1f - (dist - hardRange) / softRange;

            // satConfidence ダンピング廃止 → AA 縁を全強度で再色化
            return strength;
        }
    }
}
