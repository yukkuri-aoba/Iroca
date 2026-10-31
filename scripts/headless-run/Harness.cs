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
    // ─── --zones <json> 用の簡易設定 DTO(System.Text.Json, Unity 非依存) ───
    // Python(headless_io.write_zones_json)が組み立てるフラットなスキーマ。色は [r,g,b] 0..1。
    // 欠落フィールドは既定値(=従来 Harness のハードコード値)にフォールバックする。
    internal sealed class ZonesConfig
    {
        public List<ZoneCfg> zones { get; set; } = new List<ZoneCfg>();
        public SettingsCfg settings { get; set; } = new SettingsCfg();
    }

    // 既定値は ZonesJsonDefaults が唯一の正。ここにリテラルを書き戻さないこと
    // (IrocaAutomation.ZoneDto と乖離し、テストが製品でない挙動を測る事故になる)。
    internal sealed class ZoneCfg
    {
        public string name { get; set; } = ZonesJsonDefaults.Name;
        public float[] sample { get; set; } = ZonesJsonDefaults.NewSample();
        // 追加スポイト（マルチサンプル選択）。各要素は [r,g,b] 0..1。未指定=null=単一サンプル。
        // ★ハーネス専用フィールド★ — 製品側 ZoneDto には対応が無い。UnityEngine.JsonUtility が
        // 配列の配列(float[][])をデシリアライズできないため製品 DTO では表現できず、
        // ColorZone.extraSamples は本来 ZoneAutoTuner が自動生成するもので JSON 入力を想定しない。
        // 現在どのテストからも使われていない。test_zones_schema_parity.py の除外リスト参照。
        public float[][] samples { get; set; } = null;
        public float[] target { get; set; } = ZonesJsonDefaults.NewTarget();
        public float tolerance { get; set; } = ZonesJsonDefaults.Tolerance;
        public float valueBlend { get; set; } = ZonesJsonDefaults.ValueBlend;
        public float edgeSoftness { get; set; } = ZonesJsonDefaults.EdgeSoftness;
        public float saturationStrictness { get; set; } = ZonesJsonDefaults.SaturationStrictness;
        public float saturationGuard { get; set; } = ZonesJsonDefaults.SaturationGuard;
        public float chromaThreshold { get; set; } = ZonesJsonDefaults.ChromaThreshold;
        public float shadowDesaturation { get; set; } = ZonesJsonDefaults.ShadowDesaturation;
        public float shadowForgivenessSatMin { get; set; } = ZonesJsonDefaults.ShadowForgivenessSatMin;
        public float shadowValueFloor { get; set; } = ZonesJsonDefaults.ShadowValueFloor;
        public float partSatCeiling { get; set; } = ZonesJsonDefaults.PartSatCeiling;
        public float partHueBand { get; set; } = ZonesJsonDefaults.PartHueBand;
        public float chromaCeiling { get; set; } = ZonesJsonDefaults.ChromaCeiling;
        public float holeFillMargin { get; set; } = ZonesJsonDefaults.HoleFillMargin;
        public float outputSaturation { get; set; } = ZonesJsonDefaults.OutputSaturation;
        public bool highlightRecovery { get; set; } = ZonesJsonDefaults.HighlightRecovery;
        public bool highlightBandExpand { get; set; } = ZonesJsonDefaults.HighlightBandExpand;
        public bool applyHighlightWash { get; set; } = ZonesJsonDefaults.ApplyHighlightWash;
        public bool autoHighlightSample { get; set; } = ZonesJsonDefaults.AutoHighlightSample;
        public bool autoRecolorAnchor { get; set; } = ZonesJsonDefaults.AutoRecolorAnchor;
        public int layerIndex { get; set; } = ZonesJsonDefaults.LayerIndex;
        // 連続領域モード(連結成分アンカリング)。seedUV=[u,v] は任意の上書きシード
        // (未指定=null=自動アンカリング)。
        public bool useFloodFill { get; set; } = ZonesJsonDefaults.UseFloodFill;
        public float[] seedUV { get; set; } = null;
        // スポイト位置 [u,v](未指定=null=位置なし)。自動調整の証拠はハーネスでは evidenceMask で
        // 渡すので、ここでは「この位置を含む連結成分は包絡ゲートで落とさない」ためにだけ効く。
        public float[] sampleUV { get; set; } = null;
        // ゾーン別の含めるマスク(raw ファイルパス, [int32 w][int32 h][w*h bytes], 1=含める)。
        // 寸法は mask.raw と一致必須。未指定=null=含めるマスクなし。
        // ★ハーネス専用フィールド★ — 製品の含めるマスクは MaskFileStore / プリセット経由で、
        // zones JSON 経路(MCP/batchmode)は v1 でマスク自体を適用しない(IrocaAutomation の警告参照)。
        // テストから含めるマスク経路を駆動するための入力。test_zones_schema_parity.py の
        // HARNESS_ONLY 参照。
        public string includeMask { get; set; } = null;
        // ゾーン別の除外マスク(raw ファイルパス、形式・寸法制約は includeMask と同じ。1=除外)。
        // ★ハーネス専用フィールド★ — 製品ではマスクは MaskFileStore/セッション経由で zones JSON に
        // 載らない。再現ケース(repro_cases)とテストが MaskSnapshot.zones(ゾーン別除外)経路を
        // 駆動するための入力。
        public string excludeMask { get; set; } = null;
        // 自動調整の証拠マスク(raw ファイルパス、形式は includeMask と同じ。1=このゾーンの素材)。
        // ★ハーネス専用フィールド★ — 製品では AI マスク提案のセグメントが UI 経由で
        // ZoneAutoTuner.AnalyzeWithEvidence へ渡る想定で、zones JSON には載らない。
        // --autotune のときだけ解釈され、証拠つき導出(セグメント教師)を駆動する。
        // 通常実行(選択・再着色)には一切影響しない。
        public string evidenceMask { get; set; } = null;
    }

    internal sealed class SettingsCfg
    {
        public float edgeFeather { get; set; } = ZonesJsonDefaults.EdgeFeather;
        public int antiAliasCleanup { get; set; } = ZonesJsonDefaults.AntiAliasCleanup;
        public int holeFillPasses { get; set; } = ZonesJsonDefaults.HoleFillPasses;
        public int holeFillMinNeighbors { get; set; } = ZonesJsonDefaults.HoleFillMinNeighbors;
        public float relaxedSatMin { get; set; } = ZonesJsonDefaults.RelaxedSatMin;
        public float relaxedSatRamp { get; set; } = ZonesJsonDefaults.RelaxedSatRamp;
        public bool useDecontamination { get; set; } = ZonesJsonDefaults.UseDecontamination;
        public int decontaminationRadius { get; set; } = ZonesJsonDefaults.DecontaminationRadius;

        // 製品と同じ RecolorSettings へ写す（ProcessPixelsArray の入口はこれだけ）。
        public RecolorSettings ToRecolorSettings() => new RecolorSettings(
            edgeFeather, antiAliasCleanup,
            holeFillPasses, holeFillMinNeighbors,
            relaxedSatMin, relaxedSatRamp,
            useDecontamination, decontaminationRadius);
    }

    // ─── --batch <json> 用 DTO ───
    // 同じテクスチャに対する複数ケースを 1 プロセスで処理するための入れ物。
    // 1 ケースごとに dotnet を起動すると、起動 + 入力 raw の読み直し(4096² で 64MB)が
    // ケース数ぶん積み上がる。回帰スイートは同じテクスチャを 5 色ぶん回すのでそこが丸損だった。
    // 処理そのものは単発実行と同じ ProcessZones() を通す(経路を分岐させない)。
    internal sealed class BatchCase
    {
        public string zones { get; set; }
        public string @out { get; set; }
    }

    internal sealed class BatchConfig
    {
        public string input { get; set; }
        public string mask { get; set; }
        public List<BatchCase> cases { get; set; } = new List<BatchCase>();
    }

    // ─── --session <json> 用 DTO ───
    // 連続適用セッション(設定変更・マスク塗り・ゾーン追加の往復)の再現。--batch と違い
    // ステップ間で SelectionCache を持ち越す(製品 PreviewView の _selectionCache 相当)。
    internal sealed class SessionStep
    {
        public string zones { get; set; }
        public string mask { get; set; }
        public string @out { get; set; }
    }

    internal sealed class SessionConfig
    {
        public string input { get; set; }
        public List<SessionStep> steps { get; set; } = new List<SessionStep>();
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

        // ─── raw 規約([int32 w][int32 h][payload]、行 0 = 下端)の読み書き ───
        // 画像は RGBA 4 バイト/画素、マスクは 1 バイト/画素(非 0 = true)。
        // 寸法の検査が要る呼び出し元は ReadRaw で読んで検査してから ToColor32 / ToBoolMask へ渡す。
        private static Color32[] ToColor32(byte[] rgba, int len)
        {
            var pixels = new Color32[len];
            for (int i = 0; i < len; i++)
                pixels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
            return pixels;
        }

        private static (int w, int h, Color32[] pixels) ReadRgbaRaw(string path)
        {
            var (w, h, rgba) = ReadRaw(path, 4);
            return (w, h, ToColor32(rgba, w * h));
        }

        private static bool[] ToBoolMask(byte[] bytes, int len)
        {
            var mask = new bool[len];
            for (int i = 0; i < mask.Length; i++) mask[i] = bytes[i] != 0;
            return mask;
        }

        private static (int w, int h, bool[] mask) ReadMaskRaw(string path)
        {
            var (w, h, bytes) = ReadRaw(path, 1);
            return (w, h, ToBoolMask(bytes, w * h));
        }

        private static void WriteMaskRaw(string path, int w, int h, bool[] mask)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write(w); bw.Write(h);
            var bytes = new byte[w * h];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = mask[i] ? (byte)1 : (byte)0;
            bw.Write(bytes);
        }

        // --zones / --batch / --session の JSON はキーの大文字小文字を区別せずに読む。
        private static readonly JsonSerializerOptions CaseInsensitiveJson =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private static unsafe int RunUvCharts(string dumpPath, string textureKey, int w, int h, string outPrefix)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(dumpPath));
            if (!doc.RootElement.TryGetProperty(textureKey, out var entry))
            {
                Console.Error.WriteLine("uvcharts: texture key not found: " + textureKey);
                return 2;
            }
            var sources = new List<UvChartSource>();
            foreach (var s in entry.GetProperty("slots").EnumerateArray())
            {
                if (!s.GetProperty("isMain").GetBoolean() || !s.TryGetProperty("tris", out var tris)) continue;
                var uvFlat = new List<float>();
                foreach (var v in s.GetProperty("uv").EnumerateArray()) uvFlat.Add(v.GetSingle());
                var uv = new Vector2[uvFlat.Count / 2];
                for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(uvFlat[i * 2], uvFlat[i * 2 + 1]);
                var triList = new List<int>();
                foreach (var t in tris.EnumerateArray()) triList.Add(t.GetInt32());
                var sc = s.GetProperty("scale");
                var of = s.GetProperty("offset");
                sources.Add(new UvChartSource
                {
                    name = s.GetProperty("renderer").GetString(),
                    uv = uv,
                    triangles = triList.ToArray(),
                    scale = new Vector2(sc[0].GetSingle(), sc[1].GetSingle()),
                    offset = new Vector2(of[0].GetSingle(), of[1].GetSingle()),
                });
            }
            var sw = Stopwatch.StartNew();
            using var map = UvChartMap.Build(w, h, sources);
            long buildMs = sw.ElapsedMilliseconds;
            sw.Restart();
            map.AssignPadding();
            long padMs = sw.ElapsedMilliseconds;

            void WriteInts(string path, int* data)
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
                using var bw = new BinaryWriter(fs);
                bw.Write(w); bw.Write(h);
                bw.Write(new ReadOnlySpan<byte>(data, w * h * sizeof(int)));
            }
            WriteInts(outPrefix + "_raster.raw", map.Raster);
            WriteInts(outPrefix + "_owner.raw", map.Owner);
            WriteInts(outPrefix + "_dist.raw", map.OwnerDistance);
            var names = new List<string>();
            foreach (var s in sources) names.Add(s.name);
            Console.WriteLine("UVCHARTS " + JsonSerializer.Serialize(new
            {
                charts = map.ChartCount,
                chartSource = map.ChartSource,
                sources = names,
                paddingRadius = map.DefaultPaddingRadius,
                buildMs,
                padMs,
            }));
            return 0;
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
                autoHighlightSample = z.autoHighlightSample,
                autoRecolorAnchor = z.autoRecolorAnchor,
                outputSaturation = z.outputSaturation,
                shadowDesaturation = z.shadowDesaturation,
                shadowForgivenessSatMin = z.shadowForgivenessSatMin,
                shadowValueFloor = z.shadowValueFloor,
                partSatCeiling = z.partSatCeiling,
                partHueBand = z.partHueBand,
                chromaCeiling = z.chromaCeiling,
                holeFillMargin = z.holeFillMargin,
                layerIndex = z.layerIndex,
                useFloodFill = z.useFloodFill,
                seedUV = (z.seedUV != null && z.seedUV.Length >= 2)
                    ? new Vector2(z.seedUV[0], z.seedUV[1]) : new Vector2(-1f, -1f),
                sampleUV = (z.sampleUV != null && z.sampleUV.Length >= 2)
                    ? new Vector2(z.sampleUV[0], z.sampleUV[1]) : new Vector2(-1f, -1f),
            };
            zone.EnsureId();
            zone.UpdateCacheIfNeeded();
            return zone;
        }

        public static int Main(string[] args)
        {
            // --selkey-audit: 入力画像不要の特別モード(選択キャッシュキーの網羅性監査)。
            if (args.Length >= 1 && args[0] == "--selkey-audit")
                return RunSelectionKeyAudit();

            // --previewselftest: 入力画像不要。RecolorPreview(自動化の目視検証パネル/メトリクス)を
            // 合成 before/after で実 C# 実行し、既知の不変量を検証する。
            if (args.Length >= 1 && args[0] == "--previewselftest")
                return RunPreviewSelfTest();

            // --nativememselftest: 入力画像不要。Mono のヒープの外に置いた作業配列・色の表が、処理の後に
            // ReleaseWorkBuffers で全部解放されるか(参照の数え漏れ・返し忘れが無いか)を確かめる。
            if (args.Length >= 1 && args[0] == "--nativememselftest")
                return RunNativeMemorySelfTest();

            // --maskcodecselftest: 入力画像不要。保存形式のマスクの書き込み・展開と、展開のときに中身の同じマスクは
            // 前の配列をそのまま使う規則(Undo/Redo の書き戻し)を確かめる。
            if (args.Length >= 1 && args[0] == "--maskcodecselftest")
                return RunMaskCodecSelfTest();

            // --bitmaskselftest: 入力画像不要。ヒープの外の 1 画素 1 ビットのマスク(NativeBitMask)と、保存形式(RLE)との
            // 直接の変換が、bool[] を経由する変換と同じ結果になるか。参照の数と書く前の写しの決まりも。
            if (args.Length >= 1 && args[0] == "--bitmaskselftest")
                return RunBitMaskSelfTest();

            // --samops-*: AI マスク提案の純計算層(SamImageOps/SamCoordMapper/SamMaskPostprocess/
            // MaskRle)を実 C# で駆動し、pytest の NumPy リファレンスと突き合わせる決定的検証モード。
            if (args.Length >= 1 && args[0].StartsWith("--samops-", StringComparison.Ordinal))
                return RunSamOps(args);

            // --previewcoords <rx> <ry> <rw> <rh> <sx> <sy> <texW> <texH>: 入力画像不要。
            // プレビュー座標変換(PreviewCoords: スポイト/シード/ペイント/AI クリックの単一の正)を
            // 実 C# で評価して uv・画素座標・スクリーン往復を出力する。v 反転の退行検証用。
            if (args.Length >= 9 && args[0] == "--previewcoords")
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                var rect = new Rect(
                    float.Parse(args[1], ic), float.Parse(args[2], ic),
                    float.Parse(args[3], ic), float.Parse(args[4], ic));
                var screen = new Vector2(float.Parse(args[5], ic), float.Parse(args[6], ic));
                int tw = int.Parse(args[7], ic), th = int.Parse(args[8], ic);
                var uv = PreviewCoords.ScreenToUv(screen, rect);
                PreviewCoords.UvToPixel(uv.x, uv.y, tw, th, out int px, out int py);
                var back = PreviewCoords.UvToScreen(uv, rect);
                Console.WriteLine(string.Format(ic,
                    "PREVIEWCOORDS {0:R} {1:R} {2} {3} {4:R} {5:R}",
                    uv.x, uv.y, px, py, back.x, back.y));
                return 0;
            }

            // --uvcharts <dump.json> <textureKey> <w> <h> <outPrefix>: 入力画像不要。UV ダンプ
            // (dev_safe の IrocaUvDump 形式)から UvChartMap を実 C# で作り、にじみ代を割り当てて
            // <outPrefix>_raster.raw / _owner.raw / _dist.raw(int32、行 0 = 下端)と要約 JSON を出す。
            if (args.Length >= 6 && args[0] == "--uvcharts")
                return RunUvCharts(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), args[5]);

            // 並列テスト実行時にスレッド数を絞れるようにする(既定=未設定=製品と同じ
            // PixelProcessor.DefaultParallelism)。設定したときだけ効くので、通常実行の挙動は変わらない。
            var threadsEnv = Environment.GetEnvironmentVariable("IROCA_HARNESS_THREADS");
            if (!string.IsNullOrEmpty(threadsEnv)
                && int.TryParse(threadsEnv, out int threads) && threads > 0)
                DebugCaptureHooks.ParallelismOverride = threads;

            // IROCA_NO_PALETTE=1: 色の表を作らず、全部の段を画素ごとの計算で回す(色の表あり・なしの
            // パリティ検査用。scripts/golden/test_palette_parity.py)。設定したときだけ効く。
            if (Environment.GetEnvironmentVariable("IROCA_NO_PALETTE") == "1")
                DebugCaptureHooks.DisablePalette = true;

            // --batch <json>: 同一入力・複数ゾーン設定をまとめて処理する(テスト実行の高速化)。
            if (args.Length >= 2 && args[0] == "--batch")
                return RunBatch(args[1]);

            // --session <json>: 連続適用セッション(SelectionCache をステップ間で持ち越す)。
            if (args.Length >= 2 && args[0] == "--session")
                return RunSession(args[1]);

            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: Harness <in.raw RGBA> <mask.raw 1=exclude> <out.raw RGBA> "
                    + "[sampleR sampleG sampleB targetR targetG targetB tolerance | --zones zones.json] | --selkey-audit");
                return 2;
            }
            string inPath = args[0], maskPath = args[1], outPath = args[2];

            // 未知の引数は黙って無視せず失敗させる。過去、パーサの無い --matchDistance が
            // 渡され続け、A/B 計測が同条件を 2 回測る「死にノブ」事故があった
            // (dev_safe/docs/oklab_matching_conclusion.md)。フラグを増やしたらここにも足すこと。
            for (int i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--zones": case "--stage": i++; break;   // 値を 1 つ消費
                    case "--autotune": case "--selcache": case "--selcache-mask": case "--viastate": break;
                    default:
                        // 後方互換の位置引数(sample/target/tolerance の数値 7 個)だけ許す。
                        if (i <= 9 && args.Length >= 10 && double.TryParse(
                                args[i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out _))
                            break;
                        Console.Error.WriteLine($"unknown arg: {args[i]}");
                        return 2;
                }
            }

            // --zones <path> があれば全ゾーン設定を JSON から読む(汎用パス)。
            string zonesPath = null;
            for (int i = 3; i < args.Length - 1; i++)
                if (args[i] == "--zones") { zonesPath = args[i + 1]; break; }

            // --autotune: 各ゾーンの (sample,target) から ZoneAutoTuner を no-mask で走らせ、
            // 導出パラメータを適用してから処理する（かんたんモードの自動実行を再現）。
            bool autotune = false;
            for (int i = 3; i < args.Length; i++)
                if (args[i] == "--autotune") { autotune = true; break; }

            // --stage proxy|full-display: 段階的リファインの表示画像を再現する検証モード。
            //   proxy        = ScheduleProxyPreview と同じ鎖（src→プロキシ縮小→処理→表示縮小）
            //   full-display = ScheduleFullPreview と同じ鎖（フル解像度処理→表示縮小）
            // どちらも出力は表示解像度（Preview.MaxSize フィット）。「ユーザーが操作中に見る絵」と
            // 「確定後に見る絵」を実 C# で取り出すためのモードで、通常実行（出力=フル解像度）の
            // 経路・出力は変えない。--batch では解釈しない（単発実行専用）。
            string stage = null;
            for (int i = 3; i < args.Length - 1; i++)
                if (args[i] == "--stage") { stage = args[i + 1]; break; }
            if (stage != null && stage != "proxy" && stage != "full-display")
            {
                Console.Error.WriteLine($"unknown --stage: {stage} (proxy|full-display)");
                return 2;
            }

            // --viastate: 非破壊ビルドと同じ入口(SessionRecolor.Apply)で処理する。ゾーン・設定・マスクを
            // 保存形式(IrocaSessionState + MaskState の RLE)へ詰め直してから流すので、raw マスク経路との
            // バイト一致で「保存形式を経由しても出力が変わらない」ことを検査できる。出力はフル解像度のみ。
            bool viaState = System.Array.IndexOf(args, "--viastate") >= 0;
            if (viaState && stage != null)
            {
                Console.Error.WriteLine("--viastate と --stage は併用できません");
                return 2;
            }

            var (w, h, pixels) = ReadRgbaRaw(inPath);

            var (mw, mh, common) = ReadMaskRaw(maskPath);
            // MaskSnapshot はヒープの外の層。autotune/exCount は下で bool[] common を使うので両方保持する。
            var masks = new MaskSnapshot { common = NativeBitMask.FromBools(common, mw, mh), width = mw, height = mh, zones = null };

            List<ColorZone> zoneList;
            SettingsCfg st;
            List<string> evidencePaths = null;
            if (zonesPath != null)
            {
                List<(string zoneId, string path, bool include)> zoneMasks;
                (zoneList, st, zoneMasks, evidencePaths) = LoadZones(zonesPath);
                AttachZoneMasks(masks, zoneMasks);
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
                for (int zi = 0; zi < zoneList.Count; zi++)
                {
                    var z = zoneList[zi];
                    // 証拠マスク(zones JSON の evidenceMask)があれば証拠つき導出(セグメント教師)。
                    bool[] evidence = null;
                    int evW = 0, evH = 0;
                    string evPath = (evidencePaths != null && zi < evidencePaths.Count)
                        ? evidencePaths[zi] : null;
                    if (!string.IsNullOrEmpty(evPath))
                        (evW, evH, evidence) = ReadMaskRaw(evPath);
                    // セッションから読むものは、前のゾーンの適用(全体設定の書き込み)の後の値(製品と同じ)。
                    var sessionInput = ZoneAutoTuner.SessionInput.From(session);
                    var tune = evidence != null
                        ? ZoneAutoTuner.AnalyzeWithEvidence(pixels, w, h, z, sessionInput,
                            evidence, evW, evH,
                            excluded: useMask ? common : null,
                            maskW: useMask ? mw : 0, maskH: useMask ? mh : 0)
                        : useMask
                            ? ZoneAutoTuner.Analyze(pixels, w, h, z, sessionInput, common, mw, mh)
                            : ZoneAutoTuner.Analyze(pixels, w, h, z, sessionInput, excluded: null, maskW: 0, maskH: 0);
                    // 導出結果 → ゾーン/グローバルの写像は製品 UI と同じ TuneResult.ApplyTo
                    // （正規化サンプル・内部マルチサンプル・globals を含む。ここに項目を並べ直さない）。
                    // globals は製品と同じくセッションへ書かれるので、ハーネスの設定 DTO へ写し戻す。
                    tune.ApplyTo(z, session);
                    if (tune.applyGlobals)
                    {
                        st.antiAliasCleanup   = session.antiAliasCleanup;
                        st.useDecontamination = session.useDecontamination;
                    }
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
                        shadowValueFloor = z.shadowValueFloor,
                        partSatCeiling = z.partSatCeiling,
                        partHueBand = z.partHueBand,
                        chromaCeiling = z.chromaCeiling,
                        holeFillMargin = z.holeFillMargin,
                        applyGlobals = tune.applyGlobals,
                        antiAliasCleanup = st.antiAliasCleanup,
                        autoSamples = z.extraSamples.Count,
                        // スポイト位置の正規化が効いたか（true のとき sample は代表地色へ差し替わっている）。
                        normalized = tune.hasNormalizedSample,
                        // 証拠つき導出(セグメント教師)が使われたか。診断は evidenceDiag(null=従来)。
                        evidence = evidence != null,
                        evidenceDiag = tune.evidenceDiag,
                        sample = new[] { z.sampleColor.r, z.sampleColor.g, z.sampleColor.b },
                        // 診断用: 自動トーン抽出の実色。アブレーション計測(--autotune なしで
                        // 同一パラメータを再現する)に必要。
                        autoSampleColors = z.extraSamples.ConvertAll(c => new[] { c.r, c.g, c.b }),
                    }));
                }
            }

            // --selcache: 選択キャッシュのヒット経路が「フル再計算」と byte 一致するか自己検証する。
            // --selcache-mask はそれに続けて、マスクだけを変えた再生成も確かめる。
            // 元入力 pixels は未改変クローンを使い、ゾーンも複製するので本処理に影響しない。
            bool selCacheMask = System.Array.IndexOf(args, "--selcache-mask") >= 0;
            if (selCacheMask || System.Array.IndexOf(args, "--selcache") >= 0)
                RunSelectionCacheCheck((Color32[])pixels.Clone(), w, h, masks, zoneList, st, selCacheMask);

            // 段階的リファインの表示画像を出力して終了（--stage。上記コメント参照）。
            // 自動調整は実機同様フル解像度の pixels で済ませてから（この上のブロック）、
            // プロキシ縮小・処理・表示縮小を PreviewPipeline と同一の鎖・丸めで行う。
            if (stage != null)
            {
                PixelProcessor.ComputeFitSize(w, h, IrocaConsts.Preview.MaxSize,
                    out int prevW, out int prevH, out float dispScale);
                Color32[] display;
                if (stage == "proxy")
                {
                    PixelProcessor.ComputeFitSize(w, h, IrocaConsts.Preview.ProxyMaxSize,
                        out int proxyW, out int proxyH, out float proxyScale);
                    var proxyPixels = PixelProcessor.BoxDownsample(
                        pixels, w, h, proxyW, proxyH, proxyScale);
                    // ScheduleProxyPreview と同じく、処理前のプロキシの控えを Core の入力として渡す。
                    var proxyRaw = (Color32[])proxyPixels.Clone();
                    ProcessZones(proxyPixels, proxyW, proxyH, masks, zoneList, st, source: proxyRaw);
                    // ScheduleProxyPreview と同じ: プロキシ寸法≠表示寸法のときだけ再縮小。
                    bool needsResample = proxyW != prevW || proxyH != prevH;
                    float toDisplay = prevW / (float)proxyW;
                    display = needsResample
                        ? PixelProcessor.BoxDownsample(
                            proxyPixels, proxyW, proxyH, prevW, prevH, toDisplay)
                        : proxyPixels;
                }
                else
                {
                    // ScheduleFullPreview と同じく、写し元のソース画素を Core の入力として渡す。
                    var srcPixels = (Color32[])pixels.Clone();
                    ProcessZones(pixels, w, h, masks, zoneList, st, source: srcPixels);
                    display = dispScale < 1f
                        ? PixelProcessor.BoxDownsample(pixels, w, h, prevW, prevH, dispScale)
                        : pixels;
                }
                WriteRawRgba(outPath, prevW, prevH, display);
                Console.WriteLine(
                    $"OK-STAGE {stage} {prevW}x{prevH} -> {outPath} (zones={zoneList.Count})");
                return 0;
            }

            // ProcessPixelsArray のみを計測(dotnet 起動・raw I/O を除外)。stderr に出すので
            // stdout の "OK" を汚さない。Python 側が "PROCESS_MS " 行を拾って前後比較に使う。
            // フェーズ別内訳(HSV/Match/FloodFill/...)も stderr へ。--selcache の余分な実行を
            // 拾わないよう、本計測の直前で購読する(全ゾーン合算の 1 レポートだけが出る)。
            DebugCaptureHooks.OnPerfReport += WritePerfPhases;
            if (viaState)
            {
                var sw = Stopwatch.StartNew();
                SessionRecolor.Apply(pixels, w, h, BuildSessionState(zoneList, st, masks));
                sw.Stop();
                Console.Error.WriteLine($"PROCESS_MS {sw.Elapsed.TotalMilliseconds:F2}");
            }
            else
            {
                ProcessZones(pixels, w, h, masks, zoneList, st);
            }
            WriteRawRgba(outPath, w, h, pixels);
            Console.WriteLine($"OK {w}x{h} -> {outPath} (zones={zoneList.Count})");
            return 0;
        }

        // PerfReport のフェーズ別(PHASE)とサブ段(SUBPHASE)の内訳を stderr へ出す。
        // 1 回の ProcessPixelsArray につき 1 組で、その呼び出しの PROCESS_MS 行より前に出る。
        private static void WritePerfPhases(PerfReport rep)
        {
            if (rep.Phases != null)
                foreach (var ph in rep.Phases)
                    Console.Error.WriteLine($"PHASE {ph.Name} {ph.TotalMs:F2}");
            if (rep.SubPhases != null)
                foreach (var ph in rep.SubPhases)
                    Console.Error.WriteLine($"SUBPHASE {ph.Name} {ph.TotalMs:F2}");
        }

        /// <summary>
        /// --viastate 用: ハーネスの入力(ゾーン・設定・raw 由来のマスク)を、編集画面が保存する形
        /// (<see cref="IrocaSessionState"/>。マスクは <see cref="MaskStateCodec.Encode"/> で RLE 化)へ詰め直す。
        /// </summary>
        private static IrocaSessionState BuildSessionState(
            List<ColorZone> zones, SettingsCfg st, MaskSnapshot masks)
        {
            var state = new IrocaSessionState { zones = zones };
            st.ToRecolorSettings().CopyTo(state);
            int n = masks.width * masks.height;
            MaskStateCodec.Encode(state.maskState, masks.width, masks.height,
                UnpackMask(masks.common, n), UnpackMasks(masks.zones, n), UnpackMasks(masks.zoneIncludes, n));
            return state;
        }

        private static bool[] UnpackMask(NativeBitMask packed, int n)
        {
            if (packed == null) return null;
            var arr = new bool[n];
            for (int i = 0; i < n; i++) arr[i] = packed.Get(i);
            return arr;
        }

        private static Dictionary<string, bool[]> UnpackMasks(Dictionary<string, NativeBitMask> packed, int n)
        {
            if (packed == null) return null;
            var dict = new Dictionary<string, bool[]>();
            foreach (var kv in packed) dict[kv.Key] = UnpackMask(kv.Value, n);
            return dict;
        }

        // ─── 単発実行・--batch・--session が共有する処理本体 ───
        // ここを経由しない処理経路を足さないこと(テストが製品と違う設定を測る事故になる)。
        // selectionCache は --session だけが渡す(製品 PreviewView の持ち越しを再現)。
        // source は処理前の pixels と同じ中身の配列。プレビューと同じく写し元を渡す経路(--batch・--session・
        // --stage)はこれを渡し、書き出しと同じく Core に複製させる経路(単発実行)は null のまま。
        private static void ProcessZones(Color32[] pixels, int w, int h,
                                         MaskSnapshot masks, List<ColorZone> zoneList, SettingsCfg st,
                                         SelectionCache selectionCache = null, Color32[] source = null)
        {
            var sw = Stopwatch.StartNew();
            PixelProcessor.ProcessPixelsArray(
                pixels, w, h, masks, zoneList, st.ToRecolorSettings(),
                selectionCache: selectionCache, sourcePixels: source);
            sw.Stop();
            Console.Error.WriteLine($"PROCESS_MS {sw.Elapsed.TotalMilliseconds:F2}");
        }

        private static void WriteRawRgba(string path, int w, int h, Color32[] pixels)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write(w); bw.Write(h);
            var outBytes = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                outBytes[i * 4] = pixels[i].r; outBytes[i * 4 + 1] = pixels[i].g;
                outBytes[i * 4 + 2] = pixels[i].b; outBytes[i * 4 + 3] = pixels[i].a;
            }
            bw.Write(outBytes);
        }

        // persistentIds: --session 用の name→id 台帳。同名ゾーンをセッション内の同一ゾーンと
        // みなして id を引き継ぐ(製品ではゾーン id が適用を跨いで安定なのを再現する。
        // SelectionCache の持ち越しはゾーン id 単位なので、これが無いと毎ステップ全ミスになり
        // ヒット経路を検証できない)。null(単発/--batch)なら従来どおり毎回新規 id。
        private static (List<ColorZone> zones, SettingsCfg settings,
                        List<(string zoneId, string path, bool include)> zoneMasks,
                        List<string> evidencePaths) LoadZones(
            string path, Dictionary<string, string> persistentIds = null)
        {
            var cfg = JsonSerializer.Deserialize<ZonesConfig>(File.ReadAllText(path), CaseInsensitiveJson);
            var list = new List<ColorZone>();
            var zoneMasks = new List<(string zoneId, string path, bool include)>();
            // 証拠マスク(--autotune 専用入力)はゾーンと同じ並びで返す(未指定は null)。
            var evidencePaths = new List<string>();
            foreach (var z in cfg.zones)
            {
                var zone = BuildZone(z);
                if (persistentIds != null)
                {
                    if (persistentIds.TryGetValue(zone.name, out var known))
                        zone.id = known;
                    else
                        persistentIds[zone.name] = zone.id;
                }
                list.Add(zone);
                evidencePaths.Add(z.evidenceMask);
                // ゾーン別マスクは MaskSnapshot 側(zone.id キー)に載るため、確定済み id と
                // ファイルパスの対応をここで確定させる。
                if (!string.IsNullOrEmpty(z.includeMask))
                    zoneMasks.Add((zone.id, z.includeMask, true));
                if (!string.IsNullOrEmpty(z.excludeMask))
                    zoneMasks.Add((zone.id, z.excludeMask, false));
            }
            return (list, cfg.settings ?? new SettingsCfg(), zoneMasks, evidencePaths);
        }

        /// <summary>
        /// ゾーン別マスク(含める/除外)の raw を読み、MaskSnapshot へ取り付ける。
        /// 寸法は mask.raw(= snapshot の寸法)と一致必須(製品でもマスクは全レイヤー同一
        /// キャンバス寸法で管理されるため、不一致はテスト側の組み立てミス)。
        /// </summary>
        private static void AttachZoneMasks(
            MaskSnapshot masks, List<(string zoneId, string path, bool include)> zoneMasks)
        {
            if (zoneMasks == null || zoneMasks.Count == 0) return;
            foreach (var (zoneId, path, include) in zoneMasks)
            {
                var (iw, ih, ibytes) = ReadRaw(path, 1);
                if (iw != masks.width || ih != masks.height)
                    throw new InvalidOperationException(
                        $"ゾーン別マスク寸法 {iw}x{ih} が mask.raw 寸法 {masks.width}x{masks.height} と不一致: {path}");
                var arr = ToBoolMask(ibytes, iw * ih);
                var dict = include
                    ? (masks.zoneIncludes ??= new Dictionary<string, NativeBitMask>())
                    : (masks.zones ??= new Dictionary<string, NativeBitMask>());
                if (dict.TryGetValue(zoneId, out var earlier)) earlier.Release();
                dict[zoneId] = NativeBitMask.FromBools(arr, iw, ih);
            }
        }

        // ─── --batch: 同一入力・複数ゾーン設定をまとめて処理 ───
        // 各ケースは元入力のクローンから始め、マスクも複製して渡すので、
        // ケースを跨いだ状態の持ち越しは無い(単発実行と byte 一致することをテストで検証している)。
        private static int RunBatch(string batchPath)
        {
            var cfg = JsonSerializer.Deserialize<BatchConfig>(File.ReadAllText(batchPath), CaseInsensitiveJson);

            var (w, h, basePixels) = ReadRgbaRaw(cfg.input);

            var (mw, mh, common) = ReadMaskRaw(cfg.mask);
            foreach (var c in cfg.cases)
            {
                var (zoneList, st, zoneMasks, _) = LoadZones(c.zones);
                var pixels = (Color32[])basePixels.Clone();
                var masks = new MaskSnapshot
                {
                    common = NativeBitMask.FromBools(common, mw, mh),
                    width = mw,
                    height = mh,
                    zones = null,
                };
                AttachZoneMasks(masks, zoneMasks);
                ProcessZones(pixels, w, h, masks, zoneList, st, source: basePixels);
                masks.Release();
                WriteRawRgba(c.@out, w, h, pixels);
                Console.WriteLine($"OK {w}x{h} -> {c.@out} (zones={zoneList.Count})");
            }
            return 0;
        }

        // ─── --session: 連続適用セッション(SelectionCache 持ち越し)の再現 ───
        // 製品のプレビュー生成は毎回ソース画素から処理し直すが、SelectionCache を
        // セッション内で持ち越す(PreviewView._selectionCache)。ここを再現し、
        // 「各ステップの出力がフレッシュ単発実行と byte 一致するか」を Python 側テスト
        // (test_session_state.py)が検証する。キーの取りこぼし=誤ヒットはここで露見する。
        private static int RunSession(string sessionPath)
        {
            var cfg = JsonSerializer.Deserialize<SessionConfig>(File.ReadAllText(sessionPath), CaseInsensitiveJson);

            var (w, h, basePixels) = ReadRgbaRaw(cfg.input);

            var selCache = new SelectionCache();
            // 手ごとの内訳(PHASE/SUBPHASE)を出す。2 手目以降は JIT・初回確保を含まない暖機後の
            // 値になるので、性能計測(dev_safe/scripts/perf_bench.py)はこの経路を使う。
            DebugCaptureHooks.OnPerfReport += WritePerfPhases;
            var ids = new Dictionary<string, string>();
            int step = 0;
            foreach (var s in cfg.steps)
            {
                var (mw, mh, common) = ReadMaskRaw(s.mask);
                var masks = new MaskSnapshot
                {
                    common = NativeBitMask.FromBools(common, mw, mh),
                    width = mw,
                    height = mh,
                    zones = null,
                };
                var (zoneList, st, zoneMasks, _) = LoadZones(s.zones, ids);
                AttachZoneMasks(masks, zoneMasks);
                var pixels = (Color32[])basePixels.Clone();
                ProcessZones(pixels, w, h, masks, zoneList, st, selCache, source: basePixels);
                masks.Release();
                WriteRawRgba(s.@out, w, h, pixels);
                Console.WriteLine($"OK-SESSION step={step} {w}x{h} -> {s.@out} (zones={zoneList.Count})");
                step++;
            }
            return 0;
        }

        // ─── 選択キャッシュキー(BuildSelectionKey)の網羅性監査 ───
        // ColorZone の各 public フィールドを 1 つずつ摂動し、キーが変化するかを
        // "SELKEY <field> <0|1>" 行で stdout に出す(摂動不能な型は "?" )。
        // どのフィールドが選択に影響すべきかの判定は Python 側テスト
        // (scripts/golden/test_selection_key_audit.py) の分類台帳が行う。
        // フィールド追加時にキーへの反映を忘れると誤ヒット(古い選択のままのプレビュー)に
        // なるため、その取りこぼしを機械検出するのが目的。
        private static int RunSelectionKeyAudit()
        {
            // 直接呼ぶ(2026-10-27 まで名前の文字列で反射して呼んでいた。改名・引数の変更はコンパイルで分かる)。
            string KeyOf(ColorZone z) => PixelProcessor.BuildSelectionKey(
                z, edgeFeather: 0f, aaCleanup: 3, holeFillPasses: 5, holeFillMinNeighbors: 4,
                relaxedSatMin: 0.02f, relaxedSatRamp: 0.08f, maskW: 0, maskH: 0, mixtureBand: false);

            var baseline = new ColorZone();
            baseline.extraSamples = new List<Color> { new Color(0.3f, 0.4f, 0.5f, 1f) };
            string baseKey = KeyOf(baseline);

            foreach (var f in typeof(ColorZone).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                var z = baseline.Clone();
                if (!TryPerturbField(z, f))
                {
                    Console.WriteLine($"SELKEY {f.Name} ?");
                    continue;
                }
                bool changed = KeyOf(z) != baseKey;
                Console.WriteLine($"SELKEY {f.Name} {(changed ? 1 : 0)}");
            }
            return 0;
        }

        // ─── RecolorPreview 自己検証(入力画像不要) ───
        // 合成した before/after で ComputeMetrics / BuildComparisonPanel を実 C# 実行し、
        // 既知の不変量(変化画素数・連結成分・パネル寸法・マゼンタ着色)を検証する。
        // 自動化(IrocaAutomation)は UnityEditor 依存で headless 実行できないため、Unity 非依存の
        // 診断ロジック本体をここで直接測る。1 件でも不変量に反したら非 0 を返す。
        private static int RunPreviewSelfTest()
        {
            int fails = 0;
            void Check(bool cond, string label)
            {
                Console.WriteLine($"PREVIEWSELFTEST {(cond ? "PASS" : "FAIL")} {label}");
                if (!cond) fails++;
            }

            // 64x48 の before(勾配)。after は矩形 [16,48)x[12,36) と孤立 2 画素だけ b を 128→129 に変える。
            // → 変化画素は「自分が変えた場所」だけと厳密に一致する。
            const int w = 64, h = 48;
            var before = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    before[y * w + x] = new Color32((byte)((x * 4) & 255), (byte)((y * 4) & 255), 128, 255);

            var after = (Color32[])before.Clone();
            int rectCount = 0;
            for (int y = 12; y < 36; y++)
                for (int x = 16; x < 48; x++)
                {
                    int i = y * w + x;
                    after[i] = new Color32(before[i].r, before[i].g, 129, 255);
                    rectCount++;
                }
            // 矩形外の孤立 2 画素(別成分)。
            int iso0 = 2 * w + 2, iso1 = 45 * w + 61;
            after[iso0] = new Color32(before[iso0].r, before[iso0].g, 129, 255);
            after[iso1] = new Color32(before[iso1].r, before[iso1].g, 129, 255);
            int expectedChanged = rectCount + 2;   // 768 + 2

            var m = RecolorPreview.ComputeMetrics(before, after, w, h);
            Check(m.totalPixels == w * h, $"totalPixels={m.totalPixels}");
            Check(m.changedPixels == expectedChanged, $"changedPixels={m.changedPixels} (want {expectedChanged})");
            Check(m.componentCount == 3, $"componentCount={m.componentCount} (want 3)");
            // 最大成分は矩形(rectCount)。占有率 = rectCount / expectedChanged。
            float wantFrac = (float)rectCount / expectedChanged;
            Check(System.Math.Abs(m.largestComponentFraction - wantFrac) < 1e-4f,
                $"largestComponentFraction={m.largestComponentFraction:F5} (want {wantFrac:F5})");
            // bbox は孤立画素を含み (2,2)-(61,45) → x=2,y=2,w=60,h=44。
            Check(m.bboxX == 2 && m.bboxY == 2 && m.bboxW == 60 && m.bboxH == 44,
                $"bbox=({m.bboxX},{m.bboxY},{m.bboxW},{m.bboxH}) (want 2,2,60,44)");

            // 等倍(64<=512)なのでタイルは 64x48、パネル幅 = 64*3 + gutter*2。
            RecolorPreview.ComputeTileSize(w, h, RecolorPreview.DefaultMaxTile, out int tw, out int th);
            Check(tw == 64 && th == 48, $"tile={tw}x{th} (want 64x48)");
            var panel = RecolorPreview.BuildComparisonPanel(
                before, after, w, h, RecolorPreview.DefaultMaxTile, out int pw, out int ph);
            int expW = tw * 3 + RecolorPreview.PanelGutter * 2;
            Check(pw == expW && ph == th, $"panel={pw}x{ph} (want {expW}x{th})");
            Check(panel.Length == pw * ph, $"panelLen={panel.Length}");

            // 縮小経路: 2000x1000 → 512x256。
            RecolorPreview.ComputeTileSize(2000, 1000, RecolorPreview.DefaultMaxTile, out int dw, out int dh);
            Check(dw == 512 && dh == 256, $"downscaleTile={dw}x{dh} (want 512x256)");

            // 変化マップタイル(右端, xOff = tw*2 + gutter*2)の内容確認。
            int changeXoff = tw * 2 + RecolorPreview.PanelGutter * 2;
            // 矩形中央(変化) → マゼンタ。
            var cChanged = panel[24 * pw + (changeXoff + 32)];
            Check(cChanged.r == 255 && cChanged.g == 0 && cChanged.b == 255, "changeTile center is magenta");
            // 左上角(0,0 は非変化) → グレー(r==g==b, 非マゼンタ)。
            var cUnchanged = panel[0 * pw + (changeXoff + 0)];
            Check(cUnchanged.r == cUnchanged.g && cUnchanged.g == cUnchanged.b &&
                  !(cUnchanged.r == 255 && cUnchanged.g == 0 && cUnchanged.b == 255), "changeTile corner is gray");

            Console.WriteLine(fails == 0 ? "PREVIEWSELFTEST ALL PASS" : $"PREVIEWSELFTEST {fails} FAILED");
            return fails == 0 ? 0 : 4;
        }

        // ─── ネイティブメモリの自己検証(入力画像不要) ───
        // 作業配列(WorkPools)と色の表(PixelProcessor.Palette.cs)は Mono のヒープの外に置き、色の表は参照の数で
        // 解放する。処理の後に ReleaseWorkBuffers を呼ぶと LiveBytes が 0 に戻ること(数え漏れ・返し忘れが無いこと)を、
        // 色の表を作る経路・作らない経路・取り消し・使用中の表の追い出しで確かめる。追い出した表を使い終わる前に
        // 解放する誤りがあると、解放した大きい領域は OS へ戻るので、読んだ時点でアクセス違反で落ちる(非 0 で終わる)。
        private static int RunNativeMemorySelfTest()
        {
            int fails = 0;
            void Check(bool cond, string label)
            {
                Console.WriteLine($"NATIVEMEMSELFTEST {(cond ? "PASS" : "FAIL")} {label}");
                if (!cond) fails++;
            }

            // 色の種類が少ない画像(色の表を作る。帯ごとに濃淡 7 段)と、ノイズで種類が多い画像(作らない)。
            Color32[] Flat(int n, int seed)
            {
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        byte shade = (byte)(120 + ((x + y) % 7) * 10);
                        px[y * n + x] = (((x / 37) + (y / 53) + seed) % 4) switch
                        {
                            0 => new Color32(shade, 40, 40, 255),
                            1 => new Color32(40, shade, 60, 255),
                            2 => new Color32(230, 230, 230, 255),
                            _ => new Color32(30, 30, (byte)(shade / 2), 255),
                        };
                    }
                return px;
            }
            Color32[] Noise(int n)
            {
                var rng = new System.Random(12345);
                var px = new Color32[n * n];
                for (int i = 0; i < px.Length; i++)
                    px[i] = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
                return px;
            }
            var zone = new ColorZone
            {
                name = "nativemem",
                enabled = true,
                mode = SelectionMode.ColorPick,
                sampleColor = new Color(170 / 255f, 40 / 255f, 40 / 255f),
                targetColor = new Color(0.2f, 0.4f, 0.9f),
                tolerance = 0.3f,
            };
            zone.EnsureId();
            zone.UpdateCacheIfNeeded();
            var zones = new List<ColorZone> { zone };
            var settings = new SettingsCfg().ToRecolorSettings();
            Color32[] Run(Color32[] src, int n, System.Threading.CancellationToken ct = default)
            {
                var px = (Color32[])src.Clone();
                PixelProcessor.ProcessPixelsArray(px, n, n, null, zones, settings, ct);
                return px;
            }
            bool Same(Color32[] a, Color32[] b)
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a) return false;
                return true;
            }
            long Live() => IrocaNativeMemory.LiveBytes;
            long PoolFree() => WorkPools.Float.FreeBytes + WorkPools.Bool.FreeBytes + WorkPools.Byte.FreeBytes
                               + WorkPools.Rgba32.FreeBytes + WorkPools.Int.FreeBytes;
            int PoolRented() => WorkPools.Float.RentedCount + WorkPools.Bool.RentedCount + WorkPools.Byte.RentedCount
                                + WorkPools.Rgba32.RentedCount + WorkPools.Int.RentedCount;

            long start = Live();
            Check(start == 0, $"start live={start}");

            // 1) 色の表を作る経路。表はキャッシュに残る(プールの空きを除いた分 = 色の表。番号だけで 4 バイト/画素)。
            const int N = 1024;
            var flatA = Flat(N, 0);
            var outA = Run(flatA, N);
            long paletteA = Live() - PoolFree();
            Check(PoolRented() == 0, $"palette path: all work arrays returned (rented={PoolRented()})");
            Check(paletteA >= (long)N * N * sizeof(int), $"palette cached bytes={paletteA}");
            var outA2 = Run(flatA, N);   // キャッシュに当たる
            Check(Same(outA, outA2), "cache hit: output same");
            Check(Live() - PoolFree() == paletteA, $"cache hit: no new palette (bytes={Live() - PoolFree()}, want {paletteA})");
            PixelProcessor.ReleaseWorkBuffers();
            Check(Live() == start, $"after release (palette) live={Live()}");

            // 2) 色の表を作らない経路(画素ごとの HSV を作業配列から借りる)。
            const int NN = 512;
            Run(Noise(NN), NN);
            Check(PoolRented() == 0, $"no-palette path: all work arrays returned (rented={PoolRented()})");
            Check(Live() == PoolFree(), $"no-palette path: nothing but pool free blocks (live={Live()}, free={PoolFree()})");
            PixelProcessor.ReleaseWorkBuffers();
            Check(Live() == start, $"after release (no palette) live={Live()}");

            // 3) 途中で取り消す。取り消す時期をずらして何度か回し、どこで抜けても返し忘れが無いこと。
            const int NB = 2048;
            var flatB = Flat(NB, 1);
            for (int k = 0; k < 8; k++)
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(5 + 15 * k));
                try { Run(flatB, NB, cts.Token); }
                catch (OperationCanceledException) { }
                catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { }
            }
            Check(PoolRented() == 0, $"cancelled: all work arrays returned (rented={PoolRented()})");
            PixelProcessor.ReleaseWorkBuffers();
            Check(Live() == start, $"after release (cancelled) live={Live()}");

            // 4) 使っている表を追い出す。大きい画像 C を別スレッドで処理している間に、別の大きい画像 D の処理と
            //    ReleaseWorkBuffers(キャッシュの全消去)を繰り返す。C の表はキャッシュから外れても、C が使い終わる
            //    まで解放されてはいけない(出力が単独で処理したときと同じで、落ちないこと)。
            var flatC = Flat(NB, 2);
            var flatD = Flat(NB, 3);
            var refC = Run(flatC, NB);
            PixelProcessor.ReleaseWorkBuffers();
            bool allSame = true;
            int overlapRuns = 0;
            for (int round = 0; round < 3; round++)
            {
                Color32[] gotC = null;
                var t = System.Threading.Tasks.Task.Run(() => gotC = Run(flatC, NB));
                while (!t.IsCompleted)
                {
                    Run(flatD, NB);
                    PixelProcessor.ReleaseWorkBuffers();
                    overlapRuns++;
                }
                t.Wait();
                allSame &= Same(refC, gotC);
            }
            Check(allSame, $"evicted palette in use: output same (overlapping runs={overlapRuns})");
            Check(PoolRented() == 0, $"evicted: all work arrays returned (rented={PoolRented()})");
            PixelProcessor.ReleaseWorkBuffers();
            Check(Live() == start, $"after release (evicted in use) live={Live()}");

            // 5) 持ち主の型(NativeBuffer)の決まり。用途ごとに数える・最後の Release で解放する・返しすぎと解放後の
            //    AddRef は投げる・返し忘れはファイナライザが解放して用途つきで数える。
            long otherBefore = IrocaNativeMemory.LiveBytesOf(NativeUsage.Other);
            var nb = new NativeBuffer(4096, NativeUsage.Other);
            Check(IrocaNativeMemory.LiveBytesOf(NativeUsage.Other) == otherBefore + 4096 && nb.Ptr != IntPtr.Zero && nb.RefCount == 1,
                "buffer: allocated and counted by usage");
            Check(IrocaNativeMemory.PeakBytesOf(NativeUsage.Other) >= otherBefore + 4096, "buffer: peak recorded");
            nb.AddRef();
            nb.Release();
            Check(nb.Ptr != IntPtr.Zero && nb.RefCount == 1, "buffer: alive while a reference is held");
            nb.Release();
            Check(nb.Ptr == IntPtr.Zero && IrocaNativeMemory.LiveBytesOf(NativeUsage.Other) == otherBefore, "buffer: freed on the last release");
            bool threw = false;
            try { nb.Release(); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "buffer: over-release throws");
            threw = false;
            try { nb.AddRef(); } catch (ObjectDisposedException) { threw = true; }
            Check(threw, "buffer: AddRef after release throws");

            int finOther = IrocaNativeMemory.FinalizedCount(NativeUsage.Other);
            LeakNativeBuffer();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Check(IrocaNativeMemory.FinalizedCount(NativeUsage.Other) == finOther + 1
                  && IrocaNativeMemory.LiveBytesOf(NativeUsage.Other) == otherBefore,
                "buffer: a forgotten buffer is freed and counted by the finalizer");
            // ここまでの処理(取り消し・使っている表の追い出しを含む)で、持ち主が返し忘れた塊は無い。
            foreach (NativeUsage u in new[] { NativeUsage.Work, NativeUsage.Pixels, NativeUsage.Palette, NativeUsage.Selection, NativeUsage.Mask })
            {
                Check(IrocaNativeMemory.FinalizedCount(u) == 0, $"no buffer of {u} was left to the finalizer (count={IrocaNativeMemory.FinalizedCount(u)})");
                Check(IrocaNativeMemory.LiveBytesOf(u) == 0, $"nothing of {u} is left (live={IrocaNativeMemory.LiveBytesOf(u)})");
            }
            Check(IrocaNativeMemory.PeakBytesOf(NativeUsage.Palette) >= (long)N * N * sizeof(int), "palette peak recorded");
            Check(IrocaNativeMemory.PeakBytesOf(NativeUsage.Work) > 0, "work pool peak recorded");

            // 6) 選択キャッシュの強さ(NativeFloatBuffer。NativeBuffer の参照の数)。写している途中に辞書から外されても、
            //    写し終えるまで解放されず、写し終えた時点で解放される。Dispose の後は何も残らない。
            {
                const int SW = 64, SH = 32;
                var strengthIn = new float[SW * SH];
                for (int i = 0; i < strengthIn.Length; i++) strengthIn[i] = (i % 97) / 97f;
                var cache = new SelectionCache();
                var storedCommon = NativeBitMask.Allocate(SW, SH);
                var otherCommon = NativeBitMask.Allocate(SW, SH);
                unsafe
                {
                    fixed (float* sp = strengthIn)
                        cache.StoreFrom("z", "k", sp, default, storedCommon, null, SW, SH, SW, SH);
                }
                long sel = IrocaNativeMemory.LiveBytesOf(NativeUsage.Selection);
                Check(sel == (long)SW * SH * sizeof(float), $"selection: stored (live={sel})");
                var copied = new float[SW * SH];
                bool hit;
                unsafe
                {
                    fixed (float* dp = copied)
                        hit = cache.TryCopy("z", "k", SW, SH, dp, otherCommon, null, (c, zm) =>
                        {
                            cache.Clear();   // 写している途中に外す
                            return true;
                        }, out _);
                }
                Check(hit && copied.AsSpan().SequenceEqual(strengthIn), "selection: copied while detached in the middle");
                Check(IrocaNativeMemory.LiveBytesOf(NativeUsage.Selection) == 0,
                    $"selection: freed when the copy ends (live={IrocaNativeMemory.LiveBytesOf(NativeUsage.Selection)})");
                Check(!cache.TryGet("z", "k", SW, SH), "selection: cleared entry is gone");
                Check(storedCommon.RefCount == 1 && otherCommon.RefCount == 1, "selection: masks are released with the entry");
                storedCommon.Release();
                otherCommon.Release();
                unsafe
                {
                    fixed (float* sp = strengthIn)
                    {
                        cache.StoreFrom("z", "k", sp, default, null, null, 0, 0, SW, SH);
                        cache.StoreFrom("z", "k2", sp, default, null, null, 0, 0, SW, SH);   // 同じ寸法なら使い回す
                    }
                }
                Check(IrocaNativeMemory.LiveBytesOf(NativeUsage.Selection) == (long)SW * SH * sizeof(float),
                    "selection: the same zone reuses its buffer");
                cache.Dispose();
                Check(IrocaNativeMemory.LiveBytesOf(NativeUsage.Selection) == 0 && IrocaNativeMemory.FinalizedCount(NativeUsage.Selection) == 0,
                    "selection: nothing left after Dispose");
            }

            // 7) 作業配列のプールの決まり(テスト用の別のプールで)。手元の量は直近の貸し出し中の最大量の HoldFactor 倍まで
            //    (新しく作るときに空きを大きい順に手放す)。ReleaseFree で直近の最大量を今の貸し出し中の量へ戻す。
            //    検証用(IROCA_POOL_POISON=1)では、返した塊は寝かせるので、返したものをもう一度返すと投げる。
            bool poison = Environment.GetEnvironmentVariable("IROCA_POOL_POISON") == "1";
            var pool = new NativeWorkPool<float>(maxPerClass: 4);
            const int MB = 1 << 18;   // float で 1MB
            unsafe
            {
                float* a = pool.Rent(MB);
                pool.Return(a);
                if (poison)
                {
                    threw = false;
                    float* other = pool.Rent(MB);   // 寝かせている間は同じ塊を貸さない
                    try { pool.Return(a); } catch (InvalidOperationException) { threw = true; }
                    Check(threw && other != a, "pool (poison): returning a returned block again throws");
                    pool.Return(other);
                }
                else
                {
                    float* b = pool.Rent(2 * MB);
                    pool.Return(b);
                    Check(pool.FreeBytes == 3L * MB * sizeof(float) && pool.PeakRentedBytes == 2L * MB * sizeof(float),
                        $"pool: free blocks kept within the hold limit (free={pool.FreeBytes}, peak={pool.PeakRentedBytes})");
                    float* c = pool.Rent(4 * MB);   // 手元が 1+2+4 = 7MB > 1.5 × 4MB なので、2MB の空きを手放す
                    Check(pool.FreeBytes == 1L * MB * sizeof(float) && pool.RentedBytes == 4L * MB * sizeof(float),
                        $"pool: the largest free block is released over the hold limit (free={pool.FreeBytes}, rented={pool.RentedBytes})");
                    pool.Return(c);
                }
            }
            pool.ReleaseFree();
            Check(pool.FreeBytes == 0 && pool.RentedBytes == 0 && pool.PeakRentedBytes == 0, "pool: ReleaseFree drops free blocks and resets the peak");
            Check(Live() == start, $"after the pool rules live={Live()}");

            Console.WriteLine(fails == 0 ? "NATIVEMEMSELFTEST ALL PASS" : $"NATIVEMEMSELFTEST {fails} FAILED");
            return fails == 0 ? 0 : 4;
        }

        /// <summary>
        /// --bitmaskselftest: NativeBitMask と MaskRle.EncodeBits / DecodeBits / EncodeBitsIfAny。bool[] の口(Encode・Decode・
        /// EncodeIfAny・MaskSnapshot.Pack)と同じ文字列・同じビットになること。参照の数・書く前の写し(Writable)・
        /// 終わったときにマスクの用途の量が 0 に戻ること。
        /// </summary>
        private static unsafe int RunBitMaskSelfTest()
        {
            int fails = 0;
            void Check(bool cond, string label)
            {
                Console.WriteLine($"BITMASKSELFTEST {(cond ? "PASS" : "FAIL")} {label}");
                if (!cond) fails++;
            }
            bool SameBits(NativeBitMask m, ulong[] packed)
            {
                if (m == null || packed == null || m.Words != packed.Length) return false;
                for (int k = 0; k < packed.Length; k++) if (m.Bits[k] != packed[k]) return false;
                return true;
            }
            long live0 = IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask);
            var rng = new System.Random(20261028);
            bool[] Runs(int n, int meanRun, bool startTrue)
            {
                var m = new bool[n];
                bool v = startTrue;
                for (int i = 0; i < n;)
                {
                    int run = 1 + rng.Next(meanRun * 2);
                    for (int k = 0; k < run && i < n; k++, i++) m[i] = v;
                    v = !v;
                }
                return m;
            }
            bool[] Filled(int n, bool v)
            {
                var m = new bool[n];
                for (int i = 0; i < n; i++) m[i] = v;
                return m;
            }
            var cases = new List<(string label, bool[] m, int w, int h)>
            {
                ("37x29 short runs", Runs(37 * 29, 3, false), 37, 29),
                ("37x29 starts true", Runs(37 * 29, 40, true), 37, 29),
                ("640x480 long runs", Runs(640 * 480, 900, false), 640, 480),
                ("64x1 all true", Filled(64, true), 64, 1),
                ("65x1 all true", Filled(65, true), 65, 1),
                ("70x3 all false", Filled(210, false), 70, 3),
                ("1x1 true", Filled(1, true), 1, 1),
                ("1x1 false", Filled(1, false), 1, 1),
            };
            foreach (var (label, b, w, h) in cases)
            {
                var m = NativeBitMask.FromBools(b, w, h);
                Check(SameBits(m, MaskSnapshot.Pack(b)), "FromBools equals Pack: " + label);
                string viaBools = MaskRle.Encode(b, w, h);
                Check(MaskRle.EncodeBits(m) == viaBools, "EncodeBits equals Encode: " + label);
                Check(MaskRle.EncodeBitsIfAny(m) == MaskRle.EncodeIfAny(b, w, h), "EncodeBitsIfAny equals EncodeIfAny: " + label);
                var back = MaskRle.DecodeBits(viaBools);
                Check(back != null && back.Width == w && back.Height == h && SameBits(back, MaskSnapshot.Pack(b)),
                    "DecodeBits equals Pack: " + label);
                Check(m.Any() == MaskSnapshot.AnyTrue(b), "Any: " + label);
                back?.Release();

                // ヒープの外の RLE(Undo の履歴): 保存形式の Base64 を解いた中身と同じバイト列で、展開すると同じビット。
                var rle = MaskRle.EncodeBitsNative(m);
                Check((rle != null) == MaskSnapshot.AnyTrue(b), "EncodeBitsNative is null only when empty: " + label);
                if (rle != null)
                {
                    var expected = Convert.FromBase64String(viaBools.Substring(2));
                    var got = new byte[rle.Bytes];
                    System.Runtime.InteropServices.Marshal.Copy(rle.Ptr, got, 0, got.Length);
                    Check(got.AsSpan().SequenceEqual(expected), "EncodeBitsNative equals the saved bytes: " + label);
                    var decoded = MaskRle.DecodeBitsNative(rle);
                    Check(decoded.Width == w && decoded.Height == h && SameBits(decoded, MaskSnapshot.Pack(b)), "DecodeBitsNative: " + label);
                    Check(MaskRle.MatchesBits(m, rle), "MatchesBits same: " + label);
                    decoded.Set(0, !decoded.Get(0));
                    Check(!MaskRle.MatchesBits(decoded, rle), "MatchesBits first pixel differs: " + label);
                    decoded.Set(0, !decoded.Get(0));
                    decoded.Set(decoded.Length - 1, !decoded.Get(decoded.Length - 1));
                    Check(!MaskRle.MatchesBits(decoded, rle), "MatchesBits last pixel differs: " + label);
                    decoded.Release();
                    var otherSize = NativeBitMask.Allocate(w + 1, h);
                    Check(!MaskRle.MatchesBits(otherSize, rle), "MatchesBits other size: " + label);
                    otherSize.Release();
                    rle.Release();
                }
                m.Release();
            }
            Check(MaskRle.DecodeBits("R:" + Convert.ToBase64String(new byte[13])) == null, "DecodeBits rejects a zero size");

            // 参照の数と書く前の写し。
            var a = NativeBitMask.Allocate(100, 3);
            a.Set(5, true);
            var shared = a.AddRef();
            var w1 = a.Writable();   // 共有中なので写す(a の参照を 1 つ返す)
            Check(!ReferenceEquals(w1, a) && a.RefCount == 1 && w1.RefCount == 1 && w1.Get(5), "Writable copies a shared mask");
            w1.Set(7, true);
            Check(!shared.Get(7) && w1.Get(7), "writing the copy leaves the shared mask");
            var w2 = w1.Writable();  // 自分だけなので写さない
            Check(ReferenceEquals(w2, w1), "Writable keeps an unshared mask");
            bool threw = false;
            try { a.Get(300); } catch (ArgumentOutOfRangeException) { threw = true; }
            Check(threw, "Get out of range throws");
            shared.Release();
            w2.Release();
            threw = false;
            try { shared.Release(); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "over-release throws");
            Check(IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask) == live0, $"nothing left (live={IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask)})");

            Console.WriteLine(fails == 0 ? "BITMASKSELFTEST ALL PASS" : $"BITMASKSELFTEST {fails} FAILED");
            return fails == 0 ? 0 : 4;
        }

        // 参照を返さずに手放す(--nativememselftest の取りこぼしの検査)。呼び出し元のフレームに参照が残らないよう、
        // 埋め込ませない。
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void LeakNativeBuffer() => new NativeBuffer(8192, NativeUsage.Other);

        /// <summary>
        /// --maskcodecselftest: 保存形式のマスク(MaskStateCodec)の書き込みと展開。展開に前の配列(previous)を渡すと、
        /// 中身の同じマスクはその配列を返し、違うもの・寸法の違うものは展開し直すこと(Undo/Redo の書き戻し)。
        /// </summary>
        private static int RunMaskCodecSelfTest()
        {
            int fails = 0;
            void Check(bool cond, string label)
            {
                Console.WriteLine($"MASKCODECSELFTEST {(cond ? "PASS" : "FAIL")} {label}");
                if (!cond) fails++;
            }
            bool Same(bool[] a, bool[] b)
            {
                if (a == null || b == null) return a == b;
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
            }
            // 2^18 画素以上(符号化を区間ごとに並列にする大きさ)。
            const int W = 640, H = 480;
            bool[] Disc(int cx, int cy, int r)
            {
                var m = new bool[W * H];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                        m[y * W + x] = (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
                return m;
            }

            var common = Disc(100, 100, 40);
            var zones = new Dictionary<string, bool[]> { ["a"] = Disc(300, 200, 60), ["b"] = Disc(500, 300, 30), ["empty"] = new bool[W * H] };
            var includes = new Dictionary<string, bool[]> { ["a"] = Disc(320, 220, 10) };
            var ms = new MaskState();
            MaskStateCodec.Encode(ms, W, H, common, zones, includes);
            Check(!string.IsNullOrEmpty(ms.commonMaskBase64), "common is written");
            Check(ms.zones.Count == 2 && ms.zoneIncludes.Count == 1, $"all-false zone is not written (zones={ms.zones.Count}, includes={ms.zoneIncludes.Count})");
            var msEmpty = new MaskState();
            MaskStateCodec.Encode(msEmpty, W, H, new bool[W * H], null, null);
            Check(msEmpty.commonMaskBase64 == "", "all-false common is an empty string");

            // 1) previous なし: 全部を新しい配列へ展開する。
            var z1 = new Dictionary<string, bool[]>();
            var i1 = new Dictionary<string, bool[]>();
            bool ok1 = MaskStateCodec.Decode(ms, out int w1, out int h1, out bool[] c1, z1, i1);
            Check(ok1 && w1 == W && h1 == H, "decode without previous");
            Check(Same(c1, common) && !ReferenceEquals(c1, common), "common decoded into a new array");
            Check(z1.Count == 2 && Same(z1["a"], zones["a"]) && Same(z1["b"], zones["b"]), "zones decoded");
            Check(i1.Count == 1 && Same(i1["a"], includes["a"]), "include decoded");

            // 2) Undo で b だけが保存形式の中身(半径 30)に戻る想定。今の b は半径 45。
            var bNow = Disc(500, 300, 45);
            var bNowCopy = (bool[])bNow.Clone();
            var prev = new MaskStateCodec.Previous
            {
                width = W, height = H, common = common,
                zones = new Dictionary<string, bool[]> { ["a"] = zones["a"], ["b"] = bNow, ["gone"] = Disc(10, 10, 5) },
                includes = new Dictionary<string, bool[]> { ["a"] = includes["a"] },
            };
            var z2 = new Dictionary<string, bool[]>();
            var i2 = new Dictionary<string, bool[]>();
            MaskStateCodec.Decode(ms, out _, out _, out bool[] c2, z2, i2, prev);
            Check(ReferenceEquals(c2, common), "unchanged common keeps the previous array");
            Check(ReferenceEquals(z2["a"], zones["a"]), "unchanged zone keeps the previous array");
            Check(ReferenceEquals(i2["a"], includes["a"]), "unchanged include keeps the previous array");
            Check(!ReferenceEquals(z2["b"], bNow) && Same(z2["b"], zones["b"]), "changed zone is decoded from the state");
            Check(Same(bNow, bNowCopy), "the previous array of the changed zone is not written");
            Check(!z2.ContainsKey("gone"), "a zone missing from the state is not kept");

            // 3) 寸法が違う previous は使わない(長さが同じでも画素の並びが違う)。
            var msT = new MaskState();
            var flat = Disc(200, 150, 50);
            MaskStateCodec.Encode(msT, H, W, flat, null, null);   // 縦横を入れ替えた寸法で同じ配列を書く
            var prevT = new MaskStateCodec.Previous { width = W, height = H, common = flat };
            MaskStateCodec.Decode(msT, out int wT, out int hT, out bool[] cT, new Dictionary<string, bool[]>(),
                new Dictionary<string, bool[]>(), prevT);
            Check(wT == H && hT == W && !ReferenceEquals(cT, flat) && Same(cT, flat), "previous with other dimensions is not used");

            // 4) 詰めた形への直接の展開(MaskRle.DecodeBits)は、bool[] へ展開してから詰めたもの(MaskSnapshot.Pack)と
            //    同じ配列になる。画素数が 64 の倍数でない・ランが語の境目をまたぐ・全部 true / false・旧 bitpack(最後の
            //    バイトの余りに 1 が立っている)・壊れた文字列も。
            bool SameBits(ulong[] a, ulong[] b)
            {
                if (a == null || b == null) return a == b;
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
            }
            string Legacy(bool[] m, int lw, int lh)
            {
                var bytes = new byte[8 + (m.Length + 7) / 8];
                BitConverter.GetBytes(lw).CopyTo(bytes, 0);
                BitConverter.GetBytes(lh).CopyTo(bytes, 4);
                for (int i = 0; i < m.Length; i++) if (m[i]) bytes[8 + i / 8] |= (byte)(1 << (i % 8));
                if ((m.Length & 7) != 0) bytes[bytes.Length - 1] |= (byte)(0xFF << (m.Length & 7));   // 余りのビットに 1
                return Convert.ToBase64String(bytes);
            }
            var rng = new System.Random(20261028);
            bool[] Runs(int n, int meanRun)
            {
                var m = new bool[n];
                bool v = rng.Next(2) == 0;
                for (int i = 0; i < n;)
                {
                    int run = 1 + rng.Next(meanRun * 2);
                    for (int k = 0; k < run && i < n; k++, i++) m[i] = v;
                    v = !v;
                }
                return m;
            }
            bool[] Filled(int n, bool v)
            {
                var m = new bool[n];
                for (int i = 0; i < n; i++) m[i] = v;
                return m;
            }
            bool[] LastOnly(int n)
            {
                var m = new bool[n];
                m[n - 1] = true;
                return m;
            }
            var cases = new List<(string label, bool[] m, int w, int h)>
            {
                ("37x29 short runs", Runs(37 * 29, 5), 37, 29),
                ("37x29 long runs", Runs(37 * 29, 90), 37, 29),
                ("640x480 runs", Runs(W * H, 700), W, H),
                ("640x480 disc", Disc(300, 200, 120), W, H),
                ("65x1 all true", Filled(65, true), 65, 1),
                ("64x2 all true", Filled(128, true), 64, 2),
                ("70x3 all false", new bool[210], 70, 3),
                ("1x1 true", new[] { true }, 1, 1),
                ("last pixel only", LastOnly(100), 10, 10),
            };
            foreach (var (label, m, cw, ch) in cases)
            {
                var expected = MaskSnapshot.Pack(m);
                var rle = MaskRle.DecodeBits(MaskRle.Encode(m, cw, ch));
                Check(rle.Width == cw && rle.Height == ch && SameBits(rle.ToWords(), expected), "packed decode (RLE) equals Pack(Decode): " + label);
                var legacy = MaskRle.DecodeBits(Legacy(m, cw, ch));
                Check(legacy.Width == cw && legacy.Height == ch && SameBits(legacy.ToWords(), expected), "packed decode (legacy bitpack) equals Pack(Decode): " + label);
                rle.Release();
                legacy.Release();
                Check(SameBits(MaskSnapshot.Pack(MaskRle.Decode(Legacy(m, cw, ch), out _, out _)), expected),
                    "legacy bitpack decode ignores the padding bits: " + label);
            }
            string full = MaskRle.Encode(Runs(37 * 29, 5), 37, 29);
            string truncated = "R:" + Convert.ToBase64String(Convert.FromBase64String(full.Substring(2)), 0, 9 + 4);
            var huge = new byte[13];
            BitConverter.GetBytes(MaskRle.MaxSide + 1).CopyTo(huge, 0);
            BitConverter.GetBytes(1).CopyTo(huge, 4);
            BitConverter.GetBytes((uint)(MaskRle.MaxSide + 1)).CopyTo(huge, 9);
            // 壊れた Base64 は Debug.LogWarning を通る(ハーネスでは呼べない)ので、ここでは見ない(abprobe の codec が見る)。
            foreach (var (label, bad) in new[] { ("truncated runs", truncated), ("side over MaxSide", "R:" + Convert.ToBase64String(huge)),
                                                 ("short legacy", Convert.ToBase64String(new byte[9])) })
            {
                Check(MaskRle.Decode(bad, out _, out _) == null, "bool decode rejects: " + label);
                Check(MaskRle.DecodeBits(bad) == null, "packed decode rejects: " + label);
            }

            // 5) 保存形式から作るスナップショット(MaskStateCodec.ToSnapshot。非破壊ビルド・シーンのプレビューの入口)は、
            //    bool[] へ展開してから組み立てたもの(MaskSnapshot.FromBuffers)と同じ。切れた項目・寸法の違う項目・
            //    ゾーン id の無い項目・同じゾーン id の 2 つ目・全 false の含めるマスクも同じに扱う。
            var msX = new MaskState();
            MaskStateCodec.Encode(msX, W, H, common, zones, includes);
            msX.zones.Add(new MaskZoneEntry { zoneId = "broken", maskBase64 = truncated });
            msX.zones.Add(new MaskZoneEntry { zoneId = "other size", maskBase64 = MaskRle.Encode(Disc(10, 10, 5), H, W) });
            msX.zones.Add(new MaskZoneEntry { zoneId = "", maskBase64 = MaskRle.Encode(Disc(10, 10, 5), W, H) });
            msX.zones.Add(null);
            msX.zones.Add(new MaskZoneEntry { zoneId = "a", maskBase64 = MaskRle.Encode(Disc(50, 60, 20), W, H) });   // 後が勝つ
            msX.zones.Add(new MaskZoneEntry { zoneId = "legacy", maskBase64 = Legacy(Disc(400, 100, 33), W, H) });
            msX.zoneIncludes.Add(new MaskZoneEntry { zoneId = "b", maskBase64 = MaskRle.Encode(new bool[W * H], W, H) });   // 全 false
            var zx = new Dictionary<string, bool[]>();
            var ix = new Dictionary<string, bool[]>();
            MaskStateCodec.Decode(msX, out int wx, out int hx, out bool[] cx, zx, ix);
            var viaBools = MaskSnapshot.FromBuffers(wx, hx, cx, zx, ix);
            var direct = MaskStateCodec.ToSnapshot(msX);
            bool SameLayer(NativeBitMask a, NativeBitMask b)
            {
                if (a == null || b == null) return a == b;
                return SameBits(a.ToWords(), b.ToWords());
            }
            bool SameDict(Dictionary<string, NativeBitMask> a, Dictionary<string, NativeBitMask> b)
            {
                if (a == null || b == null) return a == b;
                if (a.Count != b.Count) return false;
                foreach (var kv in a)
                    if (!b.TryGetValue(kv.Key, out var v) || !SameLayer(kv.Value, v)) return false;
                return true;
            }
            Check(direct != null && direct.width == viaBools.width && direct.height == viaBools.height, "snapshot dimensions");
            Check(direct != null && SameLayer(direct.common, viaBools.common), "snapshot common");
            Check(direct != null && SameDict(direct.zones, viaBools.zones) && direct.zones.Count == 3,
                $"snapshot zones (count={direct?.zones?.Count})");
            Check(direct != null && SameDict(direct.zoneIncludes, viaBools.zoneIncludes) && direct.zoneIncludes.Count == 1,
                "snapshot includes (all-false include is not carried)");
            var msNoCommon = new MaskState { width = W, height = H, commonMaskBase64 = "" };
            var noCommon = MaskStateCodec.ToSnapshot(msNoCommon);
            Check(noCommon != null && noCommon.common == null && noCommon.zones.Count == 0 && noCommon.zoneIncludes == null,
                "snapshot of a state without masks");
            Check(MaskStateCodec.ToSnapshot(new MaskState()) == null && MaskStateCodec.ToSnapshot(null) == null,
                "no snapshot without dimensions");
            long maskLive = IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask);
            viaBools.Release();
            direct.Release();
            noCommon.Release();
            Check(IrocaNativeMemory.LiveBytesOf(NativeUsage.Mask) < maskLive || maskLive == 0, "releasing a snapshot returns its layers");
            var shared = MaskSnapshot.FromBuffers(W, H, common, zones, includes);
            var extra = shared.AddRef();
            shared.Release();
            Check(extra.common != null && extra.common.RefCount == 1, "the other reference still holds the layers");
            extra.Release();

            Console.WriteLine(fails == 0 ? "MASKCODECSELFTEST ALL PASS" : $"MASKCODECSELFTEST {fails} FAILED");
            return fails == 0 ? 0 : 4;
        }

        // 対象フィールドを「必ず元と異なる値」に書き換える。未知の型は false。
        private static bool TryPerturbField(ColorZone z, System.Reflection.FieldInfo f)
        {
            object v = f.GetValue(z);
            object nv;
            if (f.FieldType == typeof(bool)) nv = !(bool)v;
            else if (f.FieldType == typeof(float)) nv = (float)v + 0.1237f;
            else if (f.FieldType == typeof(int)) nv = (int)v + 1;
            else if (f.FieldType == typeof(string)) nv = ((string)v ?? "") + "_x";
            else if (f.FieldType == typeof(Color))
            {
                var c = (Color)v;
                nv = new Color(PerturbChannel(c.r), PerturbChannel(c.g), PerturbChannel(c.b), c.a);
            }
            else if (f.FieldType == typeof(Rect))
            {
                var r = (Rect)v;
                nv = new Rect(r.x + 0.1f, r.y + 0.1f, r.width * 0.8f + 0.01f, r.height * 0.8f + 0.01f);
            }
            else if (f.FieldType == typeof(Vector2))
            {
                var p = (Vector2)v;
                nv = new Vector2(p.x + 0.17f, p.y + 0.17f);
            }
            else if (f.FieldType.IsEnum)
            {
                var vals = Enum.GetValues(f.FieldType);
                int idx = Array.IndexOf(vals, v);
                nv = vals.GetValue((idx + 1) % vals.Length);
            }
            else if (f.FieldType == typeof(List<Color>))
            {
                var list = new List<Color>((List<Color>)v ?? new List<Color>());
                list.Add(new Color(0.9f, 0.1f, 0.2f, 1f));
                nv = list;
            }
            else return false;
            f.SetValue(z, nv);
            return true;
        }

        // 0..1 に収まりつつ必ず元と異なる値へ（反転だと 0.5 で不動点になる）。
        private static float PerturbChannel(float c) => c < 0.5f ? c + 0.25f : c - 0.25f;

        // 選択キャッシュ検証: 「再着色のみ変更した再生成」がキャッシュヒットでフル再計算と byte 一致し、
        // 「選択パラメータ変更」はミスして正しく再計算されることを確認する。
        //   populate_diff : 空キャッシュで populate した出力 == キャッシュ無し出力 か(0 が正)
        //   hit_vs_fresh  : ターゲット色のみ変更→ヒット復元→再着色した出力 == フル再計算 か(★0 が正)
        //   miss_vs_fresh : tolerance 変更→ミス再計算した出力 == フル再計算 か(0 が正)
        // withMaskChanges(--selcache-mask)なら続けてマスクだけを変える検証も行う(RunSelectionCacheMaskCheck)。
        private static void RunSelectionCacheCheck(
            Color32[] input, int w, int h, MaskSnapshot masks, List<ColorZone> zonesIn, SettingsCfg st,
            bool withMaskChanges)
        {
            var zones = new List<ColorZone>();
            foreach (var z in zonesIn) { var c = z.Clone(); c.UpdateCacheIfNeeded(); zones.Add(c); }

            void Run(Color32[] px, SelectionCache cache)
            {
                PixelProcessor.ProcessPixelsArray(px, w, h, masks, zones, st.ToRecolorSettings(),
                    selectionCache: cache);
            }
            int Diff(Color32[] a, Color32[] b)
            {
                int n = 0;
                for (int i = 0; i < a.Length; i++)
                    if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a) n++;
                return n;
            }

            // 1) target T1: キャッシュ無し O1 と、空キャッシュで populate した O1c。
            var o1 = (Color32[])input.Clone(); Run(o1, null);
            var cache = new SelectionCache();
            var o1c = (Color32[])input.Clone(); Run(o1c, cache);
            int dPopulate = Diff(o1, o1c);

            // 2) ターゲット色のみ変更(再着色のみ=選択キー不変)→ キャッシュ HIT。フル再計算 O2 と一致すべき。
            foreach (var z in zones)
                z.targetColor = new Color(1f - z.targetColor.r, 1f - z.targetColor.g, 1f - z.targetColor.b, 1f);
            var sw = Stopwatch.StartNew();
            var o2c = (Color32[])input.Clone(); Run(o2c, cache);
            sw.Stop();
            var swf = Stopwatch.StartNew();
            var o2 = (Color32[])input.Clone(); Run(o2, null);
            swf.Stop();
            int dHit = Diff(o2, o2c);

            // 3) tolerance 変更(選択キー変化)→ キャッシュ MISS。フル再計算と一致すべき。
            foreach (var z in zones) { z.tolerance = Mathf.Clamp01(z.tolerance + 0.05f); z.UpdateCacheIfNeeded(); }
            var o3c = (Color32[])input.Clone(); Run(o3c, cache);
            var o3 = (Color32[])input.Clone(); Run(o3, null);
            int dMiss = Diff(o3, o3c);

            Console.Error.WriteLine(
                $"SELCACHE populate_diff={dPopulate} hit_vs_fresh={dHit} miss_vs_fresh={dMiss} "
                + $"hitMs={sw.Elapsed.TotalMilliseconds:F1} freshMs={swf.Elapsed.TotalMilliseconds:F1}");

            if (withMaskChanges) RunSelectionCacheMaskCheck(input, w, h, masks, zones, st, cache, o3);
        }

        // 選択キャッシュのマスク変更の検証: 除外マスク・含めるマスクだけを変えた再生成が、キャッシュを持ち越しても
        // フル再計算と byte 一致するかを確かめる("SELCACHE-MASK" 行。差分はどれも 0 が正)。
        //   quiet   : どのゾーンも変えなかった 24px 角を共通の除外に足す(照合が 0 なら使い回しになる)
        //   busy    : いちばん変化の多い 24px 角を共通の除外に足す(そこに合うゾーンは計算し直しになる)
        //   back    : 共通の除外を元に戻す(中身は同じで別の配列)
        //   include : 先頭ゾーンの含めるマスクに quiet の角を足す(選択はそのまま使える)
        //   reuse   : 除外マスクが違うのに使い回した回数(選択キャッシュの数え)
        //   latest  : 右クリックの宛先が読む値(TryReadLatest)が、持ち越したキャッシュと同じマスクで作り直した
        //             キャッシュとで違う画素の数(各段・全ゾーンの合計)
        //   latest_excl : TryReadLatest が除外の画素で 0 を返さなかった数(保存はマスクを掛ける前なので、読むときに掛ける)
        // マスクの寸法が画像と違うときは試さない(skipped)。
        private static void RunSelectionCacheMaskCheck(Color32[] input, int w, int h, MaskSnapshot masks,
            List<ColorZone> zones, SettingsCfg st, SelectionCache cache, Color32[] current)
        {
            if (masks == null || masks.width != w || masks.height != h || zones.Count == 0)
            {
                Console.Error.WriteLine("SELCACHE-MASK skipped");
                return;
            }
            const int B = 24;
            int bestBusy = -1, busyCount = 0, quiet = -1;
            int bx = w / B, by = h / B;
            for (int k = 0; k < bx * by; k++)
            {
                int x0 = (k % bx) * B, y0 = (k / bx) * B, n = 0;
                for (int y = y0; y < y0 + B; y++)
                    for (int x = x0; x < x0 + B; x++)
                    {
                        Color32 a = input[y * w + x], b = current[y * w + x];
                        if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) n++;
                    }
                if (n == 0 && quiet < 0) quiet = k;
                if (n > busyCount) { busyCount = n; bestBusy = k; }
            }
            if (quiet < 0 || bestBusy < 0)
            {
                Console.Error.WriteLine("SELCACHE-MASK skipped");
                return;
            }
            NativeBitMask WithBlock(NativeBitMask src, int k)
            {
                var dst = src != null ? src.Clone() : NativeBitMask.Allocate(w, h);
                int x0 = (k % bx) * B, y0 = (k / bx) * B;
                for (int y = y0; y < y0 + B; y++)
                    for (int x = x0; x < x0 + B; x++)
                        dst.Set(y * w + x, true);
                return dst;
            }
            MaskSnapshot With(NativeBitMask common, Dictionary<string, NativeBitMask> includes) => new MaskSnapshot
            {
                common = common, width = w, height = h, zones = masks.zones, zoneIncludes = includes,
            };
            int latestDiff = 0, latestExcl = 0;
            var all = new int[w * h];
            for (int i = 0; i < all.Length; i++) all[i] = i;
            void CompareLatest(MaskSnapshot m)
            {
                using (var rebuilt = new SelectionCache())
                {
                    var px = (Color32[])input.Clone();
                    PixelProcessor.ProcessPixelsArray(px, w, h, m, zones, st.ToRecolorSettings(), selectionCache: rebuilt);
                    var a = new float[all.Length];
                    var b = new float[all.Length];
                    foreach (var z in zones)
                    {
                        bool okA = cache.TryReadLatest(z.id, w, h, all, a), okB = rebuilt.TryReadLatest(z.id, w, h, all, b);
                        if (okA != okB) { latestDiff += all.Length; continue; }
                        if (!okA) continue;
                        NativeBitMask zm = null;
                        m.zones?.TryGetValue(z.id, out zm);
                        for (int i = 0; i < all.Length; i++)
                        {
                            if (a[i] != b[i]) latestDiff++;
                            if (a[i] != 0f && ((m.common != null && m.common.Get(i)) || (zm != null && zm.Get(i)))) latestExcl++;
                        }
                    }
                }
            }
            int Step(MaskSnapshot m)
            {
                var withCache = (Color32[])input.Clone();
                PixelProcessor.ProcessPixelsArray(withCache, w, h, m, zones, st.ToRecolorSettings(), selectionCache: cache);
                var fresh = (Color32[])input.Clone();
                PixelProcessor.ProcessPixelsArray(fresh, w, h, m, zones, st.ToRecolorSettings());
                int n = 0;
                for (int i = 0; i < fresh.Length; i++)
                    if (fresh[i].r != withCache[i].r || fresh[i].g != withCache[i].g
                        || fresh[i].b != withCache[i].b || fresh[i].a != withCache[i].a) n++;
                CompareLatest(m);
                return n;
            }
            int reuse0 = cache.MaskReuseCount;
            var quietCommon = WithBlock(masks.common, quiet);
            int dQuiet = Step(With(quietCommon, masks.zoneIncludes));
            int dBusy = Step(With(WithBlock(quietCommon, bestBusy), masks.zoneIncludes));
            var backCommon = masks.common?.Clone();
            int dBack = Step(With(backCommon, masks.zoneIncludes));
            var includes = masks.zoneIncludes == null
                ? new Dictionary<string, NativeBitMask>() : new Dictionary<string, NativeBitMask>(masks.zoneIncludes);
            includes.TryGetValue(zones[0].id, out var inc0);
            includes[zones[0].id] = WithBlock(inc0, quiet);
            int dInclude = Step(With(backCommon, includes));
            Console.Error.WriteLine(
                $"SELCACHE-MASK quiet={dQuiet} busy={dBusy} back={dBack} include={dInclude} "
                + $"reuse={cache.MaskReuseCount - reuse0} latest={latestDiff} latest_excl={latestExcl}");
        }

        // ─── AI マスク提案の純計算層検証(--samops-*) ───
        // raw 規約: 画像 [int32 w][int32 h][RGBA w*h*4](行 0 = 画像下端 = GetPixels32 順)、
        //           マスク [int32 w][int32 h][bytes w*h](行 0 = 下端、非 0 = true)。
        // 浮動小数バイナリは float32 LE。数値の書式は InvariantCulture。
        private static int RunSamOps(string[] args)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (args[0])
            {
                // --samops-encinput <in.raw RGBA> <out.bin>
                // 出力: [int32 newW][int32 newH] + float32[3*1024*1024] CHW(正規化・パディング済み)
                case "--samops-encinput":
                {
                    var (w, h, pixels) = ReadRgbaRaw(args[1]);
                    float[] chw = SamImageOps.BuildEncoderInput(pixels, w, h);
                    SamImageOps.GetResizedSize(w, h, out int newW, out int newH);
                    using (var fs = new FileStream(args[2], FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(newW); bw.Write(newH);
                        var bytes = new byte[chw.Length * 4];
                        System.Buffer.BlockCopy(chw, 0, bytes, 0, bytes.Length);
                        bw.Write(bytes);
                    }
                    Console.WriteLine($"ENCINPUT OK {w}x{h} -> {newW}x{newH}");
                    return 0;
                }

                // --samops-coords <texW> <texH> <u> <v>
                case "--samops-coords":
                {
                    int w = int.Parse(args[1], inv), h = int.Parse(args[2], inv);
                    float u = float.Parse(args[3], inv), v = float.Parse(args[4], inv);
                    SamCoordMapper.UvTo1024(u, v, w, h, out float x, out float y);
                    SamImageOps.GetResizedSize(w, h, out int newW, out int newH);
                    Console.WriteLine(string.Format(inv, "COORDS {0:R} {1:R} {2} {3}", x, y, newW, newH));
                    return 0;
                }

                // --samops-post <texW> <texH> <floodFrac> <logits.bin f32[4*256*256]> <scores.bin f32[4]> <out.raw> [granularity 0|1|2]
                case "--samops-post":
                {
                    int w = int.Parse(args[1], inv), h = int.Parse(args[2], inv);
                    float frac = float.Parse(args[3], inv);
                    float[] logits = ReadF32(args[4], 4 * 256 * 256);
                    float[] scores = ReadF32(args[5], 4);
                    var gran = args.Length > 7
                        ? (MaskSuggestGranularity)int.Parse(args[7], inv)
                        : MaskSuggestGranularity.Auto;
                    var res = SamMaskPostprocess.SelectAndUpscale(logits, scores, w, h, frac,
                                                                  granularity: gran);
                    // 提案マスクはヒープの外(NativeBools)。書き出す分を写して手放す。
                    bool[] resMask = res.mask.ToArray();
                    res.mask.Release();
                    WriteMaskRaw(args[6], w, h, resMask);
                    Console.WriteLine(string.Format(inv,
                        "POST channel={0} score={1:R} area={2:R} warn={3}",
                        res.channel, res.score, res.areaFrac, res.floodWarning ? 1 : 0));
                    return 0;
                }

                // --samops-refine <mask.raw> <image.raw RGBA> <out.raw> : 境界色スナップ単体
                case "--samops-refine":
                {
                    var (mw2, mh2, mask2) = ReadMaskRaw(args[1]);
                    var (iw, ih, rgba2) = ReadRaw(args[2], 4);
                    if (iw != mw2 || ih != mh2)
                    {
                        Console.Error.WriteLine("refine: mask/image size mismatch");
                        return 2;
                    }
                    var px2 = ToColor32(rgba2, iw * ih);
                    SamMaskRefine.SnapBoundary(mask2, px2, iw, ih);
                    WriteMaskRaw(args[3], iw, ih, mask2);
                    Console.WriteLine($"REFINE OK {iw}x{ih}");
                    return 0;
                }

                // --samops-fringe <mask.raw> <image.raw RGBA> <out.raw> : 房外郭への境界拡張単体
                case "--samops-fringe":
                {
                    var (mw2, mh2, mask2) = ReadMaskRaw(args[1]);
                    var (iw, ih, rgba2) = ReadRaw(args[2], 4);
                    if (iw != mw2 || ih != mh2)
                    {
                        Console.Error.WriteLine("fringe: mask/image size mismatch");
                        return 2;
                    }
                    var px2 = ToColor32(rgba2, iw * ih);
                    SamMaskRefine.ExtendFringe(mask2, px2, iw, ih);
                    WriteMaskRaw(args[3], iw, ih, mask2);
                    Console.WriteLine($"FRINGE OK {iw}x{ih}");
                    return 0;
                }

                // --samops-peel <mask.raw> <image.raw RGBA> <out.raw> : 細い描き込みの合わせ直し単体
                // (SamMaskPostprocess.RefineInPlace では最後。--samops-aainclude の後)
                case "--samops-peel":
                {
                    var (mw2, mh2, mask2) = ReadMaskRaw(args[1]);
                    var (iw, ih, rgba2) = ReadRaw(args[2], 4);
                    if (iw != mw2 || ih != mh2)
                    {
                        Console.Error.WriteLine("peel: mask/image size mismatch");
                        return 2;
                    }
                    var px2 = ToColor32(rgba2, iw * ih);
                    SamMaskRefine.PeelThinSpill(mask2, px2, iw, ih);
                    WriteMaskRaw(args[3], iw, ih, mask2);
                    Console.WriteLine($"PEEL OK {iw}x{ih}");
                    return 0;
                }

                // --samops-refineall <mask.raw> <image.raw RGBA> <out.raw> [reps] : 精密化 4 段を製品の入口
                // (SamMaskPostprocess.RefineInPlace。提案が画像の一部なら切り出して回す)で通す。
                // --samops-refine → -fringe → -aainclude → -peel の連鎖(全画像)とビット同一であることの検証に使う。
                // reps(既定 1)を渡すと、製品の入口と全画像の 4 段をそれぞれ reps 回ずつ交互に回して最小の時間を出し、
                // 両者の出力の違う画素数(diff)も数える。
                // 出力: REFINEALL OK WxH crop=x,y,w,h(切り出さなかったときは crop=none) ms=<製品の入口>
                //       [full_ms=<全画像の 4 段> diff=<画素数>]
                case "--samops-refineall":
                {
                    var (mw2, mh2, mask2) = ReadMaskRaw(args[1]);
                    var (iw, ih, rgba2) = ReadRaw(args[2], 4);
                    if (iw != mw2 || ih != mh2)
                    {
                        Console.Error.WriteLine("refineall: mask/image size mismatch");
                        return 2;
                    }
                    int reps = args.Length > 4 ? int.Parse(args[4], inv) : 1;
                    var px2 = ToColor32(rgba2, iw * ih);
                    string crop = SamMaskRefine.TryDeriveRefineCropRect(mask2, iw, ih, default,
                            out _, out int cx0, out int cy0, out int cw, out int ch)
                        ? $"{cx0},{cy0},{cw},{ch}" : "none";
                    bool[] result = null, fullResult = null;
                    double best = double.MaxValue, bestFull = double.MaxValue;
                    for (int r = 0; r < reps; r++)
                    {
                        var m = (bool[])mask2.Clone();
                        var sw1 = Stopwatch.StartNew();
                        SamMaskPostprocess.RefineInPlace(m, px2, iw, ih);
                        best = Math.Min(best, sw1.Elapsed.TotalMilliseconds);
                        result = m;
                        if (reps <= 1) break;
                        var f = (bool[])mask2.Clone();
                        var sw2 = Stopwatch.StartNew();
                        SamMaskRefine.SnapBoundary(f, px2, iw, ih);
                        SamMaskRefine.ExtendFringe(f, px2, iw, ih);
                        SamMaskRefine.IncludeAaTransition(f, px2, iw, ih);
                        SamMaskRefine.PeelThinSpill(f, px2, iw, ih);
                        bestFull = Math.Min(bestFull, sw2.Elapsed.TotalMilliseconds);
                        fullResult = f;
                    }
                    WriteMaskRaw(args[3], iw, ih, result);
                    string extra = "";
                    if (fullResult != null)
                    {
                        int diff = 0;
                        for (int i = 0; i < result.Length; i++) if (result[i] != fullResult[i]) diff++;
                        extra = string.Format(inv, " full_ms={0:F1} diff={1}", bestFull, diff);
                    }
                    Console.WriteLine(string.Format(inv, "REFINEALL OK {0}x{1} crop={2} ms={3:F1}{4}",
                        iw, ih, crop, best, extra));
                    return 0;
                }

                // --samops-aainclude <mask.raw> <image.raw RGBA> <out.raw> : AA 遷移帯の包含単体
                case "--samops-aainclude":
                {
                    var (mw2, mh2, mask2) = ReadMaskRaw(args[1]);
                    var (iw, ih, rgba2) = ReadRaw(args[2], 4);
                    if (iw != mw2 || ih != mh2)
                    {
                        Console.Error.WriteLine("aainclude: mask/image size mismatch");
                        return 2;
                    }
                    var px2 = ToColor32(rgba2, iw * ih);
                    SamMaskRefine.IncludeAaTransition(mask2, px2, iw, ih);
                    WriteMaskRaw(args[3], iw, ih, mask2);
                    Console.WriteLine($"AAINCLUDE OK {iw}x{ih}");
                    return 0;
                }

                // --samops-aainclude-crop <mask.raw> <image.raw RGBA> <out.raw> :
                // AA 遷移包含のクロップ実行(コミット段の経路)。--samops-aainclude と
                // 出力ビット同一であることの機械検証に使う。
                case "--samops-aainclude-crop":
                {
                    var (mw2, mh2, mask2) = ReadMaskRaw(args[1]);
                    var (iw, ih, rgba2) = ReadRaw(args[2], 4);
                    if (iw != mw2 || ih != mh2)
                    {
                        Console.Error.WriteLine("aainclude-crop: mask/image size mismatch");
                        return 2;
                    }
                    if (SamMaskRefine.TryDeriveAaCropRect(mask2, iw, ih,
                            out int rx0, out int ry0, out int rw, out int rh, out int aaD))
                    {
                        var cropPx = new Color32[rw * rh];
                        for (int cy = 0; cy < rh; cy++)
                        {
                            int srcRow = (ry0 + cy) * iw + rx0;
                            for (int cx = 0; cx < rw; cx++)
                            {
                                int s = (srcRow + cx) * 4;
                                cropPx[cy * rw + cx] = new Color32(rgba2[s], rgba2[s + 1], rgba2[s + 2], rgba2[s + 3]);
                            }
                        }
                        SamMaskRefine.IncludeAaTransitionCropped(mask2, iw, ih, cropPx, rx0, ry0, rw, rh, aaD);
                        Console.Error.WriteLine($"AACROP rect=({rx0},{ry0}) {rw}x{rh} d={aaD}");
                    }
                    WriteMaskRaw(args[3], iw, ih, mask2);
                    Console.WriteLine($"AAINCLUDE-CROP OK {iw}x{ih}");
                    return 0;
                }

                // --samops-covertransfer <mask.raw> <dw> <dh> <out.raw> : 被覆保存の解像度転写
                case "--samops-covertransfer":
                {
                    var (sw2, sh2, src2) = ReadMaskRaw(args[1]);
                    int dw = int.Parse(args[2]), dh = int.Parse(args[3]);
                    var dst2 = SamMaskRefine.TransferCoverage(src2, sw2, sh2, dw, dh);
                    if (dst2 == null)
                    {
                        Console.Error.WriteLine("covertransfer: invalid input");
                        return 2;
                    }
                    WriteMaskRaw(args[4], dw, dh, dst2);
                    Console.WriteLine($"COVERTRANSFER OK {sw2}x{sh2} -> {dw}x{dh}");
                    return 0;
                }

                // --samops-zoomrect <mask.raw> <clickX> <clickY> : ズームイン判定
                // (クリック成分 bbox 長辺 + クロップ矩形導出。座標は下原点)
                case "--samops-zoomrect":
                {
                    var (w, h, mask) = ReadMaskRaw(args[1]);
                    int cx = int.Parse(args[2], inv), cy = int.Parse(args[3], inv);
                    int bb = SamZoomOps.ClickComponentBBoxLong(mask, w, h, cx, cy);
                    if (SamZoomOps.TryDeriveCropRect(bb, cx, cy, w, h,
                                                     out int zx0, out int zy0, out int zside))
                        Console.WriteLine($"ZOOMRECT bb={bb} crop={zx0},{zy0},{zside}");
                    else
                        Console.WriteLine($"ZOOMRECT bb={bb} NONE");
                    return 0;
                }

                // --samops-cropcoords <texW> <texH> <u> <v> <x0> <y0> <side> : クロップ 1024 座標
                case "--samops-cropcoords":
                {
                    int w = int.Parse(args[1], inv), h = int.Parse(args[2], inv);
                    float u = float.Parse(args[3], inv), v = float.Parse(args[4], inv);
                    int zx0 = int.Parse(args[5], inv), zy0 = int.Parse(args[6], inv);
                    int zside = int.Parse(args[7], inv);
                    SamCoordMapper.UvToCrop1024(u, v, w, h, zx0, zy0, zside,
                                                out float x, out float y);
                    Console.WriteLine(string.Format(inv, "CROPCOORDS {0:R} {1:R}", x, y));
                    return 0;
                }

                // --samops-crop <image.raw RGBA> <x0> <y0> <side> <out.raw> : クロップ切り出し
                case "--samops-crop":
                {
                    var (w, h, pixels) = ReadRgbaRaw(args[1]);
                    int zx0 = int.Parse(args[2], inv), zy0 = int.Parse(args[3], inv);
                    int zside = int.Parse(args[4], inv);
                    Color32[] crop = SamZoomOps.ExtractCrop(pixels, w, h, zx0, zy0, zside);
                    // ExtractCrop は zside*zside の配列を返すので、WriteRawRgba が書く w*h 画素と長さが一致する。
                    WriteRawRgba(args[5], zside, zside, crop);
                    Console.WriteLine($"CROP OK {zside}x{zside}");
                    return 0;
                }

                // --samops-paste <cropmask.raw> <texW> <texH> <x0> <y0> <out.raw> : 貼り戻し
                case "--samops-paste":
                {
                    var (cw, ch, mbytes) = ReadRaw(args[1], 1);
                    if (cw != ch)
                    {
                        Console.Error.WriteLine("paste: crop mask must be square");
                        return 2;
                    }
                    var cmask = ToBoolMask(mbytes, cw * ch);
                    int w = int.Parse(args[2], inv), h = int.Parse(args[3], inv);
                    int zx0 = int.Parse(args[4], inv), zy0 = int.Parse(args[5], inv);
                    bool[] full = SamZoomOps.PasteCrop(cmask, cw, w, h, zx0, zy0, out int trueCount);
                    WriteMaskRaw(args[6], w, h, full);
                    Console.WriteLine($"PASTE OK {w}x{h} count={trueCount}");
                    return 0;
                }

                // --samops-rle <mask.raw> <encoded.txt> [threads] : エンコード文字列を書き出し、往復一致を自己検証。
                // threads を渡すと並列度をその値にする(大きいマスクは区間に分けて並列にエンコードするので、継ぎ目の検証用)。
                case "--samops-rle":
                {
                    if (args.Length >= 4) DebugCaptureHooks.ParallelismOverride = int.Parse(args[3], inv);
                    var (w, h, mask) = ReadMaskRaw(args[1]);
                    string encoded = MaskRle.Encode(mask, w, h);
                    File.WriteAllText(args[2], encoded);
                    // 保存の経路(MaskStateCodec)が使う EncodeIfAny: 全 false なら null、それ以外は Encode と同じ文字列。
                    bool any = Array.IndexOf(mask, true) >= 0;
                    string ifAny = MaskRle.EncodeIfAny(mask, w, h);
                    if (any ? ifAny != encoded : ifAny != null)
                    {
                        Console.Error.WriteLine("RLE FAIL EncodeIfAny");
                        return 1;
                    }
                    bool[] back = MaskRle.Decode(encoded, out int dw, out int dh);
                    if (back == null || dw != w || dh != h)
                    {
                        Console.Error.WriteLine("RLE FAIL decode");
                        return 1;
                    }
                    for (int i = 0; i < mask.Length; i++)
                        if (mask[i] != back[i]) { Console.Error.WriteLine($"RLE FAIL at {i}"); return 1; }
                    Console.WriteLine($"RLE OK {w}x{h} len={encoded.Length} any={(any ? 1 : 0)}");
                    return 0;
                }

                // --samops-rle-decode <encoded.txt> : 文字列をデコードだけする(不正・悪意ある入力の拒否検証)。
                // 出力: "RLE DECODE NULL"(拒否) / "RLE DECODE OK <w>x<h> count=<真の数>"。
                case "--samops-rle-decode":
                {
                    if (args.Length < 2) { Console.Error.WriteLine("rle-decode: <encoded.txt> required"); return 2; }
                    string encoded = File.ReadAllText(args[1]).Trim();
                    bool[] mask = MaskRle.Decode(encoded, out int w, out int h);
                    if (mask == null) { Console.WriteLine("RLE DECODE NULL"); return 0; }
                    int count = 0;
                    for (int i = 0; i < mask.Length; i++) if (mask[i]) count++;
                    Console.WriteLine($"RLE DECODE OK {w}x{h} count={count}");
                    return 0;
                }

                default:
                    Console.Error.WriteLine($"unknown samops mode: {args[0]}");
                    return 2;
            }
        }

        private static float[] ReadF32(string path, int expected)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != expected * 4)
                throw new InvalidDataException($"{path}: {bytes.Length} bytes != {expected * 4}");
            var floats = new float[expected];
            System.Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
    }
}
