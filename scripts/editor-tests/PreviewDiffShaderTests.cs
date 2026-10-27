// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 差分表示のシェーダー(<c>Hidden/Iroca/PreviewDiff</c>。0.4.0 の計画の 1-2)が、差分の定義どおりに塗るか。
    /// 定義(8 ビットの値で RGB の差の合計が 10 を超えた画素を (255,220,0,160)、それ以外は透明)は製品では
    /// シェーダーにだけあるので、ここに CPU で同じ定義を書いて比べる。プレビューのテクスチャと同じ作り方
    /// (RGBA32・sRGB)の 2 枚を RenderTexture に描いて読み戻す。リニアのプロジェクトでは読むときに線形の値になる
    /// ので、暗い色(戻すときの誤差が出やすい)としきい値の前後(差の合計 9〜12)を多めに入れる。
    /// 描くのは GPU なので、-nographics では Ignore(<c>Run-EditorTests.ps1 -Graphics</c> で回す)。
    /// </summary>
    public class PreviewDiffShaderTests
    {
        private const int W = 64, H = 64;
        private const int Threshold = 10;
        private static readonly Color32 Highlight = new Color32(255, 220, 0, 160);

        [Test]
        public void Shader_IsIncludedInThePackage()
        {
            Assert.IsNotNull(Shader.Find(PreviewDiffDrawer.ShaderName),
                PreviewDiffDrawer.ShaderName + " が無い(Code/UI/PreviewDiff.shader が取り込まれていない)");
        }

        [TestCase(false, TestName = "Shader_MatchesTheCpuDefinition(Point)")]
        [TestCase(true, TestName = "Shader_MatchesTheCpuDefinition(Bilinear)")]
        public void Shader_MatchesTheCpuDefinition(bool bilinear)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("-nographics では描画を確かめられない(Run-EditorTests.ps1 -Graphics で回す)");

            Pairs(out var before, out var after);
            var expected = CpuDiff(before, after);
            int changed = 0;
            foreach (var c in expected) if (c.a > 0) changed++;
            Assert.That(changed, Is.InRange(W * H / 5, W * H * 4 / 5), "変わった画素と変わっていない画素の両方があること(テストの前提)");

            var filter = bilinear ? FilterMode.Bilinear : FilterMode.Point;
            var texBefore = NewTexture(before, filter);
            var texAfter = NewTexture(after, filter);
            var drawer = new PreviewDiffDrawer();
            var rt = new RenderTexture(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var read = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var clipTex = Shader.GetGlobalTexture("_GUIClipTexture");
            var clipMatrix = Shader.GetGlobalMatrix("unity_GUIClipTextureMatrix");
            var active = RenderTexture.active;
            try
            {
                var mat = drawer.MaterialFor(texBefore, texAfter);
                Assert.IsNotNull(mat, "シェーダーが無い");
                // 合成せずにシェーダーの出力をそのまま書く(透明の画素の色も比べられるように)。
                mat.SetFloat("_SrcBlend", (float)BlendMode.One);
                mat.SetFloat("_DstBlend", (float)BlendMode.Zero);
                // IMGUI の切り抜きは無い(全面が見えている)ことにする。
                Shader.SetGlobalTexture("_GUIClipTexture", Texture2D.whiteTexture);
                Shader.SetGlobalMatrix("unity_GUIClipTextureMatrix", Matrix4x4.zero);
                Graphics.Blit(texAfter, rt, mat);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                read.Apply();
                var actual = read.GetPixels32();

                int wrong = 0, first = -1;
                for (int i = 0; i < expected.Length; i++)
                {
                    bool ok = expected[i].a == 0
                        ? actual[i].a == 0
                        : actual[i].a == Highlight.a && Near(actual[i].r, Highlight.r) && Near(actual[i].g, Highlight.g)
                          && Near(actual[i].b, Highlight.b);
                    if (!ok) { wrong++; if (first < 0) first = i; }
                }
                if (wrong > 0)
                    Assert.Fail($"{wrong} 画素が CPU の定義と違う。最初は (x={first % W}, y={first / W}): 変更前 {before[first]}・"
                                + $"変更後 {after[first]}・差の合計 {Sum(before[first], after[first])}、期待 {expected[first]}・実際 {actual[first]}");
            }
            finally
            {
                RenderTexture.active = active;
                Shader.SetGlobalTexture("_GUIClipTexture", clipTex);
                Shader.SetGlobalMatrix("unity_GUIClipTextureMatrix", clipMatrix);
                drawer.Dispose();
                Object.DestroyImmediate(read);
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(texBefore);
                Object.DestroyImmediate(texAfter);
            }
        }

        // 製品のシェーダーと同じ定義(製品の C# には無い。以前の PreviewView.BuildDiffPixels)。
        private static Color32[] CpuDiff(Color32[] a, Color32[] b)
        {
            var d = new Color32[a.Length];
            for (int i = 0; i < d.Length; i++)
                if (Sum(a[i], b[i]) > Threshold) d[i] = Highlight;
            return d;
        }

        private static int Sum(Color32 a, Color32 b)
            => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

        private static bool Near(byte a, byte b) => Mathf.Abs(a - b) <= 1;

        // 変更前・変更後の組。半分はしきい値の前後(差の合計 8〜13)、残りは差 0・小さい差・大きい差。
        // 明るさは暗い所(0〜15)・中ほど・明るい所(240〜255)をまんべんなく。α は両方 255(プレビューと同じ)。
        private static void Pairs(out Color32[] before, out Color32[] after)
        {
            var rng = new System.Random(20261027);
            before = new Color32[W * H];
            after = new Color32[W * H];
            for (int i = 0; i < before.Length; i++)
            {
                int band = i % 3;
                int lo = band == 0 ? 0 : band == 1 ? 60 : 240, hi = band == 0 ? 15 : band == 1 ? 200 : 255;
                var a = new Color32((byte)rng.Next(lo, hi + 1), (byte)rng.Next(lo, hi + 1), (byte)rng.Next(lo, hi + 1), 255);
                int kind = rng.Next(4);
                int total = kind <= 1 ? rng.Next(8, 14) : kind == 2 ? rng.Next(0, 4) : rng.Next(20, 120);
                var b = a;
                for (int k = 0; k < total; k++)
                {
                    // 1 ずつ、元から離れる向きへ動かす(元と同じなら余地の大きい向きへ)。差の合計がちょうど total になる。
                    int ch = rng.Next(3);
                    int o = Get(a, ch), v = Get(b, ch);
                    int dir = v != o ? System.Math.Sign(v - o) : (o < 128 ? 1 : -1);
                    if (v + dir < 0 || v + dir > 255) { k--; continue; }
                    Set(ref b, ch, v + dir);
                }
                before[i] = a;
                after[i] = b;
            }
        }

        private static int Get(Color32 c, int ch) => ch == 0 ? c.r : ch == 1 ? c.g : c.b;

        private static void Set(ref Color32 c, int ch, int v)
        {
            if (ch == 0) c.r = (byte)v; else if (ch == 1) c.g = (byte)v; else c.b = (byte)v;
        }

        // プレビューのテクスチャと同じ作り方(TextureSlot.Resize: RGBA32・ミップなし・sRGB・Clamp)。
        private static Texture2D NewTexture(Color32[] px, FilterMode filter)
        {
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
            };
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
