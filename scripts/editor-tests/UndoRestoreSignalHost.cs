// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// UndoRestoreSignalTests 用の Undo 対象。IrocaWindow と同じく EditorWindow で、
    /// IrocaSessionState を [SerializeField] で持ち、OnAfterDeserialize の回数を数える
    /// (ウィンドウ本体は OnEnable で前回のテクスチャやセッションファイルを読みに行くので、テストでは使わない)。
    /// Unity は ScriptableObject をファイル名と同名のクラスで引くため、単独ファイルに置く。
    /// </summary>
    public class UndoRestoreSignalHost : EditorWindow, ISerializationCallbackReceiver
    {
        [SerializeField] internal IrocaSessionState session = IrocaSessionState.CreateDefault();

        [System.NonSerialized] internal int restoredCount;

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() => restoredCount++;
    }
}
