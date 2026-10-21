// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 後から届く結果(自動調整・AI 提案)を独立した「元に戻す」1 回分にする
    /// (<see cref="IrocaWindow.RecordAsSeparateUndo"/>)。Undo のまとまりはマウスを押す・メニューを
    /// 実行するまで切り替わらないので、待っている間に別のウィンドウでした操作と 1 回分にまとまっていた。
    /// Undo の書き戻しは Unity のシリアライザが行うので、実機でしか検査できない。
    /// </summary>
    public class SeparateUndoTests
    {
        private SampleUvUndoHost _other;
        private SampleUvUndoHost _late;
        private float _initial;

        [SetUp]
        public void SetUp()
        {
            _other = ScriptableObject.CreateInstance<SampleUvUndoHost>();
            _late = ScriptableObject.CreateInstance<SampleUvUndoHost>();
            _initial = _other.session.edgeFeather;
            Undo.ClearUndo(_other);
            Undo.ClearUndo(_late);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearUndo(_other);
            Undo.ClearUndo(_late);
            Object.DestroyImmediate(_other);
            Object.DestroyImmediate(_late);
        }

        private void LateResult(float value) =>
            IrocaWindow.RecordAsSeparateUndo("Late Result", () =>
            {
                Undo.RegisterCompleteObjectUndo(_late, "Late Result");
                _late.session.edgeFeather = value;
            });

        [Test]
        public void LateResult_IsNotMergedWithEarlierEdit()
        {
            // 待っている間の別の操作。この後マウスもメニューも無い(まとまりは切り替わらない)。
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_other, "Other Edit");
            _other.session.edgeFeather = _initial + 1f;

            LateResult(_initial + 2f);

            Undo.PerformUndo();
            Assert.AreEqual(_initial, _late.session.edgeFeather, "1 回目の Ctrl+Z で届いた結果だけが戻る");
            Assert.AreEqual(_initial + 1f, _other.session.edgeFeather, "別の操作は残る");

            Undo.PerformUndo();
            Assert.AreEqual(_initial, _other.session.edgeFeather, "2 回目で別の操作も戻る");
        }

        [Test]
        public void EditRightAfterLateResult_IsSeparate()
        {
            Undo.IncrementCurrentGroup();
            LateResult(_initial + 2f);

            // 届いた直後の別の操作(まとまりの切り替えを挟まない)。
            Undo.RecordObject(_other, "Other Edit");
            _other.session.edgeFeather = _initial + 1f;

            Undo.PerformUndo();
            Assert.AreEqual(_initial, _other.session.edgeFeather, "後の操作だけが戻る");
            Assert.AreEqual(_initial + 2f, _late.session.edgeFeather, "届いた結果は残る");
        }
    }
}
