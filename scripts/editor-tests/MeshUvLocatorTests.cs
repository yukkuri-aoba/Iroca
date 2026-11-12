// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// テクスチャからメッシュを探す（<see cref="MeshUvLocator"/>）。見つからなければ従来どおり動くのが前提なので、
    /// 「見つけるべきものを見つける」「見つからないときは null」「読めないメッシュで落ちない」を実機で見る。
    /// </summary>
    public class MeshUvLocatorTests {
        private TestAssets _assets;
        private GameObject _sceneObject;

        [SetUp] public void SetUp() => _assets = TestAssets.Create();

        [TearDown]
        public void TearDown() {
            if (_sceneObject != null) {
                Object.DestroyImmediate(_sceneObject);
            }
            _assets.Dispose();
        }

        /// <summary>離れた 2 枚の四角(= 2 チャート)を 1 サブメッシュに持つメッシュ。</summary>
        private static Mesh TwoQuads() {
            var m = new Mesh { name = "two_quads" };
            m.vertices = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
                new Vector3(2, 0, 0), new Vector3(3, 0, 0), new Vector3(3, 1, 0), new Vector3(2, 1, 0),
            };
            m.uv = new[] {
                new Vector2(0.1f, 0.1f), new Vector2(0.4f, 0.1f), new Vector2(0.4f, 0.4f), new Vector2(0.1f, 0.4f),
                new Vector2(0.6f, 0.6f), new Vector2(0.9f, 0.6f), new Vector2(0.9f, 0.9f), new Vector2(0.6f, 0.9f),
            };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            return m;
        }

        private Material MaterialWith(Texture2D tex, string name) {
            var mat = new Material(Shader.Find("Standard")) { mainTexture = tex };
            AssetDatabase.CreateAsset(mat, _assets.Folder + "/" + name + ".mat");
            return mat;
        }

        private Texture2D Tex(string name) {
            string path = _assets.WritePng(name, TestAssets.Solid(8, 8, new Color32(200, 50, 50, 255)), 8, 8);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private GameObject SceneObject(Mesh mesh, Material mat) {
            _sceneObject = new GameObject("IrocaMeshUvLocatorTest");
            _sceneObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            _sceneObject.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return _sceneObject;
        }

        [Test]
        public void FindsRendererInSceneAndBuildsCharts() {
            var tex = Tex("tex");
            SceneObject(TwoQuads(), MaterialWith(tex, "mat"));

            var r = MeshUvLocator.FindForTexture(tex);
            Assert.IsNotNull(r);
            Assert.AreEqual("scene", r.via);
            Assert.AreEqual(1, r.found.Count);
            Assert.IsTrue(r.found[0].matchedTexture);
            using var map = UvChartMap.Build(64, 64, r.Sources());
            Assert.AreEqual(2, map.ChartCount);
        }

        [Test]
        public void FollowsIrocaExportName() {
            // マテリアルが書き出し物(<名前>_recolored.png)を参照していても、元のテクスチャから見つかる
            var original = Tex("body");
            var exported = Tex("body_recolored");
            SceneObject(TwoQuads(), MaterialWith(exported, "mat"));

            var r = MeshUvLocator.FindForTexture(original);
            Assert.IsNotNull(r);
            Assert.AreEqual(1, r.found.Count);
        }

        [Test]
        public void ReturnsNullWhenNothingUsesTexture() {
            var used = Tex("used");
            var unused = Tex("unused");
            SceneObject(TwoQuads(), MaterialWith(used, "mat"));
            Assert.IsNull(MeshUvLocator.FindForTexture(unused));
        }

        [Test]
        public void FindsPrefabInSameAssetFolder() {
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
        public void PrefabCandidatesAreFilteredByDependencies() {
            // 素材フォルダの Prefab は、読み込む前に依存でこのテクスチャ(か書き出し物)を使うものだけに絞る
            var tex = Tex("tex");
            var exported = Tex("tex_recolored");
            var other = Tex("other");
            var mesh = TwoQuads();
            AssetDatabase.CreateAsset(mesh, _assets.Folder + "/mesh.asset");
            var go = SceneObject(mesh, MaterialWith(tex, "mat"));
            PrefabUtility.SaveAsPrefabAsset(go, _assets.Folder + "/uses.prefab");
            go.GetComponent<MeshRenderer>().sharedMaterial = MaterialWith(exported, "mat_exported");
            PrefabUtility.SaveAsPrefabAsset(go, _assets.Folder + "/uses_export.prefab");
            go.GetComponent<MeshRenderer>().sharedMaterial = MaterialWith(other, "mat_other");
            PrefabUtility.SaveAsPrefabAsset(go, _assets.Folder + "/other.prefab");
            Object.DestroyImmediate(_sceneObject);
            _sceneObject = null;

            var paths = MeshUvLocator.PrefabsUsing(
                MeshUvLocator.AssetRoot(AssetDatabase.GetAssetPath(tex)), MeshUvLocator.TargetTextures(tex));
            CollectionAssert.AreEquivalent(
                new[] { _assets.Folder + "/uses.prefab", _assets.Folder + "/uses_export.prefab" }, paths);
        }

        private string[] PrefabsUsing(Texture2D tex) => MeshUvLocator.PrefabsUsing(
            MeshUvLocator.AssetRoot(AssetDatabase.GetAssetPath(tex)), MeshUvLocator.TargetTextures(tex)).ToArray();

        // 依存はマテリアルへ届く種類だけをたどる。ネストした Prefab の先のマテリアルは届く。
        [Test]
        public void PrefabCandidates_FollowNestedPrefabs() {
            var tex = Tex("tex");
            var mesh = TwoQuads();
            AssetDatabase.CreateAsset(mesh, _assets.Folder + "/mesh.asset");
            var go = SceneObject(mesh, MaterialWith(tex, "mat"));
            var inner = PrefabUtility.SaveAsPrefabAsset(go, _assets.Folder + "/inner.prefab");
            Object.DestroyImmediate(_sceneObject);
            _sceneObject = new GameObject("IrocaMeshUvLocatorTest");
            ((GameObject)PrefabUtility.InstantiatePrefab(inner)).transform.SetParent(_sceneObject.transform);
            PrefabUtility.SaveAsPrefabAsset(_sceneObject, _assets.Folder + "/outer.prefab");
            Object.DestroyImmediate(_sceneObject);
            _sceneObject = null;

            CollectionAssert.AreEquivalent(
                new[] { _assets.Folder + "/inner.prefab", _assets.Folder + "/outer.prefab" }, PrefabsUsing(tex));
        }

        // ツールがマテリアルを .asset にまとめていても届く。
        [Test]
        public void PrefabCandidates_FollowMaterialsInsideAssets() {
            var tex = Tex("tex");
            var mesh = TwoQuads();
            AssetDatabase.CreateAsset(mesh, _assets.Folder + "/bundle.asset");
            var mat = new Material(Shader.Find("Standard")) { name = "inside", mainTexture = tex };
            AssetDatabase.AddObjectToAsset(mat, mesh);
            AssetDatabase.SaveAssets();
            var go = SceneObject(mesh, mat);
            PrefabUtility.SaveAsPrefabAsset(go, _assets.Folder + "/avatar.prefab");
            Object.DestroyImmediate(_sceneObject);
            _sceneObject = null;

            CollectionAssert.AreEquivalent(new[] { _assets.Folder + "/avatar.prefab" }, PrefabsUsing(tex));
            Assert.AreEqual(1, MeshUvLocator.FindForTexture(tex).found.Count);
        }

        // アニメーションの先のマテリアルは Renderer が使っているものではないので、候補にしない(候補の上限を使わない)。
        [Test]
        public void PrefabCandidates_DoNotFollowAnimations() {
            var tex = Tex("tex");
            var other = Tex("other");
            var swapped = MaterialWith(tex, "swapped");
            var clip = new AnimationClip { legacy = true };
            AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("", typeof(MeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0f, value = swapped } });
            AssetDatabase.CreateAsset(clip, _assets.Folder + "/swap.anim");
            var mesh = TwoQuads();
            AssetDatabase.CreateAsset(mesh, _assets.Folder + "/mesh.asset");
            var go = SceneObject(mesh, MaterialWith(other, "mat"));
            go.AddComponent<Animation>().AddClip(clip, "swap");
            PrefabUtility.SaveAsPrefabAsset(go, _assets.Folder + "/avatar.prefab");
            Object.DestroyImmediate(_sceneObject);
            _sceneObject = null;

            Assert.IsEmpty(PrefabsUsing(tex));
            CollectionAssert.AreEquivalent(new[] { _assets.Folder + "/avatar.prefab" }, PrefabsUsing(other));
        }

        [Test]
        public void ManualObjectWithoutReferenceReturnsAllSubmeshesUnmatched() {
            var tex = Tex("tex");
            var other = Tex("other");
            var go = SceneObject(TwoQuads(), MaterialWith(other, "mat"));

            var r = MeshUvLocator.FromObject(go, tex);
            Assert.IsNotNull(r);
            Assert.AreEqual("manual", r.via);
            Assert.AreEqual(1, r.found.Count);
            Assert.IsFalse(r.found[0].matchedTexture);
        }

        // テスト用のシェーダー(欄だけを持つ)。使った後は消す。
        private static Shader ShaderWith(string name, string properties)
            => ShaderUtil.CreateShaderAsset(
                "Shader \"Hidden/IrocaTest/" + name + "\" { Properties { " + properties + " } SubShader { Pass { } } }", false);

        // シェーダーが [MainTexture] を付けた欄(名前が標準でない)も、メインテクスチャとして見つける。色の設定の読み取りと同じ欄。
        // 以前はメッシュ探しが標準の名前だけを別に持っていて、こういうシェーダーのメッシュを見つけなかった。
        [Test]
        public void MainTextureAttribute_IsFoundByBothTheMeshSearchAndTheColorSettings() {
            var shader = ShaderWith("AlbedoMain", "[MainTexture] _Albedo (\"Albedo\", 2D) = \"white\" {}");
            var mat = new Material(shader);
            try {
                var tex = Tex("albedo");
                mat.SetTexture("_Albedo", tex);
                SceneObject(TwoQuads(), mat);

                Assert.AreEqual("_Albedo", MaterialColorSettings.MainTextureProperty(mat), "色の設定の読み取りが見る欄");
                Assert.AreSame(tex, MeshUvLocator.MainTexture(mat), "メッシュ探しのメインテクスチャ");
                var targets = new System.Collections.Generic.HashSet<Texture> { tex };
                Assert.IsTrue(MeshUvLocator.UsesTexture(mat, targets, out string prop), "メッシュ探しが [MainTexture] の欄を見ていない");
                Assert.AreEqual("_Albedo", prop);
                var found = MeshUvLocator.FindForTexture(tex);
                Assert.IsNotNull(found, "[MainTexture] の欄に差したテクスチャのメッシュを見つけない");
                Assert.AreEqual(1, found.found.Count);
            } finally {
                Object.DestroyImmediate(mat);
                Object.DestroyImmediate(shader);
            }
        }

        // _MainTex が空で、別の標準の欄(_BaseMap)に差してあるマテリアルも、今までどおり見つける。
        [Test]
        public void EmptyMainTex_FallsBackToTheOtherStandardSlots() {
            var shader = ShaderWith("TwoSlots", "_MainTex (\"Main\", 2D) = \"white\" {} _BaseMap (\"Base\", 2D) = \"white\" {}");
            var mat = new Material(shader);
            try {
                var tex = Tex("base");
                mat.SetTexture("_BaseMap", tex);
                Assert.AreSame(tex, MeshUvLocator.MainTexture(mat));
                var targets = new System.Collections.Generic.HashSet<Texture> { tex };
                Assert.IsTrue(MeshUvLocator.UsesTexture(mat, targets, out string prop));
                Assert.AreEqual("_BaseMap", prop);
            } finally {
                Object.DestroyImmediate(mat);
                Object.DestroyImmediate(shader);
            }
        }

        // マテリアルのテクスチャの欄は、メイン以外も全部見る(非破壊ビルドと登録が同じ調べ方を使う)。
        [Test]
        public void MaterialTextureSlots_SeeEverySlot() {
            var main = Tex("main");
            var emission = Tex("emission");
            var unused = Tex("unused");
            var mat = new Material(Shader.Find("Standard")) { mainTexture = main };
            try {
                mat.SetTexture("_EmissionMap", emission);
                Assert.IsTrue(MaterialTextureSlots.References(mat, main));
                Assert.IsTrue(MaterialTextureSlots.References(mat, emission), "メイン以外の欄を見ていない");
                Assert.IsFalse(MaterialTextureSlots.References(mat, unused));
                Assert.IsTrue(MaterialTextureSlots.ReferencesAny(mat, new System.Collections.Generic.HashSet<Texture> { unused, emission }));
                Assert.IsFalse(MaterialTextureSlots.ReferencesAny(mat, new System.Collections.Generic.HashSet<Texture> { unused }));
                Assert.IsFalse(MaterialTextureSlots.ReferencesAny(mat, new System.Collections.Generic.HashSet<Texture>()));
            } finally {
                Object.DestroyImmediate(mat);
            }
        }

        // 線・点のサブメッシュは色を塗る面ではない。以前は三角形として読もうとして Unity が Console にエラーを出し、
        // 「読めないメッシュ(Read/Write)」と数えて案内を出していた。
        [Test]
        public void LineSubmesh_IsSkippedWithoutErrorOrUnreadableCount() {
            var tex = Tex("tex");
            var mesh = TwoQuads();
            mesh.subMeshCount = 2;
            mesh.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0);
            mesh.SetIndices(new[] { 4, 5, 5, 6 }, MeshTopology.Lines, 1);
            var mat = MaterialWith(tex, "mat");
            var go = SceneObject(mesh, mat);
            go.GetComponent<MeshRenderer>().sharedMaterials = new[] { mat, mat };

            var r = MeshUvLocator.FindForTexture(tex);   // Console にエラーが出ると、このテストは落ちる
            Assert.IsNotNull(r);
            Assert.AreEqual(1, r.found.Count, "三角形のサブメッシュだけを見つける");
            Assert.AreEqual(0, r.unreadable, "線のサブメッシュを「読めない」と数えた");
        }

        [Test]
        public void NonReadableMeshDoesNotThrow() {
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
