// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Iroca.EditorTests
{
    /// <summary>
    /// シーン上のアバターへのプレビューのうち、NDMF に依存しない部分。
    /// <list type="bullet">
    /// <item>どこに何を映すかの規則(<see cref="PreviewTargeting"/>)</item>
    /// <item>ウィンドウの結果を受け取って RenderTexture に書く受け渡し口(<see cref="LivePreview"/>)</item>
    /// <item>登録済みレシピの出来上がりの貸し借り(<see cref="RecipePreviewTextures"/>)</item>
    /// </list>
    /// NDMF を通した結合は <c>NdmfPreviewFilterTests</c>(NDMF があるホストでだけコンパイル)。
    /// </summary>
    public class LivePreviewTests
    {
        private readonly List<Object> _created = new List<Object>();
        private LivePreview.Target _savedTarget;

        [SetUp]
        public void SetUp()
        {
            // 開いているいろかウィンドウの編集対象を壊さない(終わったら戻す)。
            _savedTarget = LivePreview.Current;
            LivePreview.SetTarget(null, null);
        }

        [TearDown]
        public void TearDown()
        {
            LivePreview.SetTarget(_savedTarget?.source, _savedTarget?.boundRecipe);
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private T Track<T>(T o) where T : Object { _created.Add(o); return o; }

        // 中身は青で埋める(作ったままの Texture2D は中身が不定で、赤の判定がぶれる)。
        private Texture2D Tex(string name, int size = 8)
        {
            var t = Track(new Texture2D(size, size, TextureFormat.RGBA32, false, false) { name = name });
            t.SetPixels32(TestAssets.Solid(size, size, new Color32(0, 0, 255, 255)));
            t.Apply();
            return t;
        }

        private IrocaRecipe Recipe(Texture2D source)
        {
            var r = Track(ScriptableObject.CreateInstance<IrocaRecipe>());
            r.sourceTexture = source;
            return r;
        }

        private GameObject Go(string name, GameObject parent = null)
        {
            var go = Track(new GameObject(name));
            if (parent != null) go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static PreviewTargeting.Registered Reg(GameObject owner, IrocaRecipe recipe)
            => new PreviewTargeting.Registered(owner, recipe, recipe.sourceTexture);

        // ───────────── 規則 ─────────────

        [Test]
        public void Targeting_UnregisteredTexture_ShowsLiveEverywhereItIsUsed()
        {
            var tex = Tex("t");
            var avatarA = Go("A");
            var avatarB = Go("B");
            var meshB = Go("mesh", avatarB);

            var plan = new PreviewTargeting.Plan(new PreviewTargeting.Registered[0], new LivePreview.Target(tex, null));
            Assert.IsFalse(plan.IsEmpty);
            Assert.IsTrue(plan.TryResolve(avatarA.transform, tex, out var a));
            Assert.IsTrue(a.IsLive);
            Assert.IsTrue(plan.TryResolve(meshB.transform, tex, out var b), "登録前は、そのテクスチャを使うすべての Renderer に映す");
            Assert.IsTrue(b.IsLive);
            Assert.IsFalse(plan.TryResolve(meshB.transform, Tex("other"), out _), "編集していないテクスチャは差し替えない");
        }

        [Test]
        public void Targeting_RegisteredTexture_FollowsTheComponentScope()
        {
            var tex = Tex("t");
            var recipe = Recipe(tex);
            var avatar = Go("avatar");
            var inside = Go("inside", avatar);
            var outside = Go("outside");

            // ウィンドウがこのレシピを編集中 → 範囲内はライブ、範囲外はビルドでも変わらないので映さない。
            var bound = new PreviewTargeting.Plan(new[] { Reg(avatar, recipe) }, new LivePreview.Target(tex, recipe));
            Assert.IsTrue(bound.TryResolve(inside.transform, tex, out var live));
            Assert.IsTrue(live.IsLive);
            Assert.IsFalse(bound.TryResolve(outside.transform, tex, out _), "登録済みのテクスチャは範囲外に映さない");

            // ウィンドウが別のもの(結び付いていない)を編集中 → 範囲内はレシピの出来上がり。
            var unbound = new PreviewTargeting.Plan(new[] { Reg(avatar, recipe) }, new LivePreview.Target(tex, null));
            Assert.IsTrue(unbound.TryResolve(inside.transform, tex, out var fromRecipe));
            Assert.IsFalse(fromRecipe.IsLive);
            Assert.AreSame(recipe, fromRecipe.recipe);

            // ウィンドウを閉じた → レシピの出来上がり。
            var closed = new PreviewTargeting.Plan(new[] { Reg(avatar, recipe) }, null);
            Assert.IsTrue(closed.TryResolve(inside.transform, tex, out var stillRecipe));
            Assert.AreSame(recipe, stillRecipe.recipe);
            Assert.IsFalse(closed.TryResolve(outside.transform, tex, out _));
        }

        [Test]
        public void Targeting_NestedComponents_DeeperWinsLikeTheBuild()
        {
            var tex = Tex("t");
            var outerRecipe = Recipe(tex);
            var innerRecipe = Recipe(tex);
            var avatar = Go("avatar");
            var outfit = Go("outfit", avatar);
            var mesh = Go("mesh", outfit);
            var body = Go("body", avatar);

            var plan = new PreviewTargeting.Plan(new[] { Reg(avatar, outerRecipe), Reg(outfit, innerRecipe) }, null);
            Assert.IsTrue(plan.TryResolve(mesh.transform, tex, out var r1));
            Assert.AreSame(innerRecipe, r1.recipe, "近い(深い)コンポーネントが勝つ");
            Assert.IsTrue(plan.TryResolve(body.transform, tex, out var r2));
            Assert.AreSame(outerRecipe, r2.recipe);

            // 外側のレシピを編集中なら、外側の範囲だけライブ(内側は内側のレシピのまま)。
            var editingOuter = new PreviewTargeting.Plan(new[] { Reg(avatar, outerRecipe), Reg(outfit, innerRecipe) },
                new LivePreview.Target(tex, outerRecipe));
            Assert.IsTrue(editingOuter.TryResolve(body.transform, tex, out var r3));
            Assert.IsTrue(r3.IsLive);
            Assert.IsTrue(editingOuter.TryResolve(mesh.transform, tex, out var r4));
            Assert.AreSame(innerRecipe, r4.recipe);
        }

        [Test]
        public void Targeting_OnlyTheRecipesOwnTextureIsReplaced()
        {
            var main = Tex("main");
            var emission = Tex("emission");
            var avatar = Go("avatar");
            var plan = new PreviewTargeting.Plan(new[] { Reg(avatar, Recipe(main)) }, null);
            Assert.IsTrue(plan.IsCandidate(main));
            Assert.IsFalse(plan.IsCandidate(emission));
            Assert.IsFalse(plan.TryResolve(avatar.transform, emission, out _));
            Assert.IsTrue(new PreviewTargeting.Plan(new PreviewTargeting.Registered[0], null).IsEmpty);
        }

        // ───────────── 受け渡し口 ─────────────

        [Test]
        public void LivePreview_TargetChangesAreAnnouncedOnce()
        {
            var tex = Tex("t");
            var recipe = Recipe(tex);
            int fired = 0;
            void OnChanged() => fired++;
            LivePreview.TargetChanged += OnChanged;
            try
            {
                LivePreview.SetTarget(tex, null);
                LivePreview.SetTarget(tex, null);
                Assert.AreEqual(1, fired, "同じ値で呼び直しても知らせない(ウィンドウは毎フレーム呼ぶ)");
                LivePreview.SetTarget(tex, recipe);
                Assert.AreEqual(2, fired, "レシピの結び付きが変わったら知らせる");
                LivePreview.SetTarget(null, recipe);
                Assert.IsNull(LivePreview.Current);
                Assert.AreEqual(3, fired);
            }
            finally
            {
                LivePreview.TargetChanged -= OnChanged;
            }
        }

        [Test]
        public void LivePreview_LendsOneTextureSizedLikeTheImportedSource()
        {
            var tex = Tex("t", 16);
            LivePreview.SetTarget(tex, null);
            var a = LivePreview.Acquire(tex);
            var b = LivePreview.Acquire(tex);
            Assert.AreSame(a, b, "同じ元テクスチャは 1 枚を共有する");
            Assert.AreEqual((16, 16), (a.width, a.height));
            Assert.AreEqual(2, LivePreview.RefCount(tex));
            LivePreview.Release(tex);
            LivePreview.Release(tex);
            Assert.AreEqual(0, LivePreview.RefCount(tex));
            Assert.IsTrue(a == null, "借り手がいなくなったら GPU 側を捨てる");
        }

        [Test]
        public void LivePreview_IgnoresResultsForATextureNoLongerBeingEdited()
        {
            var edited = Tex("edited");
            var previous = Tex("previous");
            LivePreview.SetTarget(edited, null);
            var rt = LivePreview.Acquire(previous);   // 前のテクスチャの借り手がまだ残っている
            try
            {
                // 切り替え直後に届いた前のテクスチャの結果は書かない(画素が無いので元の見た目のまま)。
                LivePreview.Push(previous, TestAssets.Solid(8, 8, new Color32(255, 0, 0, 255)), 8, 8);
                AssertPixelsIfGpu(rt, previous, expectRed: false);
            }
            finally
            {
                LivePreview.Release(previous);
            }
        }

        [Test]
        public void LivePreview_WritesPushedPixelsAndLaterResultsReplaceThem()
        {
            var tex = Tex("t");
            LivePreview.SetTarget(tex, null);
            // 借り手が現れる前に届いた結果も、借りたときに書かれる。
            LivePreview.Push(tex, TestAssets.Solid(8, 8, new Color32(255, 0, 0, 255)), 8, 8);
            var rt = LivePreview.Acquire(tex);
            try
            {
                AssertPixelsIfGpu(rt, tex, expectRed: true);
                // ドラッグ中の縮小版(寸法が違う)も同じ RenderTexture に引き伸ばして書く。
                LivePreview.Push(tex, TestAssets.Solid(4, 4, new Color32(0, 255, 0, 255)), 4, 4);
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                {
                    var c = ReadCenter((RenderTexture)rt);
                    Assert.Greater(c.g, 200, $"後の結果で置き換わっていない: {c}");
                }
            }
            finally
            {
                LivePreview.Release(tex);
            }
        }

        private static void AssertPixelsIfGpu(Texture rt, Texture2D source, bool expectRed)
        {
            // batchmode の -nographics では RenderTexture の読み戻しが効かないので、寸法だけ確かめる。
            Assert.AreEqual((source.width, source.height), (rt.width, rt.height));
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var c = ReadCenter((RenderTexture)rt);
            if (expectRed) Assert.Greater(c.r, 200, $"書かれていない: {c}");
            else Assert.Less(c.r, 200, $"編集していないテクスチャの結果が書かれた: {c}");
        }

        private static Color32 ReadCenter(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            try
            {
                t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                t.Apply();
                return t.GetPixel(rt.width / 2, rt.height / 2);
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(t);
            }
        }
    }

    /// <summary>登録済みレシピの出来上がり(非破壊ビルドと同じテクスチャ)の貸し借り。</summary>
    public class RecipePreviewTexturesTests
    {
        private const int W = 16, H = 16;
        private TestAssets _assets;
        private readonly List<string> _cacheFiles = new List<string>();

        [SetUp] public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown()
        {
            foreach (var f in _cacheFiles)
                if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
            _cacheFiles.Clear();
            _assets.Dispose();
        }

        private IrocaRecipe RedToGreen(Texture2D src, float tolerance)
        {
            var zone = new ColorZone
            {
                sampleColor = new Color(200 / 255f, 40 / 255f, 40 / 255f),
                sampleColorSet = true,
                targetColor = new Color(0.1f, 0.6f, 0.2f),
                tolerance = tolerance,
            };
            zone.EnsureId();
            var state = new IrocaSessionState();
            state.zones.Add(zone);
            var recipe = RecipeStore.Create(src, state, _assets.Folder);
            _cacheFiles.Add(System.IO.Path.Combine(RecipeTextureBuilder.CacheDir,
                RecipeTextureBuilder.CacheKey(recipe, src) + ".tex"));
            return recipe;
        }

        [Test]
        public void SameRecipeSharesOneTexture_EditedRecipeGetsANewOne()
        {
            string path = _assets.WritePng("src", TestAssets.Solid(W, H, new Color32(200, 40, 40, 255)), W, H);
            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var recipe = RedToGreen(src, 0.2f);
            int before = RecipePreviewTextures.Count;

            var pendingA = RecipePreviewTextures.Acquire(recipe, out var keyA);
            Assert.IsFalse(pendingA.IsCompleted, "初めて映すときは作りかけで返す(色替えの計算でエディタを止めない)");
            var pendingB = RecipePreviewTextures.Acquire(recipe, out var keyB);
            Assert.AreSame(pendingA, pendingB, "作りかけも共有する(同じものを 2 回作らない)");
            var a = TestAssets.Wait(pendingA);
            var b = TestAssets.Wait(pendingB);
            Assert.IsNotNull(a);
            Assert.AreSame(a, b, "同じ中身のレシピは 1 枚を共有する");
            Assert.AreEqual(keyA, keyB);
            Assert.AreEqual(before + 1, RecipePreviewTextures.Count);
            Assert.AreEqual((src.width, src.height, src.format), (a.width, a.height, a.format),
                "ビルドと同じく取り込み設定にそろえる");

            // レシピを編集すると別の鍵になり、古い借り手が返すまで両方が残る。
            var state = RecipeStore.Load(recipe);
            state.zones[0].targetColor = new Color(0.2f, 0.2f, 0.9f);
            RecipeStore.Save(recipe, state);
            _cacheFiles.Add(System.IO.Path.Combine(RecipeTextureBuilder.CacheDir,
                RecipeTextureBuilder.CacheKey(recipe, src) + ".tex"));
            var c = TestAssets.Wait(RecipePreviewTextures.Acquire(recipe, out var keyC));
            Assert.AreNotEqual(keyA, keyC);
            Assert.AreNotSame(a, c);

            RecipePreviewTextures.Release(keyA);
            Assert.IsTrue(a != null, "まだ借り手がいる");
            RecipePreviewTextures.Release(keyB);
            Assert.IsTrue(a == null, "最後の借り手が返したら捨てる");
            RecipePreviewTextures.Release(keyC);
            Assert.AreEqual(before, RecipePreviewTextures.Count);

            // 2 回目からはディスクのキャッシュがあるので、その場で貸す。
            var again = RecipePreviewTextures.Acquire(recipe, out var keyD);
            Assert.IsTrue(again.IsCompleted && again.Result != null, "キャッシュがあるのに作りかけで返した");
            RecipePreviewTextures.Release(keyD);
        }

        [Test]
        public void ReleasedWhileBuilding_TheResultIsDiscarded()
        {
            string path = _assets.WritePng("src", TestAssets.Solid(W, H, new Color32(200, 40, 40, 255)), W, H);
            var recipe = RedToGreen(AssetDatabase.LoadAssetAtPath<Texture2D>(path), 0.2f);
            int before = RecipePreviewTextures.Count;

            var pending = RecipePreviewTextures.Acquire(recipe, out var key);
            Assert.IsFalse(pending.IsCompleted);
            RecipePreviewTextures.Release(key);   // 出来上がる前にプレビューが作り直された
            Assert.AreEqual(before, RecipePreviewTextures.Count);
            Assert.IsNull(TestAssets.Wait(pending), "返したあとに出来上がったものを貸し出した(誰も捨てない)");
            Assert.AreEqual(before, RecipePreviewTextures.Count);
        }

        [Test]
        public void BrokenRecipe_LendsNothing()
        {
            string path = _assets.WritePng("src", TestAssets.Solid(W, H, new Color32(200, 40, 40, 255)), W, H);
            var empty = ScriptableObject.CreateInstance<IrocaRecipe>();
            try
            {
                empty.sourceTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                var lent = RecipePreviewTextures.Acquire(empty, out var key);
                Assert.IsTrue(lent.IsCompleted && lent.Result == null);
                Assert.IsNull(key);
            }
            finally
            {
                Object.DestroyImmediate(empty);
            }
        }
    }
}
