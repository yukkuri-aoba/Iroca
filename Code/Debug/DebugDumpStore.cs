// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Iroca.DebugTools
{
    /// <summary>
    /// <see cref="DebugCaptureContext"/> のスナップショット群を PNG として書き出す。
    /// 保存先は <c>Library/Iroca/Debug/&lt;sourceName&gt;/&lt;timestamp&gt;/</c>。
    /// <c>Assets/</c> 外の <c>Library/</c> に置くことで Unity のインポートを回避し、
    /// Project ビューへの表示を防ぐ。<c>Library/</c> は Unity のデフォルト .gitignore 対象。
    /// </summary>
    internal static class DebugDumpStore
    {
        // Assets/ 外に置くことで Unity のインポートを回避する。Library/ は既定で gitignore 対象。
        private const string DumpDirInLibrary = "Library/Iroca/Debug";

        /// <summary>
        /// 指定 context 全体を PNG + manifest.json として書き出し、書き出し先ディレクトリの
        /// 絶対パスを返す。失敗時は null を返してログにエラーを残す。
        /// <c>Library/</c> 配下に書き出すため <see cref="AssetDatabase.Refresh"/> は不要。
        /// </summary>
        public static string DumpAll(DebugCaptureContext ctx, string sourceTextureName)
        {
            if (ctx == null || ctx.Snapshots.Count == 0)
            {
                Debug.LogWarning("[Iroca.Debug] DumpAll: 空の context がわたされました。");
                return null;
            }

            string safeName = SanitizeFileName(string.IsNullOrEmpty(sourceTextureName) ? "unknown" : sourceTextureName);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outDir = Path.GetFullPath(Path.Combine(projectRoot, DumpDirInLibrary, safeName, timestamp));

            try
            {
                Directory.CreateDirectory(outDir);

                var fileEntries = new List<string>();
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
                }

                foreach (var zoneId in ctx.CollectZoneIds())
                {
                    var ownershipTex = BuildAndSaveOwnership(ctx, zoneId, outDir);
                    if (ownershipTex != null) fileEntries.Add(ownershipTex);

                    var branchTex = BuildAndSaveRecolorBranch(ctx, zoneId, outDir);
                    if (branchTex != null) fileEntries.Add(branchTex);
                }

                WriteManifest(outDir, ctx, sourceTextureName, timestamp, fileEntries);

                return outDir;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Iroca.Debug] DumpAll failed: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        private static void SaveGrayscalePng(byte[] quantized, int w, int h, string path)
        {
            if (quantized == null || quantized.Length != w * h) return;
            DebugImaging.WritePng(path, DebugImaging.Grayscale(quantized, w * h), w, h);
        }

        private static void SaveDeltaPng(byte[] delta, int w, int h, string path)
        {
            DebugImaging.WritePng(path, DebugImaging.Delta(delta, w * h), w, h);
        }

        private static string BuildAndSaveOwnership(DebugCaptureContext ctx, string zoneId, string outDir)
        {
            if (!DebugImaging.TryBuildOwnership(ctx, zoneId, out var pixels, out int w, out int h)) return null;

            string fileName = $"{SanitizeFileName(zoneId)}_ownership.png";
            string path = Path.Combine(outDir, fileName);
            DebugImaging.WritePng(path, pixels, w, h);
            return fileName;
        }

        private static string BuildAndSaveRecolorBranch(DebugCaptureContext ctx, string zoneId, string outDir)
        {
            if (!ctx.BranchMaps.TryGetValue(zoneId, out var branchMap)) return null;
            var snap = ctx.FindSnapshot(zoneId, DebugStages.Recolor);
            if (snap == null) return null;
            int w = snap.width, h = snap.height;
            int len = w * h;

            string fileName = $"{SanitizeFileName(zoneId)}_recolorBranch.png";
            string path = Path.Combine(outDir, fileName);
            DebugImaging.WritePng(path, DebugImaging.RecolorBranch(branchMap, len), w, h);
            return fileName;
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
    }
}
