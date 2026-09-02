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
                layerIndex = z.layerIndex,
                useFloodFill = z.useFloodFill,
                seedUV = (z.seedUV != null && z.seedUV.Length >= 2)
                    ? new Vector2(z.seedUV[0], z.seedUV[1]) : new Vector2(-1f, -1f),
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

            // 並列テスト実行時にスレッド数を絞れるようにする(既定=未設定=製品と同じ
            // ProcessorCount-2)。設定したときだけ効くので、通常実行の挙動は変わらない。
            var threadsEnv = Environment.GetEnvironmentVariable("IROCA_HARNESS_THREADS");
            if (!string.IsNullOrEmpty(threadsEnv)
                && int.TryParse(threadsEnv, out int threads) && threads > 0)
                DebugCaptureHooks.ParallelismOverride = threads;

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
                    case "--autotune": case "--ffcheck": case "--selcache": break;
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
                    {
                        var (iw, ih, ibytes) = ReadRaw(evPath, 1);
                        evidence = new bool[iw * ih];
                        for (int i = 0; i < evidence.Length; i++) evidence[i] = ibytes[i] != 0;
                        evW = iw; evH = ih;
                    }
                    var tune = evidence != null
                        ? ZoneAutoTuner.AnalyzeWithEvidence(pixels, w, h, z, session,
                            evidence, evW, evH,
                            excluded: useMask ? common : null,
                            maskW: useMask ? mw : 0, maskH: useMask ? mh : 0)
                        : useMask
                            ? ZoneAutoTuner.Analyze(pixels, w, h, z, session, common, mw, mh)
                            : ZoneAutoTuner.Analyze(pixels, w, h, z, session, excluded: null, maskW: 0, maskH: 0);
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

            // --ffcheck: 連結keepのクロップ転写がフル画像と一致するか自己検証(M4 完全一致プレビュー)。
            // フル画像で keep を解いてキャッシュ→同じクロップを (a)キャッシュあり (b)なし で処理し、
            // クロップ内部(境界マージン除外)をフルのクロップ領域と比較する。元入力 pixels は未改変の
            // クローンを使い、本処理に影響しない。
            if (System.Array.IndexOf(args, "--ffcheck") >= 0)
                RunFloodFillCropCheck((Color32[])pixels.Clone(), w, h, masks, zoneList, st);

            // --selcache: 選択キャッシュのヒット経路が「フル再計算」と byte 一致するか自己検証する。
            // 元入力 pixels は未改変クローンを使い、ゾーンも複製するので本処理に影響しない。
            if (System.Array.IndexOf(args, "--selcache") >= 0)
                RunSelectionCacheCheck((Color32[])pixels.Clone(), w, h, masks, zoneList, st);

            // 段階的リファインの表示画像を出力して終了（--stage。上記コメント参照）。
            // 自動調整は実機同様フル解像度の pixels で済ませてから（この上のブロック）、
            // プロキシ縮小・処理・表示縮小を PreviewView.Async と同一の鎖・丸めで行う。
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
                    ProcessZones(proxyPixels, proxyW, proxyH, masks, zoneList, st);
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
                    ProcessZones(pixels, w, h, masks, zoneList, st);
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
            // フェーズ別内訳(HSV/Match/FloodFill/...)も stderr へ。--ffcheck の余分な実行を
            // 拾わないよう、本計測の直前で購読する(全ゾーン合算の 1 レポートだけが出る)。
            DebugCaptureHooks.OnPerfReport += rep =>
            {
                if (rep.Phases != null)
                    foreach (var ph in rep.Phases)
                        Console.Error.WriteLine($"PHASE {ph.Name} {ph.TotalMs:F2}");
            };
            ProcessZones(pixels, w, h, masks, zoneList, st);
            WriteRawRgba(outPath, w, h, pixels);
            Console.WriteLine($"OK {w}x{h} -> {outPath} (zones={zoneList.Count})");
            return 0;
        }

        // ─── 単発実行・--batch・--session が共有する処理本体 ───
        // ここを経由しない処理経路を足さないこと(テストが製品と違う設定を測る事故になる)。
        // selectionCache は --session だけが渡す(製品 PreviewView の持ち越しを再現)。
        private static void ProcessZones(Color32[] pixels, int w, int h,
                                         MaskSnapshot masks, List<ColorZone> zoneList, SettingsCfg st,
                                         SelectionCache selectionCache = null)
        {
            var sw = Stopwatch.StartNew();
            PixelProcessor.ProcessPixelsArray(
                pixels, w, h, masks, zoneList,
                edgeFeather: st.edgeFeather, antiAliasCleanup: st.antiAliasCleanup,
                holeFillPasses: st.holeFillPasses, holeFillMinNeighbors: st.holeFillMinNeighbors,
                relaxedSatMin: st.relaxedSatMin, relaxedSatRamp: st.relaxedSatRamp,
                originX: 0, originY: 0, fullW: 0, fullH: 0,
                useDecontamination: st.useDecontamination, decontaminationRadius: st.decontaminationRadius,
                selectionCache: selectionCache);
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
            var cfg = JsonSerializer.Deserialize<ZonesConfig>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
                var arr = new bool[iw * ih];
                for (int i = 0; i < arr.Length; i++) arr[i] = ibytes[i] != 0;
                var dict = include
                    ? (masks.zoneIncludes ??= new Dictionary<string, ulong[]>())
                    : (masks.zones ??= new Dictionary<string, ulong[]>());
                dict[zoneId] = MaskSnapshot.Pack(arr);
            }
        }

        // ─── --batch: 同一入力・複数ゾーン設定をまとめて処理 ───
        // 各ケースは元入力のクローンから始め、マスクも複製して渡すので、
        // ケースを跨いだ状態の持ち越しは無い(単発実行と byte 一致することをテストで検証している)。
        private static int RunBatch(string batchPath)
        {
            var cfg = JsonSerializer.Deserialize<BatchConfig>(
                File.ReadAllText(batchPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var (w, h, rgba) = ReadRaw(cfg.input, 4);
            int len = w * h;
            var basePixels = new Color32[len];
            for (int i = 0; i < len; i++)
                basePixels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);

            var (mw, mh, mbytes) = ReadRaw(cfg.mask, 1);
            var common = new bool[mw * mh];
            for (int i = 0; i < common.Length; i++) common[i] = mbytes[i] != 0;
            var packed = MaskSnapshot.Pack(common);

            foreach (var c in cfg.cases)
            {
                var (zoneList, st, zoneMasks, _) = LoadZones(c.zones);
                var pixels = (Color32[])basePixels.Clone();
                var masks = new MaskSnapshot
                {
                    common = (ulong[])packed.Clone(),
                    width = mw,
                    height = mh,
                    zones = null,
                };
                AttachZoneMasks(masks, zoneMasks);
                ProcessZones(pixels, w, h, masks, zoneList, st);
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
            var cfg = JsonSerializer.Deserialize<SessionConfig>(
                File.ReadAllText(sessionPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var (w, h, rgba) = ReadRaw(cfg.input, 4);
            int len = w * h;
            var basePixels = new Color32[len];
            for (int i = 0; i < len; i++)
                basePixels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);

            var selCache = new SelectionCache();
            var ids = new Dictionary<string, string>();
            int step = 0;
            foreach (var s in cfg.steps)
            {
                var (mw, mh, mbytes) = ReadRaw(s.mask, 1);
                var common = new bool[mw * mh];
                for (int i = 0; i < common.Length; i++) common[i] = mbytes[i] != 0;
                var masks = new MaskSnapshot
                {
                    common = MaskSnapshot.Pack(common),
                    width = mw,
                    height = mh,
                    zones = null,
                };
                var (zoneList, st, zoneMasks, _) = LoadZones(s.zones, ids);
                AttachZoneMasks(masks, zoneMasks);
                var pixels = (Color32[])basePixels.Clone();
                ProcessZones(pixels, w, h, masks, zoneList, st, selCache);
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
            var mi = typeof(PixelProcessor).GetMethod(
                "BuildSelectionKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (mi == null)
            {
                Console.Error.WriteLine(
                    "BuildSelectionKey が見つかりません(リネーム?)。selkey-audit の追従修正が必要です。");
                return 3;
            }

            string KeyOf(ColorZone z) => (string)mi.Invoke(null, new object[]
            {
                z, /*edgeFeather*/0f, /*aaCleanup*/3, /*holeFillPasses*/5, /*holeFillMinNeighbors*/4,
                /*relaxedSatMin*/0.02f, /*relaxedSatRamp*/0.08f, /*commonMask*/null, /*zoneMask*/null,
                /*zoneInclude*/null, /*maskW*/0, /*maskH*/0,
            });

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
            // sourceId は UI 側(詳細プレビューの採否)専用。ハーネスは採否判定を通さず
            // parityCache を直接渡すので未設定(0)のままでよい。
            var cache = new PreviewParityCache();
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

        // 選択キャッシュ検証: 「再着色のみ変更した再生成」がキャッシュヒットでフル再計算と byte 一致し、
        // 「選択パラメータ変更」はミスして正しく再計算されることを確認する。
        //   populate_diff : 空キャッシュで populate した出力 == キャッシュ無し出力 か(0 が正)
        //   hit_vs_fresh  : ターゲット色のみ変更→ヒット復元→再着色した出力 == フル再計算 か(★0 が正)
        //   miss_vs_fresh : tolerance 変更→ミス再計算した出力 == フル再計算 か(0 が正)
        private static void RunSelectionCacheCheck(
            Color32[] input, int w, int h, MaskSnapshot masks, List<ColorZone> zonesIn, SettingsCfg st)
        {
            var zones = new List<ColorZone>();
            foreach (var z in zonesIn) { var c = z.Clone(); c.UpdateCacheIfNeeded(); zones.Add(c); }

            void Run(Color32[] px, SelectionCache cache)
            {
                PixelProcessor.ProcessPixelsArray(px, w, h, masks, zones,
                    edgeFeather: st.edgeFeather, antiAliasCleanup: st.antiAliasCleanup,
                    holeFillPasses: st.holeFillPasses, holeFillMinNeighbors: st.holeFillMinNeighbors,
                    relaxedSatMin: st.relaxedSatMin, relaxedSatRamp: st.relaxedSatRamp,
                    originX: 0, originY: 0, fullW: 0, fullH: 0,
                    cancellationToken: System.Threading.CancellationToken.None,
                    useDecontamination: st.useDecontamination, decontaminationRadius: st.decontaminationRadius,
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
                    var (w, h, rgba) = ReadRaw(args[1], 4);
                    var pixels = new Color32[w * h];
                    for (int i = 0; i < pixels.Length; i++)
                        pixels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
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
                    using (var fs = new FileStream(args[6], FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(w); bw.Write(h);
                        var bytes = new byte[w * h];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = res.maskBottomUp[i] ? (byte)1 : (byte)0;
                        bw.Write(bytes);
                    }
                    Console.WriteLine(string.Format(inv,
                        "POST channel={0} score={1:R} area={2:R} warn={3}",
                        res.channel, res.score, res.areaFrac, res.floodWarning ? 1 : 0));
                    return 0;
                }

                // --samops-refine <mask.raw> <image.raw RGBA> <out.raw> : 境界色スナップ単体
                case "--samops-refine":
                {
                    var (mw2, mh2, mbytes2) = ReadRaw(args[1], 1);
                    var mask2 = new bool[mw2 * mh2];
                    for (int i = 0; i < mask2.Length; i++) mask2[i] = mbytes2[i] != 0;
                    var (iw, ih, rgba2) = ReadRaw(args[2], 4);
                    if (iw != mw2 || ih != mh2)
                    {
                        Console.Error.WriteLine("refine: mask/image size mismatch");
                        return 2;
                    }
                    var px2 = new Color32[iw * ih];
                    for (int i = 0; i < px2.Length; i++)
                        px2[i] = new Color32(rgba2[i * 4], rgba2[i * 4 + 1], rgba2[i * 4 + 2], rgba2[i * 4 + 3]);
                    SamMaskRefine.SnapBoundary(mask2, px2, iw, ih);
                    using (var fs = new FileStream(args[3], FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(iw); bw.Write(ih);
                        var bytes = new byte[iw * ih];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = mask2[i] ? (byte)1 : (byte)0;
                        bw.Write(bytes);
                    }
                    Console.WriteLine($"REFINE OK {iw}x{ih}");
                    return 0;
                }

                // --samops-fringe <mask.raw> <image.raw RGBA> <out.raw> : 房外郭への境界拡張単体
                case "--samops-fringe":
                {
                    var (mw2, mh2, mbytes2) = ReadRaw(args[1], 1);
                    var mask2 = new bool[mw2 * mh2];
                    for (int i = 0; i < mask2.Length; i++) mask2[i] = mbytes2[i] != 0;
                    var (iw, ih, rgba2) = ReadRaw(args[2], 4);
                    if (iw != mw2 || ih != mh2)
                    {
                        Console.Error.WriteLine("fringe: mask/image size mismatch");
                        return 2;
                    }
                    var px2 = new Color32[iw * ih];
                    for (int i = 0; i < px2.Length; i++)
                        px2[i] = new Color32(rgba2[i * 4], rgba2[i * 4 + 1], rgba2[i * 4 + 2], rgba2[i * 4 + 3]);
                    SamMaskRefine.ExtendFringe(mask2, px2, iw, ih);
                    using (var fs = new FileStream(args[3], FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(iw); bw.Write(ih);
                        var bytes = new byte[iw * ih];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = mask2[i] ? (byte)1 : (byte)0;
                        bw.Write(bytes);
                    }
                    Console.WriteLine($"FRINGE OK {iw}x{ih}");
                    return 0;
                }

                // --samops-aainclude <mask.raw> <image.raw RGBA> <out.raw> : AA 遷移帯の包含単体
                case "--samops-aainclude":
                {
                    var (mw2, mh2, mbytes2) = ReadRaw(args[1], 1);
                    var mask2 = new bool[mw2 * mh2];
                    for (int i = 0; i < mask2.Length; i++) mask2[i] = mbytes2[i] != 0;
                    var (iw, ih, rgba2) = ReadRaw(args[2], 4);
                    if (iw != mw2 || ih != mh2)
                    {
                        Console.Error.WriteLine("aainclude: mask/image size mismatch");
                        return 2;
                    }
                    var px2 = new Color32[iw * ih];
                    for (int i = 0; i < px2.Length; i++)
                        px2[i] = new Color32(rgba2[i * 4], rgba2[i * 4 + 1], rgba2[i * 4 + 2], rgba2[i * 4 + 3]);
                    SamMaskRefine.IncludeAaTransition(mask2, px2, iw, ih);
                    using (var fs = new FileStream(args[3], FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(iw); bw.Write(ih);
                        var bytes = new byte[iw * ih];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = mask2[i] ? (byte)1 : (byte)0;
                        bw.Write(bytes);
                    }
                    Console.WriteLine($"AAINCLUDE OK {iw}x{ih}");
                    return 0;
                }

                // --samops-aainclude-crop <mask.raw> <image.raw RGBA> <out.raw> :
                // AA 遷移包含のクロップ実行(コミット段の経路)。--samops-aainclude と
                // 出力ビット同一であることの機械検証に使う。
                case "--samops-aainclude-crop":
                {
                    var (mw2, mh2, mbytes2) = ReadRaw(args[1], 1);
                    var mask2 = new bool[mw2 * mh2];
                    for (int i = 0; i < mask2.Length; i++) mask2[i] = mbytes2[i] != 0;
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
                    using (var fs = new FileStream(args[3], FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(iw); bw.Write(ih);
                        var bytes = new byte[iw * ih];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = mask2[i] ? (byte)1 : (byte)0;
                        bw.Write(bytes);
                    }
                    Console.WriteLine($"AAINCLUDE-CROP OK {iw}x{ih}");
                    return 0;
                }

                // --samops-covertransfer <mask.raw> <dw> <dh> <out.raw> : 被覆保存の解像度転写
                case "--samops-covertransfer":
                {
                    var (sw2, sh2, mbytes2) = ReadRaw(args[1], 1);
                    var src2 = new bool[sw2 * sh2];
                    for (int i = 0; i < src2.Length; i++) src2[i] = mbytes2[i] != 0;
                    int dw = int.Parse(args[2]), dh = int.Parse(args[3]);
                    var dst2 = SamMaskRefine.TransferCoverage(src2, sw2, sh2, dw, dh);
                    if (dst2 == null)
                    {
                        Console.Error.WriteLine("covertransfer: invalid input");
                        return 2;
                    }
                    using (var fs = new FileStream(args[4], FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(dw); bw.Write(dh);
                        var bytes = new byte[dw * dh];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = dst2[i] ? (byte)1 : (byte)0;
                        bw.Write(bytes);
                    }
                    Console.WriteLine($"COVERTRANSFER OK {sw2}x{sh2} -> {dw}x{dh}");
                    return 0;
                }

                // --samops-zoomrect <mask.raw> <clickX> <clickY> : ズームイン判定
                // (クリック成分 bbox 長辺 + クロップ矩形導出。座標は下原点)
                case "--samops-zoomrect":
                {
                    var (w, h, mbytes) = ReadRaw(args[1], 1);
                    var mask = new bool[w * h];
                    for (int i = 0; i < mask.Length; i++) mask[i] = mbytes[i] != 0;
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
                    var (w, h, rgba) = ReadRaw(args[1], 4);
                    var pixels = new Color32[w * h];
                    for (int i = 0; i < pixels.Length; i++)
                        pixels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
                    int zx0 = int.Parse(args[2], inv), zy0 = int.Parse(args[3], inv);
                    int zside = int.Parse(args[4], inv);
                    Color32[] crop = SamZoomOps.ExtractCrop(pixels, w, h, zx0, zy0, zside);
                    using (var fs = new FileStream(args[5], FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(zside); bw.Write(zside);
                        var bytes = new byte[crop.Length * 4];
                        for (int i = 0; i < crop.Length; i++)
                        {
                            bytes[i * 4] = crop[i].r; bytes[i * 4 + 1] = crop[i].g;
                            bytes[i * 4 + 2] = crop[i].b; bytes[i * 4 + 3] = crop[i].a;
                        }
                        bw.Write(bytes);
                    }
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
                    var cmask = new bool[cw * ch];
                    for (int i = 0; i < cmask.Length; i++) cmask[i] = mbytes[i] != 0;
                    int w = int.Parse(args[2], inv), h = int.Parse(args[3], inv);
                    int zx0 = int.Parse(args[4], inv), zy0 = int.Parse(args[5], inv);
                    bool[] full = SamZoomOps.PasteCrop(cmask, cw, w, h, zx0, zy0, out int trueCount);
                    using (var fs = new FileStream(args[6], FileMode.Create, FileAccess.Write))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(w); bw.Write(h);
                        var bytes = new byte[w * h];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = full[i] ? (byte)1 : (byte)0;
                        bw.Write(bytes);
                    }
                    Console.WriteLine($"PASTE OK {w}x{h} count={trueCount}");
                    return 0;
                }

                // --samops-rle <mask.raw> <encoded.txt> : エンコード文字列を書き出し、往復一致を自己検証
                case "--samops-rle":
                {
                    var (w, h, mbytes) = ReadRaw(args[1], 1);
                    var mask = new bool[w * h];
                    for (int i = 0; i < mask.Length; i++) mask[i] = mbytes[i] != 0;
                    string encoded = MaskRle.Encode(mask, w, h);
                    File.WriteAllText(args[2], encoded);
                    bool[] back = MaskRle.Decode(encoded, out int dw, out int dh);
                    if (back == null || dw != w || dh != h)
                    {
                        Console.Error.WriteLine("RLE FAIL decode");
                        return 1;
                    }
                    for (int i = 0; i < mask.Length; i++)
                        if (mask[i] != back[i]) { Console.Error.WriteLine($"RLE FAIL at {i}"); return 1; }
                    Console.WriteLine($"RLE OK {w}x{h} len={encoded.Length}");
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
