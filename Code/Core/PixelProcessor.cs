// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// テクスチャの再着色アルゴリズム本体。
    /// UnityEditor / EditorWindow / AssetDatabase に依存しない純粋ロジックだけを保持する。
    /// バックグラウンドスレッドからの呼び出しを前提にしている。
    /// </summary>
    internal static partial class PixelProcessor
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

        // ───────────── フェーズ別計測(出力不変・加算のみ) ─────────────
        // ProcessPixelsArray の各段の所要時間を全ゾーン合算で累積し PerfReport に載せる。
        // どのフェーズが重いかを実 C# で測ってから最適化するための計測専用(値・分岐は変えない)。
        private const int PhHsv = 0, PhMatch = 1, PhHighlight = 2, PhFloodFill = 3,
            PhHoleFill = 4, PhBoundary = 5, PhBlur = 6, PhDecontam = 7,
            PhRegionStats = 8, PhRecolor = 9, PhaseCount = 10;
        private static readonly string[] s_perfPhaseNames =
        {
            "HSV", "Match", "Highlight", "FloodFill", "HoleFill",
            "BoundaryRecover", "Blur", "Decontaminate", "RegionStats", "Recolor",
        };

        // ───────────── 選択キャッシュのキー生成(出力不変高速化) ─────────────
        // packed mask の内容ハッシュ(FNV-1a 64bit)。マスク編集を選択キーに反映するため。
        private static ulong MaskHash(ulong[] m)
        {
            if (m == null) return 0UL;
            ulong h = 1469598103934665603UL; // FNV offset basis
            for (int i = 0; i < m.Length; i++) { h ^= m[i]; h *= 1099511628211UL; }
            return h ^ (ulong)m.Length;
        }

        // 「選択(マッチ→マスク再適用)に影響する入力だけ」から決定論的なキーを作る。
        // ターゲット色・valueBlend・出力彩度・シャドウ脱彩・wash・autoRecolorAnchor は **含めない**
        // (これらは再着色のみに効くので、変えてもキーは同じ=選択キャッシュがヒットする)。float は
        // ビット表現で完全一致判定(丸め衝突を避ける)。曖昧なものは安全側で含める(ミスが増えるだけ)。
        private static string BuildSelectionKey(
            ColorZone z, float edgeFeather, int aaCleanup, int holeFillPasses, int holeFillMinNeighbors,
            float relaxedSatMin, float relaxedSatRamp, ulong[] commonMask, ulong[] zoneMask,
            int maskW, int maskH)
        {
            var sb = new StringBuilder(320);
            void F(float v) { sb.Append(BitConverter.SingleToInt32Bits(v)); sb.Append(','); }
            void I(int v) { sb.Append(v); sb.Append(','); }
            void B(bool v) { sb.Append(v ? '1' : '0'); sb.Append(','); }
            void C(Color c) { F(c.r); F(c.g); F(c.b); F(c.a); }

            I((int)z.mode); B(z.enabled);
            C(z.sampleColor);
            int extraN = z.extraSamples?.Count ?? 0;
            I(extraN);
            for (int i = 0; i < extraN; i++) C(z.extraSamples[i]);
            F(z.tolerance);
            // 矩形モードの選択範囲
            F(z.uvRect.x); F(z.uvRect.y); F(z.uvRect.width); F(z.uvRect.height);
            B(z.useFloodFill); F(z.seedUV.x); F(z.seedUV.y);
            F(z.edgeSoftness); F(z.saturationStrictness); F(z.valueWeight); F(z.satDistWeight);
            F(z.satRampScale); F(z.shadowForgivenessSatMin); F(z.chromaThreshold); F(z.saturationGuard);
            B(z.highlightRecovery); B(z.highlightBandExpand);
            // 選択に効くグローバル後段設定
            F(edgeFeather); I(aaCleanup); I(holeFillPasses); I(holeFillMinNeighbors);
            F(relaxedSatMin); F(relaxedSatRamp);
            // マスク内容(common + zone)。ビット列のハッシュだけでは、同じビット列を別の寸法で
            // 解釈したケースを区別できない（マスクは packed ulong[] で寸法が別持ちのため、
            // 寸法が変われば同じビット列でも指す画素が変わる）。寸法もキーに入れる
            // （レビュー §4 低 / 監査 L-6）。
            I(maskW); I(maskH);
            sb.Append(MaskHash(commonMask)); sb.Append(';');
            sb.Append(MaskHash(zoneMask)); sb.Append(';');
            return sb.ToString();
        }

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
        // originalPixels(入力画素のスナップショット)用。従来は毎回 new Color32[len](4K で 67MB)を
        // LOH に確保していた。Rent はゼロ初期化されないが Array.Copy で全域上書きするので問題なし。
        private static readonly ArrayPool<Color32> s_color32Pool =
            ArrayPool<Color32>.Create(PoolMaxArrayLength, maxArraysPerBucket: 4);
        // 連結成分ラベリング(label / BFS スタック)用。従来は呼び出しごとに new int[bw*bh]
        // (4K 全面マッチで 67MB)を LOH へ確保していた。Rent はゼロ初期化しないので label は
        // 使用前に Array.Clear すること。
        private static readonly ArrayPool<int> s_intPool =
            ArrayPool<int>.Create(PoolMaxArrayLength, maxArraysPerBucket: 4);

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
            PreviewParityCache parityCache = null,
            SelectionCache selectionCache = null)
        {
            ProcessPixelsArray(pixels, w, h, masks, sortedZones, edgeFeather, antiAliasCleanup,
                holeFillPasses, holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp,
                originX, originY, fullW, fullH, CancellationToken.None,
                useDecontamination, decontaminationRadius, decontaminationInteriorThreshold,
                debug, parityCache, selectionCache);
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
            PreviewParityCache parityCache = null,
            SelectionCache selectionCache = null)
        {
            // 引数検証。ここが無いと、不正な引数（null / 長さ不足）で Array.Copy が投げたとき
            // 直前に Rent したプール配列（4K で 64MB）が返却されずリークする。Rent は
            // finally が守る try の外にあるため、例外が出るなら Rent の前に出さないといけない
            // （レビュー §4 低）。
            if (pixels == null) throw new System.ArgumentNullException(nameof(pixels));
            if (w <= 0 || h <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(w), $"invalid size: {w}x{h}");
            long lenLong = (long)w * h;
            if (lenLong > int.MaxValue)
                throw new System.ArgumentOutOfRangeException(nameof(w), $"size too large: {w}x{h}");
            if (pixels.Length < lenLong)
                throw new System.ArgumentException(
                    $"pixels.Length({pixels.Length}) < w*h({lenLong})", nameof(pixels));
            if (sortedZones == null) throw new System.ArgumentNullException(nameof(sortedZones));

            if (fullW <= 0) fullW = w;
            if (fullH <= 0) fullH = h;

            // マスクスナップショットからローカル変数に展開(packed ulong[]、1bit/画素)
            ulong[] commonMask = masks?.common;
            int maskW = masks?.width ?? 0;
            int maskH = masks?.height ?? 0;

            int len = (int)lenLong;
            Color32[] originalPixels = s_color32Pool.Rent(len);   // 末尾の finally で Return
            System.Array.Copy(pixels, originalPixels, len);

            long _t0 = Stopwatch.GetTimestamp();
            var _perfZones = new ZonePerfEntry[sortedZones.Count];
            int _perfIdx = 0;
            // フェーズ別累積(ticks)。計測のみで出力には一切影響しない。
            var _phaseTicks = new long[PhaseCount];
            long _tp = _t0;

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
            // 行並列(per-index デリゲートは 4K で 1670 万回の呼び出しになるため行単位に集約)。
            // 各画素は独立・書き込みは自 index のみなので出力は逐次版とビット不変。
            Parallel.For(0, h, po, y =>
            {
                int rowOff = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = rowOff + x;
                    Color.RGBToHSV((Color)originalPixels[i], out pixH[i], out pixS[i], out pixV[i]);
                }
            });
            _phaseTicks[PhHsv] += Stopwatch.GetTimestamp() - _tp;

            debug?.BeginCapture(w, h);

            // decontamination 用バッファはゾーン間で再利用する(ゾーンごとの new bool[len]+
            // new Color32[len] 確保=4K で 17MB+67MB/ゾーンを回避)。aaMask は全画素で読まれるため
            // 各ゾーン頭でクリアし、decontaminatedPixels は aaMask=true の位置だけ上書き・参照される。
            bool[] decontamAaMask = null;
            Color32[] decontamPixels = null;
            // 除外マスク画素の位置(BG ドナー隠蔽用)。マスクがあるゾーンで初回に確保し再利用。
            bool[] decontamMaskExcluded = null;
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
                _tp = _tZone;

                // ArrayPool 借用は per-zone の try/finally で必ず返却する。
                // Parallel.For は po.CancellationToken でキャンセル時に OperationCanceledException を投げる。
                // 後段ヘルパー(Decontaminate/CleanAchromaFringe/GaussianBlur/FillSmallHoles/RecoverBoundaryEdges
                // /RejectNeutral/Solidify/GrowHighlightBand)にも cancellationToken を渡しており、内部 Parallel.For も
                // 同様にキャンセル例外を伝播するため、例外経路でもプールが汚染されないよう finally でガードする。
                float[] strength = null;
                float[] highlightPot = null;
                float[] matchConf = null;
                try
                {
                    // フル画像経路(メインプレビュー/Apply/Export)か部分クロップ(詳細プレビュー)か。
                    // 大域統計(連結成分/再着色アンカー/wash/領域L)はフル画像でしか正しく解けないので、
                    // フル画像では解いてキャッシュへ書き、クロップではキャッシュを転写する。
                    bool isFullImagePath = originX == 0 && originY == 0 && fullW == w && fullH == h;

                    // 選択キャッシュ: ターゲット色など「再着色のみ」の変更では、マスク再適用直後の
                    // strength(=生の選択)を復元して選択フェーズ(Match/Highlight/FloodFill/穴埋め/
                    // 境界/ブラー/マスク再適用)を丸ごと省く。フル画像経路でのみ使う。
                    string selKey = null;
                    float[] cachedStrength = null;
                    ulong[] cachedKeep = null;
                    bool selCached = false;
                    if (selectionCache != null && isFullImagePath)
                    {
                        selKey = BuildSelectionKey(zone, edgeFeather, antiAliasCleanup, holeFillPasses,
                            holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp, commonMask, zoneMask,
                            maskW, maskH);
                        selCached = selectionCache.TryGet(zone.id, selKey, w, h, out cachedStrength, out cachedKeep);
                    }

                    // 1. 元のピクセルカラーを使用した強度マップを構築(キャッシュヒット時は復元のみ)
                    strength = s_floatPool.Rent(len);
                    // matchConf は連結成分アンカリング(flood fill)のコア判定専用。FF が有効でフル画像
                    // 経路かつキャッシュミス時のみ確保・充填する(ヒット時は keep を転写するため不要)。
                    bool needMatchConf = IrocaConsts.ExperimentalFeatures.EnableFloodFill
                        && zone.mode == SelectionMode.ColorPick && zone.useFloodFill
                        && isFullImagePath && !selCached;
                    // ミス時に FF が確定した keep を、選択キャッシュにも保存して次回のヒット復元に使う。
                    ulong[] keepBitsForCache = null;

                    if (selCached)
                    {
                        // ヒット: マスク再適用直後の strength をそのまま復元(選択フェーズは全て省略)。
                        Array.Copy(cachedStrength, strength, len);
                    }
                    else
                    {
                        Array.Clear(strength, 0, len);
                        if (zone.highlightRecovery)
                        {
                            highlightPot = s_floatPool.Rent(len);
                            Array.Clear(highlightPot, 0, len);
                        }
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
                        // 彩度天井ゲート(グレーモード以外では内部で no-op): 無彩素材の彩度包絡を
                        // 超える独立した高彩度の別素材(クリーム布等)を選択から除去する。素材自身の
                        // 高彩度装飾は低彩度コア近接で保護される。ハイライト伝播・FF・穴埋めより前に
                        // 適用し、後段パスが別素材を再伝播/復元しないようにする。
                        // どちらも「サンプル色が無彩か」を起点にしたグレーモード専用の絞り込みなので、
                        // 色サンプルを持つ ColorPick モードでのみ適用する。Rect モードは sampleColor が
                        // 既定の白のままで、mode を見ないと必ずグレーモード判定が真になり、有彩画素が
                        // 削除されて矩形選択が壊れる（レビュー 2026-08-06 §4 中）。現行 UI から Rect は
                        // 設定できないが、enum は public でシリアライズ対象＝旧プリセット JSON から到達し得る。
                        if (zone.mode == SelectionMode.ColorPick)
                        {
                            Color.RGBToHSV(zone.sampleColor, out _, out float cgSS, out float cgSV);
                            ApplyChromaCeilingGate(strength, matchConf, pixS, cgSS, cgSV,
                                zone.chromaThreshold, w, h, cancellationToken);
                            // 中性ツヤ復帰(グレーモード以外では内部で no-op): 彩度整合ゲートが純白
                            // パディングと一緒に落とした「素材自身の純白ツヤ」を、選択領域に囲まれた
                            // 閉領域という空間条件だけで戻す。連結性は大域演算なのでフル画像経路限定
                            // (部分クロップではクロップ境界に接した閉領域を開領域と誤判定するため)。
                            if (isFullImagePath)
                                RecoverEnclosedNeutral(strength, matchConf, pixS, originalPixels,
                                    zone.sampleColor, zone.tolerance, cgSS, cgSV,
                                    zone.chromaThreshold, w, h, cancellationToken);
                        }
                        debug?.RecordStage(zone.id, DebugStages.Match, strength, w, h);
                    }
                    _phaseTicks[PhMatch] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // 1.a 空間伝播によるハイライト領域の回収 (モルフォロジー拡張)
                    if (highlightPot != null)
                    {
                        PropagateHighlights(strength, highlightPot, w, h, cancellationToken);
                        s_floatPool.Return(highlightPot);
                        highlightPot = null;
                        debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, strength, w, h);
                    }

                    // 1.a.1 ハイライト帯成長: matched core から「sample→白 軸上の同色相の明部」へ
                    //       strength を空間連結で伸ばし、薄いハイライトのベタ塗り化・取りこぼしを防ぐ。
                    if (!selCached && zone.highlightBandExpand && zone.highlightRecovery)
                    {
                        GrowHighlightBand(strength, originalPixels, pixH, pixS, pixV, zone, w, h, cancellationToken);
                        debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, strength, w, h);
                    }

                    _phaseTicks[PhHighlight] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // 1.a.2 連結成分アンカリング: 確信度コアを含む連結成分のみに strength を絞り込む。
                    // 連結性は大域演算のため、フル画像経路(メインプレビュー/Apply/Export)でのみ実行する。
                    // 部分クロップ(詳細プレビュー)はここでは絞り込まず色のみ=最終の上位集合になる
                    // (M4 でフル画像の keep マスクをキャッシュ転写して完全一致させる予定)。
                    if (IrocaConsts.ExperimentalFeatures.EnableFloodFill
                        && zone.mode == SelectionMode.ColorPick
                        && zone.useFloodFill)
                    {
                        if (selCached)
                        {
                            // ヒット: フル画像で確定済みの keep を parityCache へ再公開する(詳細プレビュー
                            // 転写用)。FF 自体は省略済み(復元 strength に反映済み)。
                            if (parityCache != null && cachedKeep != null)
                            {
                                parityCache.SetFullSize(w, h);
                                parityCache.SetKeep(zone.id, cachedKeep);
                            }
                        }
                        else if (isFullImagePath)
                        {
                            int seedX = -1, seedY = -1;
                            if (zone.seedUV.x >= 0f)
                            {
                                seedX = Mathf.Clamp(Mathf.RoundToInt(zone.seedUV.x * (w - 1)), 0, w - 1);
                                seedY = Mathf.Clamp(Mathf.RoundToInt(zone.seedUV.y * (h - 1)), 0, h - 1);
                            }
                            ApplyConnectedComponentMask(strength, matchConf, originalPixels, w, h, seedX, seedY, cancellationToken);
                            // フル画像で解いた keep(=残った画素 strength>0)を作り、詳細プレビュー(クロップ)へ
                            // 転写(parityCache)・次回の選択キャッシュ復元(keepBitsForCache)の両方に使う。
                            if (parityCache != null || selectionCache != null)
                            {
                                var keepBits = new ulong[(len + 63) >> 6];
                                for (int i = 0; i < len; i++)
                                    if (strength[i] > 0f) keepBits[i >> 6] |= 1UL << (i & 63);
                                keepBitsForCache = keepBits;
                                if (parityCache != null)
                                {
                                    parityCache.SetFullSize(w, h);
                                    parityCache.SetKeep(zone.id, keepBits);
                                }
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

                    _phaseTicks[PhFloodFill] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

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
                        out ppMinX, out ppMinY, out ppMaxX, out ppMaxY, cancellationToken);
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
                    // relaxed ゲート(穴埋め/境界回復)にプライマリと同じ RGB 距離ブレンドを与えるための
                    // chromaConfidence と sample RGB。低彩度サンプル(白/灰)で同色相の高彩度色を弾き、
                    // 境界回復が無関係な色を周囲へスピルさせる(対象の縁に別色のハローが出る)のを防ぐ。
                    // 有彩は cc≈1 で従来式。
                    Color.RGBToHSV(zone.sampleColor, out float gsH, out float gsS, out float gsV);
                    float relaxedChromaConf = Mathf.Min(
                        Mathf.Clamp01((gsS - zone.chromaThreshold) / 0.10f),
                        Mathf.Clamp01((gsV - 0.05f) / 0.15f));
                    float rgSampR = zone.sampleColor.r, rgSampG = zone.sampleColor.g, rgSampB = zone.sampleColor.b;
                    if (!selCached && hasPostBox)
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
                                        rgSampR, rgSampG, rgSampB, relaxedChromaConf, zone.chromaThreshold) > 0f;
                                }
                            });
                            FillSmallHoles(strength, w, h, holeFillPasses, holeFillMinNeighbors, fillAllowed,
                                ppMinX, ppMinY, ppMaxX, ppMaxY, cancellationToken);
                        }
                        finally
                        {
                            s_boolPool.Return(fillAllowed);
                        }
                    }
                    debug?.RecordStage(zone.id, DebugStages.HoleFill, strength, w, h);
                    _phaseTicks[PhHoleFill] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // 1c. 境界復元：マッチしたピクセルに隣接するマッチしないピクセルを再評価
                    //     古い固定低彩度閾値を使用して、正しい段階的な強度を与える
                    if (!selCached && antiAliasCleanup > 0 && hasPostBox)
                    {
                        RecoverBoundaryEdges(strength, w, h, pixH, pixS, pixV,
                            zone.sampleColor, zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                            zone.satDistWeight, relaxedSatMin, relaxedSatRamp, zone.shadowForgivenessSatMin, antiAliasCleanup,
                            ppMinX, ppMinY, ppMaxX, ppMaxY,
                            originalPixels, relaxedChromaConf, zone.chromaThreshold, cancellationToken);
                        debug?.RecordStage(zone.id, DebugStages.BoundaryRecover, strength, w, h);
                    }

                    _phaseTicks[PhBoundary] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // 2. スムーズな端の遷移のためのガウシアンブラー（端に限定）
                    if (!selCached && edgeFeather > 0.01f && hasPostBox)
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
                                ppMinX, ppMinY, ppMaxX, ppMaxY, cancellationToken))
                            {
                                // strength の所有権を blurOut に移し、もとの strength は返却
                                s_floatPool.Return(strength);
                                strength = blurOut;
                                blurOut = null; // 二重返却防止
                                ConstrainBlur(strength, preBlur, w, h, Mathf.CeilToInt(edgeFeather * 2.5f), cancellationToken);
                            }
                        }
                        finally
                        {
                            if (blurOut != null) s_floatPool.Return(blurOut);
                            if (preBlur != null) s_floatPool.Return(preBlur);
                        }
                        debug?.RecordStage(zone.id, DebugStages.Blur, strength, w, h);
                    }

                    _phaseTicks[PhBlur] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // 3. 除外マスクを再適用：ブラーが除外ピクセルにはみ出す可能性がある
                    if (!selCached && (commonMask != null || zoneMask != null))
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

                    // 選択フェーズ(Match〜マスク再適用)完了。ここが「生の選択」の境界で、この後の
                    // RejectNeutral/SolidifyAchromaInterior/decontam は target 依存で strength を破壊的に
                    // 書き換える。ミス時のみ、この時点の strength(コピー)+ FF keep を選択キャッシュへ保存し、
                    // 次回「再着色のみ変更」した再生成で復元して選択フェーズを丸ごと省く(出力ビット不変)。
                    if (!selCached && selectionCache != null && isFullImagePath)
                        selectionCache.Store(zone.id, selKey, strength, keepBitsForCache, w, h);

                    // 3b. AA 境界の α 分解（オプション）：strength が 0 < s < interiorThreshold の
                    //     ピクセルを「α×FG + (1-α)×BG」と見て元テクスチャの合成を逆算し、
                    //     新色で再合成する。halo（薄汚れた中間色）を構造的に除去する。
                    // 無彩サンプル/極端無彩ターゲットの重み(無彩パスと AA フィデリティ修正で共用)。
                    float zAchromaWeight = ComputeAchromaWeight(zone.sampleColor, zone.targetColor);
                    // sample の S/V (wash ゲート・デバッグ分岐・下の中性リジェクトで共用)。
                    Color.RGBToHSV(zone.sampleColor, out _, out float zSS, out float zSV);

                    // 有彩サンプル→無彩ターゲット(有彩色→白/黒/灰)の過選択除去。有彩サンプルはマッチ距離が
                    // hue 支配になり彩度差を過小評価するため、明るい中性画素(白UV背景等)を巻き込む
                    // (特に暖色サンプルで顕著。無彩画素の hue は 0 に丸められ暖色と同色相に見えるため)。
                    // 巻き込みで領域が明るい背景に支配されると後段の成分中央値Lが上がり、本体(中L)が形維持
                    // リマップで黒へ落ちる(黒化)。マッチ全段(穴埋め/境界回復)の後・内部固め/成分統計の前に、
                    // サンプル彩度の相対床を下回る中性画素を strength から除去する(高彩度コア近傍は保護)。
                    // 発動判定は **明度非依存** の ComputeAchromaSelectWeight を使う(灰色=中明度の無彩でも
                    // 発動させる。ComputeAchromaWeight の extremeness では中明度グレーで重みが落ち白背景が
                    // 灰色化する)。有彩→有彩(weight≈0)・低彩度サンプル(sS<床)では作動しない=従来挙動を完全維持。
                    float zAchromaSelectWeight = ComputeAchromaSelectWeight(zone.sampleColor, zone.targetColor);
                    if (zAchromaSelectWeight > AchromaNeutralRejectWeightMin && zSS >= NeutralRejectActiveSourceSat)
                        RejectNeutralForAchromaTarget(strength, pixS, w, h, zSS, cancellationToken);

                    // 無彩パスの内部固め: 極端な無彩ターゲット(白↔黒)では、マッチ強度が色のばらつきで内部まで
                    // フルにならず、明るい画素ほど弱く塗られて元色が残り「中央の段差」になる。陰影は塗り
                    // 強度でなく recolor の achroma レンジリマップ(gain≤1)で表現すべきなので、マッチ領域の
                    // 内部を full strength に固め、AA 縁(侵食で除いた帯)の taper だけ残す。有彩ターゲット
                    // (achromaWeight≈0)では no-op = byte 不変。
                    if (zAchromaWeight > 1e-4f)
                        SolidifyAchromaInterior(strength, w, h, zAchromaWeight, cancellationToken);

                    bool[] aaMask = null;
                    Color32[] decontaminatedPixels = null;
                    if (useDecontamination)
                    {
                        aaMask = decontamAaMask;
                        decontaminatedPixels = decontamPixels;
                        // 内部固め(上)が無彩ターゲットの内部を均一化したので、旧 AA フィデリティ修正の
                        // interior_threshold=1.01(全画素 α 再合成)は不要(むしろ内部を背景色で再合成して
                        // 段差を復活させる)。常に通常閾値で AA 縁だけをデコンタミする。
                        float effInteriorThreshold = decontaminationInteriorThreshold;
                        // 除外マスク画素は strength=0 だが「背景」ではない(サンプル同色の保護パーツで
                        // あり得る)。BG ドナーに入れると推定色がサンプル色で汚染され、マスク境界の外側に
                        // 誤色の点ノイズを塗るため、位置を渡してドナーから隠す(マスク中立化)。
                        bool[] deconExcluded = null;
                        if (commonMask != null || zoneMask != null)
                        {
                            if (decontamMaskExcluded == null) decontamMaskExcluded = new bool[len];
                            deconExcluded = decontamMaskExcluded;
                            var excl = deconExcluded;
                            // デコンタミが除外フラグを読むのは BG ドナー範囲(後段 bbox ± radius)だけ
                            // なので、そこだけ埋める。範囲外は読まれない=出力ビット不変
                            // (バッファはゾーン間で使い回すが、各ゾーンが自分の読む範囲を必ず埋める)。
                            int exY0 = Mathf.Max(0, ppMinY - decontaminationRadius);
                            int exY1 = Mathf.Min(h - 1, ppMaxY + decontaminationRadius);
                            int exX0 = Mathf.Max(0, ppMinX - decontaminationRadius);
                            int exX1 = Mathf.Min(w - 1, ppMaxX + decontaminationRadius);
                            if (hasPostBox)
                                Parallel.For(exY0, exY1 + 1, po, y =>
                                {
                                    int yf = y + originY;
                                    int rowOff = y * w;
                                    for (int x = exX0; x <= exX1; x++)
                                        excl[rowOff + x] = IsExcludedCombined(x + originX, yf, fullW, fullH,
                                            commonMask, zoneMask, maskW, maskH);
                                });
                        }
                        // 後段 bbox(ppMin/Max)を渡してデコンタミを実マッチ範囲に限定する。α 分解が
                        // 触るのは 0<strength<threshold の画素だけ=定義上この bbox 内なので出力ビット不変。
                        DecontaminateAaBoundary(originalPixels, strength, w, h,
                            zone.sampleColor, zone.targetColor,
                            decontaminationRadius, effInteriorThreshold,
                            aaMask, decontaminatedPixels, hasPostBox, cancellationToken,
                            deconExcluded, ppMinX, ppMinY, ppMaxX, ppMaxY);
                        debug?.RecordDecontamination(zone.id, aaMask, w, h);
                    }

                    _phaseTicks[PhDecontam] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

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
                    // 単色ロゴなどでは AA 縁の混色が元と違う色相へ転写され、輪郭だけが別色に転ぶ色相ノイズを
                    // 生んだ。向きを target に揃えることで L(リング除去)を保ったまま色相を均一化する。outputSaturation は
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
                        TryComputeRecolorAnchor(originalPixels, strength, w,
                            ppMinX, ppMinY, ppMaxX, ppMaxY, out float anchorL, out float anchorC, cancellationToken))
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

                    // 無彩パスの領域 L レンジを事前計算(zAchromaWeight は上で算出済み)。
                    float zRegLlo = 0f, zRegLhi = 1f, zRegLmid = 0.5f;
                    bool zHasRegL = false;
                    // 形維持リマップの center 基準(中央値)を連結成分ごとに局所化する per-pixel マップ。
                    // ゆるいマスクで明るい背景を巻き込んでも、各成分が自分の地色基準で再着色されるので、
                    // 背景よりわずかに暗いだけの明るい対象がベタ黒へ潰れない。null のときは
                    // zRegLmid(全体中央値)へフォールバック。
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
                            zHasRegL = TryComputeRegionLRange(originalPixels, strength, w,
                                ppMinX, ppMinY, ppMaxX, ppMaxY,
                                out zRegLlo, out zRegLhi, out zRegLmid, cancellationToken);
                            if (zHasRegL)
                                zRegMidMap = BuildComponentMedianLMap(originalPixels, strength, w, h, 0.05f, cancellationToken);
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

                    _phaseTicks[PhRegionStats] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    var strengthForRecolor = strength;
                    var aaMaskLocal = aaMask;
                    var decontaminatedLocal = decontaminatedPixels;
                    var claimedLocal = claimed;
                    // bbox 制限(P2-7): recolor ループは先頭で s<=0.001 を skip し近傍読みをしないため、
                    // strength>0.001 の bbox だけを走査すれば出力はビット不変。マッチ領域が小さいゾーン
                    // (ロゴ等)で OkLab 再着色の per-pixel コストを実マッチ範囲に限定する。bbox 走査は
                    // 軽い比較 1 パスで、recolor の重い per-pixel コスト削減が上回る。
                    // 走査自体は共通の並列 bbox ヘルパへ寄せる(同条件の逐次コピーだった)。
                    TryComputeStrengthBBox(strengthForRecolor, w, h, 0.001f,
                        out int rcMinX, out int rcMinY, out int rcMaxX, out int rcMaxY, cancellationToken);
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

                    // 無彩(白↔黒)再着色のエッジに残る「地色の残り」フチ消し。マッチ境界の外側 2px に残る
                    // 背景より明るい混色画素を α 分解で背景へ寄せ、暗い再着色色に対する明るいフチを消す。
                    // 有彩(zAchromaWeight≈0)では呼ばれず完全 no-op。共有のマッチ/合成経路は変更しない。
                    if (zAchromaWeight > 1e-4f && rcMaxX >= 0)
                    {
                        // 除外マスク画素の位置をフチ消しへ渡す。渡さないとフチ消しは
                        //   (a) 除外画素を BG ドナーに数えて BG 推定をサンプル色で汚染し
                        //   (b) 除外画素そのものへ target 混色を書き込む(マスク契約違反)
                        // という DecontaminateAaBoundary では 05ecca8 で塞いだ穴を残す。
                        // 埋める範囲はフチ消しが読む bbox±AchromaFringeExclusionMargin だけ
                        // (デコンタミ側の充填は ppMin/Max±decontaminationRadius かつ
                        //  useDecontamination 時のみなので、ここは独立に埋める必要がある)。
                        bool[] fringeExcluded = null;
                        if (commonMask != null || zoneMask != null)
                        {
                            if (decontamMaskExcluded == null) decontamMaskExcluded = new bool[len];
                            fringeExcluded = decontamMaskExcluded;
                            var fex = fringeExcluded;
                            const int fm = AchromaFringeExclusionMargin;
                            int fy0 = Mathf.Max(0, rcMinY - fm), fy1 = Mathf.Min(h - 1, rcMaxY + fm);
                            int fx0 = Mathf.Max(0, rcMinX - fm), fx1 = Mathf.Min(w - 1, rcMaxX + fm);
                            Parallel.For(fy0, fy1 + 1, po, y =>
                            {
                                int yf = y + originY;
                                int rowOff = y * w;
                                for (int x = fx0; x <= fx1; x++)
                                    fex[rowOff + x] = IsExcludedCombined(x + originX, yf, fullW, fullH,
                                        commonMask, zoneMask, maskW, maskH);
                            });
                        }
                        CleanAchromaFringe(pixels, originalPixels, strengthForRecolor, claimedLocal,
                            w, h, zone.sampleColor, zone.targetColor, rcMinX, rcMinY, rcMaxX, rcMaxY,
                            cancellationToken, fringeExcluded);
                    }

                    _phaseTicks[PhRecolor] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

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
                s_color32Pool.Return(originalPixels);
            }
            var _perfPhases = new PhasePerfEntry[PhaseCount];
            for (int p = 0; p < PhaseCount; p++)
                _perfPhases[p] = new PhasePerfEntry(s_perfPhaseNames[p], TicksToMs(_phaseTicks[p]));
            DebugCaptureHooks.RaisePerfReport(
                new PerfReport(TicksToMs(Stopwatch.GetTimestamp() - _t0), w, h, _perfZones, _perfPhases));
        }

        // ───────────── 領域統計(RegionStats)の並列ヒストグラム集計 ─────────────
        // 集計対象が [from,to) の連続レンジ 1 本を処理するデリゲート。戻り値は集計した画素数。
        private delegate int HistChunk(int from, int to, int[] localHist);

        /// <summary>集計対象が行レンジ [yFrom,yTo) のデリゲート。戻り値は集計した画素数。</summary>
        internal delegate int HistRowChunk(int yFrom, int yTo, int[] localHist);

        /// <summary>
        /// <see cref="AccumulateHistParallel"/> の行レンジ版。全画素 [0,len) でなく bbox の行だけを
        /// 分割したいとき(領域統計を実マッチ範囲に限定するとき)に使う。ヒストグラムは整数加算
        /// だけで集計順に依存しないので、結果は単スレッド逐次版と完全に同値。
        /// </summary>
        internal static int AccumulateHistParallelRows(int yFrom, int yTo, int[] hist,
            HistRowChunk chunk, CancellationToken ct = default)
        {
            if (yTo <= yFrom) return 0;
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            int total = 0;
            object gate = new object();
            Parallel.ForEach(Partitioner.Create(yFrom, yTo), po,
                () => new int[hist.Length],
                (range, _, local) =>
                {
                    int c = chunk(range.Item1, range.Item2, local);
                    if (c != 0) Interlocked.Add(ref total, c);
                    return local;
                },
                local =>
                {
                    lock (gate)
                        for (int b = 0; b < local.Length; b++) hist[b] += local[b];
                });
            return total;
        }

        /// <summary>
        /// [0,len) をチャンク分割し、チャンクごとにスレッドローカルのヒストグラムへ集計してから
        /// マージする。ヒストグラムは整数カウントの加算だけで**集計順に依存しない**ので、結果は
        /// 単スレッド逐次版と完全に同値(=percentile もビット不変)。領域統計の各パスは全画素走査
        /// なのに単スレッドで、無彩寄りサンプルでは処理全体の最大コストになっていた。
        /// </summary>
        /// <returns>全チャンクの集計画素数の合計。</returns>
        private static int AccumulateHistParallel(int len, int[] hist, HistChunk chunk,
            CancellationToken ct = default)
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            int total = 0;
            object gate = new object();
            Parallel.ForEach(Partitioner.Create(0, len), po,
                () => new int[hist.Length],
                (range, _, local) =>
                {
                    int c = chunk(range.Item1, range.Item2, local);
                    if (c != 0) Interlocked.Add(ref total, c);
                    return local;
                },
                local =>
                {
                    lock (gate)
                        for (int b = 0; b < local.Length; b++) hist[b] += local[b];
                });
            return total;
        }

        /// <summary>256bin ヒストグラムの percentile(0..1) を実値で返す(値域 [0, scale])。
        /// HighlightSampleCorrector.PercentileFromHist の値域一般化版。線形補間の percentile に対し
        /// 最大 1bin(scale/255)の離散化差を許容する(auto_wash_sample の前例に従う)。</summary>
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
        /// プレビュー表示用にダウンサンプルした Color32 配列を生成する。
        /// 各 dst 画素は対応する src ブロック内のピクセル平均色になる（box フィルタ）。
        /// </summary>
        public static Color32[] BoxDownsample(Color32[] src, int srcW, int srcH,
            int dstW, int dstH, float scale)
        {
            // scale と dst 寸法の整合を呼び出し側の契約に丸投げしており、不整合だと
            // src の範囲外を読んで IndexOutOfRange になっていた（レビュー §4 低）。
            // 読み出し位置を src 範囲にクランプして、契約違反を例外でなく劣化で吸収する。
            if (src == null) throw new System.ArgumentNullException(nameof(src));
            if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0 || scale <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(scale),
                    $"invalid downsample params: src={srcW}x{srcH} dst={dstW}x{dstH} scale={scale}");
            if (src.Length < (long)srcW * srcH)
                throw new System.ArgumentException(
                    $"src.Length({src.Length}) < srcW*srcH({(long)srcW * srcH})", nameof(src));

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
                int sy0 = Mathf.Clamp(Mathf.FloorToInt(y / scale), 0, srcH - 1);
                int sy1 = Mathf.Clamp(Mathf.CeilToInt((y + 1f) / scale) - 1, sy0, srcH - 1);
                for (int x = 0; x < dstW; x++)
                {
                    int sx0 = Mathf.Clamp(Mathf.FloorToInt(x / scale), 0, srcW - 1);
                    int sx1 = Mathf.Clamp(Mathf.CeilToInt((x + 1f) / scale) - 1, sx0, srcW - 1);
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
