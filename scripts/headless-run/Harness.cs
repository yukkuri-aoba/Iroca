// 実 Unity DLL を参照して PixelProcessor を Unity なしで実行する CLI ハーネス。
// Code/ と同一アセンブリにコンパイルされるので internal にアクセスできる。
using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    // DebugCaptureHooks が UI イベントで参照するだけのスタブ(headless では未使用)。
    internal class VACCWindow { }

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
        public int layerIndex { get; set; } = 0;
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

        private static ColorZone BuildZone(ZoneCfg z)
        {
            var zone = new ColorZone
            {
                name = z.name,
                enabled = true,
                mode = SelectionMode.ColorPick,
                sampleColor = Col(z.sample),
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
                autoRecolorAnchor = false,
                outputSaturation = z.outputSaturation,
                shadowDesaturation = z.shadowDesaturation,
                shadowForgivenessSatMin = z.shadowForgivenessSatMin,
                layerIndex = z.layerIndex,
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
                var session = VACCSessionState.CreateDefault();
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
                    }));
                }
            }

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
    }
}
