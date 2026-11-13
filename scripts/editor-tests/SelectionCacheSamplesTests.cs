// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 標本だけを控える選択キャッシュ(<see cref="SelectionCache.ForSamples"/>)が返す強さは、全画素を控えて同じ画素を読んだ値と
    /// ビットまで同じこと。自動化の調査(iroca_inspect の points・coverage)が、ゾーンの数 × 全画素の控えを持たずに済むための作り。
    /// </summary>
    public class SelectionCacheSamplesTests {
        private const int W = 96, H = 64;

        // 左に赤(明るさの勾配つき)、右に青、間に白。赤と青のゾーン、共通の除外(縦の帯)とゾーンの除外(横の帯)。
        private static Color32[] Scene() {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    byte v = (byte)(150 + (y * 90) / H);
                    px[y * W + x] = x < 40 ? new Color32(v, 40, 40, 255)
                        : x < 56 ? new Color32(250, 250, 250, 255)
                        : new Color32(40, 90, v, 255);
                }
            }
            return px;
        }

        private static ColorZone Zone(string name, Color sample, Color target) {
            var z = new ColorZone {
                name = name,
                sampleColor = sample,
                sampleColorSet = true,
                targetColor = target,
                targetColorSet = true,
                tolerance = 0.25f,
                edgeSoftness = 0.4f,
            };
            z.EnsureId();
            return z;
        }

        [Test]
        public void SampledCacheReturnsTheSameStrengthsAsTheFullCache() {
            var src = Scene();
            var red = Zone("red", new Color(200 / 255f, 40 / 255f, 40 / 255f), new Color(0.1f, 0.6f, 0.6f));
            var blue = Zone("blue", new Color(40 / 255f, 90 / 255f, 200 / 255f), new Color(0.9f, 0.8f, 0.1f));
            var zones = new List<ColorZone> { red, blue };

            var common = new bool[W * H];
            var redExcluded = new bool[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    common[y * W + x] = x >= 10 && x < 14;
                    redExcluded[y * W + x] = y >= 20 && y < 24;
                }
            }

            // 読む画素: 全体に散らした格子と、除外の帯の上、端。
            var indices = new List<int>();
            for (int y = 1; y < H; y += 3) {
                for (int x = 0; x < W; x += 5) {
                    indices.Add(y * W + x);
                }
            }
            indices.Add(21 * W + 5);          // ゾーンの除外の上
            indices.Add(30 * W + 11);         // 共通の除外の上
            indices.Add(0);
            indices.Add(W * H - 1);
            var idx = indices.ToArray();

            var settings = RecolorSettings.Default;
            Color32[] Run(SelectionCache cache) {
                var masks = MaskSnapshot.FromBuffers(W, H, common,
                    new Dictionary<string, bool[]> { { red.id, redExcluded } }, null);
                try {
                    var px = (Color32[])src.Clone();
                    PixelProcessor.ProcessPixelsArray(px, W, H, masks, zones, settings, default, null, cache, src);
                    return px;
                } finally {
                    masks.Release();
                }
            }

            using var full = new SelectionCache();
            using var sampled = SelectionCache.ForSamples(idx);
            var outFull = Run(full);
            var outSampled = Run(sampled);
            CollectionAssert.AreEqual(outFull, outSampled, "控え方で色替えの出力が変わった");

            int nonZero = 0;
            foreach (var z in zones) {
                var a = new float[idx.Length];
                var b = new float[idx.Length];
                Assert.IsTrue(full.TryReadLatest(z.id, W, H, idx, a), $"{z.name}: 全画素の控えが読めない");
                Assert.IsTrue(sampled.TryReadSamples(z.id, W, H, b), $"{z.name}: 標本の控えが読めない");
                for (int i = 0; i < idx.Length; i++) {
                    Assert.AreEqual(System.BitConverter.SingleToInt32Bits(a[i]), System.BitConverter.SingleToInt32Bits(b[i]),
                        $"{z.name}: 画素 {idx[i]} の強さが違う({a[i]} と {b[i]})");
                    if (a[i] > 0f) {
                        nonZero++;
                    }
                }
                // 標本の控えは、全画素の読み方・通常の控えは標本の読み方に答えない。
                Assert.IsFalse(sampled.TryReadLatest(z.id, W, H, idx, new float[idx.Length]), "標本の控えが全画素の読み方に答えた");
                Assert.IsFalse(full.TryReadSamples(z.id, W, H, new float[idx.Length]), "通常の控えが標本の読み方に答えた");
            }
            Assert.Greater(nonZero, 20, "選択された画素がほとんど無い(テストの場面が空振り)");

            // 除外の上の 2 画素は、赤では 0(共通・ゾーンの除外を掛けて返す)。
            var r = new float[idx.Length];
            sampled.TryReadSamples(red.id, W, H, r);
            Assert.AreEqual(0f, r[idx.Length - 4], "ゾーンの除外の画素が 0 でない");
            Assert.AreEqual(0f, r[idx.Length - 3], "共通の除外の画素が 0 でない");

            // 2 回目: 通常の控えは選択を使い回し、標本の控えは毎回計算する。どちらも出力は同じ。
            CollectionAssert.AreEqual(outFull, Run(full));
            CollectionAssert.AreEqual(outFull, Run(sampled));
        }

        [Test]
        public void EmptySampleListStoresNothing() {
            var src = Scene();
            var red = Zone("red", new Color(200 / 255f, 40 / 255f, 40 / 255f), new Color(0.1f, 0.6f, 0.6f));
            using var sampled = SelectionCache.ForSamples(new int[0]);
            var px = (Color32[])src.Clone();
            PixelProcessor.ProcessPixelsArray(px, W, H, null, new List<ColorZone> { red }, RecolorSettings.Default,
                default, null, sampled, src);
            Assert.IsFalse(sampled.TryReadSamples(red.id, W, H, new float[0]));
            Assert.AreEqual(0, IrocaNativeMemory.LiveBytesOf(NativeUsage.Selection), "標本が空なのに選択の控えを持った");
        }
    }
}
