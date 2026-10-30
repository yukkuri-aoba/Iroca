// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// IrocaWindow が「関係ない Undo では作り直さない」判定に使う前提を実機で確かめる。
    /// Undo.undoRedoPerformed はエディタ全体の Undo で呼ばれるので、ウィンドウは
    /// 文書の OnAfterDeserialize(Unity が状態を書き戻した印)が来たときだけマスクの再展開と
    /// プレビューの再生成を行う。前提は次の 2 つ:
    ///   ・文書(HideAndDontSave)の Undo/Redo では、undoRedoPerformed より前に OnAfterDeserialize が呼ばれる
    ///   ・別のオブジェクトの Undo/Redo では、文書の OnAfterDeserialize は呼ばれない
    /// 前者が崩れると Iroca の Undo が画面に反映されなくなり、後者が崩れると無駄な再生成に戻る。
    /// </summary>
    public class UndoRestoreSignalTests
    {
        private IrocaEditingDocument _host;
        private SampleUvUndoHost _other;
        private bool _restoredSeenByCallback;
        private int _callbacks;

        [SetUp]
        public void SetUp()
        {
            _host = IrocaEditingDocument.Create(null, null, null);
            _other = ScriptableObject.CreateInstance<SampleUvUndoHost>();
            var z = new ColorZone { name = "服" };
            z.EnsureId();
            _host.session.zones.Add(z);
            Undo.ClearAll();
            _host.ConsumeRestored();
            _restoredSeenByCallback = false;
            _callbacks = 0;
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        [TearDown]
        public void TearDown()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.ClearAll();
            Object.DestroyImmediate(_host);
            Object.DestroyImmediate(_other);
        }

        private void OnUndoRedo()
        {
            _callbacks++;
            _restoredSeenByCallback = _host.ConsumeRestored();
        }

        private void EditHost(float tolerance)
        {
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_host, "Iroca Edit");
            _host.session.zones[0].tolerance = tolerance;
            Undo.IncrementCurrentGroup();
        }

        private void EditOther()
        {
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_other, "Unrelated Edit");
            _other.session.zones.Add(new ColorZone { name = "別" });
            Undo.IncrementCurrentGroup();
        }

        [Test]
        public void OwnUndoAndRedo_SignalBeforeCallback()
        {
            EditHost(0.4f);
            _host.ConsumeRestored();

            Undo.PerformUndo();
            Assert.AreEqual(1, _callbacks);
            Assert.IsTrue(_restoredSeenByCallback, "自分の Undo では undoRedoPerformed の前に書き戻しの印が来る");

            _host.ConsumeRestored();
            Undo.PerformRedo();
            Assert.AreEqual(2, _callbacks);
            Assert.IsTrue(_restoredSeenByCallback, "Redo でも同じ");
            Assert.AreEqual(0.4f, _host.session.zones[0].tolerance);
        }

        [Test]
        public void CompleteObjectUndo_Signals()
        {
            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(_host, "Remove Zone");
            _host.session.zones.RemoveAt(0);
            Undo.IncrementCurrentGroup();
            _host.ConsumeRestored();

            Undo.PerformUndo();
            Assert.AreEqual(1, _host.session.zones.Count);
            Assert.IsTrue(_restoredSeenByCallback, "RegisterCompleteObjectUndo の Undo でも印が来る");
        }

        [Test]
        public void UnrelatedUndoAndRedo_NoSignal()
        {
            EditHost(0.4f);
            EditOther();
            _host.ConsumeRestored();

            Undo.PerformUndo();
            Assert.AreEqual(1, _callbacks, "別のオブジェクトの Undo でも undoRedoPerformed は呼ばれる");
            Assert.IsFalse(_restoredSeenByCallback, "別のオブジェクトの Undo では文書は書き戻されない");
            Assert.AreEqual(0.4f, _host.session.zones[0].tolerance);

            Undo.PerformRedo();
            Assert.AreEqual(2, _callbacks);
            Assert.IsFalse(_restoredSeenByCallback, "Redo でも同じ");
        }
    }
}
