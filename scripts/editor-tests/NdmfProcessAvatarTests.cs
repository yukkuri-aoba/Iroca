// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_NDMF_PRESENT
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// NDMF を通した結合: <c>AvatarProcessor.ProcessAvatar</c> でいろかのプラグインが走り、
    /// 元のアセットを書き換えずにマテリアルが差し替わり、コンポーネントが消えること。
    /// NDMF が入ったホスト(Iroca_Dev)でだけコンパイルされる(asmdef の IROCA_NDMF_PRESENT)。
    /// </summary>
    public class NdmfProcessAvatarTests
    {
        private const int W = 32, H = 32;
        private TestAssets _assets;
        private readonly List<Object> _created = new List<Object>();
        private string _cacheFile;

        [SetUp] public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown()
        {
            // このテストが作ったテクスチャのキャッシュを残さない。
            if (_cacheFile != null && System.IO.File.Exists(_cacheFile)) System.IO.File.Delete(_cacheFile);
            foreach (var o in _created)
                if (o != null) Object.DestroyImmediate(o);
            _created.Clear();
            nadena.dev.ndmf.AvatarProcessor.CleanTemporaryAssets();
            _assets.Dispose();
        }

        [Test]
        public void ProcessAvatar_SwapsInRecoloredTextureWithoutTouchingAssets()
        {
            var px = TestAssets.Solid(W, H, new Color32(200, 40, 40, 255));
            // 読み書き可能にしておく(差し込まれたテクスチャも元にそろって読めるので、生データを比べられる)。
            string texPath = _assets.WritePng("src", px, W, H, readable: true);
            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);

            var mat = new Material(Shader.Find("Standard")) { mainTexture = src };
            string matPath = _assets.Folder + "/m.mat";
            AssetDatabase.CreateAsset(mat, matPath);
            byte[] matBefore = System.IO.File.ReadAllBytes(TestAssets.Abs(matPath));
            byte[] texBefore = System.IO.File.ReadAllBytes(TestAssets.Abs(texPath));

            var zone = new ColorZone
            {
                sampleColor = new Color(200 / 255f, 40 / 255f, 40 / 255f),
                sampleColorSet = true,
                targetColor = new Color(0.1f, 0.6f, 0.2f),
                tolerance = 0.2f,
            };
            zone.EnsureId();
            var state = new IrocaSessionState();
            state.zones.Add(zone);
            var recipe = RecipeStore.Create(src, state, _assets.Folder);
            _cacheFile = System.IO.Path.Combine(RecipeTextureBuilder.CacheDir,
                RecipeTextureBuilder.CacheKey(recipe, src) + ".tex");

            var root = new GameObject("avatar");
            _created.Add(root);
            var outfit = new GameObject("outfit");
            outfit.transform.SetParent(root.transform, false);
            var mesh = new GameObject("mesh");
            mesh.transform.SetParent(outfit.transform, false);
            var renderer = mesh.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            outfit.AddComponent<IrocaRecolor>().recipe = recipe;

            nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(root);

            var after = renderer.sharedMaterial;
            Assert.AreNotSame(mat, after, "マテリアルが差し替わっていない(プラグインが走っていない?)");
            Assert.IsTrue(after.mainTexture.name.EndsWith("(Iroca)"), after.mainTexture.name);
            Assert.AreSame(src, mat.mainTexture, "元のマテリアルを書き換えた");
            Assert.IsEmpty(root.GetComponentsInChildren<IrocaRecolor>(true), "コンポーネントが残っている");
            AssetDatabase.SaveAssets();
            CollectionAssert.AreEqual(matBefore, System.IO.File.ReadAllBytes(TestAssets.Abs(matPath)), "マテリアルのファイルが変わった");
            CollectionAssert.AreEqual(texBefore, System.IO.File.ReadAllBytes(TestAssets.Abs(texPath)), "テクスチャのファイルが変わった");

            // 差し込まれたテクスチャの中身 = 同じレシピから作り直したもの(GPU を使わずに比べる。
            // batchmode の -nographics では RenderTexture 経由の読み戻しが効かない)。
            var tex = (Texture2D)after.mainTexture;
            Assert.AreEqual((src.width, src.height, src.format, src.mipmapCount),
                (tex.width, tex.height, tex.format, tex.mipmapCount), "取り込み設定にそろっていない");
            Assert.IsTrue(RecipeTextureBuilder.TryRecolor(recipe, out var recolored, out int w, out int h, out var failure),
                failure.ToString());
            var center = recolored[(H / 2) * W + W / 2];
            Assert.Greater(center.g, center.r, $"色替えされていない: {center}");
            var fresh = RecipeTextureBuilder.CreateMatching(recolored, w, h, src);
            try
            {
                CollectionAssert.AreEqual(fresh.GetRawTextureData(), tex.GetRawTextureData(),
                    "差し込まれたテクスチャがレシピから作ったものと違う");
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }
    }
}
#endif
