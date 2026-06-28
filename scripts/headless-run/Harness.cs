// 実 Unity DLL を参照して PixelProcessor を Unity なしで実行する CLI ハーネス。
// Code/ と同一アセンブリにコンパイルされるので internal にアクセスできる。
using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using UnityEngine;

namespace Iroca
{
    // DebugCaptureHooks が UI イベントで参照するだけのスタブ(headless では未使用)。
    internal class IrocaWindow { }

    // ZoneAutoTuner がラベル収集で参照する Localization のスタブ。
    // 実 Localization は UnityEditor.EditorPrefs に依存するため headless では使えない。
    // 自動調整の数値計算には影響しない（overwrittenLabels の文字列に使われるだけ）。
    internal static class Localization
    {
        public static string Tolerance               => "Tolerance";
        public static string SaturationStrictness    => "SaturationStrictness";
        public static string SaturationGuard         => "SaturationGuard";
        public static string ChromaThreshold         => "ChromaThreshold";
        public static string HighlightRecovery       => "HighlightRecovery";
        public static string PatternPreserve         => "PatternPreserve";
        public static string EdgeSoftness            => "EdgeSoftness";
        public static string ShadowDesaturation      => "ShadowDesaturation";
        public static string ShadowForgivenessSatMin => "ShadowForgivenessSatMin";
        public static string AntiAliasCleanup        => "AntiAliasCleanup";
        public static string UseDecontamination      => "UseDecontamination";
    }

    // ─── --zones <json> 用の簡易設定 DTO(System.Text.Json, Unity 非依存) ───
    // Python(headless_io.write_zones_json)が組み立てるフラットなスキーマ。色は [r,g,b] 0..1。
    // 欠落フィールドは既定値(=従来 Harness のハードコード値)にフォールバックする。
    internal sealed class ZonesConfig
    {
        public List<ZoneCfg> zones { get; set; } = new List<ZoneCfg>();
        public SettingsCfg settings { get; set; } = new SettingsCfg();
    }

    internal sealed class ZoneCfg
    {
        public string name { get; set; } = "Zone";
        public float[] sample { get; set; } = new[] { 1f, 1f, 1f };
        // 追加スポイト（マルチサンプル選択）。各要素は [r,g,b] 0..1。未指定=null=単一サンプル。
        public float[][] samples { get; set; } = null;
        public float[] target { get; set; } = new[] { 0f, 0f, 0f };
        public float tolerance { get; set; } = 0.2f;
        public float valueBlend { get; set; } = 1.0f;
        public float edgeSoftness { get; set; } = 0.0f;
        public float saturationStrictness { get; set; } = 0.5f;
        public float saturationGuard { get; set; } = 0.0f;
        public float chromaThreshold { get; set; } = 0.05f;
        public float shadowDesaturation { get; set; } = 0.35f;
        public float shadowForgivenessSatMin { get; set; } = 0.05f;
        public float outputSaturation { get; set; } = 1.0f;
        public bool highlightRecovery { get; set; } = false;
        public bool highlightBandExpand { get; set; } = true;
        public bool applyHighlightWash { get; set; } = false;
        // 既定 ON(ColorZone.autoRecolorAnchor と同既定)。JSON で false にすれば従来のクリック画素
        // アンカー挙動。ON/OFF を JSON から切り替えて A/B 計測できるようにフィールドを公開する。
        public bool autoRecolorAnchor { get; set; } = true;
        public int layerIndex { get; set; } = 0;
        // 連続領域モード(連結成分アンカリング)。useFloodFill=true で有効。
        // seedUV=[u,v] は任意の上書きシード(未指定=null=自動アンカリング)。
        public bool useFloodFill { get; set; } = false;
        public float[] seedUV { get; set; } = null;
        public float edgeStopThreshold { get; set; } = 0.15f;
    }

    internal sealed class SettingsCfg
    {
        public float edgeFeather { get; set; } = 0.0f;
        public int antiAliasCleanup { get; set; } = 3;
        public int holeFillPasses { get; set; } = 5;
        public int holeFillMinNeighbors { get; set; } = 4;
        public float relaxedSatMin { get; set; } = 0.02f;
        public float relaxedSatRamp { get; set; } = 0.08f;
        public bool useDecontamination { get; set; } = true;
        public int decontaminationRadius { get; set; } = 4;
    }

    internal static class Harness
    {
        private static (int w, int h, byte[] payload) ReadRaw(string path, int bpp)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);
            int w = br.ReadInt32();
            int h = br.ReadInt32();
            byte[] payload = br.ReadBytes(w * h * bpp);
            return (w, h, payload);
        }

        private static Color Col(float[] c) =>
            new Color(c != null && c.Length > 0 ? c[0] : 0f,
                      c != null && c.Length > 1 ? c[1] : 0f,
                      c != null && c.Length > 2 ? c[2] : 0f, 1f);

        private static List<Color> BuildExtraSamples(float[][] samples)
        {
            var list = new List<Color>();
            if (samples != null)
                foreach (var s in samples)
                    if (s != null && s.Length >= 3) list.Add(Col(s));
            return list;
        }

        private static ColorZone BuildZone(ZoneCfg z)
        {
            var zone = new ColorZone
            {
                name = z.name,
                enabled = true,
                mode = SelectionMode.ColorPick,
                sampleColor = Col(z.sample),
                extraSamples = BuildExtraSamples(z.samples),
                targetColor = Col(z.target),
                tolerance = z.tolerance,
                valueBlend = z.valueBlend,
                edgeSoftness = z.edgeSoftness,
                saturationStrictness = z.saturationStrictness,
                saturationGuard = z.saturationGuard,
                chromaThreshold = z.chromaThreshold,
                highlightRecovery = z.highlightRecovery,
                highlightBandExpand = z.highlightBandExpand,
                applyHighlightWash = z.applyHighlightWash,
                autoHighlightSample = false,
                autoRecolorAnchor = z.autoRecolorAnchor,
                outputSaturation = z.outputSaturation,
                shadowDesaturation = z.shadowDesaturation,
                shadowForgivenessSatMin = z.shadowForgivenessSatMin,
                layerIndex = z.layerIndex,
                useFloodFill = z.useFloodFill,
                seedUV = (z.seedUV != null && z.seedUV.Length >= 2)
                    ? new Vector2(z.seedUV[0], z.seedUV[1]) : new Vector2(-1f, -1f),
                edgeStopThreshold = z.edgeStopThreshold,
            };
            zone.EnsureId();
            zone.UpdateCacheIfNeeded();
            return zone;
        }

        public static int Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: Harness <in.raw RGBA> <mask.raw 1=exclude> <out.raw RGBA> "
                    + "[sampleR sampleG sampleB targetR targetG targetB tolerance | --zones zones.json]");
                return 2;
            }
            string inPath = args[0], maskPath = args[1], outPath = args[2];

            // --zones <path> があれば全ゾーン設定を JSON から読む(汎用パス)。
            string zonesPath = null;
            for (int i = 3; i < args.Length - 1; i++)
                if (args[i] == "--zones") { zonesPath = args[i + 1]; break; }

            // --autotune: 各ゾーンの (sample,target) から ZoneAutoTuner を no-mask で走らせ、
            // 導出パラメータを適用してから処理する（かんたんモードの自動実行を再現）。
            bool autotune = false;
            for (int i = 3; i < args.Length; i++)
                if (args[i] == "--autotune") { autotune = true; break; }

            var (w, h, rgba) = ReadRaw(inPath, 4);
            int len = w * h;
            var pixels = new Color32[len];
            for (int i = 0; i < len; i++)
                pixels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);

            var (mw, mh, mbytes) = ReadRaw(maskPath, 1);
            var common = new bool[mw * mh];
            for (int i = 0; i < common.Length; i++) common[i] = mbytes[i] != 0;
            // MaskSnapshot は packed ulong[]。autotune/exCount は下で bool[] common を使うので両方保持する。
            var masks = new MaskSnapshot { common = MaskSnapshot.Pack(common), width = mw, height = mh, zones = null };

            List<ColorZone> zoneList;
            SettingsCfg st;
            if (zonesPath != null)
            {
                var cfg = JsonSerializer.Deserialize<ZonesConfig>(
                    File.ReadAllText(zonesPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                st = cfg.settings ?? new SettingsCfg();
                zoneList = new List<ColorZone>();
                foreach (var z in cfg.zones) zoneList.Add(BuildZone(z));
            }
            else
            {
                // 後方互換: 位置引数 sample/target/tolerance(無ければユーザー実設定 クリーム→黒, tol 0.32)。
                var z = new ZoneCfg
                {
                    name = "Bandana-Triangle",
                    sample = new[] { 252 / 255f, 247 / 255f, 243 / 255f },
                    target = new[] { 0f, 0f, 0f },
                    tolerance = 0.32f,
                };
                if (args.Length >= 10)
                {
                    z.sample = new[] { int.Parse(args[3]) / 255f, int.Parse(args[4]) / 255f, int.Parse(args[5]) / 255f };
                    z.target = new[] { int.Parse(args[6]) / 255f, int.Parse(args[7]) / 255f, int.Parse(args[8]) / 255f };
                    z.tolerance = float.Parse(args[9]);
                }
                st = new SettingsCfg();
                zoneList = new List<ColorZone> { BuildZone(z) };
            }

            // 自動調整: 各ゾーンの推奨値を導出して適用する（かんたんモード/手動 Auto-Tune 相当）。
            // mask.raw に除外指定があれば実機 RunAutoTune と同様にマスクを渡す(マスク認識経路の検証用)。
            if (autotune)
            {
                int exCount = 0;
                for (int k = 0; k < common.Length; k++) if (common[k]) exCount++;
                bool useMask = exCount > 0 && exCount < common.Length;
                var session = IrocaSessionState.CreateDefault();
                foreach (var z in zoneList)
                {
                    var tune = useMask
                        ? ZoneAutoTuner.Analyze(pixels, w, h, z, session, common, mw, mh)
                        : ZoneAutoTuner.Analyze(pixels, w, h, z, session, excluded: null, maskW: 0, maskH: 0);
                    z.tolerance               = tune.tolerance;
                    z.saturationStrictness    = tune.saturationStrictness;
                    z.saturationGuard         = tune.saturationGuard;
                    z.chromaThreshold         = tune.chromaThreshold;
                    z.highlightRecovery       = tune.highlightRecovery;
                    z.valueBlend              = tune.valueBlend;
                    z.edgeSoftness            = tune.edgeSoftness;
                    z.shadowDesaturation      = tune.shadowDesaturation;
                    z.shadowForgivenessSatMin = tune.shadowForgivenessSatMin;
                    // 自動トーン抽出で得た内部サンプル（暗部/中間/明部の代表色）を適用する。
                    // これにより --autotune は実機 RunAutoTune と同じ「ユーザー操作なしの内部マルチサンプル」
                    // 挙動を再現する（テストが automatic な選択を直接測れる）。
                    z.extraSamples = tune.autoSamples ?? new List<Color>();
                    if (tune.applyGlobals)
                    {
                        st.antiAliasCleanup   = tune.antiAliasCleanup;
                        st.useDecontamination = tune.useDecontamination;
                    }
                    z.UpdateCacheIfNeeded();
                    // 導出値を stderr に JSON で出す（stdout の "OK" を汚さない）。Python が拾って記録する。
                    Console.Error.WriteLine("AUTOTUNE " + JsonSerializer.Serialize(new
                    {
                        name = z.name,
                        tolerance = z.tolerance,
                        saturationStrictness = z.saturationStrictness,
                        saturationGuard = z.saturationGuard,
                        chromaThreshold = z.chromaThreshold,
                        highlightRecovery = z.highlightRecovery,
                        valueBlend = z.valueBlend,
                        edgeSoftness = z.edgeSoftness,
                        shadowDesaturation = z.shadowDesaturation,
                        shadowForgivenessSatMin = z.shadowForgivenessSatMin,
                        applyGlobals = tune.applyGlobals,
                        antiAliasCleanup = st.antiAliasCleanup,
                        autoSamples = z.extraSamples.Count,
                    }));
                }
            }

            // --ffcheck: 連結keepのクロップ転写がフル画像と一致するか自己検証(M4 完全一致プレビュー)。
            // フル画像で keep を解いてキャッシュ→同じクロップを (a)キャッシュあり (b)なし で処理し、
            // クロップ内部(境界マージン除外)をフルのクロップ領域と比較する。元入力 pixels は未改変の
            // クローンを使い、本処理に影響しない。
            if (System.Array.IndexOf(args, "--ffcheck") >= 0)
                RunFloodFillCropCheck((Color32[])pixels.Clone(), w, h, masks, zoneList, st);

            // ProcessPixelsArray のみを計測(dotnet 起動・raw I/O を除外)。stderr に出すので
            // stdout の "OK" を汚さない。Python 側が "PROCESS_MS " 行を拾って前後比較に使う。
            var _sw = Stopwatch.StartNew();
            PixelProcessor.ProcessPixelsArray(
                pixels, w, h, masks, zoneList,
                edgeFeather: st.edgeFeather, antiAliasCleanup: st.antiAliasCleanup,
                holeFillPasses: st.holeFillPasses, holeFillMinNeighbors: st.holeFillMinNeighbors,
                relaxedSatMin: st.relaxedSatMin, relaxedSatRamp: st.relaxedSatRamp,
                originX: 0, originY: 0, fullW: 0, fullH: 0,
                useDecontamination: st.useDecontamination, decontaminationRadius: st.decontaminationRadius);
            _sw.Stop();
            Console.Error.WriteLine($"PROCESS_MS {_sw.Elapsed.TotalMilliseconds:F2}");

            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(w); bw.Write(h);
                var outBytes = new byte[len * 4];
                for (int i = 0; i < len; i++)
                {
                    outBytes[i * 4] = pixels[i].r; outBytes[i * 4 + 1] = pixels[i].g;
                    outBytes[i * 4 + 2] = pixels[i].b; outBytes[i * 4 + 3] = pixels[i].a;
                }
                bw.Write(outBytes);
            }
            Console.WriteLine($"OK {w}x{h} -> {outPath} (zones={zoneList.Count})");
            return 0;
        }

        // 連結keep のクロップ転写検証(M4)。フル画像で keep をキャッシュし、中央クロップを
        // (a)キャッシュあり (b)なし で処理。クロップ内部(境界マージン除外)をフルのクロップ領域と
        // 比較し、(a)が一致・(b)が過選択(上位集合)であることを stderr に出す。
        private static void RunFloodFillCropCheck(
            Color32[] input, int w, int h, MaskSnapshot masks, List<ColorZone> zones, SettingsCfg st)
        {
            void Process(Color32[] px, int pw, int ph, int ox, int oy, int fw, int fh, PreviewParityCache keep)
            {
                PixelProcessor.ProcessPixelsArray(px, pw, ph, masks, zones,
                    edgeFeather: st.edgeFeather, antiAliasCleanup: st.antiAliasCleanup,
                    holeFillPasses: st.holeFillPasses, holeFillMinNeighbors: st.holeFillMinNeighbors,
                    relaxedSatMin: st.relaxedSatMin, relaxedSatRamp: st.relaxedSatRamp,
                    originX: ox, originY: oy, fullW: fw, fullH: fh,
                    cancellationToken: System.Threading.CancellationToken.None,
                    useDecontamination: st.useDecontamination, decontaminationRadius: st.decontaminationRadius,
                    parityCache: keep);
            }

            // 1) フル画像で処理し keep/領域統計をキャッシュ。
            var full = (Color32[])input.Clone();
            var cache = new PreviewParityCache { generation = 1 };
            Process(full, w, h, 0, 0, 0, 0, cache);

            // 2) 中央クロップ(画像の半分)を (a)キャッシュあり (b)なし で処理。
            int cx0 = w / 4, cy0 = h / 4, cw = w / 2, ch = h / 2;
            Color32[] MakeCrop()
            {
                var c = new Color32[cw * ch];
                for (int cy = 0; cy < ch; cy++)
                    System.Array.Copy(input, (cy0 + cy) * w + cx0, c, cy * cw, cw);
                return c;
            }
            var cropCached = MakeCrop();
            var cropNoCache = MakeCrop();
            var swCached = Stopwatch.StartNew();
            Process(cropCached, cw, ch, cx0, cy0, w, h, cache);
            swCached.Stop();
            var swNoCache = Stopwatch.StartNew();
            Process(cropNoCache, cw, ch, cx0, cy0, w, h, null);
            swNoCache.Stop();
            Console.Error.WriteLine(
                $"FFCHECK_CROPMS cached={swCached.Elapsed.TotalMilliseconds:F1} "
                + $"noCache(recompute)={swNoCache.Elapsed.TotalMilliseconds:F1}");

            // 3) クロップ内部(境界 margin 除外)を full のクロップ領域と比較。
            int margin = st.holeFillPasses + System.Math.Max(0, st.antiAliasCleanup)
                         + st.decontaminationRadius + 4;
            bool Changed(Color32 a, Color32 b) => a.r != b.r || a.g != b.g || a.b != b.b;
            // 「選択(再着色されたか否か)」を full と比較する。値の差(再着色アンカー等の領域統計が
            // クロップ/フルで異なることによる)は flood fill keep の正否と無関係なので、選択集合の
            // XOR で keep 転写の正しさを切り分ける。
            int recFull = 0, recCached = 0, recNoCache = 0, interior = 0;
            int selCachedExtra = 0, selCachedMiss = 0, selNoCacheExtra = 0, selNoCacheMiss = 0;
            // 色のズレ計測(選択が一致した画素に限定)。keep 転写で選択は一致するが、autoRecolorAnchor /
            // wash サンプルが「クロップ領域の統計」から導出されるためフルと出力色が乖離する=ズーム/
            // スクロールで色が変わる症状を定量化する。
            long colDeltaSum = 0; int colDeltaMax = 0; int colDeltaCount = 0; int colDeltaOver8 = 0;
            for (int cy = margin; cy < ch - margin; cy++)
            {
                for (int cx = margin; cx < cw - margin; cx++)
                {
                    interior++;
                    int ci = cy * cw + cx;
                    int fi = (cy0 + cy) * w + (cx0 + cx);
                    Color32 src = input[fi];
                    bool rF = Changed(full[fi], src);
                    bool rC = Changed(cropCached[ci], src);
                    bool rN = Changed(cropNoCache[ci], src);
                    if (rF) recFull++;
                    if (rC) recCached++;
                    if (rN) recNoCache++;
                    if (rC && !rF) selCachedExtra++;   // cache がフルより多く選択(=keep転写漏れ)
                    if (!rC && rF) selCachedMiss++;     // cache がフルより少なく選択(=過剰除去)
                    if (rN && !rF) selNoCacheExtra++;   // cacheなしの過選択(=上位集合の超過分)
                    if (!rN && rF) selNoCacheMiss++;
                    // 選択がフルと一致した画素だけの色差(|dr|+|dg|+|db|)。
                    if (rF && rC)
                    {
                        int d = System.Math.Abs(full[fi].r - cropCached[ci].r)
                              + System.Math.Abs(full[fi].g - cropCached[ci].g)
                              + System.Math.Abs(full[fi].b - cropCached[ci].b);
                        colDeltaSum += d; colDeltaCount++;
                        if (d > colDeltaMax) colDeltaMax = d;
                        if (d > 8) colDeltaOver8++;
                    }
                }
            }
            double colMean = colDeltaCount > 0 ? (double)colDeltaSum / colDeltaCount : 0.0;
            Console.Error.WriteLine(
                $"FFCHECK interior={interior} recFull={recFull} recCached={recCached} recNoCache={recNoCache} | "
                + $"cachedVsFull selXOR(extra/miss)={selCachedExtra}/{selCachedMiss} | "
                + $"noCacheVsFull selXOR(extra/miss)={selNoCacheExtra}/{selNoCacheMiss} | "
                + $"colorDelta(mean/max/over8 of {colDeltaCount})={colMean:F2}/{colDeltaMax}/{colDeltaOver8}");
        }
    }
}
