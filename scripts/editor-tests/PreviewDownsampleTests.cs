// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 拡大表示のクロップは、フル解像度の配列から見えている範囲を直接縮小する
    /// (<see cref="PixelProcessor.BoxDownsampleRegion"/>)。以前の「切り出してから
    /// <see cref="PixelProcessor.BoxDownsample"/>」とビット単位で同じ絵になることを確かめる。
    /// </summary>
    public class PreviewDownsampleTests
    {
        private static Color32[] Noise(int w, int h, int seed)
        {
            var rng = new System.Random(seed);
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            return px;
        }

        private static Color32[] Crop(Color32[] src, int srcW, int x0, int y0, int cw, int ch)
        {
            var crop = new Color32[cw * ch];
            for (int y = 0; y < ch; y++)
                System.Array.Copy(src, (y0 + y) * srcW + x0, crop, y * cw, cw);
            return crop;
        }

        // (x0, y0, cropW, cropH, 表示倍率): 端に接するもの・割り切れない倍率・1 画素幅を含める。
        [TestCase(0, 0, 300, 200, 0.37f)]
        [TestCase(17, 9, 211, 163, 0.5f)]
        [TestCase(120, 75, 280, 225, 0.8f)]
        [TestCase(399, 0, 1, 300, 0.3f)]
        [TestCase(5, 11, 97, 1, 0.61f)]
        public void Region_MatchesCropThenDownsample(int x0, int y0, int cw, int ch, float pxPerSrc)
        {
            const int w = 400, h = 300;
            var src = Noise(w, h, x0 * 31 + y0);
            int outW = Mathf.Clamp(Mathf.RoundToInt(cw * pxPerSrc), 1, cw);
            int outH = Mathf.Clamp(Mathf.RoundToInt(ch * pxPerSrc), 1, ch);

            var want = PixelProcessor.BoxDownsample(Crop(src, w, x0, y0, cw, ch), cw, ch, outW, outH, pxPerSrc);
            var got = PixelProcessor.BoxDownsampleRegion(src, w, x0, y0, cw, ch, outW, outH, pxPerSrc);

            Assert.AreEqual(want.Length, got.Length);
            for (int i = 0; i < want.Length; i++)
                Assert.AreEqual(want[i], got[i], $"画素 {i}");
        }

        [Test]
        public void Region_RejectsOutOfRange()
        {
            var src = new Color32[100 * 50];
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                PixelProcessor.BoxDownsampleRegion(src, 100, 90, 0, 20, 10, 10, 5, 0.5f));
            Assert.Throws<System.ArgumentException>(() =>
                PixelProcessor.BoxDownsampleRegion(src, 100, 0, 45, 10, 10, 5, 5, 0.5f));
        }
    }
}
