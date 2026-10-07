// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 非破壊(NDMF)のレシピとの結び付け。
    /// <para>
    /// 編集中のテクスチャにレシピが結び付いていると、編集内容は UserSettings(個人の作業データ)に加えて
    /// レシピ(Assets。アバターの <see cref="IrocaRecolor"/> がビルド時に使う)にも保存し、開くときは
    /// レシピを正として読む。結び付くのは、そのレシピから開いたとき・登録したとき・そのテクスチャの
    /// レシピがプロジェクトに 1 つだけのとき。複数あって選ばれていなければ結び付けない(どれに書くか
    /// 決められないため。インスペクタの「いろかで開く」で選ぶ)。
    /// </para>
    /// </summary>
    public partial class IrocaWindow
    {
        [SerializeField] private IrocaRecipe _boundRecipe;

        /// <summary>今のテクスチャに結び付いたレシピ(無ければ null)。</summary>
        internal IrocaRecipe BoundRecipe =>
            _boundRecipe != null && sourceTexture != null && _boundRecipe.sourceTexture == sourceTexture
                ? _boundRecipe
                : null;

        /// <summary>インスペクタの「いろかで開く」から: レシピの元テクスチャを開き、そのレシピに結び付ける。</summary>
        internal static void OpenRecipe(IrocaRecipe recipe)
        {
            if (recipe == null || recipe.sourceTexture == null) return;
            var win = GetWindow<IrocaWindow>();
            win.Show();
            win.Focus();
            if (win.sourceTexture == recipe.sourceTexture && win.BoundRecipe == recipe) return;
            win.SwitchSourceTexture(recipe.sourceTexture, recipe);
        }

        /// <summary>
        /// 編集するテクスチャを切り替える(今のテクスチャを保存 → 差し替え → 保存済みの編集内容を読む)。
        /// <paramref name="recipe"/> を渡すと、そのレシピに結び付けて読む。
        /// </summary>
        internal void SwitchSourceTexture(Texture2D newTex, IrocaRecipe recipe = null)
        {
            // マスク保存失敗時はユーザーに通知（黙って消えないように）。
            if (!SavePersistedSessionForCurrentTexture())
                ShowNotification(new GUIContent($"{Localization.Error}: {Localization.MaskSaveFailed}"));
            // _session をまるごと差し替えるため、深い Undo (RegisterCompleteObjectUndo) を使う。
            Undo.RegisterCompleteObjectUndo(this, "Change Source Texture");
            sourceTexture = newTex;
            _boundRecipe = recipe;
            _previewView.InvalidateSourceCache();
            _maskView.ClearBuffersOnTextureChange();
            if (sourceTexture != null)
            {
                var path = AssetDatabase.GetAssetPath(sourceTexture);
                _exportView.SetSourceTextureBaseName(Path.GetFileNameWithoutExtension(path));
            }
            LoadPersistedSessionForCurrentTexture();
            // 前のテクスチャのゾーン番号を引き継がない(別のゾーンを指してしまう)。先頭のゾーンを直す対象にする。
            _maskView.ResetActiveTarget();
            RememberLastEditedTexture();
            Repaint();
        }

        /// <summary>
        /// 開いたテクスチャのレシピを決めて、そこから編集内容(マスク込み)を読む。読めたら true。
        /// 既に結び付いているレシピ(レシピから開いた・ドメインリロード)を優先し、無ければ
        /// そのテクスチャのレシピがちょうど 1 つのときだけ結び付ける。
        /// </summary>
        private bool TryLoadSessionFromRecipe()
        {
            if (sourceTexture == null) { _boundRecipe = null; return false; }
            if (BoundRecipe == null)
            {
                var found = RecipeStore.FindForTexture(sourceTexture);
                _boundRecipe = found.Count == 1 ? found[0] : null;
            }
            if (_boundRecipe == null) return false;

            var state = RecipeStore.Load(_boundRecipe);
            if (state == null) return false;   // 壊れたレシピは読まない(保存も BoundRecipe 経由で上書きしない)
            _session = state;
            EnsureAllZoneIds();
            _maskView?.RestoreFromRecipeState();
            MarkPreviewDirty();
            return true;
        }

        /// <summary>結び付いたレシピへ今の編集内容(マスク込み)を書く。マスクは呼び出し側で state へ同期済みであること。</summary>
        private void SaveSessionToBoundRecipe()
        {
            var recipe = BoundRecipe;
            if (recipe == null || _session == null) return;
            // 読めないレシピ(新しい版で作られた等)は上書きしない。
            if (!string.IsNullOrEmpty(recipe.sessionJson) && RecipeStore.Load(recipe) == null) return;
            RecipeStore.Save(recipe, _session);
        }

        /// <summary>
        /// ビルド(NDMF)の直前に呼ばれる。ウィンドウで編集中の内容を、結び付いたレシピへ書き出しておく
        /// (ウィンドウの保存はテクスチャ切り替え・ウィンドウを閉じるときなので、そのままだと
        /// 直前の編集がアップロードに乗らない)。
        /// </summary>
        private void FlushToBoundRecipe()
        {
            if (BoundRecipe == null) return;
            _maskView?.SyncBuffersToState();
            SaveSessionToBoundRecipe();
        }

        /// <summary>
        /// シーンのアバターへのプレビュー(NDMF)に、いま編集しているものを知らせる(毎フレーム呼ぶ。変化が
        /// 無ければ何もしない)。有効なゾーンが無ければ何も映さない(色が変わらないのに、アバターを
        /// プレビュー表示へ切り替えない)。画素は PreviewView がプレビューを作るたびに渡す。
        /// </summary>
        private void PublishLivePreviewTarget()
        {
            bool anyEnabled = sourceTexture != null && HasEnabledZone;
            if (anyEnabled) LivePreview.SetTarget(sourceTexture, BoundRecipe);
            else LivePreview.SetTarget(null, null);
        }

        // ───────────── 登録 ─────────────

        /// <summary>「アバターに非破壊で登録」。登録先を決め、レシピを作るか更新し、コンポーネントを付ける。</summary>
        internal void RegisterToAvatar()
        {
            if (sourceTexture == null) return;
            if (SessionRecolor.CountEnabled(_session?.zones) == 0)
            {
                EditorUtility.DisplayDialog(Localization.RegisterNonDestructive, Localization.RegisterNoZones, Localization.OK);
                return;
            }
            var selected = Selection.activeGameObject;
            if (selected != null && !RecipeRegistration.IsSceneObject(selected))
            {
                EditorUtility.DisplayDialog(Localization.RegisterNonDestructive, Localization.RegisterNotInScene, Localization.OK);
                return;
            }
            var target = RecipeRegistration.SuggestTarget(sourceTexture, selected);
            if (target == null)
            {
                EditorUtility.DisplayDialog(Localization.RegisterNonDestructive, Localization.RegisterNoTarget, Localization.OK);
                return;
            }

            // 使うレシピ: 結び付いているもの → 登録先に既にあるこのテクスチャのレシピ(登録し直し = 更新)
            // → このテクスチャのレシピがプロジェクトにちょうど 1 つならそれ → 新しく作る。
            var recipe = BoundRecipe;
            if (recipe == null) recipe = RecipeRegistration.ExistingFor(target, sourceTexture);
            if (recipe == null)
            {
                var found = RecipeStore.FindForTexture(sourceTexture);
                if (found.Count == 1) recipe = found[0];
            }
            string recipeLabel = recipe != null
                ? AssetDatabase.GetAssetPath(recipe)
                : string.Format(Localization.RegisterRecipeNew, RecipeStore.DefaultFolder + "/" + sourceTexture.name + ".asset");

            // コンポーネントは登録先に 1 つ(既にあればそこへ足す)。置き換え・まとめが起きるなら先に知らせる。
            RecipeRegistration.Describe(target, sourceTexture, recipe, out var replaced, out int components);
            // 以前「適用して保存」で書き出した画像を差しているマテリアルは、元のテクスチャへ戻す
            // (戻さないと非破壊の色替えが効かない)。書き換えられない場所のものは知らせるだけ。
            var exports = RecipeRegistration.ExportsOf(sourceTexture);
            var exportUsers = RecipeRegistration.MaterialsUsing(target, exports);
            int switchable = 0;
            foreach (var m in exportUsers) if (RecipeRegistration.IsEditableMaterial(m)) switchable++;
            var message = string.Format(Localization.RegisterConfirmFormat, target.name, sourceTexture.name, recipeLabel);
            if (switchable > 0)
                message += "\n\n" + string.Format(Localization.RegisterSwitchExportsFormat, switchable, sourceTexture.name);
            if (exportUsers.Count > switchable)
                message += "\n\n" + string.Format(Localization.RegisterLockedExportsFormat, exportUsers.Count - switchable);
            if (components >= 2)
                message += "\n\n" + string.Format(Localization.RegisterMergeFormat, components);
            else if (components == 1)
                message += "\n\n" + Localization.RegisterAddToExisting;
            if (replaced != null)
                message += "\n\n" + string.Format(Localization.RegisterReplaceFormat, sourceTexture.name,
                    AssetDatabase.GetAssetPath(replaced));
            if (!EditorUtility.DisplayDialog(Localization.RegisterNonDestructive, message,
                    Localization.RegisterOk, Localization.Cancel))
                return;

            // 作業用マスクを state へ同期してからレシピへ書く(マスク込みで保存される)。
            _maskView?.SyncBuffersToState();
            if (recipe == null)
                recipe = RecipeStore.Create(sourceTexture, _session);
            else
                RecipeStore.Save(recipe, _session);
            _boundRecipe = recipe;

            // マテリアルを戻すのとコンポーネントの登録を 1 回の Undo にまとめる。
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(Localization.RegisterNonDestructive);
            RecipeRegistration.SwitchToSource(exportUsers, sourceTexture, exports);
            var component = RecipeRegistration.Attach(target, recipe);
            Undo.CollapseUndoOperations(undoGroup);
            EditorGUIUtility.PingObject(component);
            ShowNotification(new GUIContent(string.Format(Localization.RegisterDoneFormat, target.name)));
        }
    }
}
