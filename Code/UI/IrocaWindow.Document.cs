// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    public partial class IrocaWindow
    {
        // 編集状態の持ち主(ゾーン・設定・テクスチャ・レシピ)。Undo の対象もこれ(IrocaEditingDocument の説明)。
        [SerializeField] private IrocaEditingDocument _document;

        // 上の 3 つの、保存の控え。名前を変えると利用者の作業状態がレイアウトから戻らないので残す。ウィンドウのコードは
        // ここを読み書きしない(SourceTexture・Session・Doc を使う。test_window_document_mirror が見る)。保存の直前に文書の
        // 中身を指し直し(写さない)、文書が無いとき(エディタの起動後・0.3.x から入れ替えた直後)はここから文書を作る。
        [SerializeField] private Texture2D sourceTexture;
        [SerializeField] private IrocaSessionState _session = IrocaSessionState.CreateDefault();
        [SerializeField] private IrocaRecipe _boundRecipe;

        // 編集中の文書の中身。閉じた後は作り直さない(閉じた後に走る処理が、読むだけで文書を作らないように)。
        private IrocaEditingDocument Doc
        {
            get
            {
                if (_document == null && this != null)
                {
                    _document = IrocaEditingDocument.Create(_session, sourceTexture, _boundRecipe);
                    if (_editing != null) _editing.Document = _document;
                }
                return _document;
            }
        }

        internal Texture2D SourceTexture { get => Doc.sourceTexture; set => Doc.sourceTexture = value; }
        internal IrocaSessionState Session { get => Doc.session; private set => Doc.session = value; }

        /// <summary>編集を Undo に記録する先。</summary>
        internal Object UndoTarget => Doc;

        // ドメインリロードで控えに残った複製を手放す(次の保存の直前に指し直す)。0.3.x の控えにはマスクの文字列が入っているが、
        // マスクはこの後ファイルから読むので、文書には持たない。
        private void AdoptDocumentAfterEnable()
        {
            _session = Doc.session;
            _session.maskState = new MaskState();
        }

        private void ReleaseDocument()
        {
            if (_document == null) return;
            UnityEditor.Undo.ClearUndo(_document);
            DestroyImmediate(_document);
        }

        // 読み込みスレッドから呼ばれることがあるので Unity の API を使わない(参照を指し直すだけ。文書が破棄された後でも読める)。
        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            var doc = _document;
            if (ReferenceEquals(doc, null)) return;
            sourceTexture = doc.sourceTexture;
            _session = doc.session;
            _boundRecipe = doc.boundRecipe;
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize() { }
    }
}
