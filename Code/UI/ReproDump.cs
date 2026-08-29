// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 再現データ書き出し: 現在のセッション（テクスチャ・ゾーン・マスク・設定）を、
    /// headless ハーネス / dev_safe の回帰テストがそのまま読める 1 フォルダに書き出す。
    /// 実際に使っていて「ここが変」と思った瞬間の入力一式は後から復元できないため、
    /// その場でのワンアクション保存が回帰ケース化の取り込み口になる。
    ///
    /// 書式（すべて表示どおりの向きの PNG。ハーネスへの受け渡しと seedUV の v 反転は
    /// dev_safe/Tests/regression/test_repro_cases.py が行う）:
    ///   texture.png       true source 画素（エクスポートと同一経路のフル解像度）
    ///   zones.json        有効ゾーン + グローバル設定（ハーネス --zones と同一スキーマ =
    ///                     IrocaAutomation.ZoneDto/SettingsDto を共有）
    ///   mask_common.png   共通除外マスク（白=除外。マスク解像度のまま）
    ///   zone{i}_exclude.png / zone{i}_include.png   ゾーン別マスク（存在するものだけ）
    ///   meta.json         アセットパス・寸法・バージョン・ゾーン対応表・extraSamples・
    ///                     sampleUV(スポイト位置、下原点 UV)・autoTune(最後の自動調整の由来:
    ///                     証拠の有無・正規化の有無・導出診断。自動調整していなければ空)
    /// </summary>
    internal static class ReproDump
    {
        // 前回の書き出し先(EditorPrefs)。回帰ケース化の運用は「見つけたその場で書き出す」の
        // 積み重ねなので、毎回フォルダを辿り直す摩擦を無くす。開発機で一度
        // dev_safe/Tests/repro_cases を選べば、以後は書き出し=取り込み(移動不要)になる。
        private const string LastDirPrefKey = "Iroca.ReproDump.LastDir";

        [MenuItem(IrocaConsts.MenuPath + "/再現データを書き出す...")]
        private static void ExportMenu()
        {
            IrocaWindow win = null;
            foreach (var w in Resources.FindObjectsOfTypeAll<IrocaWindow>()) { win = w; break; }
            if (win == null || win.SourceTexture == null)
            {
                EditorUtility.DisplayDialog("いろか",
                    "書き出す対象がありません。いろかウィンドウでテクスチャを開いてから実行してください。", "OK");
                return;
            }
            string remembered = EditorPrefs.GetString(LastDirPrefKey, "");
            if (!Directory.Exists(remembered)) remembered = "";
            string baseDir = EditorUtility.SaveFolderPanel("再現データの書き出し先", remembered, "");
            if (string.IsNullOrEmpty(baseDir)) return;
            EditorPrefs.SetString(LastDirPrefKey, baseDir);
            try
            {
                string outDir = Export(win, baseDir);
                EditorUtility.RevealInFinder(outDir);
                Debug.Log($"[Iroca] 再現データを書き出しました: {outDir}\n"
                          + "回帰ケース化: フォルダを dev_safe/Tests/repro_cases/<名前>/ に置く"
                          + "(詳細は同フォルダの README.md)");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("いろか", $"書き出しに失敗しました: {ex.Message}", "OK");
            }
        }

        internal static string Export(IrocaWindow win, string baseDir)
        {
            var tex = win.SourceTexture;
            if (!win.Preview.TryGetTrueSourcePixels(tex, out var px, out int w, out int h))
                throw new InvalidOperationException(
                    "ソース画素を取得できません（元ファイル読込失敗かつ非 Readable）");

            string dir = Path.Combine(baseDir, $"iroca_repro_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(dir);

            WritePng(Path.Combine(dir, "texture.png"), px, w, h);

            var zones = new List<ColorZone>();
            foreach (var z in win.Session.zones)
                if (z != null && z.enabled) zones.Add(z);

            // zones.json はハーネス --zones と同一スキーマ。DTO を自動化と共有して乖離を防ぐ
            // （スキーマ整合は test_zones_schema_parity.py が機械検査している）。
            var req = new IrocaAutomation.ZonesRequest();
            foreach (var z in zones) req.zones.Add(ToDto(z));
            req.settings = ToSettingsDto(win.Session);
            File.WriteAllText(Path.Combine(dir, "zones.json"), JsonUtility.ToJson(req, true));

            var snap = win.BuildMaskSnapshot();
            var meta = new MetaDto
            {
                assetPath = AssetDatabase.GetAssetPath(tex),
                textureW = w, textureH = h,
                maskW = snap?.width ?? 0, maskH = snap?.height ?? 0,
                unityVersion = Application.unityVersion,
                packageVersion = PackageVersion(),
                createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            if (snap != null)
            {
                WriteMaskPng(Path.Combine(dir, "mask_common.png"), snap.common, snap.width, snap.height);
                for (int i = 0; i < zones.Count; i++)
                {
                    var zm = new ZoneMetaDto { name = zones[i].name, id = zones[i].id };
                    if (zones[i].extraSamples != null)
                        foreach (var c in zones[i].extraSamples)
                            zm.extraSamples.AddRange(new[] { c.r, c.g, c.b });
                    if (zones[i].HasSampleUV)
                        zm.sampleUV.AddRange(new[] { zones[i].sampleUV.x, zones[i].sampleUV.y });
                    zm.autoTune = win.AutoTuneProvenance(zones[i].id);
                    ulong[] packed;
                    if (snap.zones != null && snap.zones.TryGetValue(zones[i].id, out packed) && packed != null)
                    {
                        zm.excludeMask = $"zone{i}_exclude.png";
                        WriteMaskPng(Path.Combine(dir, zm.excludeMask), packed, snap.width, snap.height);
                    }
                    if (snap.zoneIncludes != null && snap.zoneIncludes.TryGetValue(zones[i].id, out packed) && packed != null)
                    {
                        zm.includeMask = $"zone{i}_include.png";
                        WriteMaskPng(Path.Combine(dir, zm.includeMask), packed, snap.width, snap.height);
                    }
                    meta.zones.Add(zm);
                }
            }
            File.WriteAllText(Path.Combine(dir, "meta.json"), JsonUtility.ToJson(meta, true));
            return dir;
        }

        private static string PackageVersion()
        {
            try
            {
                var info = UnityEditor.PackageManager.PackageInfo
                    .FindForAssembly(typeof(ReproDump).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;
            }
            catch { /* 埋め込み配置などで解決できない場合 */ }
            return "unknown";
        }

        private static IrocaAutomation.ZoneDto ToDto(ColorZone z)
        {
            return new IrocaAutomation.ZoneDto
            {
                name = z.name,
                sample = new[] { z.sampleColor.r, z.sampleColor.g, z.sampleColor.b },
                target = new[] { z.targetColor.r, z.targetColor.g, z.targetColor.b },
                tolerance = z.tolerance,
                valueBlend = z.valueBlend,
                edgeSoftness = z.edgeSoftness,
                saturationStrictness = z.saturationStrictness,
                saturationGuard = z.saturationGuard,
                chromaThreshold = z.chromaThreshold,
                shadowDesaturation = z.shadowDesaturation,
                shadowForgivenessSatMin = z.shadowForgivenessSatMin,
                outputSaturation = z.outputSaturation,
                highlightRecovery = z.highlightRecovery,
                highlightBandExpand = z.highlightBandExpand,
                applyHighlightWash = z.applyHighlightWash,
                autoHighlightSample = z.autoHighlightSample,
                autoRecolorAnchor = z.autoRecolorAnchor,
                layerIndex = z.layerIndex,
                useFloodFill = z.useFloodFill,
                // 下原点 UV のまま書く(表示向き PNG に合わせた v 反転は取り込み側が行う)。
                seedUV = z.seedUV.x >= 0f ? new[] { z.seedUV.x, z.seedUV.y } : null,
            };
        }

        private static IrocaAutomation.SettingsDto ToSettingsDto(IrocaSessionState s)
        {
            return new IrocaAutomation.SettingsDto
            {
                edgeFeather = s.edgeFeather,
                antiAliasCleanup = s.antiAliasCleanup,
                holeFillPasses = s.holeFillPasses,
                holeFillMinNeighbors = s.holeFillMinNeighbors,
                relaxedSatMin = s.relaxedSatMin,
                relaxedSatRamp = s.relaxedSatRamp,
                useDecontamination = s.useDecontamination,
                decontaminationRadius = s.decontaminationRadius,
            };
        }

        private static void WritePng(string path, Color32[] pixels, int w, int h)
        {
            var tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                tmp.SetPixels32(pixels);
                tmp.Apply(false);
                File.WriteAllBytes(path, tmp.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tmp);
            }
        }

        private static void WriteMaskPng(string path, ulong[] packed, int w, int h)
        {
            var px = new Color32[w * h];
            var on = new Color32(255, 255, 255, 255);
            var off = new Color32(0, 0, 0, 255);
            for (int i = 0; i < px.Length; i++)
                px[i] = MaskSnapshot.GetBit(packed, i) ? on : off;
            WritePng(path, px, w, h);
        }

        [Serializable]
        private class ZoneMetaDto
        {
            public string name = "";
            public string id = "";
            public string excludeMask = "";
            public string includeMask = "";
            // [r,g,b, r,g,b, ...] のフラット列。JsonUtility が float[][] を書けないため。
            public List<float> extraSamples = new List<float>();
            // スポイト位置 [u, v](下原点、0-1)。未設定なら空。自動調整の証拠(SAM セグメント)は
            // この位置から取られるので、失敗ケースの再現・調査に要る。
            public List<float> sampleUV = new List<float>();
            // 最後に適用した自動調整の由来(IrocaWindow.AutoTuneProvenance)。未実行なら空。
            public string autoTune = "";
        }

        [Serializable]
        private class MetaDto
        {
            public string assetPath = "";
            public int textureW, textureH;
            public int maskW, maskH;
            public string unityVersion = "";
            public string packageVersion = "";
            public string createdAt = "";
            public List<ZoneMetaDto> zones = new List<ZoneMetaDto>();
        }
    }
}
