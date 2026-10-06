// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Iroca.DebugTools
{
    /// <summary>
    /// キャプチャから可視化用の画素配列を作る式と、画素配列の PNG 書き出し。
    /// <see cref="DebugWindow"/>（表示）・<see cref="DebugDumpStore"/>（段階ごとの PNG）・
    /// <see cref="ReproDump"/>（再現データ）が共有する唯一の実装なので、表示と書き出しの色は常に一致する。
    /// Texture2D の生成（表示側の filterMode を含む）は呼び出し側が持つ。
    /// </summary>
    internal static class DebugImaging
    {
        /// <summary>量子化 strength（0..255）を不透明なグレースケールにする（白 = 1.0 / 黒 = 0.0）。</summary>
        internal static Color32[] Grayscale(byte[] src, int len)
        {
            var pixels = new Color32[len];
            for (int i = 0; i < pixels.Length; i++)
            {
                byte v = src[i];
                pixels[i] = new Color32(v, v, v, 255);
            }
            return pixels;
        }

        /// <summary>
        /// 128 中央の差分を色にする（緑 = 強度上昇 / 赤 = 強度低下 / 黒 = 変化なし）。
        /// </summary>
        internal static Color32[] Delta(byte[] src, int len)
        {
            var pixels = new Color32[len];
            // 振幅 ×4 で増幅。背景（差分 0）は純黒にして変化を強調。
            for (int i = 0; i < pixels.Length; i++)
            {
                int signed = src[i] - 128;
                if (signed > 0)
                {
                    byte mag = (byte)Mathf.Min(255, signed * 4);
                    pixels[i] = new Color32(0, mag, 0, 255);
                }
                else if (signed < 0)
                {
                    byte mag = (byte)Mathf.Min(255, -signed * 4);
                    pixels[i] = new Color32(mag, 0, 0, 255);
                }
                else
                {
                    pixels[i] = new Color32(0, 0, 0, 255);
                }
            }
            return pixels;
        }

        /// <summary>
        /// ピクセルごとに最初に strength≥0.5（量子化 128）に達した段階を hue 円で色分けする
        /// （未到達は透明）。寸法は zone の最初のスナップショットに合わせ、長さの違う段は飛ばす。
        /// zone のスナップショットが無ければ false。
        /// </summary>
        internal static bool TryBuildOwnership(DebugCaptureContext ctx, string zoneId,
            out Color32[] pixels, out int w, out int h)
        {
            var snaps = new List<StageSnapshot>();
            foreach (var snap in ctx.Snapshots)
                if (snap.zoneId == zoneId) snaps.Add(snap);
            if (snaps.Count == 0)
            {
                pixels = null;
                w = 0;
                h = 0;
                return false;
            }

            w = snaps[0].width;
            h = snaps[0].height;
            int len = w * h;
            byte[] owner = new byte[len];
            for (int i = 0; i < len; i++) owner[i] = 255;
            for (int sIdx = 0; sIdx < snaps.Count; sIdx++)
            {
                var s = snaps[sIdx];
                if (s.strengthQuantized == null || s.strengthQuantized.Length != len) continue;
                for (int i = 0; i < len; i++)
                {
                    if (owner[i] == 255 && s.strengthQuantized[i] >= 128) owner[i] = (byte)sIdx;
                }
            }

            pixels = new Color32[len];
            for (int i = 0; i < len; i++)
            {
                if (owner[i] == 255) { pixels[i] = new Color32(0, 0, 0, 0); continue; }
                pixels[i] = StageColor(owner[i], snaps.Count);
            }
            return true;
        }

        /// <summary>Recolor 段のサブブランチ（<see cref="DebugBranch"/>）を色分けする（None は透明）。</summary>
        internal static Color32[] RecolorBranch(byte[] branchMap, int len)
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
            return pixels;
        }

        private static Color32 StageColor(int stageIdx, int totalStages)
        {
            float hue = (stageIdx / (float)Mathf.Max(1, totalStages)) % 1f;
            Color c = Color.HSVToRGB(hue, 0.8f, 0.95f);
            return new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), 255);
        }

        /// <summary>画素配列（下原点）を一時 Texture2D 経由で PNG に書く。一時テクスチャは必ず破棄する。</summary>
        internal static void WritePng(string path, Color32[] pixels, int w, int h)
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
    }
}
