// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// Scene でモデルをクリックした場所を求める(<see cref="SceneMeshPicker"/>)と、プレビューに重ねる
    /// 島の組み立て(<see cref="SceneClickHighlight"/>)。光線と三角形の計算そのものは scripts/unit-run が見るので、
    /// ここは Unity の Renderer・マテリアル・スキニングとのつなぎを見る。
    /// </summary>
    public class SceneMeshPickerTests
    {
        // 開いているシーンのアバター等と重ならないよう、遠く離れた場所に置く。
        private static readonly Vector3 Far = new Vector3(1000f, 1000f, 1000f);
        private readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made)
                if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        private T Track<T>(T o) where T : Object
        {
            _made.Add(o);
            return o;
        }

        /// <summary>離れた 2 枚の四角(= UV の島 2 つ)。並びは cross(b − a, c − a) が +z = 表は +z 側。</summary>
        private Mesh TwoQuads()
        {
            var m = Track(new Mesh { name = "two_quads" });
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
            m.RecalculateBounds();
            return m;
        }

        private Material StandardMaterial() => Track(new Material(Shader.Find("Standard")));

        private Material DoubleSidedMaterial()
        {
            // _Cull を持つ組み込みシェーダー(0 = 両面を描く)
            var mat = Track(new Material(Shader.Find("Hidden/Internal-Colored")));
            mat.SetFloat("_Cull", 0f);
            return mat;
        }

        private GameObject MeshObject(Vector3 position, Material mat)
        {
            var go = Track(new GameObject("IrocaSceneMeshPickerTest"));
            go.transform.position = position;
            go.AddComponent<MeshFilter>().sharedMesh = TwoQuads();
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>local(四角のローカル座標)を +z 側の 5 m 手前から真っ直ぐ見る光線。</summary>
        private static Ray FromFront(Vector3 objectPos, Vector2 local) =>
            new Ray(objectPos + new Vector3(local.x, local.y, 5f), Vector3.back);

        private static Ray FromBehind(Vector3 objectPos, Vector2 local) =>
            new Ray(objectPos + new Vector3(local.x, local.y, -5f), Vector3.forward);

        [Test]
        public void PicksRendererTriangleAndUv()
        {
            var go = MeshObject(Far, StandardMaterial());
            var hit = SceneMeshPicker.Pick(FromFront(Far, new Vector2(0.25f, 0.75f)));
            Assert.IsNotNull(hit);
            Assert.AreSame(go.GetComponent<MeshRenderer>(), hit.renderer);
            Assert.AreEqual(0, hit.submesh);
            Assert.AreEqual(1, hit.triangle, "upper-left triangle of the first quad");
            Assert.AreEqual(5f, hit.distance, 1e-4f);
            // UV は 0.1..0.4 の四角へ線形に写る
            Assert.AreEqual(0.1f + 0.25f * 0.3f, hit.hitUv.x, 1e-4f);
            Assert.AreEqual(0.1f + 0.75f * 0.3f, hit.hitUv.y, 1e-4f);
        }

        [Test]
        public void PicksTheNearestRenderer()
        {
            MeshObject(Far, StandardMaterial());
            var near = MeshObject(Far + new Vector3(0f, 0f, 2f), StandardMaterial());
            var hit = SceneMeshPicker.Pick(FromFront(Far, new Vector2(0.5f, 0.5f)));
            Assert.IsNotNull(hit);
            Assert.AreSame(near.GetComponent<MeshRenderer>(), hit.renderer);
            Assert.AreEqual(3f, hit.distance, 1e-4f);
        }

        [Test]
        public void FollowsTheMaterialsCulling()
        {
            // 設定の無いシェーダー(Standard)は裏を描かないので、裏から見たら当たらない
            MeshObject(Far, StandardMaterial());
            Assert.IsNull(SceneMeshPicker.Pick(FromBehind(Far, new Vector2(0.5f, 0.5f))), "back face of a culling material");

            // 両面を描くマテリアルなら裏からでも当たる
            var other = Far + new Vector3(10f, 0f, 0f);
            var go = MeshObject(other, DoubleSidedMaterial());
            var hit = SceneMeshPicker.Pick(FromBehind(other, new Vector2(0.5f, 0.5f)));
            Assert.IsNotNull(hit, "back face of a double-sided material");
            Assert.AreSame(go.GetComponent<MeshRenderer>(), hit.renderer);
        }

        [Test]
        public void IgnoresWhatIsNotShown()
        {
            var ray = FromFront(Far, new Vector2(0.5f, 0.5f));

            var hidden = MeshObject(Far, StandardMaterial());
            hidden.hideFlags = HideFlags.DontSave; // NDMF などが作るプレビュー用の複製と同じ扱い
            Assert.IsNull(SceneMeshPicker.Pick(ray), "DontSave copy");

            hidden.hideFlags = HideFlags.None;
            hidden.SetActive(false);
            Assert.IsNull(SceneMeshPicker.Pick(ray), "inactive");

            hidden.SetActive(true);
            hidden.GetComponent<MeshRenderer>().enabled = false;
            Assert.IsNull(SceneMeshPicker.Pick(ray), "renderer disabled");

            hidden.GetComponent<MeshRenderer>().enabled = true;
            Assert.IsNotNull(SceneMeshPicker.Pick(ray), "shown again");
        }

        [Test]
        public void HitsTheSkinnedMeshWhereItIsPosed()
        {
            // 拡縮のある親の下で、ボーンを動かしたスキンメッシュ。描かれている位置(BakeMesh)に当たり、
            // バインドポーズの位置には当たらない。
            var root = Track(new GameObject("IrocaSkinnedPickTest"));
            root.transform.position = Far;
            root.transform.localScale = new Vector3(2f, 2f, 2f);
            var bone = Track(new GameObject("bone"));
            bone.transform.SetParent(root.transform, false);

            var mesh = TwoQuads();
            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { bone.transform.worldToLocalMatrix * root.transform.localToWorldMatrix };
            var smr = root.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = new[] { bone.transform };
            smr.rootBone = bone.transform;
            smr.sharedMaterial = StandardMaterial();
            // アバターと同じく、bounds は rootBone 基準の localBounds(ボーンと一緒に動く)
            smr.localBounds = mesh.bounds;

            // バインドポーズのままなら、四角の (0.25, 0.75) は世界で Far + (0.5, 1.5)
            var bindRay = new Ray(Far + new Vector3(0.5f, 1.5f, 5f), Vector3.back);
            var hit = SceneMeshPicker.Pick(bindRay);
            Assert.IsNotNull(hit, "bind pose");
            Assert.AreEqual(0.1f + 0.25f * 0.3f, hit.hitUv.x, 1e-3f);
            Assert.AreEqual(0.1f + 0.75f * 0.3f, hit.hitUv.y, 1e-3f);

            // ボーンを上へ 3(ローカル)= 世界で 6 動かすと、同じ点は Far + (0.5, 7.5) に描かれる
            bone.transform.localPosition = new Vector3(0f, 3f, 0f);
            Assert.IsNull(SceneMeshPicker.Pick(bindRay), "the old position is empty after posing");
            hit = SceneMeshPicker.Pick(new Ray(Far + new Vector3(0.5f, 7.5f, 5f), Vector3.back));
            Assert.IsNotNull(hit, "posed position");
            Assert.AreSame(smr, hit.renderer);
            Assert.AreEqual(0.1f + 0.25f * 0.3f, hit.hitUv.x, 1e-3f);
            Assert.AreEqual(0.1f + 0.75f * 0.3f, hit.hitUv.y, 1e-3f);
        }

        [Test]
        public void HighlightCoversTheClickedIslandOnly()
        {
            MeshObject(Far, StandardMaterial());
            var hit = SceneMeshPicker.Pick(FromFront(Far, new Vector2(0.25f, 0.75f)));
            var tex = Track(new Texture2D(8, 8));
            var h = SceneClickHighlight.Build(tex, hit, Vector2.one, Vector2.zero);

            Assert.AreSame(tex, h.texture);
            Assert.AreEqual(hit.hitUv.x, h.point.x, 1e-5f);
            Assert.AreEqual(hit.hitUv.y, h.point.y, 1e-5f);
            // クリックした四角(島)の 2 枚だけ。もう 1 枚の四角は含まない
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 0, 2, 3 }, h.island.triangles);
            // 輪郭は四角の 4 辺(対角線は 2 枚が共有するので出ない)
            Assert.AreEqual(8, h.outline.Length);
        }

        [Test]
        public void HighlightFoldsTiledUvBackOntoTheTexture()
        {
            // Tiling 4 だと四角の UV 0.1..0.4 は 0.4..1.6 に広がり、クリック位置 (0.25, 0.75) は
            // (0.7, 1.3) → 繰り返しで (0.7, 0.3)。島も同じだけ下げて、クリック位置と同じ場所に描く。
            MeshObject(Far, StandardMaterial());
            var hit = SceneMeshPicker.Pick(FromFront(Far, new Vector2(0.25f, 0.75f)));
            var h = SceneClickHighlight.Build(Track(new Texture2D(8, 8)), hit, new Vector2(4f, 4f), Vector2.zero);

            Assert.AreEqual(0.7f, h.point.x, 1e-4f);
            Assert.AreEqual(0.3f, h.point.y, 1e-4f);
            Assert.AreEqual(0f, h.island.offset.x, 1e-6f);
            Assert.AreEqual(-1f, h.island.offset.y, 1e-6f);
            foreach (var p in h.outline)
            {
                Assert.That(p.x, Is.InRange(0.4f - 1e-4f, 1.6f + 1e-4f));
                Assert.That(p.y, Is.InRange(-0.6f - 1e-4f, 0.6f + 1e-4f));
            }
        }
    }
}
