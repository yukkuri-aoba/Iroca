// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Effect = Iroca.MaterialColorSettings.Effect;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 「アバターの色」表示。マテリアルの色の設定の読み取り(<see cref="MaterialColorSettings"/>)と、それをプレビューの
    /// 画像に掛ける描画(<see cref="AppearanceRenderer"/>)。lilToon が無いホストでは lilToon の段を Ignore、
    /// -nographics(GPU なし)では描画の段を Ignore にする。
    /// </summary>
    public class MaterialAppearanceTests
    {
        private readonly List<Object> _made = new List<Object>();
        private AppearanceRenderer _renderer;

        [TearDown]
        public void TearDown()
        {
            _renderer?.Dispose();
            _renderer = null;
            foreach (var o in _made)
                if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        private T Keep<T>(T o) where T : Object
        {
            _made.Add(o);
            return o;
        }

        private Texture2D Tex(int w, int h, System.Func<int, int, Color32> pixel, TextureWrapMode wrap = TextureWrapMode.Clamp)
        {
            var t = Keep(new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = wrap });
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = pixel(x, y);
            t.SetPixels32(px);
            t.Apply(false);
            return t;
        }

        // 色の段差がある元画像(どの画素も違う色)。
        private Texture2D Source(int w = 64, int h = 64)
            => Tex(w, h, (x, y) => new Color32((byte)(x * 4), (byte)(y * 4), (byte)(255 - x * 2 - y), 255));

        private static HashSet<Texture> Targets(Texture t) => new HashSet<Texture> { t };

        private Material LilToon()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("lilToon が入っていないホスト");
            return Keep(new Material(shader));
        }

        private static void RequireGpu()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("-nographics では描画を確かめられない(Run-EditorTests.ps1 -Graphics で回す)");
        }

        // ── 読み取り ────────────────────────────────────────────────

        [Test]
        public void Standard_MainColor_IsTint()
        {
            var tex = Source(4, 4);
            var mat = Keep(new Material(Shader.Find("Standard")) { mainTexture = tex, color = new Color(0.5f, 0.6f, 0.7f) });
            var r = MaterialColorSettings.Analyze(mat, Targets(tex));
            Assert.NotNull(r);
            Assert.AreEqual(MaterialColorSettings.Family.Generic, r.family);
            Assert.AreEqual(MaterialColorSettings.Slot.Main, r.slot);
            Assert.AreEqual(Effect.Tint, r.effects);
            AssertColor(new Color(0.5f, 0.6f, 0.7f), r.tint);
            Assert.AreEqual(AppearanceRenderer.Kind.Tint, AppearanceRenderer.KindOf(r));
            Assert.AreEqual(Effect.None, AppearanceRenderer.NotPreviewed(r));
        }

        [Test]
        public void Standard_WhiteMainColor_ChangesNothing()
        {
            var tex = Source(4, 4);
            var mat = Keep(new Material(Shader.Find("Standard")) { mainTexture = tex, color = Color.white });
            var r = MaterialColorSettings.Analyze(mat, Targets(tex));
            Assert.NotNull(r);
            Assert.IsFalse(r.AffectsColor);
            Assert.AreEqual(AppearanceRenderer.Kind.None, AppearanceRenderer.KindOf(r));
        }

        // メイン以外の欄(発光など)だけで使っているテクスチャは、色替えがそのまま付いていくので読まない。
        [Test]
        public void TextureOnlyInOtherSlot_IsNotReported()
        {
            var tex = Source(4, 4);
            var mat = Keep(new Material(Shader.Find("Standard")) { color = Color.red });
            mat.SetTexture("_EmissionMap", tex);
            Assert.IsNull(MaterialColorSettings.Analyze(mat, Targets(tex)));
        }

        [Test]
        public void LilToon_MainSettings_AreRead()
        {
            var tex = Source(4, 4);
            var mat = LilToon();
            mat.mainTexture = tex;
            mat.SetColor("_Color", new Color(0.25f, 0.25f, 0.27f));
            mat.SetVector("_MainTexHSVG", new Vector4(0.1f, 1f, 1f, 1f));
            mat.SetFloat("_MainGradationStrength", 1f);
            mat.SetTexture("_MainGradationTex", Source(4, 1));
            var r = MaterialColorSettings.Analyze(mat, Targets(tex));
            Assert.NotNull(r);
            Assert.AreEqual(MaterialColorSettings.Family.LilToon, r.family);
            Assert.AreEqual(Effect.Tint | Effect.ToneCorrection | Effect.Gradation | Effect.GradationFull, r.effects);
            if (AppearanceRenderer.LilToonBaker != null)
            {
                Assert.AreEqual(AppearanceRenderer.Kind.LilToonBaker, AppearanceRenderer.KindOf(r));
                Assert.AreEqual(Effect.None, AppearanceRenderer.NotPreviewed(r));
            }
        }

        // UV0 で重ねる 2nd はプレビューに入り、UV1 で重ねる 3rd は入らない。
        [Test]
        public void LilToon_Layers_OtherUvIsNotPreviewed()
        {
            var tex = Source(4, 4);
            var mat = LilToon();
            mat.mainTexture = tex;
            mat.SetFloat("_UseMain2ndTex", 1f);
            mat.SetFloat("_UseMain3rdTex", 1f);
            mat.SetFloat("_Main3rdTex_UVMode", 1f);
            var r = MaterialColorSettings.Analyze(mat, Targets(tex));
            Assert.AreEqual(Effect.Layer2nd | Effect.LayerOtherUv, r.effects);
            if (AppearanceRenderer.LilToonBaker == null) Assert.Ignore("lilToon の焼き込みシェーダーが無い");
            Assert.AreEqual(AppearanceRenderer.Kind.LilToonBaker, AppearanceRenderer.KindOf(r));
            Assert.AreEqual(Effect.LayerOtherUv, AppearanceRenderer.NotPreviewed(r));
        }

        // 2nd のテクスチャとして開いたときは、その層の色(_Color2nd)の掛け算として読む。
        [Test]
        public void LilToon_TextureAs2ndLayer_UsesLayerColor()
        {
            var main = Source(4, 4);
            var layer = Source(4, 4);
            var mat = LilToon();
            mat.mainTexture = main;
            mat.SetColor("_Color", new Color(0.5f, 0.5f, 0.5f));
            mat.SetFloat("_UseMain2ndTex", 1f);
            mat.SetTexture("_Main2ndTex", layer);
            mat.SetColor("_Color2nd", new Color(0.26f, 0.96f, 1f));
            var r = MaterialColorSettings.Analyze(mat, Targets(layer));
            Assert.NotNull(r);
            Assert.AreEqual(MaterialColorSettings.Slot.Layer2nd, r.slot);
            Assert.AreEqual(Effect.Tint, r.effects);
            AssertColor(new Color(0.26f, 0.96f, 1f), r.tint);
            Assert.AreEqual(AppearanceRenderer.Kind.Tint, AppearanceRenderer.KindOf(r));
        }

        // 同じ値なら要約も同じ(重複をまとめる)。値を変えたら要約が変わる(表示を作り直す)。
        [Test]
        public void Signature_FollowsValues()
        {
            var tex = Source(4, 4);
            var a = Keep(new Material(Shader.Find("Standard")) { mainTexture = tex, color = Color.gray });
            var b = Keep(new Material(Shader.Find("Standard")) { mainTexture = tex, color = Color.gray });
            var t = Targets(tex);
            Assert.AreEqual(MaterialColorSettings.Analyze(a, t).signature, MaterialColorSettings.Analyze(b, t).signature);
            b.color = Color.red;
            Assert.AreNotEqual(MaterialColorSettings.Analyze(a, t).signature, MaterialColorSettings.Analyze(b, t).signature);
        }

        [Test]
        public void FindForTexture_FindsSceneMaterial()
        {
            var tex = Source(4, 4);
            var mat = Keep(new Material(Shader.Find("Standard")) { mainTexture = tex, color = Color.gray });
            var go = Keep(new GameObject("iroca_material_color_test"));
            go.transform.position = new Vector3(1000f, 1000f, 1000f);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            var found = MaterialColorSettings.FindForTexture(tex, Targets(tex), out bool inScene);
            Assert.IsTrue(inScene);
            Assert.AreEqual(1, found.Count);
            Assert.AreSame(mat, found[0].material);
        }

        // ── 描画 ──────────────────────────────────────────────────

        // lilToon の焼き込みシェーダーで描いたメインカラーの掛け算と、自前の表(ColorTintLut)が同じになる
        // (色空間の扱いが lilToon と一致している)。
        [Test]
        public void LilToonBaker_TintOnly_MatchesTintTable()
        {
            RequireGpu();
            var src = Source();
            var mat = LilToon();
            if (AppearanceRenderer.LilToonBaker == null) Assert.Ignore("lilToon の焼き込みシェーダーが無い");
            mat.mainTexture = src;
            mat.SetColor("_Color", new Color(0.6f, 0.8f, 0.4f));
            var lil = MaterialColorSettings.Analyze(mat, Targets(src));
            Assert.AreEqual(AppearanceRenderer.Kind.LilToonBaker, AppearanceRenderer.KindOf(lil));
            var tint = new MaterialColorSettings.Report
            {
                material = mat, family = MaterialColorSettings.Family.Generic, slot = MaterialColorSettings.Slot.Main,
                tint = lil.tint, effects = Effect.Tint, signature = "tint",
            };
            _renderer = new AppearanceRenderer();
            Texture2D a = null, b = null;
            Assert.IsTrue(_renderer.Render(lil, src, ref a));
            Assert.IsTrue(_renderer.Render(tint, src, ref b));
            try { AssertClose(a.GetPixels32(), b.GetPixels32(), 1, "焼き込みと表"); }
            finally { TextureSlot.Release(ref a); TextureSlot.Release(ref b); }
        }

        // 拡大表示の切り出しは、重ね描きの層の位置まで含めてテクスチャ全体を描いたものの同じ範囲と一致する。
        // lighting が 0 だと 2nd を照明の後に重ねる(焼き込みを 2 回に分ける道を通る)。
        [Test]
        public void LilToonBaker_RegionWithLayer_MatchesWholeRender([Values(false, true)] bool decal,
                                                                    [Values(1f, 0f)] float lighting)
        {
            RequireGpu();
            var src = Source();
            var mat = LilToon();
            if (AppearanceRenderer.LilToonBaker == null) Assert.Ignore("lilToon の焼き込みシェーダーが無い");
            mat.mainTexture = src;
            mat.SetColor("_Color", new Color(0.9f, 0.7f, 0.8f));
            // 市松(半透明のマスつき)の 2nd を、繰り返し(タイル)かデカールで重ねる。
            var layer = Tex(16, 16, (x, y) => ((x / 4 + y / 4) & 1) == 0 ? new Color32(255, 40, 40, 255) : new Color32(0, 0, 0, 0),
                            TextureWrapMode.Repeat);
            mat.SetFloat("_UseMain2ndTex", 1f);
            mat.SetTexture("_Main2ndTex", layer);
            mat.SetColor("_Color2nd", Color.white);
            mat.SetFloat("_Main2ndEnableLighting", lighting);
            if (decal)
            {
                mat.SetFloat("_Main2ndTexIsDecal", 1f);
                mat.SetTextureScale("_Main2ndTex", new Vector2(2f, 2f));
                mat.SetTextureOffset("_Main2ndTex", new Vector2(-0.3f, -0.6f));
                mat.SetFloat("_Main2ndTexAngle", 0.3f);
            }
            else
            {
                mat.SetTextureScale("_Main2ndTex", new Vector2(3f, 2f));
                mat.SetTextureOffset("_Main2ndTex", new Vector2(0.1f, 0.25f));
            }
            var r = MaterialColorSettings.Analyze(mat, Targets(src));
            Assert.AreEqual(AppearanceRenderer.Kind.LilToonBaker, AppearanceRenderer.KindOf(r));

            _renderer = new AppearanceRenderer();
            Texture2D whole = null, part = null;
            Assert.IsTrue(_renderer.Render(r, src, ref whole));
            const int x0 = 16, y0 = 8, cw = 32, ch = 32;
            var all = src.GetPixels32();
            var crop = Tex(cw, ch, (x, y) => all[(y0 + y) * src.width + x0 + x]);
            var uv = new Rect(x0 / 64f, y0 / 64f, cw / 64f, ch / 64f);
            Assert.IsTrue(_renderer.RenderRegion(r, crop, uv, 64, 64, ref part));
            try
            {
                var w = whole.GetPixels32();
                var expect = new Color32[cw * ch];
                for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    expect[y * cw + x] = w[(y0 + y) * 64 + x0 + x];
                // 層が実際に載っている(元画像の掛け算だけになっていない)ことも確かめる。
                Assert.AreNotEqual(0, CountDifferent(w, Tinted(all, mat.GetColor("_Color"))), "2nd が描かれていない");
                AssertClose(expect, part.GetPixels32(), 2, "切り出しと全体");
            }
            finally { TextureSlot.Release(ref whole); TextureSlot.Release(ref part); }
        }

        // lilToon 本体の描画(照明を 1 に固定)と一致する。焼き込みどうしの比較だけだと、写し漏れた値
        // (重ね描きの DecalAnimation など)で両方が同じように崩れても通ってしまうので、本体と突き合わせる。
        [Test]
        // lighting2nd は 2nd を照明の前に重ねる割合(1 未満だと残りは照明の後 = 3rd より上に重なる)。
        public void LilToonBaker_Layers_MatchLilToonForwardPass([Values(false, true)] bool decal,
                                                                [Values(1f, 0f, 0.4f)] float lighting2nd)
        {
            RequireGpu();
            var src = Source();
            var mat = LilToon();
            if (AppearanceRenderer.LilToonBaker == null) Assert.Ignore("lilToon の焼き込みシェーダーが無い");
            mat.mainTexture = src;
            mat.SetColor("_Color", new Color(0.9f, 0.7f, 0.8f));
            var layer = Tex(16, 16, (x, y) => ((x / 4 + y / 4) & 1) == 0 ? new Color32(255, 40, 40, 255) : new Color32(0, 0, 0, 0),
                            TextureWrapMode.Repeat);
            mat.SetFloat("_UseMain2ndTex", 1f);
            mat.SetTexture("_Main2ndTex", layer);
            mat.SetColor("_Color2nd", new Color(0.3f, 1f, 1f, 1f));
            mat.SetFloat("_Main2ndEnableLighting", lighting2nd);
            mat.SetFloat("_UseMain3rdTex", 1f);
            mat.SetTexture("_Main3rdTex", layer);
            mat.SetColor("_Color3rd", new Color(1f, 1f, 0.2f, 0.5f));
            mat.SetFloat("_Main3rdTexBlendMode", 1f);
            mat.SetTextureScale("_Main3rdTex", new Vector2(1f, 3f));
            if (decal)
            {
                mat.SetFloat("_Main2ndTexIsDecal", 1f);
                mat.SetTextureScale("_Main2ndTex", new Vector2(2f, 2f));
                mat.SetTextureOffset("_Main2ndTex", new Vector2(-0.3f, -0.6f));
                mat.SetFloat("_Main2ndTexAngle", 0.3f);
            }
            else
            {
                mat.SetTextureScale("_Main2ndTex", new Vector2(3f, 2f));
                mat.SetTextureOffset("_Main2ndTex", new Vector2(0.1f, 0.25f));
            }
            var r = MaterialColorSettings.Analyze(mat, Targets(src));
            Assert.AreEqual(AppearanceRenderer.Kind.LilToonBaker, AppearanceRenderer.KindOf(r));

            var lil = RenderForward(mat, src.width, src.height);
            if (CountDifferent(lil, Tinted(src.GetPixels32(), mat.GetColor("_Color"))) == 0)
                Assert.Ignore("このホストの lilToon は 2nd / 3rd を外して組まれている(lilToon のシェーダー設定)");
            _renderer = new AppearanceRenderer();
            Texture2D ours = null;
            Assert.IsTrue(_renderer.Render(r, src, ref ours));
            try { AssertClose(lil, ours.GetPixels32(), 2, "lilToon 本体と"); }
            finally { TextureSlot.Release(ref ours); }
        }

        // 色の掛け算だけのときは、切り出しも全体の同じ範囲と一致する(画素ごとの変換)。
        [Test]
        public void Tint_RegionMatchesWholeRender()
        {
            var src = Source();
            var mat = Keep(new Material(Shader.Find("Standard")) { mainTexture = src, color = new Color(0.3f, 0.5f, 0.9f) });
            var r = MaterialColorSettings.Analyze(mat, Targets(src));
            _renderer = new AppearanceRenderer();
            Texture2D whole = null, part = null;
            Assert.IsTrue(_renderer.Render(r, src, ref whole));
            var all = src.GetPixels32();
            var crop = Tex(8, 8, (x, y) => all[(10 + y) * 64 + 20 + x]);
            Assert.IsTrue(_renderer.RenderRegion(r, crop, new Rect(20 / 64f, 10 / 64f, 8 / 64f, 8 / 64f), 64, 64, ref part));
            try
            {
                var w = whole.GetPixels32();
                var expect = new Color32[64];
                for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    expect[y * 8 + x] = w[(10 + y) * 64 + 20 + x];
                AssertClose(expect, part.GetPixels32(), 0, "切り出しと全体");
            }
            finally { TextureSlot.Release(ref whole); TextureSlot.Release(ref part); }
        }

        // ── 道具 ──────────────────────────────────────────────────

        // マテリアルに入れた色は保存のときに丸まることがある(float の完全一致では比べない)。
        private static void AssertColor(Color expect, Color actual)
        {
            Assert.AreEqual(expect.r, actual.r, 1e-4f, "r");
            Assert.AreEqual(expect.g, actual.g, 1e-4f, "g");
            Assert.AreEqual(expect.b, actual.b, 1e-4f, "b");
        }

        // lilToon 本体(前面の描画)で、照明を 1 に固定して影を切って描いた色。画面いっぱいの四角に UV の 0〜1 を張る
        // (画素の中心がテクスチャの画素の中心に当たる)。
        private Color32[] RenderForward(Material mat, int w, int h)
        {
            var m = Keep(new Material(mat));
            m.SetFloat("_AsUnlit", 1f);
            m.SetFloat("_UseShadow", 0f);
            var quad = Keep(GameObject.CreatePrimitive(PrimitiveType.Quad));
            quad.layer = 31;
            quad.transform.position = new Vector3(1000f, 1000f, 1000f);
            quad.transform.localScale = new Vector3(w / (float)h, 1f, 1f);
            var mr = quad.GetComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var cam = Keep(new GameObject("iroca_forward_test")).AddComponent<Camera>();
            cam.transform.position = new Vector3(1000f, 1000f, 999f);
            cam.orthographic = true;
            cam.orthographicSize = 0.5f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 10f;
            cam.cullingMask = 1 << 31;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var prev = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var read = Keep(new Texture2D(w, h, TextureFormat.RGBA32, false));
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                read.Apply(false);
                return read.GetPixels32();
            }
            finally
            {
                RenderTexture.active = prev;
                cam.targetTexture = null;
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        private static Color32[] Tinted(Color32[] src, Color tint)
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var r = ColorTintLut.Build(tint.r, linear);
            var g = ColorTintLut.Build(tint.g, linear);
            var b = ColorTintLut.Build(tint.b, linear);
            var dst = new Color32[src.Length];
            ColorTintLut.Apply(src, dst, r, g, b);
            return dst;
        }

        private static int CountDifferent(Color32[] a, Color32[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++)
                if (Mathf.Abs(a[i].r - b[i].r) > 2 || Mathf.Abs(a[i].g - b[i].g) > 2 || Mathf.Abs(a[i].b - b[i].b) > 2) n++;
            return n;
        }

        private static void AssertClose(Color32[] expect, Color32[] actual, int tolerance, string what)
        {
            Assert.AreEqual(expect.Length, actual.Length, what + ": 画素数");
            int worst = 0, at = -1;
            for (int i = 0; i < expect.Length; i++)
            {
                int d = Mathf.Max(Mathf.Abs(expect[i].r - actual[i].r),
                        Mathf.Max(Mathf.Abs(expect[i].g - actual[i].g), Mathf.Abs(expect[i].b - actual[i].b)));
                if (d > worst) { worst = d; at = i; }
            }
            Assert.LessOrEqual(worst, tolerance,
                $"{what}: 最大の差 {worst}（画素 {at}: 期待 {(at >= 0 ? expect[at].ToString() : "")} 実際 {(at >= 0 ? actual[at].ToString() : "")}）");
        }
    }
}
