// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
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
    internal static unsafe partial class PixelProcessor
    {
        // 並列度の規則と画素の決まりは Foundation(CoreParallelism・PixelRules)が持つ。ここは今の名前を残す転送役
        // (0.4.0 の計画の 3-4。呼び出しが多く、ハーネス・テスト・AI 提案の後処理もこの名前で呼ぶ)。
        internal static int ComputeDefaultParallelism(int processorCount) => CoreParallelism.ComputeDefault(processorCount);
        internal static int DefaultParallelism => CoreParallelism.Default;
        internal static int GetMaxParallelism() => CoreParallelism.Max();
        internal const int OpaqueAlphaMin = PixelRules.OpaqueAlphaMin;

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
            SpSelCacheStore = 14, SpSolidify = 15, SpDecontamExcl = 16,
            SpDecontam = 17, SpWashSample = 18, SpAnchor = 19, SpRegionLRange = 20, SpMedianLMap = 21,
            SpRecolorBBox = 22, SpRecolorLoop = 23, SpAchromaFringe = 24, SpPaletteHash = 25,
            SpPaletteIds = 26, SpPaletteRemap = 27, SpMixBand = 28, SubPhaseCount = 29;
        private static readonly string[] s_perfSubPhaseNames =
        {
            "Alloc", "MatchLoop", "ChromaCeiling", "EnclosedNeutral",
            "HlPropagate", "HlBand", "HlEnclosed", "FfComponents", "FfKeepBits",
            "PostBBox", "HoleFillGate", "HoleFill", "Boundary", "BlurReapply",
            "SelCacheStore", "Solidify", "DecontamExcl",
            "Decontam", "WashSample", "Anchor", "RegionLRange", "MedianLMap",
            "RecolorBBox", "RecolorLoop", "AchromaFringe", "PaletteHash", "PaletteIds", "PaletteRemap",
            "MixBand",
        };

        // 作業配列(フル解像度の float・bool など。1 ゾーンで同時に 20 本ほど借りる)は使い回す。毎回 new すると
        // プレビューのたびに数百 MB〜1GB の新規確保になる。
        // 0.3.0 から作業配列は Mono のヒープの外(NativeWorkPool、ポインタで貸し出す)に置く。以前は管理配列の
        // ArrayPool で使い回していたが、Mono のヒープは縮まないので、書き出し(元の解像度)などで一度大きく使った分が
        // エディタを閉じるまで残っていた(8K で約 3GB)。ネイティブなら ReleaseWorkBuffers ですぐ OS へ返せる。
        // ポインタにしたのは、Unity の Mono で配列より遅くならない書き方がポインタだけだったため(構造体の添字や
        // Span は Debug の最適化モードで 2〜4 倍遅い。scripts/editor-tests/KernelBenchTests.cs)。
        // 注意: プールの Rent はゼロ初期化を保証しない。既存のクリア(ClearWork)は残すこと。
        // 色の表(「画素 → 番号」と番号ごとの HSV)も Mono のヒープの外に置き、参照の数で解放する
        // (PixelProcessor.Palette.cs)。色の表が無いときの画素ごとの HSV は作業配列(s_floatPool)から借りる。
        // 入力の控え(originalPixels)はポインタで読む(ProcessPixelsArrayCore)。
        private static readonly NativeWorkPool<float> s_floatPool = WorkPools.Float;
        private static readonly NativeWorkPool<bool> s_boolPool = WorkPools.Bool;
        private static readonly NativeWorkPool<byte> s_bytePool = WorkPools.Byte;
        // デコンタミの書き込み先(decontamPixels)と、書き出しのときの入力の控え(originalPixels)。
        private static readonly NativeWorkPool<Color32> s_color32Pool = WorkPools.Rgba32;
        // 連結成分ラベリング(label / BFS スタック)用。従来は呼び出しごとに new int[bw*bh]
        // (4K 全面マッチで 67MB)を LOH へ確保していた。Rent はゼロ初期化しないので label は
        // 使用前にクリアすること。
        private static readonly NativeWorkPool<int> s_intPool = WorkPools.Int;

        // 使っていないメモリをまとめて返す窓口は Foundation の CoreMemory。ここは今の名前を残す転送役(3-4)。
        internal static void ReleaseWorkBuffers() => CoreMemory.ReleaseIdle();
        internal static Task ReleaseWorkBuffersInBackground() => CoreMemory.ReleaseIdleInBackground();

        // 借りた作業配列を 0 で埋める([start, start+count))。
        private static void ClearWork<T>(T* p, int start, int count) where T : unmanaged
            => new Span<T>(p + start, count).Clear();

        // 作業配列の中身を管理配列へ写した複製(デバッグの記録用。debug があるときだけ呼ぶ)。
        private static T[] ToManaged<T>(T* p, int count) where T : unmanaged
        {
            if (p == null) return null;
            var a = new T[count];
            new ReadOnlySpan<T>(p, count).CopyTo(a);
            return a;
        }

        // スタティック計算メソッド — バックグラウンドスレッドで実行可能
        // Texture2Dなし、UnityEngine.Object APIなし、Mathfとカラー計算のみ（いずれもスレッドセーフ）
        // originX/Y: フル解像度テクスチャでのクロップオフセット（0で全テクスチャ処理）
        // fullW/H: フル解像度テクスチャの寸法（0 = w/hと同じ、つまりクロップなし）
        // 全体設定は RecolorSettings で受け取る（呼び出し元ごとに位置引数を並べ直させない）。
        // cancellationToken: バックグラウンドのプレビュー/エクスポートが渡す
        // sourcePixels: 呼び出した時点の pixels と同じ中身の配列(長さ w*h 以上)。処理中は読むだけで、
        //   呼び出し側も書き換えないこと。渡すと入力のスナップショット(4K で 64MB の複製)を作らずにこれを読む。
        //   プレビューはソース画素を pixels へ写してから呼ぶので、写し元をそのまま渡せる。null なら内部で複製する。
        public static void ProcessPixelsArray(
            Color32[] pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones,
            in RecolorSettings settings,
            CancellationToken cancellationToken = default,
            int originX = 0, int originY = 0, int fullW = 0, int fullH = 0,
            IDebugCapture debug = null,
            PreviewParityCache parityCache = null,
            SelectionCache selectionCache = null,
            Color32[] sourcePixels = null)
        {
            // 引数検証。ここが無いと、不正な引数（null / 長さ不足）で Array.Copy が投げたとき
            // 直前に Rent したプール配列（4K で 64MB）が返却されずリークする。Rent は
            // finally が守る try の外にあるため、例外が出るなら Rent の前に出さないといけない
            // （レビュー §4 低）。
            if (pixels == null) throw new System.ArgumentNullException(nameof(pixels));
            long lenLong = CheckSize(w, h);
            if (pixels.Length < lenLong)
                throw new System.ArgumentException(
                    $"pixels.Length({pixels.Length}) < w*h({lenLong})", nameof(pixels));
            if (sourcePixels != null)
            {
                // pixels そのものは処理中に書き換わるので、入力の控えにはならない。
                if (ReferenceEquals(sourcePixels, pixels))
                    throw new System.ArgumentException("sourcePixels must not be the pixels array itself", nameof(sourcePixels));
                if (sourcePixels.Length < lenLong)
                    throw new System.ArgumentException(
                        $"sourcePixels.Length({sourcePixels.Length}) < w*h({lenLong})", nameof(sourcePixels));
            }
            // 処理の間だけ固定してポインタで読み書きする(出力の配列は呼び出し側の持ち物のまま)。
            fixed (Color32* px = pixels)
            fixed (Color32* src = sourcePixels)
                ProcessPixelsArrayCore(px, w, h, masks, sortedZones, in settings,
                    originX, originY, fullW, fullH, cancellationToken,
                    debug, parityCache, selectionCache, src);
        }

        /// <summary>
        /// <see cref="ProcessPixelsArray(Color32[], int, int, MaskSnapshot, IList{ColorZone}, in RecolorSettings, CancellationToken, int, int, int, int, IDebugCapture, PreviewParityCache, SelectionCache, Color32[])"/> の
        /// ポインタ版。Mono のヒープの外に置いた画素(プレビューの原本と出力)を写さずに処理する。
        /// pixels(w*h 画素。入力の画素を入れておくと、その場で処理結果に書き換わる)と sourcePixels(null 可。
        /// 渡すなら pixels と同じ中身で、処理中は読むだけ)は、処理が返るまで呼び出し側が保つこと。
        /// </summary>
        public static void ProcessPixelsNative(
            Color32* pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones,
            in RecolorSettings settings,
            CancellationToken cancellationToken = default,
            int originX = 0, int originY = 0, int fullW = 0, int fullH = 0,
            IDebugCapture debug = null,
            PreviewParityCache parityCache = null,
            SelectionCache selectionCache = null,
            Color32* sourcePixels = null)
        {
            if (pixels == null) throw new System.ArgumentNullException(nameof(pixels));
            CheckSize(w, h);
            if (sourcePixels == pixels)
                throw new System.ArgumentException("sourcePixels must not be the pixels buffer itself", nameof(sourcePixels));
            ProcessPixelsArrayCore(pixels, w, h, masks, sortedZones, in settings,
                originX, originY, fullW, fullH, cancellationToken,
                debug, parityCache, selectionCache, sourcePixels);
        }

        // 寸法の検証(画素数は int に収まること)。戻り値は画素数。
        private static long CheckSize(int w, int h)
        {
            if (w <= 0 || h <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(w), $"invalid size: {w}x{h}");
            long lenLong = (long)w * h;
            if (lenLong > int.MaxValue)
                throw new System.ArgumentOutOfRangeException(nameof(w), $"size too large: {w}x{h}");
            return lenLong;
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

        // pixels・sourcePixels の検証(null・寸法・同じ領域でないこと)は入口(ProcessPixelsArray / ProcessPixelsNative)が済ませる。
        private static void ProcessPixelsArrayCore(
            Color32* pixels, int w, int h,
            MaskSnapshot masks,
            IList<ColorZone> sortedZones,
            in RecolorSettings settings,
            int originX, int originY, int fullW, int fullH,
            CancellationToken cancellationToken,
            IDebugCapture debug,
            PreviewParityCache parityCache,
            SelectionCache selectionCache,
            Color32* sourcePixels)
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

            if (sortedZones == null) throw new System.ArgumentNullException(nameof(sortedZones));
            long lenLong = (long)w * h;

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
            // 入力のスナップショット。以降は読むだけなので、呼び出し側が同じ中身を持っていればそれを使う。
            // 持っていなければ(書き出し・NDMF)作業配列のプールへ写す(8K で 256MB。Mono のヒープに積まない)。
            // 写した分は末尾の finally で返す。
            bool ownsOriginal = sourcePixels == null;
            Color32* originalPixels;
            if (ownsOriginal)
            {
                originalPixels = s_color32Pool.Rent(len);
                Buffer.MemoryCopy(pixels, originalPixels, (long)len * sizeof(Color32), (long)len * sizeof(Color32));
            }
            else
            {
                originalPixels = sourcePixels;
            }

            long _t0 = Stopwatch.GetTimestamp();
            var _perfZones = new ZonePerfEntry[sortedZones.Count];
            int _perfIdx = 0;
            var _phaseTicks = new long[PhaseCount];
            var _sub = new SubPhaseClock(SubPhaseCount);
            long _tp = _t0;

            // 全ピクセルの HSV を zone ループに入る前に一括計算（zone 数に関わらず1回）
            // null 初期化してから try 内で Rent することで、
            // 2番目以降の Rent が例外を投げた場合に先行の配列をリークしない。
            // 色の表があれば表の H/S/V と画素 → 番号(pixIdx)を指す(フル解像度の配列は持たない)。
            // 表が無いときだけ画素ごとの配列を借りる(hsvRented)。読むときは HsvSlot(pixIdx, i) で引く。
            float* pixH = null, pixS = null, pixV = null;
            int* pixIdx = null;
            bool hsvRented = false;
            // ゾーンをまたいで使い回す作業配列(占有率・デコンタミ・除外フラグ・混色帯。末尾の finally で返す)。
            // 占有率: 下位ゾーンは残り(1-claimed)の範囲だけ適用され、「重なった部分は上位ゾーンのみ適用」という
            // レイヤー排他を実現する。単一ゾーン/非重複ピクセルでは常に 0 のままで、従来挙動は不変。
            var rb = new RunBuffers();
            // 色の種類の表(PixelProcessor.Palette.cs)。色だけで決まる計算を色ごとに 1 回で済ませる。
            // 種類が多い画像では null(画素ごとに計算する)。どちらでも出力はビット単位で同じ。
            ColorPalette palette = null;
            try
            {
            rb.Claimed = s_floatPool.Rent(len);
            ClearWork(rb.Claimed, 0, len);

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
                // 色ごとに求めた HSV を番号で引く(同じ色に同じ RGBToHSV を当てた値なのでビット単位で同じ)。
                pixH = palette.H; pixS = palette.S; pixV = palette.V;
                pixIdx = palette.Index;
            }
            else
            {
                hsvRented = true;
                pixH = s_floatPool.Rent(len);
                pixS = s_floatPool.Rent(len);
                pixV = s_floatPool.Rent(len);
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
            var hsv = new PixelHsv(pixH, pixS, pixV, pixIdx);
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
                rb.DecontamAaMask = s_boolPool.Rent(len);
                rb.DecontamPixels = s_color32Pool.Rent(len);
            }

            // ゾーンをまたいで変わらない入力。選択・マスクの契約へ渡す(RunLegacySelection・ApplyMaskContract)。
            var run = new RunContext
            {
                W = w, H = h, Len = len,
                OriginX = originX, OriginY = originY, FullW = fullW, FullH = fullH,
                Original = originalPixels, Pixels = pixels, Po = po, Ct = cancellationToken,
                Palette = palette, Hsv = hsv, PixH = pixH, PixS = pixS, PixV = pixV, PixIdx = pixIdx,
                CommonMask = commonMask, MaskW = maskW, MaskH = maskH, MaskColOf = maskColOf, MaskRowBase = maskRowBase,
                EdgeFeather = edgeFeather, RelaxedSatMin = relaxedSatMin, RelaxedSatRamp = relaxedSatRamp,
                AntiAliasCleanup = antiAliasCleanup, HoleFillPasses = holeFillPasses,
                HoleFillMinNeighbors = holeFillMinNeighbors,
                UseDecontamination = useDecontamination, DecontaminationRadius = decontaminationRadius,
                Buf = rb,
                Debug = debug, ParityCache = parityCache, SelectionCache = selectionCache,
                PhaseTicks = _phaseTicks, Sub = _sub,
            };

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
                // 穴埋め・境界回復は行わない)、境界の混色は再着色の直前に被覆率で塗る(PixelProcessor.MixtureBand.cs の
                // AnalyzeMixtureBand)。緩和マッチは選択を縁から一定の幅だけ広げるので、素材でない地まで拾うと
                // パーツの周りに輪が出る。
                bool zMixMode = IsMixtureBandZone(zone, useDecontamination, edgeFeather);
                long _tZone = Stopwatch.GetTimestamp();
                _tp = _tZone;
                _sub.Restart();

                // 作業配列の借用は per-zone の try/finally で必ず返却する。
                // Parallel.For は po.CancellationToken でキャンセル時に OperationCanceledException を投げる。
                // 後段ヘルパー(Decontaminate/CleanAchromaFringe/GaussianBlur/FillSmallHoles/RecoverBoundaryEdges
                // /Solidify/GrowHighlightBand)にも cancellationToken を渡しており、内部 Parallel.For も
                // 同様にキャンセル例外を伝播するため、例外経路でもプールが汚染されないよう finally でガードする。
                var zb = new ZoneBuffers();
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

                    // 含めるマスクをテクスチャ解像度の bool[] へ展開する(除外優先を焼き込む)。
                    // 用途は 3 つ: (a) 選択への強制適用(マスク再適用の直後・キャッシュ保存の前)
                    // (b) 大域統計(再着色アンカー/領域Lレンジ)からの除外 — 手動追加した画素が
                    //     地色統計を汚し、ゾーン全体の再着色が遠隔で変わるのを防ぐ
                    // (c) 中性リジェクト等 target 依存ヒューリスティック後の再主張
                    // キャッシュヒット時も (b)(c) で必要になるため selCached と無関係に作る。
                    // 含めるマスクが無ければ null のまま = 以降の全分岐が no-op(出力ビット不変)。
                    if (zoneInclude != null)
                    {
                        zb.IncludedPx = s_boolPool.Rent(len);
                        var incLocal = zb.IncludedPx;
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
                    bool* includedPx = zb.IncludedPx;

                    // 選択(旧の選択)。マスクを掛ける前の強度と後段の範囲を返す。強度の配列はゾーンの作業配列(zb)の
                    // 持ち物で、ゾーンの終わりに返す。
                    run.Tp = _tp;
                    var sel = RunLegacySelection(run, zone, zb, zoneMask, zMixMode, isFullImagePath, sampH, sampS, sampV);
                    _tp = run.Tp;
                    float* strength = sel.Strength;
                    bool hasPostBox = sel.Box.Has;
                    int ppMinX = sel.Box.MinX, ppMinY = sel.Box.MinY, ppMaxX = sel.Box.MaxX, ppMaxY = sel.Box.MaxY;

                    // マスクの契約(除外の掛け直し・含める)。
                    ApplyMaskContract(run, zone, strength, zoneMask, includedPx);

                    // 再着色の段。段どうしは z で値を受け渡す(0.4.0 の計画の 3-7b)。
                    var z = new ZoneState
                    {
                        Zone = zone, Buffers = zb, ZoneMask = zoneMask, MixMode = zMixMode, IsFullImagePath = isFullImagePath,
                        SampH = sampH, SampS = sampS, SampV = sampV, IncludedPx = includedPx,
                        Strength = strength, Box = sel.Box,
                    };
                    AchromaSolidifyStage.Run(run, z);
                    float zAchromaWeight = z.AchromaWeight;

                    run.Tp = _tp;
                    DecontamStage.Run(run, z);
                    _tp = run.Tp;
                    bool* aaMask = z.AaMask;
                    Color32* decontaminatedPixels = z.Decontaminated;

                    run.Tp = _tp;
                    RecolorStatsStage.Run(run, z);
                    _tp = run.Tp;
                    var rcParams = z.Params;
                    float zRegLmid = z.RegLmid;
                    float* zRegMidMap = z.RegMidMap;
                    float* zRegUpMap = z.RegUpMap;
                    float zEffShadowDesat = rcParams.shadowDesaturation;
                    float zWV = rcParams.washV;

                    var strengthForRecolor = strength;
                    var aaMaskLocal = aaMask;
                    var decontaminatedLocal = decontaminatedPixels;
                    var claimedLocal = rb.Claimed;
                    MixtureBandStage.Run(run, z);
                    int rcMinX = z.RcMinX, rcMinY = z.RcMinY, rcMaxX = z.RcMaxX, rcMaxY = z.RcMaxY;
                    float* mixAlphaLocal = z.MixAlpha;
                    ComposeStage.Run(run, z);

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
                        bool* fringeExcluded = null;
                        if (commonMask != null || zoneMask != null)
                        {
                            fringeExcluded = rb.MaskExcludedOrRent(len);
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
                            float oV = pixV[HsvSlot(pixIdx, i)];
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
                    zb.ReturnAll();
                }
            }

            }
            finally
            {
                palette?.Release();
                rb.ReturnAll();
                if (hsvRented)
                {
                    if (pixV != null) s_floatPool.Return(pixV);
                    if (pixS != null) s_floatPool.Return(pixS);
                    if (pixH != null) s_floatPool.Return(pixH);
                }
                if (ownsOriginal) s_color32Pool.Return(originalPixels);
            }
            var _perfPhases = new PhasePerfEntry[PhaseCount];
            for (int p = 0; p < PhaseCount; p++)
                _perfPhases[p] = new PhasePerfEntry(s_perfPhaseNames[p], TicksToMs(_phaseTicks[p]));
            DebugCaptureHooks.RaisePerfReport(
                new PerfReport(TicksToMs(Stopwatch.GetTimestamp() - _t0), w, h, _perfZones, _perfPhases,
                    _sub.ToEntries(s_perfSubPhaseNames)));
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
            => BoxDownsampleRegion(src, srcW, 0, 0, srcW, srcH, dstW, dstH, scale);

        /// <summary><see cref="BoxDownsample(Color32[], int, int, int, int, float)"/> のポインタ版(src は srcW*srcH 画素)。</summary>
        public static Color32[] BoxDownsample(Color32* src, int srcW, int srcH,
            int dstW, int dstH, float scale)
            => BoxDownsampleRegion(src, (long)srcW * srcH, srcW, 0, 0, srcW, srcH, dstW, dstH, scale);

        /// <summary>
        /// <see cref="BoxDownsample"/> の、src の一部(左下 (x0, y0)・regionW×regionH、1 行 srcStride 画素)を
        /// 切り出してから縮小したのと同じ結果を、切り出しの配列を作らずに返す。拡大表示のクロップが
        /// フル解像度の出力から直接縮小するのに使う(4K の 125% 表示でクロップ 1 枚 ~43MB の複製を避ける)。
        /// </summary>
        public static Color32[] BoxDownsampleRegion(Color32[] src, int srcStride, int x0, int y0,
            int regionW, int regionH, int dstW, int dstH, float scale)
        {
            if (src == null) throw new System.ArgumentNullException(nameof(src));
            fixed (Color32* p = src)
                return BoxDownsampleRegion(p, src.Length, srcStride, x0, y0, regionW, regionH, dstW, dstH, scale);
        }

        /// <summary>
        /// <see cref="BoxDownsampleRegion(Color32[], int, int, int, int, int, int, int, float)"/> のポインタ版。
        /// srcLength は src の画素数(読む範囲がその中に収まるかを確かめる)。
        /// </summary>
        public static Color32[] BoxDownsampleRegion(Color32* src, long srcLength, int srcStride, int x0, int y0,
            int regionW, int regionH, int dstW, int dstH, float scale)
        {
            // scale と dst 寸法の整合を呼び出し側の契約に丸投げしており、不整合だと
            // src の範囲外を読んで IndexOutOfRange になっていた（レビュー §4 低）。
            // 読み出し位置を src 範囲にクランプして、契約違反を例外でなく劣化で吸収する。
            if (src == null) throw new System.ArgumentNullException(nameof(src));
            int srcW = regionW, srcH = regionH;
            if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0 || scale <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(scale),
                    $"invalid downsample params: src={srcW}x{srcH} dst={dstW}x{dstH} scale={scale}");
            if (x0 < 0 || y0 < 0 || x0 + srcW > srcStride)
                throw new System.ArgumentOutOfRangeException(nameof(x0),
                    $"invalid region: origin=({x0},{y0}) size={srcW}x{srcH} stride={srcStride}");
            if (srcLength < (long)(y0 + srcH - 1) * srcStride + x0 + srcW)
                throw new System.ArgumentException(
                    $"src.Length({srcLength}) < region end({(long)(y0 + srcH - 1) * srcStride + x0 + srcW})", nameof(src));

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
                    {
                        int rowBase = (y0 + ky) * srcStride + x0;
                        for (int kx = sx0; kx <= sx1; kx++)
                        {
                            var p = src[rowBase + kx];
                            r += p.r; g += p.g; b += p.b; a += p.a;
                            count++;
                        }
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
