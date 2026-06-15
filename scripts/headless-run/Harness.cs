// 実 Unity DLL を参照して PixelProcessor を Unity なしで実行する CLI ハーネス。
// Code/ と同一アセンブリにコンパイルされるので internal にアクセスできる。
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    // DebugCaptureHooks が UI イベントで参照するだけのスタブ(headless では未使用)。
    internal class VACCWindow { }

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

        public static int Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: Harness <in.raw RGBA> <mask.raw 1=exclude> <out.raw RGBA> [sampleR sampleG sampleB targetR targetG targetB tolerance]");
                return 2;
            }
            string inPath = args[0], maskPath = args[1], outPath = args[2];

            // 既定はユーザー実設定(クリーム→黒, tol 0.32)。args で上書き可。
            int sr = 252, sg = 247, sb = 243, tr = 0, tg = 0, tb = 0;
            float tol = 0.32f;
            if (args.Length >= 10)
            {
                sr = int.Parse(args[3]); sg = int.Parse(args[4]); sb = int.Parse(args[5]);
                tr = int.Parse(args[6]); tg = int.Parse(args[7]); tb = int.Parse(args[8]);
                tol = float.Parse(args[9]);
            }

            var (w, h, rgba) = ReadRaw(inPath, 4);
            int len = w * h;
            var pixels = new Color32[len];
            for (int i = 0; i < len; i++)
                pixels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);

            var (mw, mh, mbytes) = ReadRaw(maskPath, 1);
            var common = new bool[mw * mh];
            for (int i = 0; i < common.Length; i++) common[i] = mbytes[i] != 0;
            var masks = new MaskSnapshot { common = common, width = mw, height = mh, zones = null };

            var zone = new ColorZone
            {
                name = "Bandana-Triangle",
                enabled = true,
                mode = SelectionMode.ColorPick,
                sampleColor = new Color(sr / 255f, sg / 255f, sb / 255f, 1f),
                targetColor = new Color(tr / 255f, tg / 255f, tb / 255f, 1f),
                tolerance = tol,
                valueBlend = 1.0f,
                edgeSoftness = 0.0f,
                saturationStrictness = 0.5f,
                saturationGuard = 0.0f,
                chromaThreshold = 0.05f,
                highlightRecovery = true,
                highlightBandExpand = true,
                applyHighlightWash = false,
                autoHighlightSample = false,
                autoRecolorAnchor = false,
                outputSaturation = 1.0f,
                shadowDesaturation = 0.35f,
                shadowForgivenessSatMin = 0.05f,
            };
            zone.EnsureId();
            zone.UpdateCacheIfNeeded();

            PixelProcessor.ProcessPixelsArray(
                pixels, w, h, masks, new List<ColorZone> { zone },
                edgeFeather: 0f, antiAliasCleanup: 3,
                holeFillPasses: 5, holeFillMinNeighbors: 4,
                relaxedSatMin: 0.02f, relaxedSatRamp: 0.08f,
                originX: 0, originY: 0, fullW: 0, fullH: 0,
                useDecontamination: true, decontaminationRadius: 4);

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
            Console.WriteLine($"OK {w}x{h} -> {outPath}");
            return 0;
        }
    }
}
