// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// メッシュの探索結果を捨てて探し直す条件（<see cref="MeshPartController.IsStale"/>）。以前はテクスチャだけで
    /// 見分けていて、シーンを替えてから同じテクスチャを開くと、消えたアバターを指したまま「メッシュ」欄が None に
    /// なっていた（個数は前のまま）。消えた Renderer は Unity の上では null と比べて等しくなるので、実機で見る。
    /// </summary>
    public class MeshPartCacheTests
    {
        private GameObject _go;
        private Texture2D _manual;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_manual != null) Object.DestroyImmediate(_manual);
        }

        private MeshUvLocator.Result SceneResult()
        {
            _go = new GameObject("iroca_mesh_cache_test");
            var result = new MeshUvLocator.Result { via = "scene" };
            result.found.Add(new MeshUvLocator.Found { renderer = _go.AddComponent<MeshRenderer>() });
            return result;
        }

        [Test]
        public void RendererDestroyed_IsStale()
        {
            var found = SceneResult();
            int stamp = MeshPartController.SceneStamp();
            Assert.IsFalse(MeshPartController.IsStale(found, null, stamp, stamp));

            Object.DestroyImmediate(_go);
            Assert.IsTrue(MeshPartController.IsStale(found, null, stamp, stamp), "シーンを閉じた・アバターを消した");
            // 手動で指定した物でも、使っていた Renderer が消えたら自動へ戻す
            _manual = new Texture2D(1, 1);
            Assert.IsTrue(MeshPartController.IsStale(found, _manual, stamp, stamp));
        }

        [Test]
        public void AutomaticResult_IsStaleWhenScenesChange()
        {
            var found = SceneResult();
            int stamp = MeshPartController.SceneStamp();
            Assert.IsFalse(MeshPartController.IsStale(found, null, stamp, stamp));
            Assert.IsTrue(MeshPartController.IsStale(found, null, stamp, stamp + 1));
        }

        [Test]
        public void NothingFound_IsStaleWhenScenesChange()
        {
            // 見つからなかった結果も、別のシーンを開いたら探し直す(そのシーンにはアバターがあるかもしれない)
            int stamp = MeshPartController.SceneStamp();
            Assert.IsFalse(MeshPartController.IsStale(null, null, stamp, stamp));
            Assert.IsTrue(MeshPartController.IsStale(null, null, stamp, stamp + 1));
        }

        [Test]
        public void ManualChoice_SurvivesSceneChangeWhileItExists()
        {
            // 手動の指定(FBX・Prefab など、シーンを替えても残る物)は、シーンが替わってもそのまま使う
            var found = SceneResult();
            _manual = new Texture2D(1, 1);
            int stamp = MeshPartController.SceneStamp();
            Assert.IsFalse(MeshPartController.IsStale(found, _manual, stamp, stamp + 1));
        }

        [Test]
        public void SceneStamp_IsStableWhileScenesStayOpen()
        {
            Assert.AreEqual(MeshPartController.SceneStamp(), MeshPartController.SceneStamp());
        }
    }
}
