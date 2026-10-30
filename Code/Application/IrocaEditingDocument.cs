// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 編集中の文書の中身(0.4.0 の計画のフェーズ 5)。Undo の対象はウィンドウでなくこのオブジェクトなので、Undo/Redo で
    /// 巻き戻るのは編集内容だけになる(ウィンドウを記録していたときは、ズーム・前後比較・開閉・書き出しの設定まで一緒に戻った)。
    /// <para>
    /// HideAndDontSave なので、シーンの切り替えとドメインリロードを生き残り、シーンにもアセットにも保存されない。エディタを
    /// 終了すると消える(ウィンドウが保存の控えとディスクから作り直す)。Unity は ScriptableObject をファイル名と同名のクラスで
    /// 引くので、単独のファイルに置く。
    /// </para>
    /// </summary>
    internal sealed class IrocaEditingDocument : ScriptableObject, ISerializationCallbackReceiver
    {
        [SerializeField] internal IrocaSessionState session = IrocaSessionState.CreateDefault();
        [SerializeField] internal Texture2D sourceTexture;
        [SerializeField] internal IrocaRecipe boundRecipe;

        // Unity が中身を書き戻した(Undo/Redo・ドメインリロード)印。
        // Undo.undoRedoPerformed はエディタ全体の Undo で呼ばれるので、シーンや別ウィンドウの
        // Ctrl+Z でも、これが立っていなければ編集内容は変わっていない。Undo の書き戻しは
        // シリアライザ経由で OnAfterDeserialize を通る(UndoRestoreSignalTests が実機で確かめている)。
        [System.NonSerialized] private bool _restoredByUnity;

        internal static IrocaEditingDocument Create(IrocaSessionState session, Texture2D sourceTexture, IrocaRecipe boundRecipe)
        {
            var doc = CreateInstance<IrocaEditingDocument>();
            doc.hideFlags = HideFlags.HideAndDontSave;
            doc.session = session ?? IrocaSessionState.CreateDefault();
            doc.sourceTexture = sourceTexture;
            doc.boundRecipe = boundRecipe;
            return doc;
        }

        /// <summary>前に呼んでから Unity が中身を書き戻していれば true(印は消える)。</summary>
        internal bool ConsumeRestored()
        {
            bool restored = _restoredByUnity;
            _restoredByUnity = false;
            return restored;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        // 読み込みスレッドから呼ばれることがあるので、印を立てる以外のことはしない。
        void ISerializationCallbackReceiver.OnAfterDeserialize() => _restoredByUnity = true;
    }
}
