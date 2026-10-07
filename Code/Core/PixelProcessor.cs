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

        // ZoneAutoTuner など Core の他クラスからも同じ規則で並列度を決められるよう internal。
        // 各所で Environment.ProcessorCount - 2 を直書きすると、デバッグ時のスレッド数指定が
        // 一部の処理にだけ効かず、計測が食い違う。
        internal static int GetMaxParallelism()
        {
            int ov = DebugCaptureHooks.ParallelismOverride;
            return ov > 0 ? Math.Min(ov, Environment.ProcessorCount) : s_defaultParallelism;
        }

        // 不透明とみなす α の下限。統計・連結成分・閉領域・自動調整のサンプリングの対象画素はこれ以上。
        // HighlightSampleCorrector と ZoneAutoTuner も同じ規約で数えるので internal。
        internal const int OpaqueAlphaMin = 128;

        private static double TicksToMs(long ticks) =>
            ticks * 1000.0 / Stopwatch.Frequency;

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
        // フェーズの内訳(サブ段)。ゾーンループ内の主要な呼び出しごとに区切る(区切り間の時間を
        // 次の区切りに計上する。SubPhaseClock 参照)。キャッシュ命中で省いた段は 0 のまま。計測専用。
        private const int SpAlloc = 0, SpMatchLoop = 1, SpChromaCeiling = 2, SpEnclosedNeutral = 3,
            SpHlPropagate = 4, SpHlBand = 5, SpHlEnclosed = 6, SpFfComponents = 7, SpFfKeepBits = 8,
            SpPostBBox = 9, SpHoleFillGate = 10, SpHoleFill = 11, SpBoundary = 12, SpBlurReapply = 13,
            SpSelCacheStore = 14, SpRejectNeutral = 15, SpSolidify = 16, SpDecontamExcl = 17,
            SpDecontam = 18, SpWashSample = 19, SpAnchor = 20, SpRegionLRange = 21, SpMedianLMap = 22,
            SpRecolorBBox = 23, SpRecolorLoop = 24, SpAchromaFringe = 25, SpPaletteHash = 26,
            SpPaletteIds = 27, SpPaletteRemap = 28, SpMixBand = 29, SubPhaseCount = 30;
        private static readonly string[] s_perfSubPhaseNames =
        {
            "Alloc", "MatchLoop", "ChromaCeiling", "EnclosedNeutral",
            "HlPropagate", "HlBand", "HlEnclosed", "FfComponents", "FfKeepBits",
            "PostBBox", "HoleFillGate", "HoleFill", "Boundary", "BlurReapply",
            "SelCacheStore", "RejectNeutral", "Solidify", "DecontamExcl",
            "Decontam", "WashSample", "Anchor", "RegionLRange", "MedianLMap",
            "RecolorBBox", "RecolorLoop", "AchromaFringe", "PaletteHash", "PaletteIds", "PaletteRemap",
            "MixBand",
        };

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
            ulong[] zoneInclude, int maskW, int maskH, bool mixtureBand)
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
            F(z.uvRect.x); F(z.uvRect.y); F(z.uvRect.width); F(z.uvRect.height);
            B(z.useFloodFill); F(z.seedUV.x); F(z.seedUV.y); F(z.sampleUV.x); F(z.sampleUV.y);
            F(z.edgeSoftness); F(z.saturationStrictness); F(z.valueWeight); F(z.satDistWeight);
            F(z.satRampScale); F(z.shadowForgivenessSatMin); F(z.shadowValueFloor); F(z.partSatCeiling); F(z.partHueBand); F(z.chromaCeiling); F(z.chromaThreshold); F(z.saturationGuard);
            B(z.highlightRecovery); B(z.highlightBandExpand);
            F(edgeFeather); I(aaCleanup); I(holeFillPasses); I(holeFillMinNeighbors);
            F(relaxedSatMin); F(relaxedSatRamp);
            // 混色帯モード(IsMixtureBandZone)では穴埋め・境界回復を行わない = 選択そのものが変わる。
            // 境界クリーンアップの ON/OFF で切り替わるので、キーに入れる。
            B(mixtureBand);
            // マスク内容(common + zone)。ビット列のハッシュだけでは、同じビット列を別の寸法で
            // 解釈したケースを区別できない（マスクは packed ulong[] で寸法が別持ちのため、
            // 寸法が変われば同じビット列でも指す画素が変わる）。寸法もキーに入れる
            // （レビュー §4 低 / 監査 L-6）。
            I(maskW); I(maskH);
            sb.Append(MaskHash(commonMask)); sb.Append(';');
            sb.Append(MaskHash(zoneMask)); sb.Append(';');
            sb.Append(MaskHash(zoneInclude)); sb.Append(';');
            return sb.ToString();
        }

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
        private static readonly ArrayPool<byte> s_bytePool =
            ArrayPool<byte>.Create(PoolMaxArrayLength, maxArraysPerBucket: 4);
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
        // 全体設定は RecolorSettings で受け取る（呼び出し元ごとに位置引数を並べ直させない）。
        // cancellationToken: バックグラウンドのプレビュー/エクスポートが渡す
        public static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones,
            in RecolorSettings settings,
            CancellationToken cancellationToken = default,
            int originX = 0, int originY = 0, int fullW = 0, int fullH = 0,
            IDebugCapture debug = null,
            PreviewParityCache parityCache = null,
            SelectionCache selectionCache = null)
        {
            ProcessPixelsArrayCore(pixels, w, h, masks, sortedZones, in settings,
                originX, originY, fullW, fullH, cancellationToken,
                debug, parityCache, selectionCache);
        }

        // デコンタミの「内部」判定しきい値。ユーザー設定ではない固定値。
        private const float DecontaminationInteriorThreshold = 0.97f;

        // 以下は「複数の箇所で同じ値でなければならない」しきい値。片側だけ変えると bbox 限定のビット不変や
        // 排他条件が崩れるので、名前で束ねておく。
        // ガウシアンブラーの半径 = ceil(sigma × これ)。GaussianBlur 本体・後段 bbox の余白・ConstrainBlur の
        // 半径が同じでないと、bbox の外へ広がったブラーを取りこぼす。
        private const float GaussianRadiusPerSigma = 2.5f;
        // edgeFeather がこれを超えたら境界ぼかしを走らせる。後段 bbox の余白・ぼかしの実行条件・
        // 混色帯モードとの排他(IsMixtureBandZone)が同じ境目でないと、余白にぼかし半径が入らない設定や
        // ぼかしと混色帯が同時に走る設定が生まれる。
        private const float EdgeFeatherBlurMin = 0.01f;
        // 再着色を適用する最小の強度。これ以下の画素は再着色ループが読み飛ばすので、再着色 bbox の
        // しきい値とデバッグの分岐の写しも同じ値にする(bbox の外を飛ばしてもビット不変になる前提)。
        private const float RecolorMinStrength = 0.001f;
        // 無彩パス(achromaWeight)を有効とみなす下限。有彩 × 有彩(weight≈0)では完全 no-op にする境目で、
        // 内部固め・領域 L の算出・無彩フチ消し・RecolorPixel 内の使用条件が同じ境目でそろう。
        private const float AchromaWeightActiveMin = 1e-4f;

        private static void ProcessPixelsArrayCore(
            Color32[] pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones,
            in RecolorSettings settings,
            int originX, int originY, int fullW, int fullH,
            CancellationToken cancellationToken,
            IDebugCapture debug,
            PreviewParityCache parityCache,
            SelectionCache selectionCache)
        {
            // 全体設定を同じ名前のローカルへ展開する。in 引数はラムダから参照できないので、
            // 本体(ラムダを含む)はこのローカルだけを読み、settings は以降参照しない。
            float edgeFeather = settings.edgeFeather;
            int antiAliasCleanup = settings.antiAliasCleanup;
            int holeFillPasses = settings.holeFillPasses;
            int holeFillMinNeighbors = settings.holeFillMinNeighbors;
            float relaxedSatMin = settings.relaxedSatMin;
            float relaxedSatRamp = settings.relaxedSatRamp;
            bool useDecontamination = settings.useDecontamination;
            int decontaminationRadius = settings.decontaminationRadius;

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
            // マスク(除外・含める)の判定表(作業座標の列・行 → マスクの画素)。拡縮の割り算を画素ごとでなく
            // 列と行ごとに 1 回だけにする。マスクが無ければ null(=除外なし・含めるなし)。
            int[] maskColOf = null, maskRowBase = null;
            if (masks != null && maskW > 0 && maskH > 0)
                BuildMaskIndexMap(w, h, originX, originY, fullW, fullH, maskW, maskH,
                    out maskColOf, out maskRowBase);

            int len = (int)lenLong;
            Color32[] originalPixels = s_color32Pool.Rent(len);   // 末尾の finally で Return
            System.Array.Copy(pixels, originalPixels, len);

            long _t0 = Stopwatch.GetTimestamp();
            var _perfZones = new ZonePerfEntry[sortedZones.Count];
            int _perfIdx = 0;
            var _phaseTicks = new long[PhaseCount];
            var _sub = new SubPhaseClock(SubPhaseCount);
            long _tp = _t0;

            // 全ピクセルの HSV を zone ループに入る前に一括計算（zone 数に関わらず1回）
            // null 初期化してから try 内で Rent することで、
            // 2番目以降の Rent が例外を投げた場合に先行の配列をリークしない。
            float[] pixH = null, pixS = null, pixV = null;
            // 占有率バッファ: 優先度の高い(リスト上位の)ゾーンが書き込んだカバレッジを
            // ピクセル単位で累積する。下位ゾーンは残り(1-claimed)の範囲だけ適用され、
            // 「重なった部分は上位ゾーンのみ適用」というレイヤー排他を実現する。
            // 単一ゾーン/非重複ピクセルでは常に 0 のままで、従来挙動は不変。
            float[] claimed = null;
            // 混色帯の被覆率(AnalyzeMixtureBand の出力)。初めて要るゾーンで借り、末尾の finally で返す。
            float[] mixAlpha = null;
            // 色の種類の表(PixelProcessor.Palette.cs)。色だけで決まる計算を色ごとに 1 回で済ませる。
            // 種類が多い画像では null(画素ごとに計算する)。どちらでも出力はビット単位で同じ。
            ColorPalette palette = null;
            // decontamination 用バッファ(下で借りる。末尾の finally で返す)。
            bool[] decontamAaMask = null;
            Color32[] decontamPixels = null;
            // 除外マスク画素の位置(BG ドナー隠蔽用)。マスクがあるゾーンで初回に借り、ゾーン間で再利用。
            bool[] decontamMaskExcluded = null;
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
            _sub.Restart();
            palette = GetOrBuildPalette(originalPixels, w, h, po, _sub);
            // 行並列(per-index デリゲートは 4K で 1670 万回の呼び出しになるため行単位に集約)。
            // 各画素は独立・書き込みは自 index のみなので出力は逐次版とビット不変。
            if (palette != null)
            {
                // 色ごとに求めた HSV を配る(同じ色に同じ RGBToHSV を当てた値なのでビット単位で同じ)。
                int[] pIdx = palette.Index;
                float[] pH = palette.H, pS = palette.S, pV = palette.V;
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        int k = pIdx[i];
                        pixH[i] = pH[k]; pixS[i] = pS[k]; pixV[i] = pV[k];
                    }
                });
            }
            else
            {
                Parallel.For(0, h, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = rowOff + x;
                        Color.RGBToHSV((Color)originalPixels[i], out pixH[i], out pixS[i], out pixV[i]);
                    }
                });
            }
            _phaseTicks[PhHsv] += Stopwatch.GetTimestamp() - _tp;

            debug?.BeginCapture(w, h);

            // decontamination 用バッファはゾーン間で再利用し、プールから借りる(旧: 処理ごとに new bool[len]+
            // new Color32[len]=4K で 17MB+67MB。Mono では大きな new が GC の停止を招く)。借りた配列は
            // ゼロ初期化されないが、どれも書いた位置しか読まない: aaMask は各ゾーン頭(DecontaminateAaBoundary)
            // で全域クリア、decontaminatedPixels は aaMask=true / 混色帯の対象の位置だけ書いて読む、
            // 除外フラグは各ゾーンが自分の読む範囲を埋める(ゾーン間で使い回すので、もともと前のゾーンの
            // 値が残っていても壊れない作り)。
            if (useDecontamination)
            {
                decontamAaMask = s_boolPool.Rent(len);
                decontamPixels = s_color32Pool.Rent(len);
            }

            foreach (var zone in sortedZones)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ulong[] zoneMask = null;
                if (masks != null && masks.zones != null && !string.IsNullOrEmpty(zone.id))
                    masks.zones.TryGetValue(zone.id, out zoneMask);
                // 含めるマスク(ゾーン別のみ)。除外と重なった画素は除外が勝つ。
                ulong[] zoneInclude = null;
                if (masks != null && masks.zoneIncludes != null && !string.IsNullOrEmpty(zone.id))
                    masks.zoneIncludes.TryGetValue(zone.id, out zoneInclude);

                // Parallel.For に入る前にキャッシュを確定させてホットループ内の条件分岐を排除
                zone.UpdateCacheIfNeeded();
                // 混色帯モード(境界クリーンアップ ON かつ有彩サンプル): 選択は主マッチの結果だけにして(緩和マッチの
                // 穴埋め・境界回復は行わない)、境界の混色は再着色の直前に被覆率で塗る(PixelProcessor.Decontam.cs の
                // AnalyzeMixtureBand)。緩和マッチは選択を縁から一定の幅だけ広げるので、素材でない地まで拾うと
                // パーツの周りに輪が出る。
                bool zMixMode = IsMixtureBandZone(zone, useDecontamination, edgeFeather);
                long _tZone = Stopwatch.GetTimestamp();
                _tp = _tZone;
                _sub.Restart();

                // ArrayPool 借用は per-zone の try/finally で必ず返却する。
                // Parallel.For は po.CancellationToken でキャンセル時に OperationCanceledException を投げる。
                // 後段ヘルパー(Decontaminate/CleanAchromaFringe/GaussianBlur/FillSmallHoles/RecoverBoundaryEdges
                // /RejectNeutral/Solidify/GrowHighlightBand)にも cancellationToken を渡しており、内部 Parallel.For も
                // 同様にキャンセル例外を伝播するため、例外経路でもプールが汚染されないよう finally でガードする。
                float[] strength = null;
                float[] highlightPot = null;
                float[] matchConf = null;
                bool[] includedPx = null;
                float[] regMidMapRented = null;   // 成分中央値の地図をプールから借りたとき(ゾーンの終わりに返す)
                try
                {
                    // サンプル色の HSV(ゾーン内で不変。彩度天井ゲート・別パーツの色相幅・緩和マッチ・
                    // 中性リジェクト・再着色パラメータが同じ値を使う)。
                    Color.RGBToHSV(zone.sampleColor, out float sampH, out float sampS, out float sampV);

                    // フル画像経路(メインプレビュー/Apply/Export)か部分クロップ(詳細プレビュー)か。
                    // 大域統計(連結成分/再着色アンカー/wash/領域L)はフル画像でしか正しく解けないので、
                    // フル画像では解いてキャッシュへ書き、クロップではキャッシュを転写する。
                    // 部分クロップ経路は現在は検証用(製品 UI の詳細プレビューはフル段出力の切り出し)。
                    bool isFullImagePath = originX == 0 && originY == 0 && fullW == w && fullH == h;

                    // 選択キャッシュ: ターゲット色など「再着色のみ」の変更では、マスク再適用直後の
                    // strength(=生の選択)を復元して選択フェーズ(Match/Highlight/FloodFill/穴埋め/
                    // 境界/ブラー/マスク再適用)を丸ごと省く。フル画像経路でのみ使う。
                    string selKey = null;
                    float[] cachedStrength = null;
                    ulong[] cachedKeep = null;
                    ulong[] cachedForced = null;
                    bool selCached = false;
                    if (selectionCache != null && isFullImagePath)
                    {
                        selKey = BuildSelectionKey(zone, edgeFeather, antiAliasCleanup, holeFillPasses,
                            holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp, commonMask, zoneMask,
                            zoneInclude, maskW, maskH, zMixMode);
                        selCached = selectionCache.TryGet(zone.id, selKey, w, h,
                            out cachedStrength, out cachedKeep, out cachedForced);
                    }

                    // 含めるマスクをテクスチャ解像度の bool[] へ展開する(除外優先を焼き込む)。
                    // 用途は 3 つ: (a) 選択への強制適用(マスク再適用の直後・キャッシュ保存の前)
                    // (b) 大域統計(再着色アンカー/領域Lレンジ)からの除外 — 手動追加した画素が
                    //     地色統計を汚し、ゾーン全体の再着色が遠隔で変わるのを防ぐ
                    // (c) 中性リジェクト等 target 依存ヒューリスティック後の再主張
                    // キャッシュヒット時も (b)(c) で必要になるため selCached と無関係に作る。
                    // 含めるマスクが無ければ null のまま = 以降の全分岐が no-op(出力ビット不変)。
                    if (zoneInclude != null)
                    {
                        includedPx = s_boolPool.Rent(len);
                        var incLocal = includedPx;
                        Parallel.For(0, h, po, y =>
                        {
                            int rowOff = y * w;
                            for (int x = 0; x < w; x++)
                            {
                                incLocal[rowOff + x] =
                                    IsIncludedAt(maskColOf, maskRowBase, x, y, zoneInclude)
                                    && !IsExcludedAt(maskColOf, maskRowBase, x, y, commonMask, zoneMask);
                            }
                        });
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
                        Array.Copy(cachedStrength, strength, len);
                    }
                    else
                    {
                        // strength / highlightPot / matchConf は下のマッチのループが全画素に書く(除外マスクの
                        // 画素には 0)ので、先にゼロで埋めない(旧: 3 本とも単一スレッドの Array.Clear)。
                        if (zone.highlightRecovery) highlightPot = s_floatPool.Rent(len);
                        if (needMatchConf) matchConf = s_floatPool.Rent(len);

                        var strengthLocal = strength;
                        var highlightPotLocal = highlightPot;
                        var matchConfLocal = matchConf;
                        _sub.Mark(SpAlloc);
                        // ColorPick のマッチは画素の色(と色から求めた HSV)だけで決まる(位置を見るのは
                        // Rect モードだけ)ので、色の表があれば色ごとに 1 回だけ求めて配る。
                        float[] palStrength = null, palPot = null, palConf = null;
                        if (palette != null && zone.mode == SelectionMode.ColorPick)
                        {
                            int pc = palette.Count;
                            palStrength = new float[pc];
                            palPot = new float[pc];
                            palConf = new float[pc];
                            Color32[] pCol = palette.Colors;
                            float[] pH = palette.H, pS = palette.S, pV = palette.V;
                            float[] outS = palStrength, outPot = palPot, outConf = palConf;
                            ForEachPaletteChunk(pc, po, (k0, k1) =>
                            {
                                for (int k = k0; k < k1; k++)
                                    zone.GetMatchScoresPrecomputedHSV(pH[k], pS[k], pV[k], (Color)pCol[k],
                                        0, 0, fullW, fullH, out outS[k], out outPot[k], out outConf[k]);
                            });
                        }
                        int[] palIdxMatch = palette?.Index;
                        Parallel.For(0, h, po, y =>
                        {
                            int yf = y + originY;
                            int rowOff = y * w;
                            for (int x = 0; x < w; x++)
                            {
                                int xf = x + originX;
                                int i = rowOff + x;
                                if (IsExcludedAt(maskColOf, maskRowBase, x, y, commonMask, zoneMask))
                                {
                                    strengthLocal[i] = 0f;
                                    if (highlightPotLocal != null) highlightPotLocal[i] = 0f;
                                    if (matchConfLocal != null) matchConfLocal[i] = 0f;
                                    continue;
                                }

                                float s, hPot, mc;
                                if (palStrength != null)
                                {
                                    int k = palIdxMatch[i];
                                    s = palStrength[k]; hPot = palPot[k]; mc = palConf[k];
                                }
                                else
                                {
                                    zone.GetMatchScoresPrecomputedHSV(pixH[i], pixS[i], pixV[i], (Color)originalPixels[i], xf, yf, fullW, fullH, out s, out hPot, out mc);
                                }
                                strengthLocal[i] = s;
                                if (highlightPotLocal != null) highlightPotLocal[i] = hPot;
                                if (matchConfLocal != null) matchConfLocal[i] = mc;
                            }
                        });
                        _sub.Mark(SpMatchLoop);
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
                            ApplyChromaCeilingGate(strength, matchConf, pixS, sampS, sampV,
                                zone.chromaThreshold, zone.chromaCeiling, w, h, cancellationToken);
                                _sub.Mark(SpChromaCeiling);
                            // 中性ツヤ復帰(グレーモード以外では内部で no-op): 彩度整合ゲートが純白
                            // パディングと一緒に落とした「素材自身の純白ツヤ」を、選択領域に囲まれた
                            // 閉領域という空間条件だけで戻す。連結性は大域演算なのでフル画像経路限定
                            // (部分クロップではクロップ境界に接した閉領域を開領域と誤判定するため)。
                            if (isFullImagePath)
                                RecoverEnclosedNeutral(strength, matchConf, pixS, originalPixels,
                                    zone.sampleColor, zone.tolerance, sampS, sampV,
                                    zone.chromaThreshold, w, h, cancellationToken);
                        }
                        _sub.Mark(SpEnclosedNeutral);
                        debug?.RecordStage(zone.id, DebugStages.Match, strength, w, h);
                    }
                    _phaseTicks[PhMatch] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // 1.a 空間伝播によるハイライト領域の回収 (モルフォロジー拡張)
                    if (highlightPot != null)
                    {
                        PropagateHighlights(strength, highlightPot, w, h, cancellationToken);
                        _sub.Mark(SpHlPropagate);
                        s_floatPool.Return(highlightPot);
                        highlightPot = null;
                        debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, strength, w, h);
                    }

                    // 1.a.1 ハイライト帯成長: matched core から「sample→白 軸上の同色相の明部」へ
                    //       strength を空間連結で伸ばし、薄いハイライトのベタ塗り化・取りこぼしを防ぐ。
                    if (!selCached && zone.highlightBandExpand && zone.highlightRecovery)
                    {
                        GrowHighlightBand(strength, originalPixels, pixH, pixS, pixV, zone, w, h, cancellationToken,
                            palette);
                        _sub.Mark(SpHlBand);
                        debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, strength, w, h);
                    }

                    // 1.a.3 閉領域ハイライト復帰: 選択に囲まれ、グロー(本体より明るい周囲)に縁取られた
                    //       「色相の回った有彩の芯」を空間条件で戻す(PixelProcessor.Highlight.cs の
                    //       RecoverEnclosedHighlight 参照)。連結性は大域演算なのでフル画像で解き、復帰画素の
                    //       ビット集合を parityCache(詳細プレビューのクロップ転写)と選択キャッシュ(ヒット時の
                    //       再公開)に持たせる。クロップは色だけでは芯を選べない(それがこの復帰の存在理由)ので、
                    //       転写が無いとズーム位置で芯の色が変わる。ハイライト補助(highlightRecovery)の一部。
                    ulong[] forcedBits = null;
                    if (zone.mode == SelectionMode.ColorPick && zone.highlightRecovery)
                    {
                        if (selCached)
                        {
                            forcedBits = cachedForced;
                        }
                        else if (isFullImagePath)
                        {
                            var bits = (parityCache != null || selectionCache != null)
                                ? new ulong[(len + 63) >> 6] : null;
                            int nForced = RecoverEnclosedHighlight(strength, originalPixels,
                                pixH, pixS, pixV, zone, w, h, bits, cancellationToken);
                            _sub.Mark(SpHlEnclosed);
                            if (nForced > 0)
                            {
                                forcedBits = bits;
                                debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, strength, w, h);
                            }
                        }
                        else if (parityCache != null
                                 && parityCache.fullW == fullW && parityCache.fullH == fullH)
                        {
                            var fb = parityCache.GetForced(zone.id);
                            if (fb != null)
                                ApplyCachedForcedMask(strength, w, h, originX, originY, fullW, fb);
                        }
                        if (isFullImagePath && parityCache != null)
                        {
                            parityCache.SetFullSize(w, h);
                            parityCache.SetForced(zone.id, forcedBits);   // null=該当なし(旧エントリを消す)
                        }
                    }

                    _phaseTicks[PhHighlight] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // 1.a.2 連結成分アンカリング: 確信度コアを含む連結成分のみに strength を絞り込む。
                    // 連結性は大域演算のため、フル画像経路(メインプレビュー/Apply/Export)でのみ実行する。
                    // 部分クロップは、parityCache にフル画像で解いた keep があれば転写して一致させ(下の
                    // else if)、無ければ絞り込まず色のみ=最終の上位集合(安全側)になる。
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
                            // 別パーツの色相幅は有彩サンプルでだけ意味を持つ(グレーモードの色相は不定)。
                            float ffHueBand =
                                sampS > ColorZone.GrayModeEffectiveChromaThreshold(sampV, zone.chromaThreshold)
                                    ? zone.partHueBand : 0f;
                            // スポイト位置を含む成分は、色の包絡ゲートで落とさない(クリックした場所を残す)。
                            int anchorX = -1, anchorY = -1;
                            if (zone.HasSampleUV)
                                PreviewCoords.UvToPixel(zone.sampleUV.x, zone.sampleUV.y, w, h,
                                    out anchorX, out anchorY);
                            ApplyConnectedComponentMask(strength, matchConf, originalPixels, w, h, seedX, seedY,
                                pixV, zone.shadowValueFloor, pixS, zone.partSatCeiling,
                                pixH, ffHueBand, sampH, sampS * ColorZone.HueReliableSatFrac,
                                anchorX, anchorY, cancellationToken);
                                _sub.Mark(SpFfComponents);
                            // フル画像で解いた keep(=残った画素 strength>0)を作り、詳細プレビュー(クロップ)へ
                            // 転写(parityCache)・次回の選択キャッシュ復元(keepBitsForCache)の両方に使う。
                            if (parityCache != null || selectionCache != null)
                            {
                                var keepBits = PackPositiveBits(strength, len, po);
                                _sub.Mark(SpFfKeepBits);
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
                    int ppBlurRadius = edgeFeather > EdgeFeatherBlurMin ? Mathf.CeilToInt(edgeFeather * GaussianRadiusPerSigma) : 0;
                    int ppMargin = holeFillPasses + Mathf.Max(0, antiAliasCleanup) + ppBlurRadius + 2;
                    int ppMinX, ppMinY, ppMaxX, ppMaxY;
                    bool hasPostBox = TryComputeStrengthBBox(strength, w, h, 0f,
                        out ppMinX, out ppMinY, out ppMaxX, out ppMaxY, cancellationToken);
                        _sub.Mark(SpPostBBox);
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
                    float relaxedChromaConf = Mathf.Min(
                        Mathf.Clamp01((sampS - zone.chromaThreshold) / 0.10f),
                        Mathf.Clamp01((sampV - 0.05f) / 0.15f));
                    float rgSampR = zone.sampleColor.r, rgSampG = zone.sampleColor.g, rgSampB = zone.sampleColor.b;
                    // 緩和マッチ(穴埋めの許可判定と境界回復が同じ引数で呼ぶ)は色だけで決まるので、
                    // 色の表があれば色ごとに 1 回だけ求めて両方で使う(ビット単位で同じ)。
                    float[] palRelaxed = null;
                    if (palette != null && !selCached && hasPostBox
                        && (long)palette.Count * 2 <= (long)(ppMaxX - ppMinX + 1) * (ppMaxY - ppMinY + 1))
                    {
                        palRelaxed = new float[palette.Count];
                        Color32[] rCol = palette.Colors;
                        float[] rH = palette.H, rS = palette.S, rV = palette.V;
                        float[] outRel = palRelaxed;
                        ForEachPaletteChunk(palette.Count, po, (k0, k1) =>
                        {
                            for (int k = k0; k < k1; k++)
                            {
                                Color32 hop = rCol[k];
                                outRel[k] = GetRelaxedMatchStrength(
                                    rH[k], rS[k], rV[k], sampH, sampS, sampV,
                                    zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                                    zone.satDistWeight, relaxedSatMin,
                                    hop.r / 255f, hop.g / 255f, hop.b / 255f,
                                    rgSampR, rgSampG, rgSampB, relaxedChromaConf, zone.chromaThreshold,
                                    zone.chromaCeiling);
                            }
                        });
                    }
                    int[] palIdxRelaxed = palette?.Index;
                    if (!selCached && hasPostBox && !zMixMode)
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
                                if (palRelaxed != null)
                                {
                                    for (int x = ppMinX; x <= ppMaxX; x++)
                                    {
                                        int i = rowOff + x;
                                        fillAllowedLocal[i] = palRelaxed[palIdxRelaxed[i]] > 0f;
                                    }
                                    return;
                                }
                                for (int x = ppMinX; x <= ppMaxX; x++)
                                {
                                    int i = rowOff + x;
                                    Color32 hop = originalPixels[i];
                                    fillAllowedLocal[i] = GetRelaxedMatchStrength(
                                        pixH[i], pixS[i], pixV[i], sampH, sampS, sampV,
                                        zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                                        zone.satDistWeight, relaxedSatMin,
                                        hop.r / 255f, hop.g / 255f, hop.b / 255f,
                                        rgSampR, rgSampG, rgSampB, relaxedChromaConf, zone.chromaThreshold,
                                        zone.chromaCeiling) > 0f;
                                }
                            });
                            _sub.Mark(SpHoleFillGate);
                            FillSmallHoles(strength, w, h, holeFillPasses, holeFillMinNeighbors, fillAllowed,
                                ppMinX, ppMinY, ppMaxX, ppMaxY, cancellationToken);
                        }
                        finally
                        {
                            s_boolPool.Return(fillAllowed);
                        }
                    }
                    _sub.Mark(SpHoleFill);
                    debug?.RecordStage(zone.id, DebugStages.HoleFill, strength, w, h);
                    _phaseTicks[PhHoleFill] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // 1c. 境界復元：マッチしたピクセルに隣接するマッチしないピクセルを再評価
                    //     緩和マッチ(relaxedSatMin)で、正しい段階的な強度を与える
                    if (!selCached && antiAliasCleanup > 0 && hasPostBox && !zMixMode)
                    {
                        RecoverBoundaryEdges(strength, w, h, pixH, pixS, pixV,
                            zone.sampleColor, zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                            zone.satDistWeight, relaxedSatMin, antiAliasCleanup,
                            ppMinX, ppMinY, ppMaxX, ppMaxY,
                            originalPixels, relaxedChromaConf, zone.chromaThreshold, zone.chromaCeiling,
                            cancellationToken, palRelaxed, palIdxRelaxed);
                        _sub.Mark(SpBoundary);
                        debug?.RecordStage(zone.id, DebugStages.BoundaryRecover, strength, w, h);
                    }

                    _phaseTicks[PhBoundary] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    if (!selCached && edgeFeather > EdgeFeatherBlurMin && hasPostBox)
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
                                ConstrainBlur(strength, preBlur, w, h, Mathf.CeilToInt(edgeFeather * GaussianRadiusPerSigma), cancellationToken);
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
                            int rowOff = y * w;
                            for (int x = 0; x < w; x++)
                            {
                                int i = rowOff + x;
                                if (IsExcludedAt(maskColOf, maskRowBase, x, y, commonMask, zoneMask)) strengthForReapply[i] = 0f;
                            }
                        });
                        debug?.RecordStage(zone.id, DebugStages.MaskReapply, strength, w, h);
                    }

                    // 3a. 含めるマスク適用: 指定画素を full strength で選択へ強制追加する。
                    //     除外の再適用より後に置くが、includedPx には除外優先が焼き込み済みなので
                    //     順序に依らず「除外が勝つ」。ブラー等の後段パスより後 = マスク解像度の
                    //     ハードエッジになる(除外マスクと同じ WYSIWYG 契約)。選択キャッシュ保存の
                    //     前に置くことで、キー(含めるマスク内容ハッシュ)と保存内容が常に対応する。
                    if (!selCached && includedPx != null)
                    {
                        ForceIncluded(strength, includedPx, w, h, po);
                        debug?.RecordStage(zone.id, DebugStages.MaskReapply, strength, w, h);
                    }

                    // 選択フェーズ(Match〜マスク再適用)完了。ここが「生の選択」の境界で、この後の
                    // RejectNeutral/SolidifyAchromaInterior/decontam は target 依存で strength を破壊的に
                    // 書き換える。ミス時のみ、この時点の strength(コピー)+ FF keep を選択キャッシュへ保存し、
                    // 次回「再着色のみ変更」した再生成で復元して選択フェーズを丸ごと省く(出力ビット不変)。
                    _sub.Mark(SpBlurReapply);
                    if (!selCached && selectionCache != null && isFullImagePath)
                        selectionCache.Store(zone.id, selKey, strength, keepBitsForCache, forcedBits, w, h);

                    // 無彩サンプル/極端無彩ターゲットの重み(無彩パスと AA フィデリティ修正で共用)。
                    _sub.Mark(SpSelCacheStore);
                    float zAchromaWeight = ComputeAchromaWeight(zone.sampleColor, zone.targetColor);

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
                    if (zAchromaSelectWeight > AchromaNeutralRejectWeightMin && sampS >= NeutralRejectActiveSourceSat)
                    {
                        RejectNeutralForAchromaTarget(strength, pixS, w, h, sampS, cancellationToken);
                        // 含める画素の再主張: 中性リジェクトは target 依存で strength を破壊的に
                        // 書き換え、ユーザーが明示的に含めた画素(例: 白ツヤ)まで落とし得る。
                        // 「含める」は明示指示なのでヒューリスティックより優先する。
                        // 本段は選択キャッシュのヒット経路でも毎回走るため、selCached と無関係に適用する。
                        if (includedPx != null)
                            ForceIncluded(strength, includedPx, w, h, po);
                    }

                    _sub.Mark(SpRejectNeutral);
                    // 無彩パスの内部固め: 極端な無彩ターゲット(白↔黒)では、マッチ強度が色のばらつきで内部まで
                    // フルにならず、明るい画素ほど弱く塗られて元色が残り「中央の段差」になる。陰影は塗り
                    // 強度でなく recolor の achroma レンジリマップ(gain≤1)で表現すべきなので、マッチ領域の
                    // 内部を full strength に固め、AA 縁(侵食で除いた帯)の taper だけ残す。有彩ターゲット
                    // (achromaWeight≈0)では no-op = byte 不変。
                    if (zAchromaWeight > AchromaWeightActiveMin)
                        SolidifyAchromaInterior(strength, w, h, zAchromaWeight, cancellationToken);

                    _sub.Mark(SpSolidify);
                    // 3b. AA 境界の α 分解（オプション）：strength が 0 < s < interiorThreshold の
                    //     ピクセルを「α×FG + (1-α)×BG」と見て元テクスチャの合成を逆算し、
                    //     新色で再合成する。halo（薄汚れた中間色）を構造的に除去する。
                    bool[] aaMask = null;
                    Color32[] decontaminatedPixels = null;
                    if (useDecontamination)
                    {
                        aaMask = decontamAaMask;
                        decontaminatedPixels = decontamPixels;
                        // 内部固め(上)が無彩ターゲットの内部を均一化したので、旧 AA フィデリティ修正の
                        // interior_threshold=1.01(全画素 α 再合成)は不要(むしろ内部を背景色で再合成して
                        // 段差を復活させる)。常に通常閾値で AA 縁だけをデコンタミする。
                        float effInteriorThreshold = DecontaminationInteriorThreshold;
                        // 除外マスク画素は strength=0 だが「背景」ではない(サンプル同色の保護パーツで
                        // あり得る)。BG ドナーに入れると推定色がサンプル色で汚染され、マスク境界の外側に
                        // 誤色の点ノイズを塗るため、位置を渡してドナーから隠す(マスク中立化)。
                        bool[] deconExcluded = null;
                        if (commonMask != null || zoneMask != null)
                        {
                            if (decontamMaskExcluded == null) decontamMaskExcluded = s_boolPool.Rent(len);
                            deconExcluded = decontamMaskExcluded;
                            // デコンタミが除外フラグを読むのは BG ドナー範囲(後段 bbox ± radius)だけ
                            // なので、そこだけ埋める。範囲外は読まれない=出力ビット不変
                            // (バッファはゾーン間で使い回すが、各ゾーンが自分の読む範囲を必ず埋める)。
                            int exY0 = Mathf.Max(0, ppMinY - decontaminationRadius);
                            int exY1 = Mathf.Min(h - 1, ppMaxY + decontaminationRadius);
                            int exX0 = Mathf.Max(0, ppMinX - decontaminationRadius);
                            int exX1 = Mathf.Min(w - 1, ppMaxX + decontaminationRadius);
                            if (hasPostBox)
                                FillExcludedRect(deconExcluded, w, exX0, exY0, exX1, exY1,
                                    maskColOf, maskRowBase, commonMask, zoneMask, po);
                        }
                        // 後段 bbox(ppMin/Max)を渡してデコンタミを実マッチ範囲に限定する。α 分解が
                        // 触るのは 0<strength<threshold の画素だけ=定義上この bbox 内なので出力ビット不変。
                        _sub.Mark(SpDecontamExcl);
                        DecontaminateAaBoundary(originalPixels, strength, w, h,
                            zone.sampleColor, zone.targetColor,
                            decontaminationRadius, effInteriorThreshold,
                            aaMask, decontaminatedPixels, hasPostBox, cancellationToken,
                            deconExcluded, ppMinX, ppMinY, ppMaxX, ppMaxY,
                            solidifyOnly: zMixMode);
                        _sub.Mark(SpDecontam);
                        debug?.RecordDecontamination(zone.id, aaMask, w, h);
                    }

                    _phaseTicks[PhDecontam] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // 4. 強度でブレンドした再色付けを適用
                    // (サンプルの S/V は sampS/sampV としてゾーン先頭で算出済み)
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
                    // (部分クロップ経路は現在は検証用。製品 UI の詳細プレビューはフル段出力の切り出し)。
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

                    _sub.Mark(SpWashSample);
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
                            ppMinX, ppMinY, ppMaxX, ppMaxY, out float anchorL, out float anchorC,
                            includedPx, cancellationToken, palette))
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
                    _sub.Mark(SpAnchor);
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

                    // 無彩パスの領域 L 中央値を事前計算(zAchromaWeight は上で算出済み)。
                    float zRegLmid = 0.5f;
                    bool zHasRegL = false;
                    // 形維持リマップの center 基準(中央値)を連結成分ごとに局所化する per-pixel マップ。
                    // ゆるいマスクで明るい背景を巻き込んでも、各成分が自分の地色基準で再着色されるので、
                    // 背景よりわずかに暗いだけの明るい対象がベタ黒へ潰れない。null のときは
                    // zRegLmid(全体中央値)へフォールバック。
                    float[] zRegMidMap = null;
                    if (zAchromaWeight > AchromaWeightActiveMin)
                    {
                        if (useCachedStats)
                        {
                            // クロップ: フル画像の領域 L 統計と成分中央値マップ(該当クロップ領域)を転写。
                            zHasRegL = cachedStats.hasRegL;
                            zRegLmid = cachedStats.regLmid;
                            if (zHasRegL && cachedStats.regMidMapFull != null)
                                zRegMidMap = CropFullMidMap(cachedStats.regMidMapFull, w, h, originX, originY, fullW);
                        }
                        else
                        {
                            zHasRegL = TryComputeRegionLRange(originalPixels, strength, w,
                                ppMinX, ppMinY, ppMaxX, ppMaxY,
                                out zRegLmid, includedPx, cancellationToken, palette);
                                _sub.Mark(SpRegionLRange);
                            if (zHasRegL)
                            {
                                // 地図をキャッシュへ渡す(下)ときだけ新しく確保し、それ以外はプールから借りる。
                                bool keepMap = isFullImagePath && parityCache != null;
                                zRegMidMap = BuildComponentMedianLMap(originalPixels, strength, w, h, 0.05f, cancellationToken,
                                    palette, rentMap: !keepMap);
                                if (!keepMap) regMidMapRented = zRegMidMap;
                            }
                        }
                    }

                    _sub.Mark(SpMedianLMap);
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
                            regLmid = zRegLmid,
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
                    TryComputeStrengthBBox(strengthForRecolor, w, h, RecolorMinStrength,
                        out int rcMinX, out int rcMinY, out int rcMaxX, out int rcMaxY, cancellationToken);
                    _sub.Mark(SpRecolorBBox);
                    // ゾーン不変の再着色パラメータをループ前に 1 回だけ構築(in 渡しで per-pixel コピー回避)。
                    var rcParams = new RecolorParams(
                        zOkMagScale, zTa, zTb, zOkGray, zOkGa, zOkGb,
                        zSL, zTL, zSC, zOkChromaMaxMag, zValueBlend, zEffShadowDesat,
                        sampS, zTR, zTG, zTB, zWR, zWG, zWB, zWV,
                        zApplyWash, zAchromaWeight, zOsat, zHasRegL);
                    // 混色帯(選択境界の AA・にじみ)の解析。境界クリーンアップ ON かつ有彩サンプルのゾーンでは、
                    // 境界の画素を「被覆率ぶんだけ隣の素材の変化を足す」合成の式で塗る(PixelProcessor.Decontam.cs
                    // の AnalyzeMixtureBand)。対象の画素は mixAlpha ≥ 0、色は decontaminatedPixels に入り、
                    // 下のループが読む。無彩サンプル(グレーモード相当)は色度で被覆率を測れないので従来の経路。
                    float[] mixAlphaLocal = null;
                    if (zMixMode && rcMaxX >= 0)
                    {
                        if (mixAlpha == null) mixAlpha = s_floatPool.Rent(len);
                        bool[] mixExcluded = null;
                        if (commonMask != null || zoneMask != null)
                        {
                            // 解析が読むのは bbox ± (2·探索半径 + 1)。その範囲の除外フラグを埋める。
                            if (decontamMaskExcluded == null) decontamMaskExcluded = s_boolPool.Rent(len);
                            mixExcluded = decontamMaskExcluded;
                            int mm = 2 * Mathf.Max(MixBandRadius + 1, decontaminationRadius) + 1;
                            int my0 = Mathf.Max(0, rcMinY - mm), my1 = Mathf.Min(h - 1, rcMaxY + mm);
                            int mx0 = Mathf.Max(0, rcMinX - mm), mx1 = Mathf.Min(w - 1, rcMaxX + mm);
                            FillExcludedRect(mixExcluded, w, mx0, my0, mx1, my1,
                                maskColOf, maskRowBase, commonMask, zoneMask, po);
                        }
                        AnalyzeMixtureBand(originalPixels, strengthForRecolor, w, h, decontaminationRadius,
                            in rcParams, zRegMidMap, zRegLmid,
                            MostChromaticSample(zone), includedPx,
                            decontaminatedPixels, mixAlpha,
                            rcMinX, rcMinY, rcMaxX, rcMaxY,
                            out int mixMinX, out int mixMinY, out int mixMaxX, out int mixMaxY,
                            mixExcluded, cancellationToken);
                        // 帯は選択の外側にも伸びるので、ループの範囲を広げる。
                        rcMinX = mixMinX; rcMinY = mixMinY; rcMaxX = mixMaxX; rcMaxY = mixMaxY;
                        mixAlphaLocal = mixAlpha;
                    }
                    _sub.Mark(SpMixBand);
                    // 再着色色は画素の色(RGBA と色から求めた V)とゾーン定数だけで決まる。成分別 L マップ
                    // (無彩パス)を使わないゾーンでは色ごとに 1 回だけ求めて配る。表の大きさが再着色の
                    // 範囲より大きい(小さなロゴ等)ときは画素ごとに求めたほうが安いので使わない。
                    Color32[] palRecolored = null;
                    if (palette != null && zRegMidMap == null && rcMaxX >= 0
                        && (long)palette.Count * 2 <= (long)(rcMaxX - rcMinX + 1) * (rcMaxY - rcMinY + 1))
                    {
                        int pc = palette.Count;
                        var outRc = new Color32[pc];
                        Color32[] pCol = palette.Colors;
                        float[] pV = palette.V;
                        var rcParamsLocal = rcParams;
                        float lmid = zRegLmid;
                        ForEachPaletteChunk(pc, po, (k0, k1) =>
                        {
                            for (int k = k0; k < k1; k++)
                            {
                                Color32 c = pCol[k];
                                outRc[k] = RecolorPixel(c.r, c.g, c.b, pV[k], c.a / 255f, in rcParamsLocal, lmid);
                            }
                        });
                        palRecolored = outRc;
                    }
                    int[] palIdxRecolor = palette?.Index;
                    // 色ごとの表を作れないゾーン(成分別 L マップあり)は、スレッドごとのメモで使い回す。
                    int recolorMemoEpoch = palette != null && palRecolored == null && rcMaxX >= 0
                        ? NextRecolorMemoEpoch() : 0;
                    // rcMaxX<0 はマッチ画素なし → 全画素 continue で何もしないのと同じ(出力不変)。
                    if (rcMaxX >= 0)
                    Parallel.For(rcMinY, rcMaxY + 1, po, y =>
                    {
                        int rowOff = y * w;
                        for (int x = rcMinX; x <= rcMaxX; x++)
                        {
                            int i = rowOff + x;
                            float s = strengthForRecolor[i];
                            float maRaw = mixAlphaLocal != null ? mixAlphaLocal[i] : -1f;
                            // 解析が「素材そのもの」と判定した弱い選択の画素: 全強度で再着色する。
                            if (maRaw >= MixAsMaterial) { s = 1f; maRaw = -1f; }
                            bool mix = maRaw >= 0f;
                            if (s <= RecolorMinStrength && !mix) continue;
                            // 上位(リスト上位)ゾーンが既に占有した分を差し引いた実効強度 es。
                            // 残り(room)が無ければこのゾーンは適用しない(= 上位が排他)。
                            float room = 1f - claimedLocal[i];
                            if (room <= 0.001f) continue;
                            if (mix)
                            {
                                // 混色帯: 被覆率 α のぶんだけ、隣の素材の変化(decontaminatedLocal − 元)を足す。
                                // 占有も α だけ。残り(1−α)は背景のぶんで、下位ゾーンが自分の被覆率ぶんを重ねられる。
                                float ma = maRaw;
                                float a = ma < room ? ma : room;
                                if (a > 0f)
                                {
                                    Color32 mo = originalPixels[i], mt = decontaminatedLocal[i];
                                    if (a >= ma && claimedLocal[i] <= 0.0001f)
                                        pixels[i] = mt;
                                    else
                                    {
                                        float k = a / ma;
                                        Color32 cur = pixels[i];
                                        pixels[i] = new Color32(
                                            (byte)Mathf.Clamp(Mathf.RoundToInt(cur.r + (mt.r - mo.r) * k), 0, 255),
                                            (byte)Mathf.Clamp(Mathf.RoundToInt(cur.g + (mt.g - mo.g) * k), 0, 255),
                                            (byte)Mathf.Clamp(Mathf.RoundToInt(cur.b + (mt.b - mo.b) * k), 0, 255),
                                            cur.a);
                                    }
                                    claimedLocal[i] = Mathf.Min(1f, claimedLocal[i] + a);
                                }
                                continue;
                            }
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
                            float regMid = (zRegMidMap != null && zRegMidMap[i] > 0f) ? zRegMidMap[i] : zRegLmid;
                            Color32 recolored = palRecolored != null
                                ? palRecolored[palIdxRecolor[i]]
                                : recolorMemoEpoch != 0
                                    ? RecolorPixelMemo(recolorMemoEpoch, palIdxRecolor[i], regMid,
                                        op, pixV[i], alpha, in rcParams)
                                    : RecolorPixel(
                                        op.r, op.g, op.b,
                                        pixV[i], alpha,
                                        in rcParams,
                                        regMid);
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
                    _sub.Mark(SpRecolorLoop);
                    debug?.RecordStage(zone.id, DebugStages.Recolor, strength, w, h);

                    // 無彩(白↔黒)再着色のエッジに残る「地色の残り」フチ消し。マッチ境界の外側 2px に残る
                    // 背景より明るい混色画素を α 分解で背景へ寄せ、暗い再着色色に対する明るいフチを消す。
                    // 有彩(zAchromaWeight≈0)では呼ばれず完全 no-op。共有のマッチ/合成経路は変更しない。
                    // 混色帯の解析が走ったゾーン(有彩サンプル)では、外側の縁もそちらが合成の式で塗り終えている。
                    if (zAchromaWeight > AchromaWeightActiveMin && rcMaxX >= 0 && mixAlphaLocal == null)
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
                            if (decontamMaskExcluded == null) decontamMaskExcluded = s_boolPool.Rent(len);
                            fringeExcluded = decontamMaskExcluded;
                            const int fm = AchromaFringeExclusionMargin;
                            int fy0 = Mathf.Max(0, rcMinY - fm), fy1 = Mathf.Min(h - 1, rcMaxY + fm);
                            int fx0 = Mathf.Max(0, rcMinX - fm), fx1 = Mathf.Min(w - 1, rcMaxX + fm);
                            FillExcludedRect(fringeExcluded, w, fx0, fy0, fx1, fy1,
                                maskColOf, maskRowBase, commonMask, zoneMask, po);
                        }
                        CleanAchromaFringe(pixels, originalPixels, strengthForRecolor, claimedLocal,
                            w, h, zone.sampleColor, zone.targetColor, rcMinX, rcMinY, rcMaxX, rcMaxY,
                            cancellationToken, fringeExcluded);
                    }

                    _sub.Mark(SpAchromaFringe);
                    _phaseTicks[PhRecolor] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                    // Recolor 段で各ピクセルに適用されたサブブランチを記録する。
                    // hot loop には分岐を増やさず、debug 有効時だけ追加の Parallel.For で
                    // 上の RecolorPixel 内の条件式を再評価する。
                    // 優先度: Decontaminate > Shadow > Highlight > Base。
                    // shadow と highlight は条件上ほぼ排他（oV<thr と oV>sV）だが念のため shadow を優先。
                    // NOTE: 実際の RecolorPixel に渡した値を使うこと。
                    //   Shadow: zone.shadowDesaturation でなく zEffShadowDesat (autoRecolorAnchor 時に補正済み)
                    //   Highlight: sampV でなく zWV (HighlightSampleCorrector で補正した実効 wash サンプルの V)
                    if (debug != null)
                    {
                        byte[] branchMap = new byte[len];
                        float zoneShadowDesat = zEffShadowDesat;
                        float zoneSV = zWV;
                        float zoneSS = sampS;
                        bool zoneApplyWash = zone.applyHighlightWash;
                        var aaMaskForBranch = aaMask;
                        var mixForBranch = mixAlphaLocal;
                        int bx0 = rcMinX, by0 = rcMinY, bx1 = rcMaxX, by1 = rcMaxY;
                        Parallel.For(0, len, po, i =>
                        {
                            // 混色帯で塗った画素(選択の外側も含む)は、合成で塗る分岐として記録する。
                            // mixAlpha が有効なのは解析した矩形の中だけ。
                            if (mixForBranch != null)
                            {
                                int bx = i % w, by = i / w;
                                if (bx >= bx0 && bx <= bx1 && by >= by0 && by <= by1 && mixForBranch[i] >= 0f
                                    && mixForBranch[i] < MixAsMaterial)
                                {
                                    branchMap[i] = (byte)(mixForBranch[i] > 0f ? DebugBranch.Decontaminate : DebugBranch.None);
                                    return;
                                }
                            }
                            if (strengthForRecolor[i] <= RecolorMinStrength)
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
                    if (regMidMapRented != null) s_floatPool.Return(regMidMapRented);
                    if (highlightPot != null) s_floatPool.Return(highlightPot);
                    if (strength != null) s_floatPool.Return(strength);
                    if (matchConf != null) s_floatPool.Return(matchConf);
                    if (includedPx != null) s_boolPool.Return(includedPx);
                }
            }

            }
            finally
            {
                palette?.Release();
                if (decontamMaskExcluded != null) s_boolPool.Return(decontamMaskExcluded);
                if (decontamPixels != null) s_color32Pool.Return(decontamPixels);
                if (decontamAaMask != null) s_boolPool.Return(decontamAaMask);
                if (mixAlpha != null) s_floatPool.Return(mixAlpha);
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
                new PerfReport(TicksToMs(Stopwatch.GetTimestamp() - _t0), w, h, _perfZones, _perfPhases,
                    _sub.ToEntries(s_perfSubPhaseNames)));
        }

        /// <summary>集計対象が行レンジ [yFrom,yTo) のデリゲート。戻り値は集計した画素数。</summary>
        internal delegate int HistRowChunk(int yFrom, int yTo, int[] localHist);

        /// <summary>
        /// 行レンジ [yFrom,yTo) をチャンク分割し、チャンクごとにスレッドローカルのヒストグラムへ
        /// 集計してからマージする。bbox の行だけを分割したいとき(領域統計を実マッチ範囲に限定する
        /// とき)に使う。ヒストグラムは整数カウントの加算だけで**集計順に依存しない**ので、結果は
        /// 単スレッド逐次版と完全に同値(=percentile もビット不変)。
        /// </summary>
        /// <returns>全チャンクの集計画素数の合計。</returns>
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

        /// <summary>256bin ヒストグラムの percentile(0..1) を実値で返す(値域 [0, scale])。
        /// bin→値は b/(Length-1)·scale(256bin なら b/255·scale)。線形補間の percentile に対し
        /// 最大 1bin(scale/255)の離散化差を許容する(auto_wash_sample の前例に従う)。</summary>
        internal static float HistValueAtPercentile(int[] hist, int total, float pct, float scale)
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
        /// マスク(除外・含める)の判定表を作る。作業座標の列 x → マスクの列(mx)と、行 y → マスクの行の先頭(my·maskW)。
        /// 作業画像はテクスチャ(texW×texH)の (originX, originY) からの切り出しで、マスクの解像度は
        /// テクスチャと違ってよい(整数の拡縮で対応するマスク画素へ、端は切り詰め)。
        /// 以前は画素ごとにこの割り算をしていた(同じ式なので判定は同じ)。
        /// </summary>
        private static void BuildMaskIndexMap(int w, int h, int originX, int originY, int texW, int texH,
            int maskW, int maskH, out int[] colOf, out int[] rowBase)
        {
            colOf = new int[w];
            for (int x = 0; x < w; x++) colOf[x] = Mathf.Clamp((x + originX) * maskW / texW, 0, maskW - 1);
            rowBase = new int[h];
            for (int y = 0; y < h; y++) rowBase[y] = Mathf.Clamp((y + originY) * maskH / texH, 0, maskH - 1) * maskW;
        }

        /// <summary>作業座標 (x, y) が除外(共通 ∪ ゾーン)か。表は BuildMaskIndexMap(無ければ除外なし)。</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsExcludedAt(int[] colOf, int[] rowBase, int x, int y,
            ulong[] commonMask, ulong[] zoneMask)
        {
            if (colOf == null) return false;
            int idx = rowBase[y] + colOf[x];
            return MaskSnapshot.GetBit(commonMask, idx) || MaskSnapshot.GetBit(zoneMask, idx);
        }

        /// <summary>
        /// 作業座標 (x, y) がゾーンの含めるマスク(1=強制的に選択へ含める)に入るか。座標の対応は
        /// 除外と同じ表(BuildMaskIndexMap。無ければ含めるなし)。除外優先は呼び出し側が組む。
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsIncludedAt(int[] colOf, int[] rowBase, int x, int y, ulong[] includeMask)
            => colOf != null && MaskSnapshot.GetBit(includeMask, rowBase[y] + colOf[x]);

        /// <summary>
        /// 矩形 [x0, x1] × [y0, y1](両端含む)の除外フラグ(IsExcludedAt)を dst に書く。範囲外は触らない。
        /// デコンタミ・混色帯・無彩フチ消しの 3 か所が、それぞれ自分の読む範囲だけをこれで埋める。
        /// </summary>
        private static void FillExcludedRect(bool[] dst, int w, int x0, int y0, int x1, int y1,
            int[] colOf, int[] rowBase, ulong[] commonMask, ulong[] zoneMask, ParallelOptions po)
        {
            Parallel.For(y0, y1 + 1, po, y =>
            {
                int rowOff = y * w;
                for (int x = x0; x <= x1; x++)
                    dst[rowOff + x] = IsExcludedAt(colOf, rowBase, x, y, commonMask, zoneMask);
            });
        }

        /// <summary>含める画素(included[i] が true)の strength を 1(full strength)にする。他の画素は触らない。</summary>
        private static void ForceIncluded(float[] strength, bool[] included, int w, int h, ParallelOptions po)
        {
            Parallel.For(0, h, po, y =>
            {
                int rowOff = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = rowOff + x;
                    if (included[i]) strength[i] = 1f;
                }
            });
        }

        /// <summary>
        /// src[0..len) のうち値が正(&gt; 0)の画素のビットを立てた ulong 配列(64 画素で 1 語、
        /// 画素 i は語 i &gt;&gt; 6 のビット i &amp; 63)を返す。フル画像で解いた keep(strength&gt;0)を
        /// パリティキャッシュ・選択キャッシュへ渡すときに使う。
        /// </summary>
        private static ulong[] PackPositiveBits(float[] src, int len, ParallelOptions po)
        {
            // 64 画素ごとの語は互いに独立なので、語のまとまりごとに並列で作る(出力ビット不変)。
            int nWords = (len + 63) >> 6;
            var keepBits = new ulong[nWords];
            Parallel.For(0, (nWords + 1023) >> 10, po, c =>
            {
                int w0 = c << 10, w1 = Math.Min(nWords, w0 + 1024);
                for (int wi = w0; wi < w1; wi++)
                {
                    int b = wi << 6, e = Math.Min(len, b + 64);
                    ulong bits = 0UL;
                    for (int i = b; i < e; i++)
                        if (src[i] > 0f) bits |= 1UL << (i - b);
                    keepBits[wi] = bits;
                }
            });
            return keepBits;
        }

        /// <summary>
        /// 長辺を maxSize に収める等比縮小の寸法規約（単一の正）。長辺が maxSize 以下なら
        /// scale=1（縮小なし）。表示寸法（Preview.MaxSize）・プロキシ寸法（Preview.ProxyMaxSize）・
        /// headless ハーネスのプレビュー段検証が全てこの丸めを共有する
        /// （呼び出し側が独自に丸めるとテストが製品と別の絵を測る）。
        /// </summary>
        public static void ComputeFitSize(int srcW, int srcH, int maxSize,
            out int dstW, out int dstH, out float scale)
        {
            int srcLong = Mathf.Max(srcW, srcH);
            scale = maxSize >= srcLong ? 1f : maxSize / (float)srcLong;
            dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));
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
