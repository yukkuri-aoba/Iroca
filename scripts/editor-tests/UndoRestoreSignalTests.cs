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
    /// OnAfterDeserialize(Unity が状態を書き戻した印)が来たときだけマスクの再展開と
    /// プレビューの再生成を行う。前提は次の 2 つ:
    ///   ・ウィンドウ自身の Undo/Redo では、undoRedoPerformed より前に OnAfterDeserialize が呼ばれる
    ///   ・別のオブジェクトの Undo/Redo では、ウィンドウの OnAfterDeserialize は呼ばれない
    /// 前者が崩れると Iroca の Undo が画面に反映されなくなり、後者が崩れると無駄な再生成に戻る。
    /// </summary>
    public class UndoRestoreSignalTests
    {
        private UndoRestoreSignalHost _host;
        private SampleUvUndoHost _other;
        private int _restoredSeenByCallback;
        private int _callbacks;

        [SetUp]
        public void SetUp()
        {
            _host = ScriptableObject.CreateInstance<UndoRestoreSignalHost>();
            _other = ScriptableObject.CreateInstance<SampleUvUndoHost>();
            var z = new ColorZone { name = "服" };
            z.EnsureId();
            _host.session.zones.Add(z);
            Undo.ClearAll();
            _host.restoredCount = 0;
            _restoredSeenByCallback = -1;
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
            _restoredSeenByCallback = _host.restoredCount;
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
            _host.restoredCount = 0;

            Undo.PerformUndo();
            Assert.AreEqual(1, _callbacks);
            Assert.Greater(_restoredSeenByCallback, 0, "自分の Undo では undoRedoPerformed の前に書き戻しの印が来る");

            _host.restoredCount = 0;
            Undo.PerformRedo();
            Assert.AreEqual(2, _callbacks);
            Assert.Greater(_restoredSeenByCallback, 0, "Redo でも同じ");
            Assert.AreEqual(0.4f, _host.session.zones[0].tolerance);
        }

        [Test]
        public void CompleteObjectUndo_Signals()
        {
            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(_host, "Remove Zone");
            _host.session.zones.RemoveAt(0);
            Undo.IncrementCurrentGroup();
            _host.restoredCount = 0;

            Undo.PerformUndo();
            Assert.AreEqual(1, _host.session.zones.Count);
            Assert.Greater(_restoredSeenByCallback, 0, "RegisterCompleteObjectUndo の Undo でも印が来る");
        }

        [Test]
        public void UnrelatedUndoAndRedo_NoSignal()
        {
            EditHost(0.4f);
            EditOther();
            _host.restoredCount = 0;

            Undo.PerformUndo();
            Assert.AreEqual(1, _callbacks, "別のオブジェクトの Undo でも undoRedoPerformed は呼ばれる");
            Assert.AreEqual(0, _restoredSeenByCallback, "別のオブジェクトの Undo ではウィンドウは書き戻されない");
            Assert.AreEqual(0.4f, _host.session.zones[0].tolerance);

            Undo.PerformRedo();
            Assert.AreEqual(2, _callbacks);
            Assert.AreEqual(0, _restoredSeenByCallback, "Redo でも同じ");
        }
    }
}
