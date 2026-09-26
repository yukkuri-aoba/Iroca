// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// SampleUvUndoTests 用の Undo 対象。IrocaWindow と同じく IrocaSessionState を
    /// [SerializeField] で持つだけの入れ物（ウィンドウ本体は OnEnable で前回のテクスチャや
    /// セッションファイルを読みに行くので、テストでは使わない）。
    /// Unity は ScriptableObject をファイル名と同名のクラスで引くため、単独ファイルに置く。
    /// </summary>
    public class SampleUvUndoHost : ScriptableObject
    {
        [SerializeField] internal IrocaSessionState session = IrocaSessionState.CreateDefault();
    }
}
