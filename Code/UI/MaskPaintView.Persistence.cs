// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;

namespace Iroca {
    internal partial class MaskPaintView {

        private string MaskTexturePath() => SessionPersistence.StorageKeyOf(_host.SourceTexture);

        /// <summary>
        /// 現在の全マスク（共通 + 各ゾーン）をプロジェクト下の MaskCache ファイルへ
        /// 永続化する。ディスク書き込みに失敗したときだけ false（呼び出し側で通知に使う）。
        /// </summary>
        public bool SaveToSession() => SaveMasks(_host.Editing.ToSaveState().maskState);

        // ここから下の読み書きの中身(どのファイルから読むか・読めなかった印・レシピへの逃げ道)は EditingSession が持つ
        // (EditingSession.Persistence.cs)。ここは、マスクの画面の後始末(AI 提案の取り消し・重ね表示の作り直し)を足すだけ。

        /// <summary>保存形式にしたマスクを MaskCache ファイルへ書く(<see cref="SaveToSession"/> と同じ)。</summary>
        public bool SaveMasks(MaskState ms) => _host.Editing.SaveMasksToFile(ms);

        /// <summary>
        /// MaskCache ファイルから全マスクを復元する(<see cref="EditingSession.RestoreMasksFromFile"/>)。
        /// MaskCache ファイルから層を読めたら true(ドメインリロードの後に、履歴の版とファイルのどちらを採るかを決めるのに使う。
        /// ファイルを 2 回読まないように返す)。
        /// </summary>
        public bool RestoreFromSession() {
            if (MaskTexturePath() == null) {
                return false;
            }
            // テクスチャ(セッション)切替: AI 提案の積み上げ・進行中推論は旧テクスチャ由来なので破棄
            _suggestController?.OnSourceChangedOrClosing();
            bool fileHasLayers = _host.Editing.RestoreMasksFromFile();
            maskDirty = true;
            return fileHasLayers;
        }

        /// <summary>
        /// いまのテクスチャのマスクのファイルが読めるかを確かめ直す(Undo でテクスチャが戻ったとき。
        /// <see cref="EditingSession.RecheckMaskFile"/>)。
        /// </summary>
        public void RecheckMaskFile() => _host.Editing.RecheckMaskFile();

        /// <summary>結び付いたレシピのマスクを編集中のマスクへ読む(ファイルが古いと分かっているとき)。読めなければ何もしない。</summary>
        public void RestoreMasksFromBoundRecipe() {
            if (_host.Editing.RestoreMasksFromBoundRecipe()) {
                maskDirty = true;
            }
        }

        /// <summary>
        /// レシピから読んだ編集状態(<c>maskState</c> にマスクが入っている)のマスクを編集中のマスクへ展開し、編集状態からは外す
        /// (<see cref="EditingSession.RestoreMasksFromRecipeState"/>)。
        /// </summary>
        public void RestoreFromRecipeState() {
            if (MaskTexturePath() == null) {
                return;
            }
            _suggestController?.OnSourceChangedOrClosing();
            _host.Editing.RestoreMasksFromRecipeState();
            maskDirty = true;
        }

        /// <summary>
        /// テクスチャの切り替え・リセットのときに、マスクと重ね表示(全体用・拡大表示用)を捨てる。
        /// <para>
        /// 以前は全体用の重ね表示だけを捨てていた。拡大表示用のジョブ・届いた結果・当てた範囲の覚えが残るので、前のテクスチャの
        /// 拡大用の重ね表示が後から届くと、同じ寸法・同じ範囲のときに新しいテクスチャの上へ数フレーム出ていた。
        /// </para>
        /// </summary>
        public void ClearBuffersOnTextureChange() {
            ReleaseOverlayState();
            Workspace.Clear();
            overlayBuiltW = overlayBuiltH = 0;
            maskDirty = true;
        }

        /// <summary>
        /// 現在のマスク状態を IrocaPresetData の commonMaskBase64 / zoneMasks / zoneIncludeMasks に書き出す。
        /// 符号化と「どの層を書くか」(全部 0 の層は書かない。層が 1 つも無ければ寸法も書かない)は、レシピ・編集内容のファイルと
        /// 同じ <see cref="MaskWorkspace.EncodeTo"/> に任せ、その結果をプリセットの欄へ写す(<see cref="PresetApplier.WriteMasks"/>)。
        /// </summary>
        public void WriteToPreset(IrocaPresetData data) {
            if (data == null || maskWidth <= 0 || maskHeight <= 0) {
                return;
            }
            var ms = new MaskState();
            Workspace.EncodeTo(ms);
            PresetApplier.WriteMasks(ms, data);
        }
    }
}
