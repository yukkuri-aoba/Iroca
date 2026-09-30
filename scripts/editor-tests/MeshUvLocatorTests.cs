// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests
{
    /// <summary>
    /// テクスチャからメッシュを探す（<see cref="MeshUvLocator"/>）。見つからなければ従来どおり動くのが前提なので、
    /// 「見つけるべきものを見つける」「見つからないときは null」「読めないメッシュで落ちない」を実機で見る。
    /// </summary>
    public class MeshUvLocatorTests
    {
        private TestAssets _assets;
        private GameObject _sceneObject;

        [SetUp] public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown()
        {
            if (_sceneObject != null) Object.DestroyImmediate(_sceneObject);
            _assets.Dispose();
        }

        /// <summary>離れた 2 枚の四角(= 2 チャート)を 1 サブメッシュに持つメッシュ。</summary>
        private static Mesh TwoQuads()
        {
            var m = new Mesh { name = "two_quads" };
            m.vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
                new Vector3(2, 0, 0), new Vector3(3, 0, 0), new Vector3(3, 1, 0), new Vector3(2, 1, 0),
            };
            m.uv = new[]
            {
                new Vector2(0.1f, 0.1f), new Vector2(0.4f, 0.1f), new Vector2(0.4f, 0.4f), new Vector2(0.1f, 0.4f),
                new Vector2(0.6f, 0.6f), new Vector2(0.9f, 0.6f), new Vector2(0.9f, 0.9f), new Vector2(0.6f, 0.9f),
            };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            return m;
        }

        private Material MaterialWith(Texture2D tex, string name)
        {
            var mat = new Material(Shader.Find("Standard")) { mainTexture = tex };
            AssetDatabase.CreateAsset(mat, _assets.Folder + "/" + name + ".mat");
            return mat;
        }

        private Texture2D Tex(string name)
        {
            string path = _assets.WritePng(name, TestAssets.Solid(8, 8, new Color32(200, 50, 50, 255)), 8, 8);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private GameObject SceneObject(Mesh mesh, Material mat)
        {
            _sceneObject = new GameObject("IrocaMeshUvLocatorTest");
            _sceneObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            _sceneObject.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return _sceneObject;
        }

        [Test]
        public void FindsRendererInSceneAndBuildsCharts()
        {
            var tex = Tex("tex");
            SceneObject(TwoQuads(), MaterialWith(tex, "mat"));

            var r = MeshUvLocator.FindForTexture(tex);
            Assert.IsNotNull(r);
            Assert.AreEqual("scene", r.via);
            Assert.AreEqual(1, r.found.Count);
            Assert.IsTrue(r.found[0].matchedTexture);
            var map = UvChartMap.Build(64, 64, r.Sources());
            Assert.AreEqual(2, map.ChartCount);
        }

        [Test]
        public void FollowsIrocaExportName()
        {
            // マテリアルが書き出し物(<名前>_recolored.png)を参照していても、元のテクスチャから見つかる
            var original = Tex("body");
            var exported = Tex("body_recolored");
            SceneObject(TwoQuads(), MaterialWith(exported, "mat"));

            var r = MeshUvLocator.FindForTexture(original);
            Assert.IsNotNull(r);
            Assert.AreEqual(1, r.found.Count);
        }

        [Test]
        public void ReturnsNullWhenNothingUsesTexture()
        {
            var used = Tex("used");
            var unused = Tex("unused");
            SceneObject(TwoQuads(), MaterialWith(used, "mat"));
            Assert.IsNull(MeshUvLocator.FindForTexture(unused));
        }

        [Test]
        public void FindsPrefabInSameAssetFolder()
        {
            var tex = Tex("tex");
            var mesh = TwoQuads();
            AssetDatabase.CreateAsset(mesh, _assets.Folder + "/mesh.asset");
            var go = SceneObject(mesh, MaterialWith(tex, "mat"));
            PrefabUtility.SaveAsPrefabAsset(go, _assets.Folder + "/avatar.prefab");
            Object.DestroyImmediate(_sceneObject);
            _sceneObject = null;

            var r = MeshUvLocator.FindForTexture(tex);
            Assert.IsNotNull(r);
            Assert.AreEqual("project", r.via);
            Assert.AreEqual(1, r.found.Count);
        }

        [Test]
        public void ManualObjectWithoutReferenceReturnsAllSubmeshesUnmatched()
        {
            var tex = Tex("tex");
            var other = Tex("other");
            var go = SceneObject(TwoQuads(), MaterialWith(other, "mat"));

            var r = MeshUvLocator.FromObject(go, tex);
            Assert.IsNotNull(r);
            Assert.AreEqual("manual", r.via);
            Assert.AreEqual(1, r.found.Count);
            Assert.IsFalse(r.found[0].matchedTexture);
        }

        [Test]
        public void NonReadableMeshDoesNotThrow()
        {
            // Read/Write 無効のメッシュ: Editor で読めるなら見つかり、読めなければ unreadable に数える。どちらでも落ちない
            var tex = Tex("tex");
            var mesh = TwoQuads();
            mesh.UploadMeshData(true);
            SceneObject(mesh, MaterialWith(tex, "mat"));
            LogAssert.ignoreFailingMessages = true;

            var r = MeshUvLocator.FindForTexture(tex);
            int found = r?.found.Count ?? 0;
            int unreadable = r?.unreadable ?? 0;
            Debug.Log($"[MeshUvLocatorTests] non-readable mesh: found={found} unreadable={unreadable}");
            Assert.AreEqual(1, found + unreadable,
                "読めないメッシュは見つかったか unreadable のどちらかになる(null は取りこぼし)");
        }
    }
}
