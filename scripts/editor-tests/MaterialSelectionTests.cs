// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 新しい選択(T5)が Unity の中でも動くこと。切り替えは開発者向け(<see cref="DebugCaptureHooks.UseMaterialSelection"/>)で、
    /// 精度は headless の回帰が測るので、ここでは配線だけを見る: 選んだ素材だけが塗られる・地図を使い回す・メモリが戻る。
    /// </summary>
    public class MaterialSelectionTests
    {
        private const int Size = 128;
        private bool _saved;

        [SetUp]
        public void SetUp()
        {
            _saved = DebugCaptureHooks.UseMaterialSelection;
            DebugCaptureHooks.UseMaterialSelection = true;
        }

        [TearDown]
        public void TearDown() => DebugCaptureHooks.UseMaterialSelection = _saved;

        private static Color32[] TwoColors()
        {
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    pixels[y * Size + x] = x < Size / 2 ? new Color32(200, 30, 30, 255) : new Color32(30, 30, 200, 255);
            return pixels;
        }

        private static List<ColorZone> ZoneOnLeft()
        {
            var zone = new ColorZone
            {
                name = "z",
                mode = SelectionMode.ColorPick,
                sampleColor = new Color(200f / 255f, 30f / 255f, 30f / 255f, 1f),
                sampleUV = new Vector2(0.25f, 0.5f),
                targetColor = new Color(30f / 255f, 200f / 255f, 30f / 255f, 1f),
            };
            zone.UpdateCacheIfNeeded();
            return new List<ColorZone> { zone };
        }

        [Test]
        public void PaintsOnlyTheMaterialOfTheSample()
        {
            var pixels = TwoColors();
            var input = (Color32[])pixels.Clone();
            PixelProcessor.ProcessPixelsArray(pixels, Size, Size, null, ZoneOnLeft(), RecolorSettings.Default, CancellationToken.None);
            int left = Size / 2 * Size / 2 + Size / 4;
            int right = Size / 2 * Size / 2 + Size - Size / 4;
            Assert.AreNotEqual(input[left], pixels[left], "スポイトの素材が塗られていない");
            Assert.AreEqual(input[right], pixels[right], "別の素材が塗られた");
        }

        [Test]
        public void ReusesTheSceneAcrossRuns_AndReleasesItsMemory()
        {
            long live = IrocaNativeMemory.LiveBytesOf(NativeUsage.Selection);
            var cache = new SelectionCache();
            int builds = 0;
            var first = TwoColors();
            PixelProcessor.ProcessPixelsArray(first, Size, Size, null, ZoneOnLeft(), RecolorSettings.Default, CancellationToken.None, selectionCache: cache);
            Assert.Greater(IrocaNativeMemory.LiveBytesOf(NativeUsage.Selection), live, "地図がキャッシュに残っていない");
            var second = TwoColors();
            PixelProcessor.ProcessPixelsArray(second, Size, Size, null, ZoneOnLeft(), RecolorSettings.Default, CancellationToken.None, selectionCache: cache);
            CollectionAssert.AreEqual(first, second, "キャッシュがあっても結果は同じ");
            unsafe
            {
                var scene = cache.AcquireScene(Size, Size, () => { builds++; return null; });
                Assert.AreEqual(0, builds, "同じ原本なのに地図を作り直そうとした");
                scene.Dispose();
            }
            cache.Dispose();
            Assert.AreEqual(live, IrocaNativeMemory.LiveBytesOf(NativeUsage.Selection), "閉じた後に領域の用途のメモリが残っている");
        }
    }
}
