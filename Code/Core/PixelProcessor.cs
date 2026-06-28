// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// バックグラウンド処理に渡すためのマスク一式のイミュータブルスナップショット。
    /// 配列は deep clone 済み（呼び出し側の書き換えと競合しない）。
    /// </summary>
    internal class MaskSnapshot
    {
        // 1 画素 = 1 bit のビットパック表現(true=除外)。bool[] を deep clone すると 4K で 16.7MB/枚に
        // なりプレビュー再生成・ペイントのたびに GC を圧迫するため、スナップショットは 1/8 サイズの
        // ulong[] で保持する。idx 番目の画素は (arr[idx>>6] >> (idx&63)) & 1。範囲は width*height。
        // 注: 作業用マスク(MaskPaintView.exclusionMask / zoneMasks)や保存形式(MaskFileStore)は
        //     bool[] / RLE のまま。ここはスレッドへ渡すスナップショットの内部表現のみを packed 化する。
        public ulong[] common;
        public int width;
        public int height;
        public Dictionary<string, ulong[]> zones;

        /// <summary>bool[](true=除外)を 1bit/画素の ulong[] にパックする。null は null を返す。</summary>
        public static ulong[] Pack(bool[] mask)
        {
            if (mask == null) return null;
            var packed = new ulong[(mask.Length + 63) >> 6];
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) packed[i >> 6] |= 1UL << (i & 63);
            return packed;
        }

        /// <summary>パック済みマスクの idx 番目ビットを読む(null・範囲外は false)。</summary>
        public static bool GetBit(ulong[] packed, int idx)
        {
            if (packed == null) return false;
            int word = idx >> 6;
            if (word < 0 || word >= packed.Length) return false;
            return (packed[word] & (1UL << (idx & 63))) != 0UL;
        }
    }

    /// <summary>
    /// 連結成分アンカリングの「残った画素(=full画像の keep)」をゾーン別にビットパックで保持する
    /// キャッシュ。連結性は大域演算でフル画像経路でしか正しく解けないため、メインプレビュー(フル
    /// 画像)で解いた結果をここへ書き、詳細プレビュー(部分クロップ)へフル座標で転写して両者を一致
    /// させる。1画素=1bit(true=残す)。fullW*fullH を表現。スレッド安全性は「未公開の新規インスタンス
    /// にだけ書き込み、公開後は不変として読むだけ」という運用で担保する(UI 側が世代スタンプで管理)。
    /// </summary>
    // 詳細プレビュー(クロップ)をメインプレビュー(フル画像)と完全一致させるための転写キャッシュ。
    // クロップは可視範囲のピクセルしか持たないため、「マッチ領域全体の統計からしか正しく決まらない値」を
    // クロップ内統計で再計算すると値がズレ、ズーム/スクロールで選択や出力色が変わってしまう。これを防ぐ
    // ため、フル画像で 1 度だけ解いた結果をゾーン別に保持してクロップ処理へ転写する。
    //  ・keep  : 連結成分アンカリング(flood fill)で「残す」と判定された画素ビット(=選択結果)。
    //  ・stats : 再着色アンカー(autoRecolorAnchor)・wash 実効サンプル・無彩再着色の領域 L 統計。
    //            いずれもマッチ領域全体の統計から導出されるので、クロップ領域だけでは別の色になる。
    internal sealed class PreviewParityCache
    {
        // この内容が有効な入力状態の識別子(UI 側のプレビュー世代)。詳細側が一致時のみ参照する。
        public int generation;
        public int fullW;
        public int fullH;
        private readonly Dictionary<string, ulong[]> _keep = new Dictionary<string, ulong[]>();
        private readonly Dictionary<string, ZoneRecolorStats> _stats = new Dictionary<string, ZoneRecolorStats>();

        // フル画像処理が確定した入力寸法。keep / stats のどちらを書く場合も最初に設定する
        // (flood fill OFF でも stats 転写を効かせるため keep 書き込みとは独立に呼ぶ)。
        public void SetFullSize(int w, int h) { fullW = w; fullH = h; }

        public void SetKeep(string zoneId, ulong[] keep)
        {
            if (!string.IsNullOrEmpty(zoneId)) _keep[zoneId] = keep;
        }

        public ulong[] GetKeep(string zoneId)
        {
            if (!string.IsNullOrEmpty(zoneId) && _keep.TryGetValue(zoneId, out var k)) return k;
            return null;
        }

        public void SetStats(string zoneId, in ZoneRecolorStats s)
        {
            if (!string.IsNullOrEmpty(zoneId)) _stats[zoneId] = s;
        }

        public bool TryGetStats(string zoneId, out ZoneRecolorStats s)
        {
            if (!string.IsNullOrEmpty(zoneId)) return _stats.TryGetValue(zoneId, out s);
            s = default;
            return false;
        }
    }

    // フル画像のマッチ領域統計から導出され、クロップへそのまま転写すべき再着色パラメータ。
    // クロップ内のピクセル統計から再計算すると値が変わり、出力色がズーム位置で揺れる原因になる。
    internal struct ZoneRecolorStats
    {
        public bool anchorApplied;       // autoRecolorAnchor が実際に適用されたか(false=スポイト色アンカーのまま)
        public float anchorL, anchorC;   // OkLab アンカー(マッチ領域の明部地色)
        public float effShadowDesat;     // アンカー補正後の実効シャドウ脱彩
        public float washR, washG, washB, washV; // wash(ハイライト白射影)の実効サンプル
        public bool hasRegL;             // 無彩再着色の領域 L レンジが有効か
        public float regLlo, regLhi, regLmid;
        public float[] regMidMapFull;    // 無彩再着色の成分別中央値 L マップ(フル画像 per-pixel)。null=不要
    }

    /// <summary>
    /// テクスチャの再着色アルゴリズム本体。
    /// UnityEditor / EditorWindow / AssetDatabase に依存しない純粋ロジックだけを保持する。
    /// バックグラウンドスレッドからの呼び出しを前提にしている。
    /// </summary>
    internal static class PixelProcessor
    {
        // Parallel.For で使用する既定の CPU コア数制限。
        // Unity Editor は多数のスレッドを使用するため、全コア並列で
        // スレッドプールを圧迫するのを防ぐため 2 コア分をあけておく。
        // オーバーライドは DebugCaptureHooks.ParallelismOverride で設定可能。
        private static readonly int s_defaultParallelism =
            Math.Max(1, Environment.ProcessorCount - 2);

        private static int GetMaxParallelism()
        {
            int ov = DebugCaptureHooks.ParallelismOverride;
            return ov > 0 ? Math.Min(ov, Environment.ProcessorCount) : s_defaultParallelism;
        }

        private static double TicksToMs(long ticks) =>
            ticks * 1000.0 / Stopwatch.Frequency;

        // ───────────── 大テクスチャ向け専用 ArrayPool ─────────────
        // ArrayPool<T>.Shared は既定でバケット上限 2^20 要素。それを超える Rent は
        // 毎回 new[] を返し Return は捨てるため、2K(4.2M)/4K(16.8M) ではプールが
        // 全く効かず Rent ごとに LOH 新規確保が発生する(プレビュー再生成のたびに数百MB〜1GB)。
        // 4096²=2^24 までプールする専用プールに差し替えて GC churn を抑える。
        // maxArraysPerBucket は ProcessPixelsArray が 1 ゾーンで同時に Rent する本数(〜20)を
        // 満たす値にする(Create プールは GC 自動トリムされないため上限で保持メモリを抑える)。
        // 注意: Create プールの Rent はゼロ初期化を保証しない。既存の Array.Clear は残すこと。
        private const int PoolMaxArrayLength = 1 << 24; // 16,777,216 (4096²)
        private static readonly ArrayPool<float> s_floatPool =
            ArrayPool<float>.Create(PoolMaxArrayLength, maxArraysPerBucket: 24);
        private static readonly ArrayPool<bool> s_boolPool =
            ArrayPool<bool>.Create(PoolMaxArrayLength, maxArraysPerBucket: 8);

        // スタティック計算メソッド — バックグラウンドスレッドで実行可能
        // Texture2Dなし、UnityEngine.Object APIなし、Mathfとカラー計算のみ（いずれもスレッドセーフ）
        // originX/Y: フル解像度テクスチャでのクロップオフセット（0で全テクスチャ処理）
        // fullW/H: フル解像度テクスチャの寸法（0 = w/hと同じ、つまりクロップなし）
        // useDecontamination: AA境界でα分解＋再合成を行い halo を除去
        public static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones, float edgeFeather, int antiAliasCleanup,
            int holeFillPasses = 5, int holeFillMinNeighbors = 4,
            float relaxedSatMin = 0.02f, float relaxedSatRamp = 0.08f,
            int originX = 0, int originY = 0, int fullW = 0, int fullH = 0,
            bool useDecontamination = true, int decontaminationRadius = 4,
            float decontaminationInteriorThreshold = 0.97f,
            IDebugCapture debug = null,
            PreviewParityCache parityCache = null)
        {
            ProcessPixelsArray(pixels, w, h, masks, sortedZones, edgeFeather, antiAliasCleanup,
                holeFillPasses, holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp,
                originX, originY, fullW, fullH, CancellationToken.None,
                useDecontamination, decontaminationRadius, decontaminationInteriorThreshold,
                debug, parityCache);
        }

        // キャンセルトークン対応バージョン — バックグラウンドプレビューから使用
        public static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones, float edgeFeather, int antiAliasCleanup,
            int holeFillPasses, int holeFillMinNeighbors,
            float relaxedSatMin, float relaxedSatRamp,
            int originX, int originY, int fullW, int fullH,
            CancellationToken cancellationToken,
            bool useDecontamination = true, int decontaminationRadius = 4,
            float decontaminationInteriorThreshold = 0.97f,
            IDebugCapture debug = null,
            PreviewParityCache parityCache = null)
        {
            if (fullW <= 0) fullW = w;
            if (fullH <= 0) fullH = h;

            // マスクスナップショットからローカル変数に展開(packed ulong[]、1bit/画素)
            ulong[] commonMask = masks?.common;
            int maskW = masks?.width ?? 0;
            int maskH = masks?.height ?? 0;

            int len = w * h;
            Color32[] originalPixels = new Color32[len];
            System.Array.Copy(pixels, originalPixels, len);

            long _t0 = Stopwatch.GetTimestamp();
            var _perfZones = new ZonePerfEntry[sortedZones.Count];
            int _perfIdx = 0;

            // 全ピクセルの HSV を zone ループに入る前に一括計算（zone 数に関わらず1回）
            // null 初期化してから try 内で Rent することで、
            // 2番目以降の Rent が例外を投げた場合に先行の配列をリークしない。
            float[]? pixH = null, pixS = null, pixV = null;
            // 占有率バッファ: 優先度の高い(リスト上位の)ゾーンが書き込んだカバレッジを
            // ピクセル単位で累積する。下位ゾーンは残り(1-claimed)の範囲だけ適用され、
            // 「重なった部分は上位ゾーンのみ適用」というレイヤー排他を実現する。
            // 単一ゾーン/非重複ピクセルでは常に 0 のままで、従来挙動は不変。
            float[]? claimed = null;
            try
            {
            pixH = s_floatPool.Rent(len);
            pixS = s_floatPool.Rent(len);
            pixV = s_floatPool.Rent(len);
            claimed = s_floatPool.Rent(len);
            Array.Clear(claimed, 0, len);

            // po を HSV 計算 + foreach 内の全 Parallel.For で共用。
            // MaxDegreeOfParallelism で Unity Editor のスレッドプール圧迫を防ぐ。
            var po = new ParallelOptions
            {
                CancellationToken      = cancellationToken,
                MaxDegreeOfParallelism = GetMaxParallelism(),
            };
            Parallel.For(0, len, po, i =>
            {
                Color.RGBToHSV((Color)originalPixels[i], out pixH[i], out pixS[i], out pixV[i]);
            });

            debug?.BeginCapture(w, h);

            // decontamination 用バッファはゾーン間で再利用する(ゾーンごとの new bool[len]+
            // new Color32[len] 確保=4K で 17MB+67MB/ゾーンを回避)。aaMask は全画素で読まれるため
            // 各ゾーン頭でクリアし、decontaminatedPixels は aaMask=true の位置だけ上書き・参照される。
            bool[] decontamAaMask = null;
            Color32[] decontamPixels = null;
            if (useDecontamination)
            {
                decontamAaMask = new bool[len];
                decontamPixels = new Color32[len];
            }

            foreach (var zone in sortedZones)
            {
                // キャンセルチェック: 新しいプレビューリクエストが来た場合は即座に中断
                cancellationToken.ThrowIfCancellationRequested();

                // このゾーンに紐付くゾーン別マスクを取得（存在しなければ null、packed ulong[]）
                ulong[] zoneMask = null;
                if (masks != null && masks.zones != null && !string.IsNullOrEmpty(zone.id))
                    masks.zones.TryGetValue(zone.id, out zoneMask);

                // po は foreach 外で定義済みなので再宣言しない。
                // Parallel.For に入る前にキャッシュを確定させてホットループ内の条件分岐を排除
                zone.UpdateCacheIfNeeded();
                long _tZone = Stopwatch.GetTimestamp();

                // ArrayPool 借用は per-zone の try/finally で必ず返却する。
                // Parallel.For は po.CancellationToken でキャンセル時に OperationCanceledException
                // を投げ、FillSmallHoles 等の内部 Parallel.For 利用も例外を伝播し得るため、
                // 例外経路でもプールが汚染されないよう finally でガードする。
                float[] strength = null;
                float[] highlightPot = null;
                float[] matchConf = null;
                try
                {
                    // 1. 元のピクセルカラーを使用した強度マップを構築
                    strength = s_floatPool.Rent(len);
                    Array.Clear(strength, 0, len);
                    if (zone.highlightRecovery)
                    {
                        highlightPot = s_floatPool.Rent(len);
                        Array.Clear(highlightPot, 0, len);
                    }
                    // フル画像経路(メインプレビュー/Apply/Export)か部分クロップ(詳細プレビュー)か。
                    // 大域統計(連結成分/再着色アンカー/wash/領域L)はフル画像でしか正しく解けないので、
                    // フル画像では解いてキャッシュへ書き、クロップではキャッシュを転写する。
                    bool isFullImagePath = originX == 0 && originY == 0 && fullW == w && fullH == h;

                    // matchConf は連結成分アンカリング(flood fill)のコア判定専用。FF が有効でフル画像
                    // 経路のときだけ確保・充填する(部分クロップはキャッシュ転写で絞り込むため不要)。
                    bool needMatchConf = IrocaConsts.ExperimentalFeatures.EnableFloodFill
                        && zone.mode == SelectionMode.ColorPick && zone.useFloodFill
                        && isFullImagePath;
                    if (needMatchConf)
                    {
                        matchConf = s_floatPool.Rent(len);
                        Array.Clear(matchConf, 0, len);
                    }

                    var strengthLocal = strength;
                    var highlightPotLocal = highlightPot;
                    var matchConfLocal = matchConf;
                    Parallel.For(0, h, po, y =>
                    {
                        int yf = y + originY;
                        int rowOff = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int xf = x + originX;
                            int i = rowOff + x;
                            if (IsExcludedCombined(xf, yf, fullW, fullH, commonMask, zoneMask, maskW, maskH)) continue;

                            float s, hPot, mc;
                            zone.GetMatchScoresPrecomputedHSV(pixH[i], pixS[i], pixV[i], (Color)originalPixels[i], xf, yf, fullW, fullH, out s, out hPot, out mc);
                            strengthLocal[i] = s;
                            if (highlightPotLocal != null) highlightPotLocal[i] = hPot;
                            if (matchConfLocal != null) matchConfLocal[i] = mc;
                        }
                    });
                    debug?.RecordStage(zone.id, DebugStages.Match, strength, w, h);

                    // 1.a 空間伝播によるハイライト領域の回収 (モルフォロジー拡張)
                    if (highlightPot != null)
                    {
                        PropagateHighlights(strength, highlightPot, w, h);
                        s_floatPool.Return(highlightPot);
                        highlightPot = null;
                        debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, strength, w, h);
                    }

                    // 1.a.1 ハイライト帯成長: matched core から「sample→白 軸上の同色相の明部」へ
                    //       strength を空間連結で伸ばし、薄いハイライトのベタ塗り化・取りこぼしを防ぐ。
                    if (zone.highlightBandExpand && zone.highlightRecovery)
                    {
                        GrowHighlightBand(strength, originalPixels, pixH, pixS, pixV, zone, w, h);
                        debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, strength, w, h);
                    }

                    // 1.a.2 連結成分アンカリング: 確信度コアを含む連結成分のみに strength を絞り込む。
                    // 連結性は大域演算のため、フル画像経路(メインプレビュー/Apply/Export)でのみ実行する。
                    // 部分クロップ(詳細プレビュー)はここでは絞り込まず色のみ=最終の上位集合になる
                    // (M4 でフル画像の keep マスクをキャッシュ転写して完全一致させる予定)。
                    if (IrocaConsts.ExperimentalFeatures.EnableFloodFill
                        && zone.mode == SelectionMode.ColorPick
                        && zone.useFloodFill)
                    {
                        if (isFullImagePath)
                        {
                            int seedX = -1, seedY = -1;
                            if (zone.seedUV.x >= 0f)
                            {
                                seedX = Mathf.Clamp(Mathf.RoundToInt(zone.seedUV.x * (w - 1)), 0, w - 1);
                                seedY = Mathf.Clamp(Mathf.RoundToInt(zone.seedUV.y * (h - 1)), 0, h - 1);
                            }
                            ApplyConnectedComponentMask(strength, matchConf, originalPixels, w, h, seedX, seedY);
                            // フル画像で解いた keep(=残った画素 strength>0)をゾーン別にキャッシュへ書き出し、
                            // 詳細プレビュー(クロップ)へ転写して完全一致させる(どの判定経路でも最終結果を反映)。
                            if (parityCache != null)
                            {
                                var keepBits = new ulong[(len + 63) >> 6];
                                for (int i = 0; i < len; i++)
                                    if (strength[i] > 0f) keepBits[i >> 6] |= 1UL << (i & 63);
                                parityCache.SetFullSize(w, h);
                                parityCache.SetKeep(zone.id, keepBits);
                            }
                            debug?.RecordStage(zone.id, DebugStages.FloodFill, strength, w, h);
                        }
                        else if (parityCache != null
                                 && parityCache.fullW == fullW && parityCache.fullH == fullH)
                        {
                            // 部分クロップ(詳細プレビュー): フル画像で解いた keep をフル座標で転写。
                            // keep が無い/寸法不一致なら絞り込まず色のみ=上位集合(安全側)。
                            var keepBits = parityCache.GetKeep(zone.id);
                            if (keepBits != null)
                            {
                                ApplyCachedKeepMask(strength, w, h, originX, originY, fullW, keepBits);
                                debug?.RecordStage(zone.id, DebugStages.FloodFill, strength, w, h);
                            }
                        }
                    }

                    // 後段パス(穴埋め/境界回復/ブラー)を実マッチ範囲＋余白に限定する bbox (P2-7 拡張)。
                    // strength>0 を新たに変えうるのは「現に matched な画素の近傍」だけで、各パスが領域を
                    // 外側へ伸ばす最大幅は 穴埋め=±holeFillPasses / 境界回復=±antiAliasCleanup /
                    // ブラー=±radius。その総和ぶん余白を取った bbox の外は、処理の前後で常に 0 のまま=
                    // 出力ビット不変。bbox 内だけを走査することで、小マッチ(ロゴ等)で全 16.8M 画素の
                    // 近傍走査を避ける。Array.Copy/Clear は全画素のまま(memcpy で安価)残し、重い近傍
                    // 走査・relaxed 判定だけを bbox に絞る。マッチ皆無(hasPostBox=false)なら後段は全て no-op。
                    int ppBlurRadius = edgeFeather > 0.01f ? Mathf.CeilToInt(edgeFeather * 2.5f) : 0;
                    int ppMargin = holeFillPasses + Mathf.Max(0, antiAliasCleanup) + ppBlurRadius + 2;
                    int ppMinX, ppMinY, ppMaxX, ppMaxY;
                    bool hasPostBox = TryComputeStrengthBBox(strength, w, h, 0f,
                        out ppMinX, out ppMinY, out ppMaxX, out ppMaxY);
                    if (hasPostBox)
                    {
                        ppMinX = Mathf.Max(0, ppMinX - ppMargin);
                        ppMinY = Mathf.Max(0, ppMinY - ppMargin);
                        ppMaxX = Mathf.Min(w - 1, ppMaxX + ppMargin);
                        ppMaxY = Mathf.Min(h - 1, ppMaxY + ppMargin);
                    }
                    else { ppMinX = 0; ppMinY = 0; ppMaxX = -1; ppMaxY = -1; } // 空 bbox(後段スキップ)

                    // 1b. 孤立した穴を埋める：アンチエイリアス処理された端のピクセルは低彩度を持つことが多く
                    //     satConfidenceで見落とされて、元のカラーの孤立したドットを残す
                    //     ゼロ強度ピクセルが主にマッチしたピクセルに囲まれている場合は埋める。
                    //     穴埋め relaxed ゲート (2026-06-07 再移植): 画素自身が relaxed マッチ
                    //     (dist<tolerance) を通る色だけを穴埋め候補に許可する。薄いロゴ等で
                    //     「マッチ領域に囲まれただけの背景グレー/白」を full strength に塗ってしまう
                    //     フリンジ(白/灰ノイズ)を構造的に防ぐ。境界回復(RecoverBoundaryEdges)と同一基準。
                    // WS-M: relaxed ゲート(穴埋め/境界回復)にプライマリと同じ RGB 距離ブレンドを
                    // 与えるための chromaConfidence と sample RGB。低彩度サンプルで同色相の高彩度色
                    // (白→赤バンダナ等)を弾き、境界回復の色スピル(緑ハロー)を防ぐ。有彩は cc≈1 で従来式。
                    Color.RGBToHSV(zone.sampleColor, out float gsH, out float gsS, out float gsV);
                    float relaxedChromaConf = Mathf.Min(
                        Mathf.Clamp01((gsS - zone.chromaThreshold) / 0.10f),
                        Mathf.Clamp01((gsV - 0.05f) / 0.15f));
                    float rgSampR = zone.sampleColor.r, rgSampG = zone.sampleColor.g, rgSampB = zone.sampleColor.b;
                    if (hasPostBox)
                    {
                        bool[] fillAllowed = s_boolPool.Rent(len);
                        try
                        {
                            // fillAllowed は FillSmallHoles が中心画素 idx でのみ参照する(近傍は strength を読む)
                            // ため、bbox 内だけ計算すれば足りる。bbox 外は読まれない=出力ビット不変。
                            var fillAllowedLocal = fillAllowed;
                            Parallel.For(ppMinY, ppMaxY + 1, po, y =>
                            {
                                int rowOff = y * w;
                                for (int x = ppMinX; x <= ppMaxX; x++)
                                {
                                    int i = rowOff + x;
                                    Color32 hop = originalPixels[i];
                                    fillAllowedLocal[i] = GetRelaxedMatchStrength(
                                        pixH[i], pixS[i], pixV[i], gsH, gsS, gsV,
                                        zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                                        zone.satDistWeight, relaxedSatMin, relaxedSatRamp,
                                        zone.shadowForgivenessSatMin,
                                        hop.r / 255f, hop.g / 255f, hop.b / 255f,
                                        rgSampR, rgSampG, rgSampB, relaxedChromaConf) > 0f;
                                }
                            });
                            FillSmallHoles(strength, w, h, holeFillPasses, holeFillMinNeighbors, fillAllowed,
                                ppMinX, ppMinY, ppMaxX, ppMaxY);
                        }
                        finally
                        {
                            s_boolPool.Return(fillAllowed);
                        }
                    }
                    debug?.RecordStage(zone.id, DebugStages.HoleFill, strength, w, h);

                    // 1c. 境界復元：マッチしたピクセルに隣接するマッチしないピクセルを再評価
                    //     古い固定低彩度閾値を使用して、正しい段階的な強度を与える
                    if (antiAliasCleanup > 0 && hasPostBox)
                    {
                        RecoverBoundaryEdges(strength, w, h, pixH, pixS, pixV,
                            zone.sampleColor, zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                            zone.satDistWeight, relaxedSatMin, relaxedSatRamp, zone.shadowForgivenessSatMin, antiAliasCleanup,
                            ppMinX, ppMinY, ppMaxX, ppMaxY,
                            originalPixels, relaxedChromaConf);
                        debug?.RecordStage(zone.id, DebugStages.BoundaryRecover, strength, w, h);
                    }

                    // 2. スムーズな端の遷移のためのガウシアンブラー（端に限定）
                    if (edgeFeather > 0.01f && hasPostBox)
                    {
                        // ガウシアンブラー用の一時バッファ。GaussianBlur 内部の Parallel.For
                        // でキャンセルが入っても preBlur/blurOut が漏れないよう try/finally で囲む。
                        float[] preBlur = null;
                        float[] blurOut = null;
                        try
                        {
                            preBlur = s_floatPool.Rent(len);
                            Array.Copy(strength, preBlur, len);
                            blurOut = s_floatPool.Rent(len);
                            if (GaussianBlur(strength, blurOut, w, h, edgeFeather,
                                ppMinX, ppMinY, ppMaxX, ppMaxY))
                            {
                                // strength の所有権を blurOut に移し、もとの strength は返却
                                s_floatPool.Return(strength);
                                strength = blurOut;
                                blurOut = null; // 二重返却防止
                                ConstrainBlur(strength, preBlur, w, h, Mathf.CeilToInt(edgeFeather * 2.5f));
                            }
                        }
                        finally
                        {
                            if (blurOut != null) s_floatPool.Return(blurOut);
                            if (preBlur != null) s_floatPool.Return(preBlur);
                        }
                        debug?.RecordStage(zone.id, DebugStages.Blur, strength, w, h);
                    }

                    // 3. 除外マスクを再適用：ブラーが除外ピクセルにはみ出す可能性がある
                    if (commonMask != null || zoneMask != null)
                    {
                        var strengthForReapply = strength;
                        Parallel.For(0, h, po, y =>
                        {
                            int yf = y + originY;
                            int rowOff = y * w;
                            for (int x = 0; x < w; x++)
                            {
                                int xf = x + originX;
                                int i = rowOff + x;
                                if (IsExcludedCombined(xf, yf, fullW, fullH, commonMask, zoneMask, maskW, maskH)) strengthForReapply[i] = 0f;
                            }
                        });
                        debug?.RecordStage(zone.id, DebugStages.MaskReapply, strength, w, h);
                    }

                    // 3b. AA 境界の α 分解（オプション）：strength が 0 < s < interiorThreshold の
                    //     ピクセルを「α×FG + (1-α)×BG」と見て元テクスチャの合成を逆算し、
                    //     新色で再合成する。halo（薄汚れた中間色）を構造的に除去する。
                    //     詳細は dev_safe/docs/edge_decontamination.md を参照。
                    // 無彩サンプル/極端無彩ターゲットの重み(WS-R と AA フィデリティ修正で共用)。
                    float zAchromaWeight = ComputeAchromaWeight(zone.sampleColor, zone.targetColor);
                    // sample の S/V (wash ゲート・デバッグ分岐・下の中性リジェクトで共用)。
                    Color.RGBToHSV(zone.sampleColor, out _, out float zSS, out float zSV);

                    // 有彩サンプル→無彩ターゲット(赤→白/黒/灰)の過選択除去。有彩サンプルはマッチ距離が
                    // hue 支配になり彩度差を過小評価するため、暖色寄りで明るい中性画素(白UV背景等)を巻き込む。
                    // 巻き込みで領域が明るい背景に支配されると後段の成分中央値Lが上がり、本体(中L)が形維持
                    // リマップで黒へ落ちる(黒化)。マッチ全段(穴埋め/境界回復)の後・内部固め/成分統計の前に、
                    // サンプル彩度の相対床を下回る中性画素を strength から除去する(高彩度コア近傍は保護)。
                    // 発動判定は **明度非依存** の ComputeAchromaSelectWeight を使う(灰色=中明度の無彩でも
                    // 発動させる。ComputeAchromaWeight の extremeness では中明度グレーで重みが落ち白背景が
                    // 灰色化する)。有彩→有彩(weight≈0)・低彩度サンプル(sS<床)では作動しない=従来挙動を完全維持。
                    float zAchromaSelectWeight = ComputeAchromaSelectWeight(zone.sampleColor, zone.targetColor);
                    if (zAchromaSelectWeight > AchromaNeutralRejectWeightMin && zSS >= NeutralRejectActiveSourceSat)
                        RejectNeutralForAchromaTarget(strength, pixS, w, h, zSS);

                    // WS-R 内部固め: 極端な無彩ターゲット(白↔黒)では、マッチ強度が色のばらつきで内部まで
                    // フルにならず、明るい画素ほど弱く塗られて元色が残り「中央の段差」になる。陰影は塗り
                    // 強度でなく recolor の achroma レンジリマップ(gain≤1)で表現すべきなので、マッチ領域の
                    // 内部を full strength に固め、AA 縁(侵食で除いた帯)の taper だけ残す。有彩ターゲット
                    // (achromaWeight≈0)では no-op = byte 不変。algorithm.py SOLIDIFY_ACHROMA_INTERIOR と同期。
                    if (zAchromaWeight > 1e-4f)
                        SolidifyAchromaInterior(strength, w, h, zAchromaWeight);

                    bool[] aaMask = null;
                    Color32[] decontaminatedPixels = null;
                    if (useDecontamination)
                    {
                        aaMask = decontamAaMask;
                        decontaminatedPixels = decontamPixels;
                        // 内部固め(上)が無彩ターゲットの内部を均一化したので、旧 AA フィデリティ修正の
                        // interior_threshold=1.01(全画素 α 再合成)は不要(むしろ内部を背景色で再合成して
                        // 段差を復活させる)。常に通常閾値で AA 縁だけをデコンタミする。algorithm.py 同期。
                        float effInteriorThreshold = decontaminationInteriorThreshold;
                        DecontaminateAaBoundary(originalPixels, strength, w, h,
                            zone.sampleColor, zone.targetColor,
                            decontaminationRadius, effInteriorThreshold,
                            aaMask, decontaminatedPixels);
                        debug?.RecordDecontamination(zone.id, aaMask, w, h);
                    }

                    // 4. 強度でブレンドした再色付けを適用
                    // (zSS/zSV は上の中性リジェクト前に算出済み)
                    // ハイライト白方向射影(wash)・OkLab リカラーに必要な sample / target RGB を事前取得
                    float zSR = zone.sampleColor.r;
                    float zSG = zone.sampleColor.g;
                    float zSB = zone.sampleColor.b;
                    float zTR = zone.targetColor.r;
                    float zTG = zone.targetColor.g;
                    float zTB = zone.targetColor.b;
                    float zValueBlend = zone.valueBlend;
                    float zOutputSat = zone.outputSaturation;
                    bool zApplyWash = zone.applyHighlightWash;

                    // 詳細プレビュー(クロップ)は可視範囲のピクセルしか持たないため、wash 実効サンプル・
                    // 再着色アンカー・領域 L をクロップ内統計から再計算すると値がズレ、ズーム/スクロールで
                    // 出力色が変わってしまう。フル画像で解いた値をキャッシュから転写して完全一致させる。
                    // キャッシュが無い/寸法不一致のとき(キャッシュ生成前の過渡状態)のみ従来どおり計算する。
                    ZoneRecolorStats cachedStats = default;
                    bool useCachedStats = !isFullImagePath && parityCache != null
                        && parityCache.fullW == fullW && parityCache.fullH == fullH
                        && parityCache.TryGetStats(zone.id, out cachedStats);

                    // 俯瞰スポイト補正: ハイライト合成(wash)に使う実効サンプル。テクスチャの地色
                    // (同色相・低V)を自動導出し、明るい所をスポイトしても wash がドーム全体に効く
                    // ようにする。autoHighlightSample=false / 低彩度 / 地色不足のときは sample のまま。
                    float zWR, zWG, zWB, zWV;
                    if (useCachedStats)
                    {
                        zWR = cachedStats.washR; zWG = cachedStats.washG;
                        zWB = cachedStats.washB; zWV = cachedStats.washV;
                    }
                    else
                    {
                        Color zWash = HighlightSampleCorrector.ComputeWashSample(
                            originalPixels, pixH, pixS, pixV, w, h, zone);
                        zWR = zWash.r; zWG = zWash.g; zWB = zWash.b;
                        Color.RGBToHSV(zWash, out _, out _, out zWV);
                    }

                    // OkLab 明度保持リカラーのゾーン定数を事前計算 (per-pixel コスト削減)。
                    // sample/target を OkLab に変換。彩度(a,b)は「大きさを |chroma|/sC で正規化し、
                    // 向きは target 色相(zTa,zTb)に均一化」する。旧版は source の色相を回転保持していたが、
                    // 単色ロゴでは AA縁の混色がオレンジ寄りに転写され輪郭に色相ノイズを生んだ。向きを
                    // target に揃えることで L(リング除去)を保ったまま色相を均一化する。outputSaturation は
                    // 大きさスケールに畳み込む。sample が無彩(zSC≈0)なら target chroma を一律付与する。
                    RgbToOklab(zSR, zSG, zSB, out float zSL, out float zSa, out float zSb);
                    RgbToOklab(zTR, zTG, zTB, out float zTL, out float zTa, out float zTb);
                    float zSC = Mathf.Sqrt(zSa * zSa + zSb * zSb);
                    float zOsat = zOutputSat < 0.999f ? zOutputSat : 1f;
                    bool zOkGray = zSC <= 1e-4f;
                    // サンプル自動補正: アンカー (zSL, zSC) をスポイト画素からマッチ領域の代表色
                    // (明部の地色)へ置換する。スポイトを陰影のどの明るさで取ってもパーツの明部が
                    // target 色に一致する。マッチング(strength)・wash・デコンタミはスポイト色の
                    // まま＝再着色範囲は不変。フォールバック時(false)は従来挙動。
                    // クロップ(useCachedStats)はフル画像で確定した値を転写する(クロップ統計だと別色になる)。
                    float zEffShadowDesat = zone.shadowDesaturation;
                    bool zAnchorApplied = false;
                    float zAnchorL = 0f, zAnchorC = 0f;
                    if (useCachedStats)
                    {
                        if (cachedStats.anchorApplied) { zSL = cachedStats.anchorL; zSC = cachedStats.anchorC; }
                        zEffShadowDesat = cachedStats.effShadowDesat;
                    }
                    else if (zone.autoRecolorAnchor && !zOkGray &&
                        TryComputeRecolorAnchor(originalPixels, strength, out float anchorL, out float anchorC))
                    {
                        float zSC0 = zSC;
                        zSL = anchorL;
                        zSC = anchorC;
                        zAnchorApplied = true;
                        zAnchorL = anchorL;
                        zAnchorC = anchorC;
                        // 暗部脱彩の領域相対化(アンカー採用時のみ): 絶対 V 閾値のままだと暗い
                        // パーツは全体が閾値未満になり一律最大50%脱彩される(=入力明度で出力彩度
                        // が変わる)。閾値に地色アンカーの V(明部の代表明度)を乗じ「パーツ内の
                        // 相対的な暗部」だけを脱彩する。アンカー非採用時は従来の絶対閾値で完全互換。
                        // algorithm.py recolor_pixels の eff_shadow_desat と同期。
                        if (zEffShadowDesat > 0f)
                        {
                            OklabToRgb(zSL,
                                zSa / Mathf.Max(zSC0, 1e-9f) * zSC,
                                zSb / Mathf.Max(zSC0, 1e-9f) * zSC,
                                out float aRr, out float aGg, out float aBb);
                            Color.RGBToHSV(new Color(aRr, aGg, aBb), out _, out _, out float repV);
                            zEffShadowDesat *= repV;
                        }
                    }
                    float zOkMagScale = 0f, zOkGa = 0f, zOkGb = 0f;
                    if (zOkGray)
                    {
                        zOkGa = zTa * zOsat;
                        zOkGb = zTb * zOsat;
                    }
                    else
                    {
                        // na = |chroma| * (osat/sC) * zTa, nb = 同 zTb。oC=sC(sample) で (zTa,zTb)=target に一致。
                        zOkMagScale = zOsat / zSC;
                    }
                    // ChromaAmpMaxFactor キャップの上限 mag をゾーン定数として 1 回だけ算出する。
                    // 旧版は RecolorPixel 内で画素ごとに tC=sqrt(zTa²+zTb²) と maxMag=(zSC/tC)·Factor を
                    // 再計算していたが、zTa/zTb/zSC はゾーン不変なので per-pixel で常に同値=冗長だった。
                    // ホットな再着色ループから sqrt+除算を除去する(出力はビット不変)。キャップ非適用
                    // (Factor<=0 または target が無彩で tC≈0)のときは +∞ にして per-pixel の比較を no-op 化。
                    float zOkChromaMaxMag = float.PositiveInfinity;
                    if (ChromaAmpMaxFactor > 0f)
                    {
                        float zTC = Mathf.Sqrt(zTa * zTa + zTb * zTb);
                        if (zTC > 1e-4f) zOkChromaMaxMag = (zSC / zTC) * ChromaAmpMaxFactor;
                    }

                    // WS-R: 無彩再着色パスの領域 L レンジを事前計算(zAchromaWeight は上で算出済み)。
                    float zRegLlo = 0f, zRegLhi = 1f, zRegLmid = 0.5f;
                    bool zHasRegL = false;
                    // 形維持リマップの center 基準(中央値)を連結成分ごとに局所化する per-pixel マップ。
                    // ゆるいマスクで白背景を巻き込んでも、各成分が自分の地色基準で再着色されるので
                    // 三角がベタ黒へ潰れない。null のときは zRegLmid(全体中央値)へフォールバック。
                    float[] zRegMidMap = null;
                    if (zAchromaWeight > 1e-4f)
                    {
                        if (useCachedStats)
                        {
                            // クロップ: フル画像の領域 L 統計と成分中央値マップ(該当クロップ領域)を転写。
                            zHasRegL = cachedStats.hasRegL;
                            zRegLlo = cachedStats.regLlo; zRegLhi = cachedStats.regLhi; zRegLmid = cachedStats.regLmid;
                            if (zHasRegL && cachedStats.regMidMapFull != null)
                                zRegMidMap = CropFullMidMap(cachedStats.regMidMapFull, w, h, originX, originY, fullW);
                        }
                        else
                        {
                            zHasRegL = TryComputeRegionLRange(originalPixels, strength,
                                out zRegLlo, out zRegLhi, out zRegLmid);
                            if (zHasRegL)
                                zRegMidMap = BuildComponentMedianLMap(originalPixels, strength, w, h, 0.05f);
                        }
                    }

                    // フル画像で確定した領域統計をキャッシュへ書き、詳細プレビュー(クロップ)へ転写する。
                    // flood fill の有無と独立に書く(アンカー/wash 転写は FF OFF でも必要)。zRegMidMap は
                    // フル画像 per-pixel(w==fullW)なのでそのまま保持し、クロップ側で該当領域を切り出す。
                    if (isFullImagePath && parityCache != null)
                    {
                        parityCache.SetFullSize(w, h);
                        parityCache.SetStats(zone.id, new ZoneRecolorStats
                        {
                            anchorApplied = zAnchorApplied,
                            anchorL = zAnchorL,
                            anchorC = zAnchorC,
                            effShadowDesat = zEffShadowDesat,
                            washR = zWR, washG = zWG, washB = zWB, washV = zWV,
                            hasRegL = zHasRegL,
                            regLlo = zRegLlo, regLhi = zRegLhi, regLmid = zRegLmid,
                            regMidMapFull = zRegMidMap,
                        });
                    }

                    var strengthForRecolor = strength;
                    var aaMaskLocal = aaMask;
                    var decontaminatedLocal = decontaminatedPixels;
                    var claimedLocal = claimed;
                    // bbox 制限(P2-7): recolor ループは先頭で s<=0.001 を skip し近傍読みをしないため、
                    // strength>0.001 の bbox だけを走査すれば出力はビット不変。マッチ領域が小さいゾーン
                    // (ロゴ等)で OkLab 再着色の per-pixel コストを実マッチ範囲に限定する。bbox 走査は
                    // 軽い比較 1 パスで、recolor の重い per-pixel コスト削減が上回る。
                    int rcMinX = w, rcMaxX = -1, rcMinY = h, rcMaxY = -1;
                    for (int yy = 0; yy < h; yy++)
                    {
                        int rb = yy * w;
                        for (int xx = 0; xx < w; xx++)
                        {
                            if (strengthForRecolor[rb + xx] > 0.001f)
                            {
                                if (xx < rcMinX) rcMinX = xx;
                                if (xx > rcMaxX) rcMaxX = xx;
                                if (yy < rcMinY) rcMinY = yy;
                                if (yy > rcMaxY) rcMaxY = yy;
                            }
                        }
                    }
                    // ゾーン不変の再着色パラメータをループ前に 1 回だけ構築(in 渡しで per-pixel コピー回避)。
                    var rcParams = new RecolorParams(
                        zOkMagScale, zTa, zTb, zOkGray, zOkGa, zOkGb,
                        zSL, zTL, zSC, zOkChromaMaxMag, zValueBlend, zEffShadowDesat,
                        zSS, zTR, zTG, zTB, zWR, zWG, zWB, zWV,
                        zApplyWash, zAchromaWeight, zOsat, zHasRegL, zRegLlo, zRegLhi);
                    // rcMaxX<0 はマッチ画素なし → 全画素 continue で何もしないのと同じ(出力不変)。
                    if (rcMaxX >= 0)
                    Parallel.For(rcMinY, rcMaxY + 1, po, y =>
                    {
                        int rowOff = y * w;
                        for (int x = rcMinX; x <= rcMaxX; x++)
                        {
                            int i = rowOff + x;
                            float s = strengthForRecolor[i];
                            if (s <= 0.001f) continue;
                            // 上位(リスト上位)ゾーンが既に占有した分を差し引いた実効強度 es。
                            // 残り(room)が無ければこのゾーンは適用しない(= 上位が排他)。
                            float room = 1f - claimedLocal[i];
                            if (room <= 0.001f) continue;
                            float es = s < room ? s : room;
                            bool topMost = claimedLocal[i] <= 0.0001f;
                            if (aaMaskLocal != null && aaMaskLocal[i])
                            {
                                // AA pixel: use decontaminated value (overrides standard mix)。
                                // 最上位(room=1)なら従来どおり完全置換。下位なら残り分だけ被せる。
                                // デコンタミ値は合成済みエッジ色なので、このエッジ画素は上位として占有する。
                                pixels[i] = room >= 0.999f
                                    ? decontaminatedLocal[i]
                                    : Color32.Lerp(pixels[i], decontaminatedLocal[i], room);
                                claimedLocal[i] = 1f;
                                continue;
                            }
                            Color32 op = originalPixels[i];
                            float alpha = op.a / 255f;
                            Color32 recolored = RecolorPixel(
                                op.r, op.g, op.b,
                                pixV[i], alpha,
                                in rcParams,
                                (zRegMidMap != null && zRegMidMap[i] > 0f) ? zRegMidMap[i] : zRegLmid);
                            if (topMost)
                            {
                                // 最上位の寄与(claimed≈0)。es=s なので従来挙動と完全一致し、
                                // 単一ゾーン/非重複ピクセルの出力はバイト単位で不変。
                                pixels[i] = es >= 0.999f ? recolored : Color32.Lerp(pixels[i], recolored, es);
                            }
                            else
                            {
                                // 下位ゾーン: front-to-back over。透明な残り領域へ es 分だけ色を充填する。
                                // pixels[i] += (recolored - original) * es （上位が置いた色は保持される）。
                                Color32 cur = pixels[i];
                                pixels[i] = new Color32(
                                    (byte)Mathf.Clamp(Mathf.RoundToInt(cur.r + (recolored.r - op.r) * es), 0, 255),
                                    (byte)Mathf.Clamp(Mathf.RoundToInt(cur.g + (recolored.g - op.g) * es), 0, 255),
                                    (byte)Mathf.Clamp(Mathf.RoundToInt(cur.b + (recolored.b - op.b) * es), 0, 255),
                                    (byte)Mathf.Clamp(Mathf.RoundToInt(cur.a + (recolored.a - op.a) * es), 0, 255));
                            }
                            claimedLocal[i] = es >= 1f ? 1f : Mathf.Min(1f, claimedLocal[i] + es);
                        }
                    });
                    debug?.RecordStage(zone.id, DebugStages.Recolor, strength, w, h);

                    // 無彩(白↔黒)再着色のエッジ「残留クリーム」フチ消し。マッチ境界の外側 2px に残る
                    // 背景より明るい混色画素を α 分解で背景へ寄せ、暗い再着色色に対する明るいフチを消す。
                    // 有彩(zAchromaWeight≈0)では呼ばれず完全 no-op。共有のマッチ/合成経路は変更しない。
                    if (zAchromaWeight > 1e-4f && rcMaxX >= 0)
                        CleanAchromaFringe(pixels, originalPixels, strengthForRecolor, claimedLocal,
                            w, h, zone.sampleColor, zone.targetColor, rcMinX, rcMinY, rcMaxX, rcMaxY);

                    // Recolor 段で各ピクセルに適用されたサブブランチを記録する。
                    // hot loop には分岐を増やさず、debug 有効時だけ追加の Parallel.For で
                    // 上の RecolorPixel 内の条件式を再評価する。
                    // 優先度: Decontaminate > Shadow > Highlight > Base。
                    // shadow と highlight は条件上ほぼ排他（oV<thr と oV>sV）だが念のため shadow を優先。
                    // NOTE: 実際の RecolorPixel に渡した値を使うこと。
                    //   Shadow: zone.shadowDesaturation でなく zEffShadowDesat (autoRecolorAnchor 時に補正済み)
                    //   Highlight: zSV でなく zWV (HighlightSampleCorrector で補正した実効 wash サンプルの V)
                    if (debug != null)
                    {
                        byte[] branchMap = new byte[len];
                        float zoneShadowDesat = zEffShadowDesat;
                        float zoneSV = zWV;
                        float zoneSS = zSS;
                        bool zoneApplyWash = zone.applyHighlightWash;
                        var aaMaskForBranch = aaMask;
                        Parallel.For(0, len, po, i =>
                        {
                            if (strengthForRecolor[i] <= 0.001f)
                            {
                                branchMap[i] = (byte)DebugBranch.None;
                                return;
                            }
                            if (aaMaskForBranch != null && aaMaskForBranch[i])
                            {
                                branchMap[i] = (byte)DebugBranch.Decontaminate;
                                return;
                            }
                            float oV = pixV[i];
                            if (zoneShadowDesat > 0f && oV < zoneShadowDesat)
                            {
                                branchMap[i] = (byte)DebugBranch.Shadow;
                                return;
                            }
                            if (zoneApplyWash && zoneSS > 0.01f && oV > zoneSV)
                            {
                                branchMap[i] = (byte)DebugBranch.Highlight;
                                return;
                            }
                            branchMap[i] = (byte)DebugBranch.Base;
                        });
                        debug.RecordRecolorBranches(zone.id, branchMap, w, h);
                    }
                    _perfZones[_perfIdx++] = new ZonePerfEntry(zone.id, TicksToMs(Stopwatch.GetTimestamp() - _tZone));
                }
                finally
                {
                    if (highlightPot != null) s_floatPool.Return(highlightPot);
                    if (strength != null) s_floatPool.Return(strength);
                    if (matchConf != null) s_floatPool.Return(matchConf);
                }
            } // foreach zone

            } // end try (pixH/S/V)
            finally
            {
                if (claimed != null) s_floatPool.Return(claimed);
                if (pixV != null) s_floatPool.Return(pixV);
                if (pixS != null) s_floatPool.Return(pixS);
                if (pixH != null) s_floatPool.Return(pixH);
            }
        DebugCaptureHooks.RaisePerfReport(
            new PerfReport(TicksToMs(Stopwatch.GetTimestamp() - _t0), w, h, _perfZones));
        }

        /// <summary>
        /// AA 境界での α 分解 + 再合成（color decontamination / alpha matting）。
        /// 元テクスチャは「pixel = α × FG + (1-α) × BG」で合成されているため、
        /// HSV transfer を直接適用すると AA ピクセル（混色）が薄汚れた中間色になる（halo）。
        /// このメソッドは BG を局所近傍の strength=0 ピクセルから推定し、
        /// α を RGB 空間の射影で計算して、新色 target で再合成する。
        ///
        /// 出力:
        ///   aaMask[i] = true なら pixels[i] を decontaminatedPixels[i] で上書きすべき
        ///   それ以外は通常の HSV transfer にフォールバック
        /// </summary>
        private static void DecontaminateAaBoundary(
            Color32[] originalPixels, float[] strength, int w, int h,
            Color sampleColor, Color targetColor,
            int radius, float interiorThreshold,
            bool[] aaMask, Color32[] decontaminatedPixels)
        {
            int len = w * h;
            // 呼び出し側がゾーン間で再利用するバッファを渡す。aaMask は全画素で読まれるため
            // 前ゾーンの結果をクリアしてから書き込む。decontaminatedPixels は aaMask=true の
            // 位置だけ下で上書きされ、その位置だけ参照されるためクリア不要。
            Array.Clear(aaMask, 0, len);
            bool[] localAaMask = aaMask;
            Color32[] localDecontaminatedPixels = decontaminatedPixels;

            // 局所 BG 推定: strength=0 のピクセルだけを使った近傍和とその密度
            // 0..255 のスケールで計算（後で divide で平均化）
            // null 初期化してから try 内で Rent することでリークを防ぐ。
            float[]? wR = null, wG = null, wB = null, wD = null;
            float[]? bgRSum = null, bgGSum = null, bgBSum = null, bgDensity = null;
            try
            {
            wR = s_floatPool.Rent(len);
            wG = s_floatPool.Rent(len);
            wB = s_floatPool.Rent(len);
            wD = s_floatPool.Rent(len);
            bgRSum = s_floatPool.Rent(len);
            bgGSum = s_floatPool.Rent(len);
            bgBSum = s_floatPool.Rent(len);
            bgDensity = s_floatPool.Rent(len);
            // Rent はゼロ初期化を保証しないので strength>0 のピクセルを明示的にゼロ化
            Array.Clear(wR, 0, len);
            Array.Clear(wG, 0, len);
            Array.Clear(wB, 0, len);
            Array.Clear(wD, 0, len);
            var decontamPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
            Parallel.For(0, len, decontamPo, i =>
            {
                // アルファが0のピクセルはRGBがゴミデータ(黒など)の可能性が高いためBG推定から除外
                if (strength[i] <= 0f && originalPixels[i].a > 0)
                {
                    wR[i] = originalPixels[i].r;
                    wG[i] = originalPixels[i].g;
                    wB[i] = originalPixels[i].b;
                    wD[i] = 1f;
                }
            });
            BoxFilterSum(wR, bgRSum, w, h, radius);
            BoxFilterSum(wG, bgGSum, w, h, radius);
            BoxFilterSum(wB, bgBSum, w, h, radius);
            BoxFilterSum(wD, bgDensity, w, h, radius);

            // sample / target を 0..255 スケールに揃える
            float sR = sampleColor.r * 255f;
            float sG = sampleColor.g * 255f;
            float sB = sampleColor.b * 255f;
            float tR = targetColor.r * 255f;
            float tG = targetColor.g * 255f;
            float tB = targetColor.b * 255f;
            const float DegenEps = 1f; // ‖sample - BG‖² 下限（≈1 階調）

            Parallel.For(0, len, decontamPo, i =>
            {
                float s = strength[i];
                if (s <= 0f || s >= interiorThreshold) return;
                float density = bgDensity[i];
                if (density < 1f)
                {
                    // 近傍 radius 内に背景(非選択)画素が皆無 = この弱AA画素は選択領域の「内部」。
                    // 背景が無いので α 分解で再合成できないが、内部なら元色を残すべきでない。weak strength
                    // のままだと再着色が部分的になり元色(例: 白文字×青地の縁の薄青)が残留する。full に
                    // 固めて完全再着色する(SolidifyAchromaInterior の有彩ターゲット版・内部限定)。
                    // 境界(背景に接する縁)は density>=1 で従来どおり α 分解されるので AA ソフトさは不変。
                    strength[i] = 1f;
                    return;
                }

                float bR = bgRSum[i] / density;
                float bG = bgGSum[i] / density;
                float bB = bgBSum[i] / density;

                float dirR = sR - bR;
                float dirG = sG - bG;
                float dirB = sB - bB;
                float dirSq = dirR * dirR + dirG * dirG + dirB * dirB;
                if (dirSq < DegenEps) return; // sample ≈ BG → α が定義できない

                float pR = originalPixels[i].r;
                float pG = originalPixels[i].g;
                float pB = originalPixels[i].b;

                float dot = (pR - bR) * dirR + (pG - bG) * dirG + (pB - bB) * dirB;
                float alpha = dot / dirSq;
                if (alpha < 0f) alpha = 0f;
                else if (alpha > 1f) alpha = 1f;

                // BGとSampleで合成される線分からの距離の2乗を確認。
                // 大きく外れている場合は全く別の色（陰影や別パーツ等）であり、α分解の前提が崩れるためスキップ
                float projR = bR + alpha * dirR;
                float projG = bG + alpha * dirG;
                float projB = bB + alpha * dirB;
                float distSq = (pR - projR) * (pR - projR) + (pG - projG) * (pG - projG) + (pB - projB) * (pB - projB);
                if (distSq > 3000f) return; // 許容誤差。各チャンネル約31のズレまで許容

                float oneMinusAlpha = 1f - alpha;
                float resR = alpha * tR + oneMinusAlpha * bR;
                float resG = alpha * tG + oneMinusAlpha * bG;
                float resB = alpha * tB + oneMinusAlpha * bB;

                localAaMask[i] = true;
                localDecontaminatedPixels[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt(resR), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(resG), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(resB), 0, 255),
                    originalPixels[i].a);
            });
            } // end try
            finally
            {
                if (bgDensity != null) s_floatPool.Return(bgDensity);
                if (bgBSum   != null) s_floatPool.Return(bgBSum);
                if (bgGSum   != null) s_floatPool.Return(bgGSum);
                if (bgRSum   != null) s_floatPool.Return(bgRSum);
                if (wD != null) s_floatPool.Return(wD);
                if (wB != null) s_floatPool.Return(wB);
                if (wG != null) s_floatPool.Return(wG);
                if (wR != null) s_floatPool.Return(wR);
            }
        }

        /// <summary>
        /// 無彩(白↔黒)再着色のエッジ「残留クリーム」フチ消し(無彩ゾーンのみ呼ばれる)。
        /// 二値マッチ+デコンタミは選択 tolerance ちょうどで止まるため、その外側 1〜2px に残る
        /// 「地色↔背景の混色で背景より明るい(残留クリーム)」画素が、暗い再着色色に対して明るい
        /// フチに見える。ここをマッチ境界の外側 AchromaFringeMatchRadius px に限り α 分解
        /// (出力 = α·target + (1-α)·背景)で背景側へ寄せてフチを消す。背景優勢(α 小)の画素だけ
        /// 対象にし、白寄り(α≈1)の画素は除外して白拒否を維持する。脚色でなく元の混色の打ち消し。
        /// </summary>
        private static void CleanAchromaFringe(
            Color32[] pixels, Color32[] originalPixels, float[] strength, float[] claimed,
            int w, int h, Color sampleColor, Color targetColor,
            int bbMinX, int bbMinY, int bbMaxX, int bbMaxY)
        {
            int len = w * h;
            float[] wR = null, wG = null, wB = null, wD = null;
            float[] bgRSum = null, bgGSum = null, bgBSum = null, bgD = null;
            float[] wM = null, mNear = null;
            try
            {
                wR = s_floatPool.Rent(len); wG = s_floatPool.Rent(len);
                wB = s_floatPool.Rent(len); wD = s_floatPool.Rent(len);
                bgRSum = s_floatPool.Rent(len); bgGSum = s_floatPool.Rent(len);
                bgBSum = s_floatPool.Rent(len); bgD = s_floatPool.Rent(len);
                wM = s_floatPool.Rent(len); mNear = s_floatPool.Rent(len);
                Array.Clear(wR, 0, len); Array.Clear(wG, 0, len);
                Array.Clear(wB, 0, len); Array.Clear(wD, 0, len); Array.Clear(wM, 0, len);
                var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
                // 背景候補(非マッチ かつ α>0)と、マッチ指標を準備
                Parallel.For(0, len, po, i =>
                {
                    float s = strength[i];
                    if (s <= 0f && originalPixels[i].a > 0)
                    {
                        wR[i] = originalPixels[i].r; wG[i] = originalPixels[i].g;
                        wB[i] = originalPixels[i].b; wD[i] = 1f;
                    }
                    if (s > 0.05f) wM[i] = 1f;
                });
                BoxFilterSum(wR, bgRSum, w, h, 4);
                BoxFilterSum(wG, bgGSum, w, h, 4);
                BoxFilterSum(wB, bgBSum, w, h, 4);
                BoxFilterSum(wD, bgD, w, h, 4);
                BoxFilterSum(wM, mNear, w, h, AchromaFringeMatchRadius);

                float sR = sampleColor.r * 255f, sG = sampleColor.g * 255f, sB = sampleColor.b * 255f;
                float tR = targetColor.r * 255f, tG = targetColor.g * 255f, tB = targetColor.b * 255f;
                int x0 = Mathf.Max(0, bbMinX - AchromaFringeMatchRadius);
                int x1 = Mathf.Min(w - 1, bbMaxX + AchromaFringeMatchRadius);
                int y0 = Mathf.Max(0, bbMinY - AchromaFringeMatchRadius);
                int y1 = Mathf.Min(h - 1, bbMaxY + AchromaFringeMatchRadius);
                Parallel.For(y0, y1 + 1, po, y =>
                {
                    int row = y * w;
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = row + x;
                        if (strength[i] > 1e-4f) continue;            // マッチ済みは既存処理が担当
                        if (claimed != null && claimed[i] > 0.001f) continue; // 上位ゾーン占有は不可侵
                        if (mNear[i] < 1f) continue;                  // マッチ境界の近傍のみ
                        float density = bgD[i];
                        if (density < 1f) continue;
                        float bR = bgRSum[i] / density, bG = bgGSum[i] / density, bB = bgBSum[i] / density;
                        float dirR = sR - bR, dirG = sG - bG, dirB = sB - bB;
                        float dirSq = dirR * dirR + dirG * dirG + dirB * dirB;
                        if (dirSq < 1f) continue;                     // sample≈BG → α 未定義
                        float pR = originalPixels[i].r, pG = originalPixels[i].g, pB = originalPixels[i].b;
                        float alpha = ((pR - bR) * dirR + (pG - bG) * dirG + (pB - bB) * dirB) / dirSq;
                        // 残留クリームのある背景優勢画素のみ。白寄り(α≈1)は除外=白拒否を維持。
                        if (alpha < AchromaFringeMinAlpha || alpha > AchromaFringeMaxAlpha) continue;
                        float projR = bR + alpha * dirR, projG = bG + alpha * dirG, projB = bB + alpha * dirB;
                        float distSq = (pR - projR) * (pR - projR) + (pG - projG) * (pG - projG) + (pB - projB) * (pB - projB);
                        if (distSq > 3000f) continue;                 // 線から外れる=別色 → 触らない
                        float om = 1f - alpha;
                        pixels[i] = new Color32(
                            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * tR + om * bR), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * tG + om * bG), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * tB + om * bB), 0, 255),
                            originalPixels[i].a);
                        if (claimed != null) claimed[i] = 1f;
                    }
                });
            }
            finally
            {
                if (mNear != null) s_floatPool.Return(mNear);
                if (wM != null) s_floatPool.Return(wM);
                if (bgD != null) s_floatPool.Return(bgD);
                if (bgBSum != null) s_floatPool.Return(bgBSum);
                if (bgGSum != null) s_floatPool.Return(bgGSum);
                if (bgRSum != null) s_floatPool.Return(bgRSum);
                if (wD != null) s_floatPool.Return(wD);
                if (wB != null) s_floatPool.Return(wB);
                if (wG != null) s_floatPool.Return(wG);
                if (wR != null) s_floatPool.Return(wR);
            }
        }

        /// <summary>
        /// 分離型ボックス和フィルタ。各ピクセル位置で (2r+1)×(2r+1) 窓内の合計を dst に書き込む
        /// （境界はゼロ拡張：画像外の寄与を 0 として無視）。スライディングウィンドウで O(N) で計算。
        /// 内部 temp バッファは ArrayPool から借用・返却するのでヒープアロケーションなし。
        /// dst は呼び出し元が事前に確保すること（ArrayPool.Rent 推奨）。
        /// </summary>
        private static void BoxFilterSum(float[] src, float[] dst, int w, int h, int r)
        {
            int len = w * h;
            float[] temp = s_floatPool.Rent(len);
            var filterPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
            try
            {
                // 水平パス
                Parallel.For(0, h, filterPo, y =>
                {
                    int rowOff = y * w;
                    float sum = 0f;
                    int initEnd = Mathf.Min(r, w - 1);
                    for (int k = 0; k <= initEnd; k++) sum += src[rowOff + k];
                    temp[rowOff] = sum;
                    for (int x = 1; x < w; x++)
                    {
                        int subIdx = x - 1 - r;
                        int addIdx = x + r;
                        if (subIdx >= 0) sum -= src[rowOff + subIdx];
                        if (addIdx < w) sum += src[rowOff + addIdx];
                        temp[rowOff + x] = sum;
                    }
                });

                // 垂直パス
                Parallel.For(0, w, filterPo, x =>
                {
                    float sum = 0f;
                    int initEnd = Mathf.Min(r, h - 1);
                    for (int k = 0; k <= initEnd; k++) sum += temp[k * w + x];
                    dst[x] = sum;
                    for (int y = 1; y < h; y++)
                    {
                        int subIdx = y - 1 - r;
                        int addIdx = y + r;
                        if (subIdx >= 0) sum -= temp[subIdx * w + x];
                        if (addIdx < h) sum += temp[addIdx * w + x];
                        dst[y * w + x] = sum;
                    }
                });
            }
            finally
            {
                s_floatPool.Return(temp);
            }
        }

        // 連結成分アンカリングの確信度コア絶対床(matchConf 不在時のフォールバック専用)。
        private const float CoreAbsoluteFloor = 0.6f;

        /// <summary>
        /// 連結成分アンカリング: strength&gt;0 の画素を4連結でラベリングし、確信度コアを含む成分のみ残す。
        /// コア = matchConf &gt; 0(= マッチ距離が固定半径 ColorZone.CoreMatchDistance 未満 = サンプル色に
        /// 確信できる近さ。matchConf が null のときのみ従来の strength&gt;=max(P75,CoreAbsoluteFloor) に
        /// フォールバック)。matchConf は strength(有彩では彩度ゲートのみ/edgeSoftness=0 で二値)と違い
        /// 「色がどれだけ近いか」を表すため:
        ///   ・色は遠いが彩度が高い別素材の塊 → matchConf=0 → コアにならず落ちる(過選択ノイズを除去)
        ///   ・色は近いが strength が中位の分離領域(陰影/別アイランド) → matchConf&gt;0 → 残る(取りこぼし防止)
        /// 半径が固定なので tolerance を上げても下げてもコア判定は安定(tolerance≤半径なら全マッチが
        /// コア=FF 実質 OFF で取りこぼしゼロ、tolerance&gt;半径で初めて遠い滲みを落とす)。サイズでは
        /// 切らない（小さくても高信頼な成分＝三角などは残す）。確信できるコアが一つも無い(全体が弱マッチ)
        /// 場合は recall 保護のため絞り込まない(=連結制約 OFF と同挙動)。
        /// seedX&gt;=0 のときはその成分だけを残す上書きモード(無効シード=bleed/背景上 なら自動へフォールバック)。
        /// 連結予測子(strength&gt;0 &amp;&amp; α&gt;=128)・4近傍は BuildComponentMedianLMap と一致させ成分定義を統一する。
        /// </summary>
        private static void ApplyConnectedComponentMask(
            float[] strength, float[] matchConf, Color32[] px, int w, int h, int seedX, int seedY)
        {
            // matched 画素(strength>0 && α>=128)の bbox。ラベリングは bbox 内に限定(全画素確保を回避)。
            int minX = w, maxX = -1, minY = h, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int rb = y * w;
                for (int x = 0; x < w; x++)
                    if (strength[rb + x] > 0f && px[rb + x].a >= 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            }
            if (maxX < 0) return; // マッチ皆無

            // フォールバック用コア閾値(matchConf 不在時のみ使用)= 正の strength 群の P75 と絶対床の
            // 大きい方。matchConf があるときは固定半径ベースの色一致確信度で判定するので不要。
            float coreThreshold = 0f;
            int posCount = 0;
            if (matchConf == null)
            {
                var sHist = new int[256];
                for (int y = minY; y <= maxY; y++)
                {
                    int rb = y * w;
                    for (int x = minX; x <= maxX; x++)
                    {
                        int gi = rb + x;
                        if (strength[gi] > 0f && px[gi].a >= 128)
                        {
                            sHist[Mathf.Clamp((int)(strength[gi] * 255f), 0, 255)]++;
                            posCount++;
                        }
                    }
                }
                coreThreshold = Mathf.Max(
                    HistValueAtPercentile(sHist, posCount, 0.75f, 1f), CoreAbsoluteFloor);
            }

            // コア判定: matchConf があれば色一致確信度(>0 = 固定半径内)で、無ければ strength 閾値で判定。
            bool IsCore(int gi) => matchConf != null
                ? matchConf[gi] > 0f
                : strength[gi] >= coreThreshold;

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            int[] label = new int[bw * bh];
            var hasCore = new List<bool>();   // hasCore[lab-1] = その成分にコア画素があるか
            var queue = new Queue<int>();

            for (int ly = 0; ly < bh; ly++)
            {
                for (int lx = 0; lx < bw; lx++)
                {
                    int li = ly * bw + lx;
                    if (label[li] != 0) continue;
                    if (!(strength[(ly + minY) * w + (lx + minX)] > 0f
                          && px[(ly + minY) * w + (lx + minX)].a >= 128)) continue;

                    int lab = hasCore.Count + 1;
                    bool core = false;
                    label[li] = lab;
                    queue.Enqueue(li);
                    while (queue.Count > 0)
                    {
                        int ci = queue.Dequeue();
                        int cx = ci % bw, cy = ci / bw;
                        if (IsCore((cy + minY) * w + (cx + minX))) core = true;
                        TryEnq(ci - 1, cx > 0);
                        TryEnq(ci + 1, cx < bw - 1);
                        TryEnq(ci - bw, cy > 0);
                        TryEnq(ci + bw, cy < bh - 1);
                    }
                    hasCore.Add(core);

                    void TryEnq(int ni, bool inBounds)
                    {
                        if (!inBounds || label[ni] != 0) return;
                        int nx = ni % bw, ny = ni / bw;
                        if (!(strength[(ny + minY) * w + (nx + minX)] > 0f
                              && px[(ny + minY) * w + (nx + minX)].a >= 128)) return;
                        label[ni] = lab;
                        queue.Enqueue(ni);
                    }
                }
            }

            // 残す成分を決定。seed 上書き優先、無効/未指定ならコア規則。
            int keepLabel = 0; // 0 = コア規則, >0 = その label だけ残す
            if (seedX >= minX && seedX <= maxX && seedY >= minY && seedY <= maxY)
            {
                int sl = label[(seedY - minY) * bw + (seedX - minX)];
                if (sl != 0) keepLabel = sl; // 有効シード → その成分のみ
                // sl==0(bleed/背景上) → 自動へフォールバック(keepLabel=0 のまま)
            }

            if (keepLabel == 0)
            {
                bool anyCore = false;
                for (int c = 0; c < hasCore.Count; c++) if (hasCore[c]) { anyCore = true; break; }
                if (!anyCore) return; // 確信できるコアが皆無 → 絞り込まない(recall 保護)
            }

            for (int ly = 0; ly < bh; ly++)
            {
                int rb = (ly + minY) * w;
                for (int lx = 0; lx < bw; lx++)
                {
                    int lab = label[ly * bw + lx];
                    if (lab == 0) continue;
                    bool keep = keepLabel > 0 ? (lab == keepLabel) : hasCore[lab - 1];
                    if (!keep) strength[rb + (lx + minX)] = 0f;
                }
            }
        }

        /// <summary>
        /// 連結成分アンカリングの keep(フル画像で解いた「残す画素」1bit/画素)を部分クロップへ転写する。
        /// クロップの各画素をフル座標(originX/Y オフセット)で keep 参照し、残さない画素の strength を 0 化。
        /// これにより詳細プレビュー(クロップ)が大域演算を再実行せずにメイン/最終と完全一致する。
        /// </summary>
        // フル画像 per-pixel マップ(成分別中央値 L 等)から、クロップ(originX/Y, w×h)に対応する
        // 矩形を切り出してクロップ座標の新しい配列に詰める。詳細プレビューがフル画像と同じ成分基準で
        // 再着色できるようにするための転写。行ごとに連続コピーするだけ(全画素 1 回読み)。
        private static float[] CropFullMidMap(float[] full, int w, int h, int originX, int originY, int fullW)
        {
            var map = new float[w * h];
            for (int y = 0; y < h; y++)
                System.Array.Copy(full, (y + originY) * fullW + originX, map, y * w, w);
            return map;
        }

        private static void ApplyCachedKeepMask(
            float[] strength, int w, int h, int originX, int originY, int fullW, ulong[] keep)
        {
            for (int y = 0; y < h; y++)
            {
                int rowOff = y * w;
                int fRow = (y + originY) * fullW + originX;
                for (int x = 0; x < w; x++)
                {
                    int i = rowOff + x;
                    if (strength[i] <= 0f) continue;
                    int gi = fRow + x;
                    int word = gi >> 6;
                    if (word < 0 || word >= keep.Length || (keep[word] & (1UL << (gi & 63))) == 0UL)
                        strength[i] = 0f;
                }
            }
        }

        /// <summary>
        /// ハイライト候補領域をコア領域から空間伝播させてマスク化する。
        /// strengthの強いピクセル(コア)から、ハイライト候補スコア(highlightPot)を持つ隣接ピクセルへ
        /// strengthを徐々に伝播させ、孤立した白いシャツなどを染めないようにする。
        /// </summary>
        private static void PropagateHighlights(float[] strength, float[] highlightPot, int w, int h)
        {
            // bbox 制限(P1-5): strength を更新し得るのは下のガード `pot > 0f` を満たす画素のみ。
            // highlightPot>0 の bbox だけ走査すれば、bbox 外画素は元々 skip(何もしない)、bbox 端の
            // 近傍読み(i±1 / i±w)が指す bbox 外画素は pot=0 で本関数では不変のため値が一致し、
            // スイープ順序も bbox 内の pot>0 画素の相対順は全面走査と同一。よって出力はビット不変。
            // ハイライト候補はテクスチャの一部に偏在するため実効コストを大きく削減できる。
            int minX = w, maxX = -1, minY = h, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int rb = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (highlightPot[rb + x] > 0f)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return;   // pot>0 の画素が無い → 伝播対象なし

            int passes = 3;
            for (int p = 0; p < passes; p++)
            {
                bool changed = false;

                // 左上から右下へのパス (bbox 内のみ走査)
                for (int y = minY; y <= maxY; y++)
                {
                    int rowBase = y * w;
                    for (int x = minX; x <= maxX; x++)
                    {
                        int i = rowBase + x;
                        float pot = highlightPot[i];
                        if (pot > 0f && strength[i] < pot)
                        {
                            float maxNeighbor = 0f;
                            if (x > 0) maxNeighbor = Mathf.Max(maxNeighbor, strength[i - 1]);
                            if (y > 0) maxNeighbor = Mathf.Max(maxNeighbor, strength[i - w]);

                            // 右と下も覗き見る (現在の状態で)
                            if (x < w - 1) maxNeighbor = Mathf.Max(maxNeighbor, strength[i + 1]);
                            if (y < h - 1) maxNeighbor = Mathf.Max(maxNeighbor, strength[i + w]);

                            if (maxNeighbor > 0.1f)
                            {
                                float newS = Mathf.Min(pot, maxNeighbor * 0.95f);
                                if (newS > strength[i])
                                {
                                    strength[i] = newS;
                                    changed = true;
                                }
                            }
                        }
                    }
                }

                // 右下から左上へのパス (bbox 内のみ走査)
                for (int y = maxY; y >= minY; y--)
                {
                    int rowBase = y * w;
                    for (int x = maxX; x >= minX; x--)
                    {
                        int i = rowBase + x;
                        float pot = highlightPot[i];
                        if (pot > 0f && strength[i] < pot)
                        {
                            float maxNeighbor = 0f;
                            if (x < w - 1) maxNeighbor = Mathf.Max(maxNeighbor, strength[i + 1]);
                            if (y < h - 1) maxNeighbor = Mathf.Max(maxNeighbor, strength[i + w]);

                            // 左と上も覗き見る
                            if (x > 0) maxNeighbor = Mathf.Max(maxNeighbor, strength[i - 1]);
                            if (y > 0) maxNeighbor = Mathf.Max(maxNeighbor, strength[i - w]);

                            if (maxNeighbor > 0.1f)
                            {
                                float newS = Mathf.Min(pot, maxNeighbor * 0.95f);
                                if (newS > strength[i])
                                {
                                    strength[i] = newS;
                                    changed = true;
                                }
                            }
                        }
                    }
                }

                if (!changed) break;
            }
        }

        // ハイライト帯成長で使用する定数。
        private const float HlBandCoreThreshold = 0.90f;  // 信頼コア（本体）とみなす strength 下限
        private const float HlBandAxisEps       = 0.10f;  // sample→白 軸からの許容残差（RGB ユークリッド）
        private const float HlBandMinSampleSat  = 0.20f;  // 源色がこれ未満（灰色寄り）なら無効
        private const float HlBandMinSatFrac    = 0.15f;  // 帯候補の彩度下限（源色相対）。白素材への流入を防ぐ

        // L 再マップの彩度ゲート定数。彩度が sample の何割に達したら remap をフル適用するか。
        // これ未満の低彩度画素は元 L を保持し、target が sample より明るい場合の暗部持ち上げ
        // (=ロゴ周辺の白/灰ノイズ)を防ぐ。algorithm.py の OKLAB_REMAP_FULL_CHROMA_FRAC と同期。
        private const float OklabRemapFullChromaFrac = 0.35f;

        // chroma 増幅キャップ(有彩ターゲット向け WS-R 拡張): tC > sC の色相変化で OkLab→RGB の
        // lum 感度が高まりバンディング発生(bandana green: tC/sC=1.57→contrast 2.23x, orange: 1.17→1.58x)。
        // output chroma = mag*tC が sC*Factor を超えないよう mag を制限。algorithm.py CHROMA_AMP_MAX_FACTOR と同期。
        private const float ChromaAmpMaxFactor = 1.0f;

        // ───────── WS-R: 無彩サンプル / 極端無彩ターゲットの再着色破綻対策 ─────────
        // 通常の再着色は sample 彩度 sC を「分母・基準」に使う前提(mag=oC/sC, 彩度ゲート
        // chroma_frac=oC/(sC·FULL_FRAC))。サンプルが無彩(白/黒/灰)だと前提が崩れ、白い三角→黒で
        // (1) 彩度ゲートが白を明るく残す＝まだら (2) 2区間リマップが sL より明るい画素を 1.0 へ拡張
        // (3) mag=oC/sC が微小彩度ノイズを増幅＝脚色 が起きる。サンプルが無彩 or ターゲットが極端
        // 無彩(白/黒)のとき、L=マッチ領域 L レンジを target ヘッドルームへ収める順序保存リマップ /
        // 彩度=uniform target chroma へ achroma_weight で連続ブレンドする。有彩×有彩では weight=0 で
        // 従来式とバイト不変。不変条件: 単調・順序保存・gain≤1(増幅禁止)。algorithm.py ACHROMA_* と同期。
        private const float AchromaSampleC  = 0.06f;  // sample OkLab chroma がこれ未満で無彩扱い(→1)
        private const float AchromaTargetC  = 0.06f;  // target OkLab chroma がこれ未満で無彩扱い
        private const float AchromaRangeGain = 1.0f;  // [旧] レンジリマップ出力幅 = 元幅 × min(gain,1)。form 版へ移行。
        // 形(立体感)維持版: 成分の地色基準を target 側 offset に置き、偏差を gain 倍して陰影を知覚可能に拡張。
        private const float AchromaFormGain = 2.5f;    // 基準からの偏差の増幅率(知覚補償)
        private const float AchromaFormOffset = 0.16f; // 地色基準を置く target 側 offset(黒=0+, 白=1-)
        // 成分の地色基準に使う L パーセンタイル。中央値(0.5)だと、ゆるい/広いマスクで暗い珊瑚縁が
        // 成分に混入したとき基準が下振れし、模様ごとに明るさが不揃いになる。高め(0.8)にすると暗い
        // 混入に頑健で「素材本来の地色レベル」に揃う(並んだ三角が均一になる)。
        private const float AchromaRefPercentile = 0.80f;
        private const float AchromaRegionCoreThr = 0.5f; // 領域 L レンジを取る strength 下限

        // 彩度整合ゲート(緩和マッチのグレーモード分岐用)。ColorZone.cs の同名 const と必ず一致させること。
        // 緩和マッチ(穴埋め/AA クリーンアップ)のグレー分岐は値距離 |pV-sV| で判定するため、明るい
        // クリームサンプルに対し純白(中性)が近接して一致してしまう。プライマリ経路と同じ相対彩度床で排除する。
        private const float ChromaGateActivateSat = 0.02f; // この tint 未満のサンプルでは無効
        private const float ChromaGateFloorFrac = 0.5f;    // サンプル彩度 sS*frac 未満は「中性すぎ」
        private const float ChromaGatePenalty = 1.0f;      // 最大加算距離(tolerance 単位)

        // 有彩サンプル→無彩極端ターゲット(赤→白/黒等)の「中性画素リジェクト・フロア」定数。
        // 有彩サンプルはマッチ距離が hue 支配で彩度差を過小評価し、明るい中性画素(白UV背景等)を巻き込む。
        // 無彩ターゲットのときだけ、サンプル彩度の相対床 sS·Frac 未満の画素を strength から除去する
        // (高彩度コア近傍 Radius px は保護=赤自身の脱彩した陰影/AA縁を守る)。値は既存定数を流用。
        // algorithm.py の match_sat_floor(achroma-gate 分岐)と同期。
        private const float AchromaNeutralRejectWeightMin = 0.5f; // 白↔黒の極端無彩ターゲットでのみ作動
        private const float NeutralRejectActiveSourceSat = 0.40f; // サンプルがこの彩度以上(=有彩)でのみ作動
        // サンプル彩度 sS·frac 未満を「中性(=白背景)」とみなして弾く。0.10 は中性の白背景
        // (彩度≈0)だけを落とし、模様に乗った淡い赤(彩度~0.05+)は残す値。大きい(0.30)と淡赤も
        // 巻き添えで弾いて模様上の赤が放置される。小さすぎる(<0.05)と白背景の取りこぼし。
        private const float NeutralRejectFloorFrac = 0.07f;
        // ソフト床のフェード開始 = floor·RampFrac。これ未満(中性の白背景)は完全に弾き、floor 以上は
        // 完全保持、間は連続フェードで境界の二値ノイズを消す。
        private const float NeutralRejectRampFrac = 0.5f;
        private const int NeutralRejectProtectRadius = 2;         // 高彩度コアからこの px 以内は保護

        // 無彩フチ消し(CleanAchromaFringe)の定数。マッチ境界の外側に残る「残留クリーム」混色画素
        // (背景より明るく、暗い再着色色に対し明るいフチに見える)を α 分解で背景へ寄せて消す。
        private const int AchromaFringeMatchRadius = 2;    // マッチ境界からこの px 以内の外側を対象
        private const float AchromaFringeMinAlpha = 0.05f; // これ未満=残留クリームほぼ無し→触らない
        private const float AchromaFringeMaxAlpha = 0.70f; // これ超=白/地色寄り→除外(白拒否を維持)

        // サンプル自動補正(再着色アンカー正規化)の定数。algorithm.py の ANCHOR_* と同期。
        // すべて領域統計に対する相対量(特定色/座標/テクスチャ非依存)。
        private const float AnchorStrengthMin   = 0.9f;   // コアマッチのみ採用(AA縁・feather裾の混色を除外)
        private const int   AnchorMinPixels     = 100;    // これ未満はフォールバック(ComputeWashSample と同基準)
        private const float AnchorBodySatrFrac  = 0.5f;   // 地色とみなす OkLab 飽和度(C/L)下限(候補中央値比)。
                                                          // C/L は乗算シェーディング不変(L,C とも k^(1/3) 比例)なので
                                                          // wash 済み明部・AA縁グレーを陰影レベル非依存に除外できる。
        private const float AnchorLPct          = 0.90f;  // 代表 L percentile(鏡面ハイライト芯の最上位~10%を除外)
        private const float AnchorLBandLoPct    = 0.80f;  // 代表 C を取る L 帯の下限 percentile
        private const float AnchorLBandHiPct    = 0.97f;  // 同上限 percentile
        private const float AnchorMinLSpread    = 0.02f;  // L の P95-P05 がこれ未満=フラット領域は補正不要
        private const float AnchorSatrHistMax   = 4f;     // 飽和度(C/L)ヒストグラムの値域上限
        private const float AnchorChromaHistMax = 0.5f;   // chroma ヒストグラムの値域上限(OkLab C は ~0.33 まで)

        /// <summary>256bin ヒストグラムの percentile(0..1) を実値で返す(値域 [0, scale])。
        /// HighlightSampleCorrector.PercentileFromHist の値域一般化版。Python np.percentile は
        /// 線形補間のため最大 1bin(scale/255)の離散化差を許容する(auto_wash_sample の前例に従う)。</summary>
        private static float HistValueAtPercentile(int[] hist, int total, float pct, float scale)
        {
            int target = Mathf.Clamp(Mathf.CeilToInt(total * pct), 1, total);
            int cum = 0;
            for (int b = 0; b < hist.Length; b++)
            {
                cum += hist[b];
                if (cum >= target) return b / (float)(hist.Length - 1) * scale;
            }
            return scale;
        }

        /// <summary>
        /// サンプル自動補正: OkLab 再着色のアンカー (sL, sC) をマッチ領域の統計から推定する。
        ///
        /// OkLab 再着色は (sL, sC) を不動点とする相対写像のため、スポイトした画素の陰影レベルが
        /// そのまま出力全体の明度・彩度バイアスになる(明部平均 HSV-S で最大 ~0.22 のブレを実測)。
        /// 本関数は「パーツの明るい面の地色」を統計的に推定して返し、スポイト位置非依存にする。
        /// マッチング・wash には影響しない(呼び出し側がアンカー 2 値だけを置き換える)。
        ///
        /// algorithm.py の estimate_anchor_oklab と同期(percentile の離散化差 ≤1/255 は許容)。
        /// </summary>
        /// <returns>false = フォールバック(スポイト色のまま従来挙動)。
        /// 条件: コア画素&lt;100 / 地色画素&lt;100 / フラット領域(L スプレッド&lt;0.02) / 推定 C≈0。</returns>
        private static bool TryComputeRecolorAnchor(
            Color32[] px, float[] strength, out float anchorL, out float anchorC)
        {
            anchorL = 0f;
            anchorC = 0f;
            int len = px.Length;

            // コア画素(strength>=AnchorStrengthMin & a>=128)の OkLab (L, C) を pass1 で一度だけ
            // 計算して圧縮配列に保存し、pass2/3 はそれを読む。従来は 3 パスとも全画素を走査して
            // 同じ画素の RgbToOklab を再計算していた(4K で計 50M 回の OkLab 変換)。出力は同値。
            // 配列は s_floatPool から借用(candCount<=len なので 2^24 までプール内)。
            float[] candL = s_floatPool.Rent(len);
            float[] candC = s_floatPool.Rent(len);
            try
            {
                // pass 1: (L, C) を保存しつつ飽和度(C/L)ヒストグラム → 中央値から地色下限を決める
                var satrHist = new int[256];
                int candCount = 0;
                for (int i = 0; i < len; i++)
                {
                    if (strength[i] < AnchorStrengthMin || px[i].a < 128) continue;
                    RgbToOklab(px[i].r, px[i].g, px[i].b,
                        out float L, out float a, out float b);
                    float C = Mathf.Sqrt(a * a + b * b);
                    candL[candCount] = L;
                    candC[candCount] = C;
                    candCount++;
                    float satr = C / Mathf.Max(L, 1e-4f);
                    int bin = Mathf.Clamp((int)(satr / AnchorSatrHistMax * 255f), 0, 255);
                    satrHist[bin]++;
                }
                if (candCount < AnchorMinPixels) return false;
                float satrFloor = AnchorBodySatrFrac *
                    HistValueAtPercentile(satrHist, candCount, 0.5f, AnchorSatrHistMax);

                // pass 2: 地色画素(飽和度 ≥ 下限)の L ヒストグラム → 代表 L と L 帯
                var lHist = new int[256];
                int bodyCount = 0;
                for (int k = 0; k < candCount; k++)
                {
                    float L = candL[k];
                    if (candC[k] / Mathf.Max(L, 1e-4f) < satrFloor) continue;
                    lHist[Mathf.Clamp((int)(L * 255f), 0, 255)]++;
                    bodyCount++;
                }
                if (bodyCount < AnchorMinPixels) return false;
                float l05 = HistValueAtPercentile(lHist, bodyCount, 0.05f, 1f);
                float l95 = HistValueAtPercentile(lHist, bodyCount, 0.95f, 1f);
                if (l95 - l05 < AnchorMinLSpread) return false;  // フラット領域: どこを取っても同じ
                anchorL = HistValueAtPercentile(lHist, bodyCount, AnchorLPct, 1f);
                float bandLo = HistValueAtPercentile(lHist, bodyCount, AnchorLBandLoPct, 1f);
                float bandHi = HistValueAtPercentile(lHist, bodyCount, AnchorLBandHiPct, 1f);

                // pass 3: L 帯内の地色画素の chroma 中央値 → 代表 C
                var cHist = new int[256];
                int bandCount = 0;
                for (int k = 0; k < candCount; k++)
                {
                    float L = candL[k];
                    float c = candC[k];
                    if (c / Mathf.Max(L, 1e-4f) < satrFloor) continue;
                    if (L < bandLo || L > bandHi) continue;
                    cHist[Mathf.Clamp((int)(c / AnchorChromaHistMax * 255f), 0, 255)]++;
                    bandCount++;
                }
                if (bandCount < 1) return false;
                anchorC = HistValueAtPercentile(cHist, bandCount, 0.5f, AnchorChromaHistMax);
                return anchorC > 1e-4f;
            }
            finally
            {
                s_floatPool.Return(candL);
                s_floatPool.Return(candC);
            }
        }

        /// <summary>
        /// 有彩サンプル→無彩極端ターゲット時の中性画素リジェクト・フロア。サンプル彩度の相対床
        /// (sS·NeutralRejectFloorFrac)未満の画素のうち、高彩度マッチコアから NeutralRejectProtectRadius
        /// px より遠いものの strength を 0 にする。hue 支配距離で巻き込んだ明るい中性背景(白UV背景等)を
        /// 落としつつ、コア近傍の脱彩した陰影/AA縁は保護する(彩度だけでは両者を区別できないため空間距離で
        /// 分離=algorithm.py match_sat_floor と同設計)。呼び出し側で achromaWeight/サンプル彩度を gate。
        /// </summary>
        private static void RejectNeutralForAchromaTarget(float[] strength, float[] pixS, int w, int h, float sS)
        {
            const float matchThr = 0.05f;
            float floor = sS * NeutralRejectFloorFrac;
            int len = w * h;
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
            bool[] cur = s_boolPool.Rent(len);
            bool[] nxt = s_boolPool.Rent(len);
            try
            {
                // 高彩度マッチコア(マッチ済み かつ 彩度>=床)。
                for (int i = 0; i < len; i++) cur[i] = strength[i] > matchThr && pixS[i] >= floor;
                // 4 近傍 dilation を ProtectRadius 回(コア近傍を保護領域に広げる)。画像端外は false。
                for (int it = 0; it < NeutralRejectProtectRadius; it++)
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
                    var tmp = cur; cur = nxt; nxt = tmp;
                }
                var protectedCore = cur;
                var strengthL = strength;
                var pixSL = pixS;
                // ソフトな床: floor で二値カットすると、淡赤→クリームの階調(彩度が床付近で揺らぐ帯)で
                // 再着色/非再着色が斑に混在しジャギーノイズになる。floorLo..floor を連続フェードにして
                // 境界を滑らかにする。中性の白背景(彩度<=floorLo)は w=0 で従来通り完全に弾く。
                float floorLo = floor * NeutralRejectRampFrac;
                float rampSpan = Mathf.Max(floor - floorLo, 1e-5f);
                Parallel.For(0, len, po, i =>
                {
                    if (protectedCore[i]) return;
                    float wgt = Mathf.Clamp01((pixSL[i] - floorLo) / rampSpan);
                    if (wgt < 1f) strengthL[i] *= wgt;
                });
            }
            finally
            {
                s_boolPool.Return(cur);
                s_boolPool.Return(nxt);
            }
        }

        /// <summary>
        /// WS-R 内部固め: マッチ領域(strength&gt;matchThr)を erodePx だけ侵食した「内部」の
        /// strength を full(=strength→1 へ achromaWeight 比でフェード)に固める。AA 縁(侵食で
        /// 除いた帯)は元の taper を保つ。極端な無彩ターゲット(白↔黒)で、明るい画素ほど弱く
        /// マッチして元色が残る「中央の段差」を消すための前処理。陰影は後段 recolor の achroma
        /// レンジリマップ(gain≤1)が担う。algorithm.py の SOLIDIFY_ACHROMA_INTERIOR と同値。
        /// </summary>
        private static void SolidifyAchromaInterior(float[] strength, int w, int h, float achromaWeight)
        {
            const float matchThr = 0.05f;
            const int erodePx = 2;
            int len = w * h;
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
            bool[] cur = s_boolPool.Rent(len);
            bool[] nxt = s_boolPool.Rent(len);
            try
            {
                for (int i = 0; i < len; i++) cur[i] = strength[i] > matchThr;
                // 4 近傍 erosion を erodePx 回。画像端の外は「非マッチ」とみなす(scipy 既定と同じ)。
                for (int it = 0; it < erodePx; it++)
                {
                    var curL = cur; var nxtL = nxt;
                    Parallel.For(0, h, po, y =>
                    {
                        int rowOff = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = rowOff + x;
                            bool keep = curL[i]
                                && x > 0 && curL[i - 1]
                                && x < w - 1 && curL[i + 1]
                                && y > 0 && curL[i - w]
                                && y < h - 1 && curL[i + w];
                            nxtL[i] = keep;
                        }
                    });
                    var tmp = cur; cur = nxt; nxt = tmp;
                }
                var interior = cur;
                var strengthL = strength;
                Parallel.For(0, len, po, i =>
                {
                    if (interior[i]) strengthL[i] = strengthL[i] + (1f - strengthL[i]) * achromaWeight;
                });
            }
            finally
            {
                s_boolPool.Return(cur);
                s_boolPool.Return(nxt);
            }
        }

        /// <summary>
        /// WS-R/AA フィデリティ用: 「無彩サンプル / 極端無彩ターゲット」の重み(0..1)を sample/target
        /// 色から求める。有彩サンプル×有彩ターゲットで 0。algorithm.py _achroma_weight と同値。
        /// </summary>
        private static float ComputeAchromaWeight(Color sample, Color target)
        {
            RgbToOklab(sample.r, sample.g, sample.b, out _, out float sa, out float sb);
            RgbToOklab(target.r, target.g, target.b, out float tL, out float ta, out float tb);
            float sC = Mathf.Sqrt(sa * sa + sb * sb);
            float tC = Mathf.Sqrt(ta * ta + tb * tb);
            float achromaSample = Mathf.Clamp01(1f - sC / AchromaSampleC);
            float targetExtremeness = 1f - 4f * tL * (1f - tL);
            float targetAchroma = Mathf.Clamp01(1f - tC / AchromaTargetC);
            return Mathf.Max(achromaSample, targetExtremeness * targetAchroma);
        }

        /// <summary>
        /// 中性背景リジェクト(選択保護)用の無彩重み。**明度に依存しない**。
        /// ComputeAchromaWeight は collapse_blend に targetExtremeness=(1-4tL(1-tL)) を掛けるため、
        /// 中明度グレー(tL≈0.5)で重みが≈0 に落ち、灰色ターゲットでは中性背景リジェクトが発動せず
        /// 「白い背景が灰色になる」破綻が出る(黒/白は extremeness≈1 で発動)。選択保護に必要なのは
        /// 『ターゲットが無彩か』だけで明度は無関係なので、extremeness を外し彩度のみで判定する。
        /// algorithm.py _achroma_select_weight と同期。
        /// </summary>
        private static float ComputeAchromaSelectWeight(Color sample, Color target)
        {
            RgbToOklab(sample.r, sample.g, sample.b, out _, out float sa, out float sb);
            RgbToOklab(target.r, target.g, target.b, out _, out float ta, out float tb);
            float sC = Mathf.Sqrt(sa * sa + sb * sb);
            float tC = Mathf.Sqrt(ta * ta + tb * tb);
            float achromaSample = Mathf.Clamp01(1f - sC / AchromaSampleC);
            float targetAchroma = Mathf.Clamp01(1f - tC / AchromaTargetC);
            return Mathf.Max(achromaSample, targetAchroma);
        }

        /// <summary>
        /// WS-R 形維持リマップ用: マッチ領域を 4 近傍連結成分に分け、各成分の OkLab L の
        /// **地色基準(AchromaRefPercentile=P80)** をその成分の全画素へ配る per-pixel マップを返す。
        /// center 基準を **成分ごとに局所化**することで、ゆるいマスクが白背景(L≈1.0)を巻き込んで
        /// 全体中央値を白へ汚染し、本来の対象(クリーム三角 L≈0.95)がベタ黒へ潰れる不具合を防ぐ。
        /// 基準に中央値でなく高パーセンタイル(P80)を使うのは、暗い珊瑚縁が成分に混入しても
        /// 基準が下振れせず「素材本来の地色レベル」に揃い、並んだ模様(三角列)が均一になるため。
        /// 各成分は自分の地色を基準に再着色され、白背景は黒へ・三角は陰影付きの暗色へ正しく写る。
        /// strength>thr の画素のみ連結対象。マッチ無しは null。
        /// </summary>
        private static float[] BuildComponentMedianLMap(
            Color32[] px, float[] strength, int w, int h, float thr)
        {
            int len = w * h;
            int minX = w, maxX = -1, minY = h, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int rb = y * w;
                for (int x = 0; x < w; x++)
                    if (strength[rb + x] > thr && px[rb + x].a >= 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            }
            if (maxX < 0) return null;

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            int[] label = new int[bw * bh];
            var hists = new List<int[]>();   // hists[lab-1] = 成分の L ヒストグラム(256bin)
            var sizes = new List<int>();
            var queue = new Queue<int>();

            for (int ly = 0; ly < bh; ly++)
            {
                for (int lx = 0; lx < bw; lx++)
                {
                    int li = ly * bw + lx;
                    if (label[li] != 0) continue;
                    if (!(strength[(ly + minY) * w + (lx + minX)] > thr
                          && px[(ly + minY) * w + (lx + minX)].a >= 128)) continue;

                    int lab = hists.Count + 1;
                    var hist = new int[256];
                    int size = 0;
                    label[li] = lab;
                    queue.Enqueue(li);
                    while (queue.Count > 0)
                    {
                        int ci = queue.Dequeue();
                        int cx = ci % bw, cy = ci / bw;
                        int gi = (cy + minY) * w + (cx + minX);
                        RgbToOklab(px[gi].r, px[gi].g, px[gi].b, out float L, out _, out _);
                        hist[Mathf.Clamp((int)(L * 255f), 0, 255)]++;
                        size++;
                        TryEnq(ci - 1, cx > 0);
                        TryEnq(ci + 1, cx < bw - 1);
                        TryEnq(ci - bw, cy > 0);
                        TryEnq(ci + bw, cy < bh - 1);
                    }
                    hists.Add(hist);
                    sizes.Add(size);

                    void TryEnq(int ni, bool inBounds)
                    {
                        if (!inBounds || label[ni] != 0) return;
                        int nx = ni % bw, ny = ni / bw;
                        if (!(strength[(ny + minY) * w + (nx + minX)] > thr
                              && px[(ny + minY) * w + (nx + minX)].a >= 128)) return;
                        label[ni] = lab;
                        queue.Enqueue(ni);
                    }
                }
            }

            var med = new float[hists.Count];
            for (int c = 0; c < hists.Count; c++)
                med[c] = HistValueAtPercentile(hists[c], sizes[c], AchromaRefPercentile, 1f);

            var map = new float[len];
            for (int ly = 0; ly < bh; ly++)
            {
                int rb = (ly + minY) * w;
                for (int lx = 0; lx < bw; lx++)
                {
                    int lab = label[ly * bw + lx];
                    if (lab != 0) map[rb + (lx + minX)] = med[lab - 1];
                }
            }
            return map;
        }

        /// <summary>
        /// WS-R 無彩レンジリマップ用: マッチ領域の OkLab L の (P05, P95, 中央値) を求める。
        /// core 画素(strength>=AchromaRegionCoreThr かつ α>=128)が少なすぎる場合は strength>0 へ
        /// フォールバック。マッチ画素が無ければ false(呼び出し側は achroma パスをスキップ)。
        /// 特定色/座標非依存の領域統計のみ(脚色しない不変条件)。algorithm.py _region_l_range と同期
        /// (percentile はヒストグラム離散化のため Python np.percentile と ≤1/255 の差を許容)。
        /// </summary>
        private static bool TryComputeRegionLRange(
            Color32[] px, float[] strength, out float lo, out float hi, out float mid)
        {
            lo = 0f; hi = 1f; mid = 0.5f;
            int len = px.Length;
            var hist = new int[256];
            int count = 0;
            float thr = AchromaRegionCoreThr;
            for (int pass = 0; pass < 2; pass++)
            {
                Array.Clear(hist, 0, hist.Length);
                count = 0;
                for (int i = 0; i < len; i++)
                {
                    if (strength[i] < thr || px[i].a < 128) continue;
                    RgbToOklab(px[i].r, px[i].g, px[i].b, out float L, out _, out _);
                    hist[Mathf.Clamp((int)(L * 255f), 0, 255)]++;
                    count++;
                }
                if (count >= 50 || pass == 1) break;
                thr = 1e-4f;   // フォールバック: strength>0 の全マッチ画素
            }
            if (count < 1) return false;
            lo  = HistValueAtPercentile(hist, count, 0.05f, 1f);
            hi  = HistValueAtPercentile(hist, count, 0.95f, 1f);
            mid = HistValueAtPercentile(hist, count, 0.50f, 1f);
            return true;
        }

        /// <summary>
        /// ハイライト帯成長: matched core（本体）から「sample→白 直線上に乗った同色相の
        /// 明部画素」へ strength を空間連結で伸ばす。グローバル tolerance を上げずに
        /// 描き込みハイライトの薄い帯を full strength で拾い、ベタ塗り化・取りこぼしを防ぐ。
        ///
        /// 判定（元テクスチャと比較して「本体色が白く飛んだ画素」か）:
        ///   候補 = pV&gt;sV ∧ 同色相(hd&lt;hueCap) ∧ sample→白 軸からの残差&lt;eps
        ///          ∧ sS×MinSatFrac ≤ pS &lt; sS
        /// 安全ゲート（俯瞰: 周囲の構造を見る）:
        ///   候補のうち core(strength≥THR) に 4 連結で到達できる画素のみ採用。
        ///   孤立した同系色の島（別パーツ・白素材）は core に触れないので入らない。
        /// 採用画素は strength=1 にし、後段の P5 白寄せで階調を保ったまま再着色する。
        /// </summary>
        private static void GrowHighlightBand(
            float[] strength, Color32[] originalPixels,
            float[] pixH, float[] pixS, float[] pixV, ColorZone zone, int w, int h)
        {
            float sH, sS, sV;
            Color.RGBToHSV(zone.sampleColor, out sH, out sS, out sV);
            if (sS < HlBandMinSampleSat) return;

            float sR = zone.sampleColor.r, sG = zone.sampleColor.g, sB = zone.sampleColor.b;
            float dR = 1f - sR, dG = 1f - sG, dB = 1f - sB;   // sample → 白 方向
            float dsq = dR * dR + dG * dG + dB * dB;
            if (dsq < 1e-6f) return;

            float hueCap = Mathf.Max(0.05f, zone.tolerance * 0.3f);
            float satFloor = sS * HlBandMinSatFrac;

            int len = w * h;
            bool[] candidate = new bool[len];
            bool[] visited = new bool[len];
            var queue = new Queue<int>();

            // 候補判定: 各画素は独立(他画素を参照しない)なので並列化する。candidate[] は
            // 走査順に依存せず、書き込みは distinct index のため出力は逐次版とビット不変。
            var hlbPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
            Parallel.For(0, len, hlbPo, i =>
            {
                float pV = pixV[i];
                if (pV > sV)
                {
                    float pS = pixS[i];
                    if (pS < sS && pS >= satFloor)
                    {
                        float hd = Mathf.Abs(pixH[i] - sH);
                        if (hd > 0.5f) hd = 1f - hd;
                        if (hd < hueCap)
                        {
                            Color32 op = originalPixels[i];
                            float pr = op.r / 255f, pg = op.g / 255f, pb = op.b / 255f;
                            float ox = pr - sR, oy = pg - sG, oz = pb - sB;
                            float wv = (ox * dR + oy * dG + oz * dB) / dsq;
                            if (wv < 0f) wv = 0f; else if (wv > 1f) wv = 1f;
                            float rr = pr - (sR + wv * dR);
                            float rg = pg - (sG + wv * dG);
                            float rb = pb - (sB + wv * dB);
                            if (rr * rr + rg * rg + rb * rb < HlBandAxisEps * HlBandAxisEps)
                                candidate[i] = true;
                        }
                    }
                }
            });

            // core をシードとして収集する(BFS の Queue は非スレッドセーフ・逐次のまま。
            // enqueue 順は従来と同一の i 昇順で、BFS 到達集合も順序非依存のため出力不変)。
            for (int i = 0; i < len; i++)
            {
                if (strength[i] >= HlBandCoreThreshold)
                {
                    visited[i] = true;
                    queue.Enqueue(i);
                }
            }

            if (queue.Count == 0) return;

            // core から候補領域へ 4 連結 BFS（候補セルのみ拡張）
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % w;
                int y = idx / w;
                TryVisit(idx - 1, x > 0);
                TryVisit(idx + 1, x < w - 1);
                TryVisit(idx - w, y > 0);
                TryVisit(idx + w, y < h - 1);
            }

            void TryVisit(int ni, bool inBounds)
            {
                if (!inBounds || visited[ni] || !candidate[ni]) return;
                visited[ni] = true;
                if (strength[ni] < 1f) strength[ni] = 1f;
                queue.Enqueue(ni);
            }
        }

        // 共通マスクとゾーン別マスクを OR 結合した除外判定。
        // どちらか片方でも true ならそのピクセルはこのゾーン処理から除外される。
        private static bool IsExcludedCombined(int x, int y, int texW, int texH,
            ulong[] commonMask, ulong[] zoneMask, int maskW, int maskH)
        {
            if (commonMask == null && zoneMask == null) return false;
            if (maskW <= 0 || maskH <= 0) return false;
            int mx = Mathf.Clamp(x * maskW / texW, 0, maskW - 1);
            int my = Mathf.Clamp(y * maskH / texH, 0, maskH - 1);
            int idx = my * maskW + mx;
            if (MaskSnapshot.GetBit(commonMask, idx)) return true;
            if (MaskSnapshot.GetBit(zoneMask, idx)) return true;
            return false;
        }

        /// <summary>
        /// ガウシアンブラーを src に適用して dst に書き込む。
        /// 内部 temp バッファは ArrayPool から借用・返却するのでヒープアロケーションなし。
        /// dst は呼び出し元が事前に確保すること（ArrayPool.Rent 推奨）。
        /// radius &lt; 1 のとき何もしない（dst は未定義のまま）。
        /// 戻り値: ブラー処理を行った場合 true、スキップした場合 false。
        /// </summary>
        private static bool GaussianBlur(float[] src, float[] dst, int w, int h, float sigma,
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1)
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
            var gaussPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
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

        private static void ConstrainBlur(float[] blurred, float[] original, int w, int h, int radius)
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
                for (int i = 0; i < len; i++)
                    mask[i] = original[i] > 0f ? 1f : 0f;

                BoxFilterSum(mask, neighborSum, w, h, radius);

                Parallel.For(0, len, new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() }, i =>
                {
                    if (original[i] > 0f) return; // already matched
                    if (neighborSum[i] <= 0f)
                        blurred[i] = 0f;
                });
            }
            finally
            {
                if (neighborSum != null) s_floatPool.Return(neighborSum);
                if (mask        != null) s_floatPool.Return(mask);
            }
        }

        /// <summary>
        /// strength &gt; thr の画素を囲むバウンディングボックス(min/max)を求める。
        /// 後段パス(穴埋め/境界回復/ブラー)を実マッチ範囲＋余白に限定し、全画素走査を避けるために使う。
        /// </summary>
        /// <returns>マッチ画素が 1 つ以上あれば true(false のとき bbox は空で、後段パスは no-op)。</returns>
        private static bool TryComputeStrengthBBox(float[] strength, int w, int h, float thr,
            out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = w; minY = h; maxX = -1; maxY = -1;
            for (int y = 0; y < h; y++)
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
                    if (rowMinX < minX) minX = rowMinX;
                    if (rowMaxX > maxX) maxX = rowMaxX;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            return maxX >= 0;
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
            int boxMinX = 0, int boxMinY = 0, int boxMaxX = -1, int boxMaxY = -1)
        {
            if (passes <= 0) return;

            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけ近傍走査する(P2-7)。
            // 矩形外は呼び出し側が「処理前後とも 0」を保証するので走査を省いても出力ビット不変。
            // Array.Copy(全画素)は安価なので残し、近傍を読む重いループだけを矩形に絞る。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            int len = w * h;
            float[] buffer = s_floatPool.Rent(len);
            var fillPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
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
            Color32[] originalPixels = null, float chromaConfidence = 1f)
        {
            if (passes <= 0) return;

            // bbox 未指定(boxMaxX<0)なら全画素。指定時はその矩形内だけ走査する(P2-7、出力ビット不変)。
            if (boxMaxX < 0) { boxMinX = 0; boxMinY = 0; boxMaxX = w - 1; boxMaxY = h - 1; }

            float sH, sS, sV;
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);
            // WS-M: relaxed ゲートの RGB ブレンド用 sample RGB(0..1)。
            float rcSampR = sampleColor.r, rcSampG = sampleColor.g, rcSampB = sampleColor.b;

            int len = w * h;
            float[] buffer = s_floatPool.Rent(len);
            var recoverPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
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
                            rpR, rpG, rpB, rcSampR, rcSampG, rcSampB, chromaConfidence);
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
            float sR = 0f, float sG = 0f, float sB = 0f, float chromaConfidence = 1f)
        {
            // ColorZone.GetColorMatchScoresと同じ動的頃値：暗いサンプルほどグレースケールモードの範囲を広げる
            float effectiveChromaThreshold = Mathf.Lerp(0.30f, 0.05f, Mathf.Clamp01(sV / 0.20f));

            // 純白装飾はそのまま残す: relaxedSatMin 未満は弾く（ハードゲート）
            // ただしサンプル自体が高彩度の場合のみ適用（暗サンプルの低彩度ピクセルは通過させる）
            if (pS < relaxedSatMin && sS > effectiveChromaThreshold) return 0f;

            // サンプルが暗い型（指定出来ない彩度）の場合: 動的頃値を使って判定
            if (sS <= effectiveChromaThreshold)
            {
                // 主経路(GetColorMatchScores グレーモード)と同じ RGB 距離で判定する。
                // 旧実装は値距離 |pV-sV| のみで、明るいサンプルでは色に関係なく「明るい」だけで
                // 一致したため、珊瑚やバンダナ縁(salmon→白)が境界回復/穴埋めで黒く滲み、三角の
                // 元領域を超えて黒がはみ出していた。RGB 距離なら主経路と同じく珊瑚(距離>tol)を拒否し、
                // 三角自身の AA 縁(cream 寄り)だけを回復する。暗サンプルでは Lerp で pS へ収束=従来同等。
                float dr = pR - sR, dg = pG - sG, db = pB - sB;
                float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * 0.57735027f;
                float darknessFactor = Mathf.Clamp01((0.3f - sV) / 0.3f);
                float effectiveDist = Mathf.Lerp(rgbDist, pS, darknessFactor);
                // 彩度整合ゲート(GetColorMatchScores のグレーモードと同じ)。サンプルが微小な tint を
                // 持つとき、それより著しく中性寄りの候補(純白 UV 背景等)を距離加算でソフト排除する。
                // 明るいクリームサンプルでは値距離だと純白(pV≈sV)が一致するため、ここでも必要。
                // sS≈0(真の無彩サンプル)では作動しない=従来挙動を維持。明部限定(gateWeight)で
                // 暗いサンプル(中性が正常)では矛盾を避けフェードさせる。ColorZone.cs と同期。
                if (sS > ChromaGateActivateSat)
                {
                    float gateWeight = Mathf.Clamp01(sV / 0.3f);
                    float satFloor = sS * ChromaGateFloorFrac;
                    float shortfall = Mathf.Clamp01((satFloor - pS) / Mathf.Max(satFloor, 1e-4f));
                    effectiveDist += shortfall * ChromaGatePenalty * tolerance * gateWeight;
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

            // WS-M (2026-06-14): 低彩度サンプル(白/灰)では HSV 距離が hue 支配になり、同色相の
            // 高彩度色(off-white→赤バンダナ等)を弾けず境界回復が別色を周囲へ大量スピルさせる。
            // プライマリ(CalculateHybridDistance)と同じ RGB 距離ブレンドで整合させる:
            // dist = lerp(rgbDist, hsvDist, chromaConfidence)。有彩サンプルは cc≈1 で従来式と一致。
            // algorithm.py relaxed_match_strength と同期。数学レビュー §2.3 対応。
            if (chromaConfidence < 0.999f)
            {
                float dr = pR - sR, dg = pG - sG, db = pB - sB;
                float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * 0.57735027f;
                dist = rgbDist * (1f - chromaConfidence) + dist * chromaConfidence;
            }

            // シャドウ（暗い色）の境界距離許容は廃止（プライマリ CalculateHybridDistance と同期、
            // algorithm.py DARK_FORGIVENESS_DISTANCE_REDUCE=False）。緩和マッチ(穴埋め/境界回復)で
            // near-black の別マテリアルを tolerance 内へ逆送し巻き込みを広げる経路だったため除去する。

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

        // ───────────── OkLab 知覚色空間ヘルパー (リング除去の中核) ─────────────
        // リング(ドーナツ)の根本原因は「HSV の V/S は知覚的でないため、S/V を保持して
        // 色相だけ変えると元の単調な輝度 falloff が変換先の色の輝度応答で非単調化する」こと。
        // OkLab で L(知覚明度)を保持し彩度(a,b)を sample→target の線形写像で移すことで、
        // 明度構造を完全保存しリング・白部サイズ変化・ベタ塗りを構造的に排除する。
        //
        // パフォーマンス(P1-3): 再着色ホットループは画素あたり ~9 回の Mathf.Pow を呼んでいた
        // (Pow は乗算の数十倍コスト)。Pow を以下で置換する:
        //  - SrgbToLinear: 入力が byte/255 の 256 通りしかない per-pixel 経路は 256 エントリ LUT で
        //    厳密置換(s_srgbToLinearLut)。任意 float 入力(ゾーン定数)は従来どおり Pow。
        //  - Cbrt: Mathf.Pow(x,1/3) は cbrt の近似。Python 参照は np.cbrt なので MathF.Cbrt へ置換
        //    すると参照に近づき、かつ Pow を除去できる。
        //  - LinearToSrgb: 出力は byte に量子化されるため LUT+線形補間で視覚的に無損失に置換。
        private const int LinToSrgbLutSize = 4096;
        private static readonly float[] s_srgbToLinearLut = BuildSrgbToLinearLut();
        private static readonly float[] s_linearToSrgbLut = BuildLinearToSrgbLut();

        private static float[] BuildSrgbToLinearLut()
        {
            // インデックス = 0..255 の byte 値。SrgbToLinear(i/255f) と完全一致。
            var lut = new float[256];
            for (int i = 0; i < 256; i++) lut[i] = SrgbToLinear(i / 255f);
            return lut;
        }

        private static float[] BuildLinearToSrgbLut()
        {
            // インデックス k は線形値 k/LinToSrgbLutSize に対応。線形補間用に末尾 +1 エントリ。
            var lut = new float[LinToSrgbLutSize + 1];
            for (int k = 0; k <= LinToSrgbLutSize; k++)
                lut[k] = LinearToSrgbExact((float)k / LinToSrgbLutSize);
            return lut;
        }

        // 任意 float 入力向けの厳密版(ゾーン定数の RgbToOklab と LUT 構築に使用)。
        private static float SrgbToLinear(float c)
        {
            c = Mathf.Clamp01(c);
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        // LUT 構築専用の厳密版。
        private static float LinearToSrgbExact(float c)
        {
            c = Mathf.Clamp01(c);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        // ホットループ用 LUT 版(4096 分割 + 線形補間)。LinearToSrgbExact と視覚的に無損失。
        private static float LinearToSrgb(float c)
        {
            if (c <= 0f) return 0f;
            if (c >= 1f) return 1f;
            float f = c * LinToSrgbLutSize;
            int k = (int)f;                 // c<1 なので 0..LinToSrgbLutSize-1
            float frac = f - k;
            float a = s_linearToSrgbLut[k];
            return a + (s_linearToSrgbLut[k + 1] - a) * frac;
        }

        private static float Cbrt(float x)
        {
            // MathF.Cbrt は負値も正しく扱い、Mathf.Pow(x,1/3) より高精度(Python np.cbrt 相当)。
            return MathF.Cbrt(x);
        }

        // OkLab 行列本体。linear RGB から OkLab を計算する。
        private static void OklabFromLinear(float lr, float lg, float lb,
            out float L, out float a, out float bb)
        {
            float l = 0.4122214708f * lr + 0.5363325363f * lg + 0.0514459929f * lb;
            float m = 0.2119034982f * lr + 0.6806995451f * lg + 0.1073969566f * lb;
            float s = 0.0883024619f * lr + 0.2817188376f * lg + 0.6299787005f * lb;
            float l_ = Cbrt(l), m_ = Cbrt(m), s_ = Cbrt(s);
            L = 0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_;
            a = 1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_;
            bb = 0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_;
        }

        // 任意 float 入力版(ゾーン定数: sample/target 色)。
        private static void RgbToOklab(float r, float g, float b,
            out float L, out float a, out float bb)
        {
            OklabFromLinear(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b), out L, out a, out bb);
        }

        // byte 入力版(per-pixel 経路)。SrgbToLinear を 256 エントリ LUT で厳密に表引きする。
        private static void RgbToOklab(byte r, byte g, byte b,
            out float L, out float a, out float bb)
        {
            OklabFromLinear(s_srgbToLinearLut[r], s_srgbToLinearLut[g], s_srgbToLinearLut[b],
                out L, out a, out bb);
        }

        private static void OklabToRgb(float L, float a, float b,
            out float r, out float g, out float bb)
        {
            float l_ = L + 0.3963377774f * a + 0.2158037573f * b;
            float m_ = L - 0.1055613458f * a - 0.0638541728f * b;
            float s_ = L - 0.0894841775f * a - 1.2914855480f * b;
            float l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
            float lr = +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
            float lg = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
            float lb = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
            r = LinearToSrgb(lr);
            g = LinearToSrgb(lg);
            bb = LinearToSrgb(lb);
        }

        // RecolorPixel のゾーン不変パラメータ(再着色ホットループの前に 1 回だけ確定する値)をまとめた
        // readonly struct。in 渡しで per-pixel のコピーを避ける。フィールドは旧 RecolorPixel 引数を
        // そのまま転記(型・順序・意味を保持)。約30引数の緩和=シグネチャ整理のみで数値ロジックは不変
        // (architecture_review_2026-06-27 §4.2(C))。
        private readonly struct RecolorParams
        {
            public readonly float okMagScale, okTa, okTb;
            public readonly bool okGray;
            public readonly float okGa, okGb;
            public readonly float okSL, okTL, okSC, okChromaMaxMag;
            public readonly float valueBlend, shadowDesaturation;
            public readonly float sS, tR, tG, tB;
            public readonly float washR, washG, washB, washV;
            public readonly bool applyHighlightWash;
            public readonly float achromaWeight, osat;
            public readonly bool hasRegL;
            public readonly float regLlo, regLhi;
            public RecolorParams(
                float okMagScale, float okTa, float okTb, bool okGray, float okGa, float okGb,
                float okSL, float okTL, float okSC, float okChromaMaxMag,
                float valueBlend, float shadowDesaturation, float sS, float tR, float tG, float tB,
                float washR, float washG, float washB, float washV, bool applyHighlightWash,
                float achromaWeight, float osat, bool hasRegL, float regLlo, float regLhi)
            {
                this.okMagScale = okMagScale; this.okTa = okTa; this.okTb = okTb;
                this.okGray = okGray; this.okGa = okGa; this.okGb = okGb;
                this.okSL = okSL; this.okTL = okTL; this.okSC = okSC; this.okChromaMaxMag = okChromaMaxMag;
                this.valueBlend = valueBlend; this.shadowDesaturation = shadowDesaturation;
                this.sS = sS; this.tR = tR; this.tG = tG; this.tB = tB;
                this.washR = washR; this.washG = washG; this.washB = washB; this.washV = washV;
                this.applyHighlightWash = applyHighlightWash;
                this.achromaWeight = achromaWeight; this.osat = osat;
                this.hasRegL = hasRegL; this.regLlo = regLlo; this.regLhi = regLhi;
            }
        }

        private static Color32 RecolorPixel(
            byte oRb, byte oGb, byte oBb,
            float oV, float alpha,
            in RecolorParams p,
            float regLmid)
        {
            // ゾーン不変パラメータをローカルへ展開する。以降の本体ロジックは従来のまま=出力バイト不変。
            float okMagScale = p.okMagScale, okTa = p.okTa, okTb = p.okTb;
            bool okGray = p.okGray;
            float okGa = p.okGa, okGb = p.okGb;
            float okSL = p.okSL, okTL = p.okTL, okSC = p.okSC, okChromaMaxMag = p.okChromaMaxMag;
            float valueBlend = p.valueBlend, shadowDesaturation = p.shadowDesaturation;
            float sS = p.sS, tR = p.tR, tG = p.tG, tB = p.tB;
            float washR = p.washR, washG = p.washG, washB = p.washB, washV = p.washV;
            bool applyHighlightWash = p.applyHighlightWash;
            float achromaWeight = p.achromaWeight, osat = p.osat;
            bool hasRegL = p.hasRegL;
            float regLlo = p.regLlo, regLhi = p.regLhi;
            // === OkLab 明度マップ + 彩度(向きは target 色相に均一化)リカラー ===
            // L: 2区間線形リマップ (0→0, sL→tL, 1→1)。base を target 明度へ寄せる。単調維持
            //    (リング無し)・ガンマット内(クリップ無し)・白→白/黒→黒。明度を完全保持すると
            //    暗い色→黄色が brown 化するため base は target 明度に合わせる。
            // (a,b)_new = |chroma|·(osat/sC)·(target 色相単位ベクトル) ← 大きさは元 chroma に比例、
            //    向きは target に均一化。旧版の「色相回転保持」は単色ロゴの AA縁でオレンジの色相
            //    ノイズを生んだため、向きを揃えて L を保ったまま色相を均一化する。
            RgbToOklab(oRb, oGb, oBb, out float oL, out float oa, out float ob);
            float oC = Mathf.Sqrt(oa * oa + ob * ob);   // 元画素の chroma (L 彩度ゲートでも使う)
            float na, nb;
            if (okGray)
            {
                // sample が無彩(グレー): target chroma を一律付与(旧 newS=tS 相当)
                na = okGa;
                nb = okGb;
            }
            else
            {
                float mag = oC * okMagScale;            // |chroma|/sC · osat
                // WS-R: 無彩サンプルでは oC/sC が微小彩度ノイズを増幅(脚色)。uniform target 彩度
                // (osat, oC 非依存)へ achromaWeight でフェードし増幅を止める。weight=0 で従来式。
                if (achromaWeight > 1e-4f)
                    mag = mag * (1f - achromaWeight) + osat * achromaWeight;
                // WS-R 有彩版: tC > sC のとき output chroma = mag*tC が sC*Factor を超えないよう制限。
                // achromaWeight=1 時は上記で mag=osat 固定済みなのでキャップは no-op。
                // 上限 mag はゾーン定数 okChromaMaxMag に事前算出済み(キャップ非適用時は +∞ で
                // この比較は no-op)。旧版は per-pixel で tC=sqrt(okTa²+okTb²) と maxMag を再計算していた。
                if (mag > okChromaMaxMag) mag = okChromaMaxMag;
                na = mag * okTa;                        // 向きは target 色相 (zTa, zTb)
                nb = mag * okTb;
            }
            // 2区間線形リマップ: [0,sL]→[0,tL], [sL,1]→[tL,1]。sL→tL を不動点に base を target 明度へ。
            float remapL = oL <= okSL
                ? (oL / Mathf.Max(okSL, 1e-4f)) * okTL
                : okTL + (oL - okSL) / Mathf.Max(1f - okSL, 1e-4f) * (1f - okTL);
            // 彩度ゲート付き L 再マップ (2026-06-07): 低彩度画素では remap(=明るさの持ち上げ)を抑え、
            // 元の L(暗さ)を保持する。リング/brown 化は「本来のベース色」=高彩度画素で起きる現象なので
            // remap が必要なのは高彩度画素だけ。一方、ベース×暗部/白の混色や AA 縁(低彩度)に remap を
            // かけると、target が sample より知覚的に明るいとき暗部が持ち上がり「明るい灰スペック」
            // =ロゴ周辺の白/灰ノイズになる。chroma_frac=oC/(sC·FULL_FRAC) で彩度が sample の FULL_FRAC 割に
            // 達したらフル remap、それ未満は元 L を保持。sample 彩度に対する相対量なので色非依存。
            // sample 無彩(okGray)時は従来どおり一律 remap。algorithm.py の OKLAB_REMAP_* と同期。
            float effRemapL = remapL;
            if (!okGray && okSC > 1e-4f)
            {
                float chromaFrac = Mathf.Clamp01(oC / (okSC * OklabRemapFullChromaFrac));
                effRemapL = oL + (remapL - oL) * chromaFrac;
            }
            // valueBlend=1 でフル階調、<1 で target フラットトーンへ寄せる。
            float nL = okTL * (1f - valueBlend) + effRemapL * valueBlend;

            // WS-R: 無彩再着色パスの L。マッチ領域の L レンジ[lo,hi]を target 側ヘッドルームへ
            // 順序保存で収める(2区間リマップ・彩度ゲートを迂回)。白い三角→黒のまだら/明度崩壊を直す。
            // algorithm.py recolor_pixels と同期。weight=0(有彩×有彩)では完全 no-op=バイト不変。
            if (achromaWeight > 1e-4f && hasRegL)
            {
                // 形(立体感)維持: 領域中央値を target 側の控えめ offset(center)に置き、中央値からの
                // 偏差を AchromaFormGain 倍して陰影を知覚可能な大きさへ拡張する。暗部は 0 へ、明部は
                // center 近辺の暗灰に収め、白残り(段差)は clamp で防ぐ。単調・領域統計由来で特定座標
                // 非依存。元の微小陰影をそのまま写すと暗部/明部で知覚的に平坦化(ベタ黒/ベタ白)するため、
                // 控えめに増幅する(知覚補償)。algorithm.py recolor_pixels と同期。
                float center = okTL < 0.5f ? AchromaFormOffset : (1f - AchromaFormOffset);
                float rangeRemap = Mathf.Clamp(center + (oL - regLmid) * AchromaFormGain, 0f, 1f);
                float nLAchroma = okTL * (1f - valueBlend) + rangeRemap * valueBlend;
                nL = nL * (1f - achromaWeight) + nLAchroma * achromaWeight;
            }

            // 暗部脱彩 (旧 shadowDesaturation の OkLab 等価): 暗い画素の chroma を最大 50% 抑制。
            if (shadowDesaturation > 0f && oV < shadowDesaturation)
            {
                float shadowIntensity = Mathf.Clamp01((shadowDesaturation - oV) / shadowDesaturation);
                float fac = 1f - 0.5f * shadowIntensity;
                na *= fac;
                nb *= fac;
            }

            OklabToRgb(nL, na, nb, out float outR, out float outG, out float outB);
            Color result = new Color(Mathf.Clamp01(outR), Mathf.Clamp01(outG), Mathf.Clamp01(outB), 1f);

            // ハイライト合成 (P5 軸射影版・residual なし / 2026-05-26):
            // ピクセルを「sample → (1,1,1) 白直線」に射影して進行度 w を取り、
            // 同じ w を使って target 軸上の対応点を求める。residual (軸からのずれ) は
            // 捨てる — これにより青の色相歪みが target 側 (赤) にコピーされてピンク化する
            // P5 純正版の副作用を構造的に除去する。
            //
            //   proj_t = target + w * (white - target)
            //   result = Lerp(hsv_result, proj_t, valRise)
            //
            // 直感: pixel が sample から白に向けて 0.99 進んでいるなら (ハイライト中心)、
            //       target からも白に向けて 0.99 進んだ点が出力色。中心は完全な白では
            //       なく「target に向けて 1% 染まった白」(= わずかに赤い白)。
            //       周辺 (w=0.5) は target と white の中間 (例: 赤と白で薄い赤)。
            //
            // 境界 (oV ≤ sV) は valRise=0 で hsv_result に切り戻すため不連続なし。
            // wash 用サンプル(washR/G/B/V): 既定は match と同じ sample。俯瞰スポイト補正では
            // パーツ地色(同色相・低V)が渡され、ハイライト合成 (oV>washV) がドーム全体に効く。
            // match/base は sample のままなので再着色範囲は不変(新規 FP なし)。
            //
            // applyHighlightWash ゲート (2026-06-04): この白寄せ射影は既定 OFF のオプトイン。
            // OFF のときは HSV transfer のみで明部の明度・彩度構造を温存する。Python 参照
            // algorithm.py recolor_pixels の `if apply_highlight_wash:` と同期。
            if (applyHighlightWash && sS > 0.01f && oV > washV)
            {
                float dR = 1f - washR;
                float dG = 1f - washG;
                float dB = 1f - washB;
                float dirSq = dR * dR + dG * dG + dB * dB;
                if (dirSq > 1e-6f)
                {
                    // wash 射影は 0..1 の RGB で行うため byte 入力をここで float に戻す。
                    float oR = oRb / 255f, oG = oGb / 255f, oB = oBb / 255f;
                    float pR = oR - washR;
                    float pG = oG - washG;
                    float pB = oB - washB;
                    float w = Mathf.Clamp01((pR * dR + pG * dG + pB * dB) / dirSq);

                    // target 軸上の対応点 (residual は捨てる)
                    float projTR = tR + w * (1f - tR);
                    float projTG = tG + w * (1f - tG);
                    float projTB = tB + w * (1f - tB);

                    // 軸残差フェード (2026-06-04): 元画素が wash→白 軸からどれだけ外れて
                    // いるか(resid)に応じて白寄せを減衰させる。
                    //   真の鏡面ハイライト = 地色が光で白く飛んだもの = 軸上(resid≈0) → フル白寄せ
                    //   有彩の模様        = 別色・高彩度        = 軸外(resid 大)  → 白寄せ 0
                    // 軸外で proj_t(=軸上の脱彩点)への置換を止めるので、明るい同系色の模様まで
                    // 白化して模様が壊れる過剰白化を構造的に排除する。残差を捨てる(P5)のは
                    // resid≈0 の画素に限られるためピンク化抑止特性も維持。dev_safe algorithm.py と同期。
                    float axR = washR + w * dR;
                    float axG = washG + w * dG;
                    float axB = washB + w * dB;
                    float rr = oR - axR, rg = oG - axG, rb = oB - axB;
                    float resid = Mathf.Sqrt(rr * rr + rg * rg + rb * rb);
                    float axisFade = Mathf.Clamp01(1f - resid / HlBandAxisEps);

                    // 境界連続性のため valRise でフェード (oV=washV で valRise=0、hsv_result に戻る)
                    float valRise = Mathf.Clamp01((oV - washV) / Mathf.Max(0.05f, 1f - washV)) * axisFade;
                    result.r = Mathf.Lerp(result.r, projTR, valRise);
                    result.g = Mathf.Lerp(result.g, projTG, valRise);
                    result.b = Mathf.Lerp(result.b, projTB, valRise);
                }
            }

            result.a = alpha;
            return result;
        }

        /// <summary>
        /// プレビュー表示用にダウンサンプルした Color32 配列を生成する。
        /// 各 dst 画素は対応する src ブロック内のピクセル平均色になる（box フィルタ）。
        /// </summary>
        public static Color32[] BoxDownsample(Color32[] src, int srcW, int srcH,
            int dstW, int dstH, float scale)
        {
            Color32[] dst = new Color32[dstW * dstH];
            // 合計値が int の範囲を超えないように long を使用。
            // 例: 8192x8192 の画像を 512x512 に縮小すると 1 ピクセル当たり 256 サンプル以上、
            // 合計が byte(255) * 256 = 65280 を超え、バッチサイズ次第では int でも桁数が
            // 増えるため安全側に倒す。
            // 行ごとに dst の異なる領域へ書き込み src は読み取り専用なので、y で行並列化できる
            // (出力ビット不変)。4K→512 で 16.8M 画素読みのためメインスレッド/ジョブどちらでも効く。
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism() };
            Parallel.For(0, dstH, po, y =>
            {
                int sy0 = Mathf.FloorToInt(y / scale);
                int sy1 = Mathf.Min(Mathf.CeilToInt((y + 1f) / scale) - 1, srcH - 1);
                for (int x = 0; x < dstW; x++)
                {
                    int sx0 = Mathf.FloorToInt(x / scale);
                    int sx1 = Mathf.Min(Mathf.CeilToInt((x + 1f) / scale) - 1, srcW - 1);
                    long r = 0, g = 0, b = 0, a = 0;
                    int count = 0;
                    for (int ky = sy0; ky <= sy1; ky++)
                        for (int kx = sx0; kx <= sx1; kx++)
                        {
                            var p = src[ky * srcW + kx];
                            r += p.r; g += p.g; b += p.b; a += p.a;
                            count++;
                        }
                    if (count <= 0) count = 1; // ゼロ除算ガード（理論上は到達しないが念のため）
                    dst[y * dstW + x] = new Color32(
                        (byte)(r / count), (byte)(g / count),
                        (byte)(b / count), (byte)(a / count));
                }
            });
            return dst;
        }
    }
}
