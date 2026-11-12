// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// シーンの Renderer の変化の版(<see cref="SceneRendererChanges"/>)。通知を受け取り始めるのは最初に版を読んだときなので、
    /// 読んだ後の変化が版に出ること(= 受け取り始めていること)を確かめる。
    /// </summary>
    public class SceneRendererChangesTests {
        private GameObject _created;

        [TearDown]
        public void TearDown() {
            if (_created != null) {
                Object.DestroyImmediate(_created);
            }
        }

        [UnityTest]
        public IEnumerator Version_RisesWhenARendererIsCreated_AfterTheFirstRead() {
            int before = SceneRendererChanges.Version;   // ここで受け取り始める(既に始まっていれば何もしない)

            _created = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(_created, "Create Cube");   // Unity の変更の通知は、Undo に載せた変化で出る
            // 通知はエディタのコマの終わりにまとめて出る。
            for (int i = 0; i < 5 && SceneRendererChanges.Version == before; i++) {
                yield return null;
            }

            Assert.AreNotEqual(before, SceneRendererChanges.Version, "Renderer を作った後も版が上がらない(通知を受け取っていない)");
        }

        [Test]
        public void Version_IsStable_WhenNothingChanges() {
            int a = SceneRendererChanges.Version;
            int b = SceneRendererChanges.Version;
            Assert.AreEqual(a, b, "読むだけで版が上がる(毎回シーンを探し直すことになる)");
        }
    }
}
