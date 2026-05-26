using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace VRCAvatarColorChanger.DebugTools
{
    /// <summary>
    /// <see cref="DebugCaptureContext"/> のスナップショット群を PNG として書き出す。
    /// 保存先は <c>&lt;Project&gt;/UserSettings/VACC/DebugDumps/&lt;sourceName&gt;/&lt;timestamp&gt;/</c>。
    /// </summary>
    internal static class DebugDumpStore
    {
        private const string DumpDirRelative = "UserSettings/VACC/DebugDumps";

        /// <summary>
        /// 指定 context 全体を PNG + manifest.json として書き出し、書き出し先ディレクトリを返す。
        /// 失敗時は null を返してログにエラーを残す。
        /// </summary>
        public static string DumpAll(DebugCaptureContext ctx, string sourceTextureName)
        {
            if (ctx == null || ctx.Snapshots.Count == 0)
            {
                Debug.LogWarning("[VACC.Debug] DumpAll: 空の context がわたされました。");
                return null;
            }

            string safeName = SanitizeFileName(string.IsNullOrEmpty(sourceTextureName) ? "unknown" : sourceTextureName);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outDir = Path.Combine(projectRoot, DumpDirRelative, safeName, timestamp);

            try
            {
                Directory.CreateDirectory(outDir);

                // zone × stage の strength と delta を書き出し
                var fileEntries = new List<string>();
                int globalIdx = 0;
                var stageIndexPerZone = new Dictionary<string, int>();
                foreach (var snap in ctx.Snapshots)
                {
                    int stageIdx;
                    if (!stageIndexPerZone.TryGetValue(snap.zoneId, out stageIdx)) stageIdx = 0;
                    stageIndexPerZone[snap.zoneId] = stageIdx + 1;

                    string baseName = $"{stageIdx:D2}_{SanitizeFileName(snap.zoneId)}_{snap.stageName}";
                    string strengthPath = Path.Combine(outDir, baseName + "_strength.png");
                    SaveGrayscalePng(snap.strengthQuantized, snap.width, snap.height, strengthPath);
                    fileEntries.Add(Path.GetFileName(strengthPath));

                    if (snap.deltaQuantized != null)
                    {
                        string deltaPath = Path.Combine(outDir, baseName + "_delta.png");
                        SaveDeltaPng(snap.deltaQuantized, snap.width, snap.height, deltaPath);
                        fileEntries.Add(Path.GetFileName(deltaPath));
                    }
                    globalIdx++;
                }

                // zone ごとの ownership と recolor branch
                foreach (var zoneId in ctx.CollectZoneIds())
                {
                    var ownershipTex = BuildAndSaveOwnership(ctx, zoneId, outDir);
                    if (ownershipTex != null) fileEntries.Add(ownershipTex);

                    var branchTex = BuildAndSaveRecolorBranch(ctx, zoneId, outDir);
                    if (branchTex != null) fileEntries.Add(branchTex);
                }

                // manifest.json
                WriteManifest(outDir, ctx, sourceTextureName, timestamp, fileEntries);

                return outDir;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VACC.Debug] DumpAll failed: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        // ──────────────────────────────────────────────────────
        private static void SaveGrayscalePng(byte[] quantized, int w, int h, string path)
        {
            if (quantized == null || quantized.Length != w * h) return;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
            try
            {
                var pixels = new Color32[w * h];
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte v = quantized[i];
                    pixels[i] = new Color32(v, v, v, 255);
                }
                FlipVertical(pixels, w, h);
                tex.SetPixels32(pixels);
                tex.Apply(false);
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static void SaveDeltaPng(byte[] delta, int w, int h, string path)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
            try
            {
                var pixels = new Color32[w * h];
                for (int i = 0; i < pixels.Length; i++)
                {
                    int signed = delta[i] - 128;
                    if (signed > 0)
                    {
                        byte mag = (byte)Mathf.Min(255, signed * 2);
                        pixels[i] = new Color32(0, mag, 0, 255);
                    }
                    else if (signed < 0)
                    {
                        byte mag = (byte)Mathf.Min(255, -signed * 2);
                        pixels[i] = new Color32(mag, 0, 0, 255);
                    }
                    else
                    {
                        pixels[i] = new Color32(32, 32, 32, 255);
                    }
                }
                FlipVertical(pixels, w, h);
                tex.SetPixels32(pixels);
                tex.Apply(false);
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static string BuildAndSaveOwnership(DebugCaptureContext ctx, string zoneId, string outDir)
        {
            var snaps = new List<StageSnapshot>();
            foreach (var s in ctx.Snapshots)
                if (s.zoneId == zoneId) snaps.Add(s);
            if (snaps.Count == 0) return null;

            int w = snaps[0].width, h = snaps[0].height;
            int len = w * h;
            byte[] owner = new byte[len];
            for (int i = 0; i < len; i++) owner[i] = 255;
            for (int sIdx = 0; sIdx < snaps.Count; sIdx++)
            {
                var s = snaps[sIdx];
                if (s.strengthQuantized == null || s.strengthQuantized.Length != len) continue;
                for (int i = 0; i < len; i++)
                    if (owner[i] == 255 && s.strengthQuantized[i] >= 128) owner[i] = (byte)sIdx;
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
            try
            {
                var pixels = new Color32[len];
                for (int i = 0; i < len; i++)
                {
                    if (owner[i] == 255) { pixels[i] = new Color32(0, 0, 0, 0); continue; }
                    float hue = (owner[i] / (float)Mathf.Max(1, snaps.Count)) % 1f;
                    Color c = Color.HSVToRGB(hue, 0.8f, 0.95f);
                    pixels[i] = new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), 255);
                }
                FlipVertical(pixels, w, h);
                tex.SetPixels32(pixels);
                tex.Apply(false);
                string fileName = $"{SanitizeFileName(zoneId)}_ownership.png";
                string path = Path.Combine(outDir, fileName);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                return fileName;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static string BuildAndSaveRecolorBranch(DebugCaptureContext ctx, string zoneId, string outDir)
        {
            if (!ctx.BranchMaps.TryGetValue(zoneId, out var branchMap)) return null;
            var snap = ctx.FindSnapshot(zoneId, DebugStages.Recolor);
            if (snap == null) return null;
            int w = snap.width, h = snap.height;
            int len = w * h;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
            try
            {
                var pixels = new Color32[len];
                for (int i = 0; i < len; i++)
                {
                    switch ((DebugBranch)branchMap[i])
                    {
                        case DebugBranch.Base:          pixels[i] = new Color32(0, 200, 200, 255); break;
                        case DebugBranch.Highlight:     pixels[i] = new Color32(255, 160, 0, 255); break;
                        case DebugBranch.Shadow:        pixels[i] = new Color32(160, 60, 200, 255); break;
                        case DebugBranch.Decontaminate: pixels[i] = new Color32(255, 230, 0, 255); break;
                        default:                        pixels[i] = new Color32(0, 0, 0, 0); break;
                    }
                }
                FlipVertical(pixels, w, h);
                tex.SetPixels32(pixels);
                tex.Apply(false);
                string fileName = $"{SanitizeFileName(zoneId)}_recolorBranch.png";
                string path = Path.Combine(outDir, fileName);
                File.WriteAllBytes(path, tex.EncodeToPNG());
                return fileName;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static void WriteManifest(string outDir, DebugCaptureContext ctx, string sourceName, string timestamp, List<string> files)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"sourceTextureName\": \"{EscapeJson(sourceName ?? "")}\",");
            sb.AppendLine($"  \"timestamp\": \"{timestamp}\",");
            sb.AppendLine($"  \"width\": {ctx.Width},");
            sb.AppendLine($"  \"height\": {ctx.Height},");
            sb.AppendLine($"  \"snapshotCount\": {ctx.Snapshots.Count},");
            sb.AppendLine("  \"zones\": [");
            var zones = ctx.CollectZoneIds();
            for (int i = 0; i < zones.Count; i++)
            {
                sb.Append($"    \"{EscapeJson(zones[i])}\"");
                if (i < zones.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ],");
            sb.AppendLine("  \"stages\": [");
            for (int i = 0; i < ctx.Snapshots.Count; i++)
            {
                var s = ctx.Snapshots[i];
                sb.Append($"    {{ \"zoneId\": \"{EscapeJson(s.zoneId)}\", \"stageName\": \"{EscapeJson(s.stageName)}\" }}");
                if (i < ctx.Snapshots.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ],");
            sb.AppendLine("  \"files\": [");
            for (int i = 0; i < files.Count; i++)
            {
                sb.Append($"    \"{EscapeJson(files[i])}\"");
                if (i < files.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(outDir, "manifest.json"), sb.ToString());
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                bool ok = true;
                foreach (var inv in invalid) if (ch == inv) { ok = false; break; }
                sb.Append(ok ? ch : '_');
            }
            return sb.ToString();
        }

        private static void FlipVertical(Color32[] pixels, int w, int h)
        {
            for (int y = 0; y < h / 2; y++)
            {
                int top = y * w;
                int bot = (h - 1 - y) * w;
                for (int x = 0; x < w; x++)
                {
                    var t = pixels[top + x];
                    pixels[top + x] = pixels[bot + x];
                    pixels[bot + x] = t;
                }
            }
        }
    }
}
