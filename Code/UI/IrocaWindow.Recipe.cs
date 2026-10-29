// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
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
            // メニューと同じ開き方にする(素の GetWindow だとタブ名が型名になり、大きさも既定にならない)
            ShowWindow();
            var win = GetWindow<IrocaWindow>();
            win.Show();
            win.Focus();
            if (win.sourceTexture == recipe.sourceTexture && win.BoundRecipe == recipe) return;
            win.SwitchSourceTexture(recipe.sourceTexture, recipe);
        }

        /// <summary>
        /// 自動化 API(MCP)から: テクスチャを開く(そのテクスチャのレシピがプロジェクトに 1 つだけならそれに結び付く)。
        /// 既に開いていれば何もしない。
        /// </summary>
        internal static void OpenTexture(Texture2D texture)
        {
            if (texture == null) return;
            // メニューと同じ開き方にする(素の GetWindow だとタブ名が型名になり、大きさも既定にならない)
            ShowWindow();
            var win = GetWindow<IrocaWindow>();
            win.Show();
            win.Focus();
            if (win.sourceTexture == texture) return;
            win.SwitchSourceTexture(texture);
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
            _boundRecipe = SessionPersistence.RecipeFor(sourceTexture, BoundRecipe);
            if (_boundRecipe == null) return false;

            // 壊れたレシピは読まない(保存も BoundRecipe 経由で上書きしない)。
            var state = SessionPersistence.LoadFromRecipe(_boundRecipe);
            if (state == null) return false;
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
            // 外(git など)で書き換わったレシピを、窓の古い内容で上書きしない。窓が残るならレシピから読み直す。
            if (!SessionPersistence.SaveToRecipe(recipe, _session)) ScheduleRecipeReload();
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

        // ───────────── 外(自動化 API・MCP)からの変更 ─────────────

        /// <summary>
        /// <paramref name="texture"/> を表示している窓の、いまの編集内容の複製(マスク込み)。表示している窓が無ければ null。
        /// 窓は編集をすぐにはレシピへ書かないので、自動化 API はこれを土台にして、利用者が窓で見ているものに色替えを足す。
        /// </summary>
        internal static IrocaSessionState EditingStateOf(Texture2D texture)
        {
            var win = WindowShowing(texture);
            if (win == null || win._session == null) return null;
            win._maskView?.SyncBuffersToState();
            return JsonUtility.FromJson<IrocaSessionState>(JsonUtility.ToJson(win._session));
        }

        /// <summary>
        /// <paramref name="texture"/> を表示している窓があれば true と、その窓が結び付いているレシピ(無ければ null)。
        /// 自動化 API が、窓の編集内容を書き込み先のレシピの土台にしてよいか(同じレシピか)を確かめるのに使う。
        /// </summary>
        internal static bool IsShowing(Texture2D texture, out IrocaRecipe boundRecipe)
        {
            var win = WindowShowing(texture);
            boundRecipe = win != null ? win.BoundRecipe : null;
            return win != null;
        }

        /// <summary>
        /// 自動化 API がレシピを書き換えた後に呼ぶ。そのテクスチャを表示している窓を、今の編集内容を保存せずに
        /// レシピから読み直す(保存すると、外の変更を窓の古い内容で上書きしてしまう)。読み直しは窓の Undo に
        /// 積むので、Ctrl+Z で外の変更の前に戻せる(戻した内容は窓の次の保存でレシピにも書かれる)。
        /// </summary>
        internal static void ReloadFromRecipe(IrocaRecipe recipe, string undoName)
        {
            if (recipe == null || recipe.sourceTexture == null) return;
            var win = WindowShowing(recipe.sourceTexture);
            if (win == null) return;
            win._maskView?.SyncBuffersToState();
            Undo.RegisterCompleteObjectUndo(win, undoName);
            win._boundRecipe = recipe;
            win.LoadPersistedSessionForCurrentTexture();
            win._maskView?.ResetActiveTarget();
            win.Repaint();
        }

        /// <summary>
        /// 自動化 API の自動調整が AI の証拠を求めるときの受け口。AI の提案を受け取るのは窓のコントローラ
        /// だけにしておかないと、提案を奪い合う(MaskSuggestController の説明)ので、窓があればそれを返す。
        /// 窓が <paramref name="texture"/> を表示していれば、窓のプレビューと同じ画素とキーも返す(埋め込みを使い回し、
        /// 窓の AI 提案の元を切り替えない。呼び出し側は <paramref name="pixels"/> を使い終えたら Release する)。
        /// 窓が無い・AI が無ければ null(呼び出し側がサービスを直接使う)。
        /// </summary>
        internal static MaskSuggestController EvidenceChannel(Texture2D texture, out NativePixels pixels, out string key)
        {
            pixels = null;
            key = null;
            foreach (var e in EditingSessionRegistry.Snapshot())
            {
                if (!(e is IrocaWindow w) || w._maskView == null) continue;
                var ctl = w._maskView.SuggestController;
                if (ctl == null) continue;
                if (w.sourceTexture == texture && w._previewView != null
                    && w._previewView.TryGetTrueSourcePixels(texture, out var shared, out _, out _))
                {
                    pixels = shared.AddRef();
                    key = w._previewView.TrueSourceCacheKey();
                }
                return ctl;
            }
            return null;
        }

        private static IrocaWindow WindowShowing(Texture2D texture)
        {
            if (texture == null) return null;
            foreach (var e in EditingSessionRegistry.Snapshot())
                if (e is IrocaWindow w && w.sourceTexture == texture) return w;
            return null;
        }

        // ───────────── 外(git など)でのレシピの書き換え ─────────────
        // 窓が最後にレシピと同期した中身と違えば外からの変更とみなし、書き戻さずにレシピから読み直す
        // (仕組みと理由は SessionPersistence の「外でのレシピの書き換え」)。

        [System.NonSerialized] private bool _recipeReloadScheduled;
        // 読めないと分かったレシピの中身(同じ中身で読み直しを繰り返さない・警告を何度も出さない)。
        [System.NonSerialized] private string _unreadableRecipeJson;

        /// <summary>
        /// 窓の有効化(開いた・ドメインリロード)で呼ぶ。ドメインリロードでは、レシピを読み直さずメモリ上の内容を
        /// 使うので、その前に取り込まれた変更(git の pull にスクリプトが含まれていた場合など)はここで拾う。
        /// </summary>
        private void CheckRecipeAfterEnable() => ScheduleRecipeReload();

        /// <summary>
        /// レシピ(.asset)が取り込まれた(git の pull・checkout の後など)ときに呼ばれる(<see cref="RecipeImportWatcher"/>)。
        /// 結び付いたレシピが外で書き換わっていれば読み直す。次の保存を待たないのは、古い内容のまま編集を続けさせないため。
        /// </summary>
        internal void OnRecipeAssetsImported()
        {
            if (SessionPersistence.RecipeChangedOutside(BoundRecipe)) ScheduleRecipeReload();
        }

        /// <summary>
        /// Undo/Redo の後に呼ぶ。結び付いたレシピの中身が、窓が前に同期したことのある中身へ戻っていれば
        /// (Undo でレシピも戻ったとき)、外からの変更ではないので同期した中身として覚え直す。
        /// internal は RecipeSyncTests が直接呼ぶため。
        /// </summary>
        internal void AdoptRecipeAfterUndo()
        {
            var recipe = BoundRecipe;
            if (recipe != null && SessionPersistence.RecipeChangedOutside(recipe)
                && SessionPersistence.WasRecipeSyncedBefore(recipe))
                SessionPersistence.MarkRecipeSynced(recipe);
        }

        /// <summary>
        /// 外で書き換わったレシピを、次のエディタの更新で読み直す。保存の途中(閉じる・ビルドの直前など)からも
        /// 呼ばれるので、その場では読み直さない。
        /// </summary>
        private void ScheduleRecipeReload()
        {
            if (_recipeReloadScheduled) return;
            _recipeReloadScheduled = true;
            EditorApplication.delayCall += ReloadChangedRecipe;
        }

        // internal は RecipeSyncTests が次のエディタの更新を待たずに呼ぶため。
        internal void ReloadChangedRecipe()
        {
            _recipeReloadScheduled = false;
            if (this == null) return;   // 窓が閉じられた
            var recipe = BoundRecipe;
            if (recipe == null || !SessionPersistence.RecipeChangedOutside(recipe)) return;
            // 書き出し・自動調整の途中やマスクを塗っている途中は、終わってから読み直す。
            if (IsJobBlockingUI || (_maskView != null && _maskView.isPainting))
            {
                ScheduleRecipeReload();
                return;
            }
            // 読めない(新しい版のいろかで作られた・壊れた)なら読み直さない。保存もしない(覚えた中身と違うので)。
            // 「保存先のレシピ」に読めない旨が出る。
            if (ReferenceEquals(_unreadableRecipeJson, recipe.sessionJson)) return;
            if (RecipeStore.Load(recipe) == null)
            {
                _unreadableRecipeJson = recipe.sessionJson;
                return;
            }
            _maskView?.SyncBuffersToState();
            // 外の変更を拾うのは後から(delayCall)なので、利用者が直前にした操作と同じ「元に戻す」にまとめない
            // (まとめると、Ctrl+Z 1 回で読み直しと一緒に直前の操作まで戻る)。
            SeparateUndo.Record("Iroca: Reload Recipe", () =>
            {
                Undo.RegisterCompleteObjectUndo(this, "Iroca: Reload Recipe");
                LoadPersistedSessionForCurrentTexture();
                _maskView?.ResetActiveTarget();
            });
            string message = string.Format(Localization.RecipeReloadedFormat, recipe.name);
            Debug.Log("[Iroca] " + message);
            ShowNotification(new GUIContent(message));
            Repaint();
        }

        // ───────────── 登録 ─────────────

        /// <summary>「アバターに非破壊で登録」。登録先を決め、レシピを作るか更新し、コンポーネントを付ける。</summary>
        internal void RegisterToAvatar()
        {
            if (sourceTexture == null) return;
            // Play 中に付けたコンポーネントは Play を止めると消え、書き出した画像から元へ戻したマテリアル(アセット)
            // だけが残る。
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog(Localization.RegisterNonDestructive, Localization.RegisterInPlayMode, Localization.OK);
                return;
            }
            if (RecolorInput.CountEnabled(_session?.zones) == 0)
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
                : string.Format(Localization.RegisterRecipeNew, RecipeStore.PathForNew(sourceTexture));
            // 読めないレシピ(新しい版のいろかで作られた・壊れた)は上書きしない(戻せないため。自動化 API と同じ判定)。
            if (recipe != null && !string.IsNullOrEmpty(recipe.sessionJson) && RecipeStore.Load(recipe) == null)
            {
                EditorUtility.DisplayDialog(Localization.RegisterNonDestructive,
                    string.Format(Localization.RegisterRecipeUnreadableFormat, recipeLabel), Localization.OK);
                return;
            }
            // 保存先のレシピが外(git など)で書き換わっていたら、窓の古い内容で上書きせず、読み直してから
            // 確かめてもらう。
            if (recipe != null && recipe == BoundRecipe && SessionPersistence.RecipeChangedOutside(recipe))
            {
                EditorUtility.DisplayDialog(Localization.RegisterNonDestructive,
                    string.Format(Localization.RegisterRecipeChangedOutsideFormat, recipeLabel), Localization.OK);
                ScheduleRecipeReload();
                return;
            }

            // コンポーネントは登録先に 1 つ(既にあればそこへ足す)。置き換え・まとめが起きるなら先に知らせる。
            RecipeRegistration.Describe(target, sourceTexture, recipe, out var replaced, out int components);
            // 以前「適用して保存」で書き出した画像を差しているマテリアルは、元のテクスチャへ戻す
            // (戻さないと非破壊の色替えが効かない)。書き換えられない場所のものは知らせるだけ。
            var exports = RecipeRegistration.ExportsOf(sourceTexture);
            var exportUsers = RecipeRegistration.MaterialsUsing(target, exports);
            int switchable = 0;
            foreach (var m in exportUsers) if (RecipeRegistration.IsEditableMaterial(m)) switchable++;
            // 戻すのは .mat そのものなので、登録先の外で同じマテリアルを使っている所も元の色に戻る。開いているシーンで
            // 数えて知らせ、それらは戻さずに登録することもできるようにする。
            var shared = RecipeRegistration.UsedOutside(target, exportUsers, out int sharedPlaces);
            // 冒頭の「マテリアルは変わりません」は、戻すマテリアルがあるときは正しくない。
            var message = string.Format(switchable > 0 ? Localization.RegisterConfirmSwitchFormat : Localization.RegisterConfirmFormat,
                target.name, sourceTexture.name, recipeLabel);
            if (switchable > 0)
                message += "\n\n" + string.Format(Localization.RegisterSwitchExportsFormat, switchable, sourceTexture.name);
            if (shared.Count > 0)
                message += "\n\n" + string.Format(Localization.RegisterSharedExportsFormat, shared.Count, sharedPlaces,
                    Localization.RegisterKeepShared);
            if (exportUsers.Count > switchable)
                message += "\n\n" + string.Format(Localization.RegisterLockedExportsFormat, exportUsers.Count - switchable);
            if (components >= 2)
                message += "\n\n" + string.Format(Localization.RegisterMergeFormat, components);
            else if (components == 1)
                message += "\n\n" + Localization.RegisterAddToExisting;
            if (replaced != null)
                message += "\n\n" + string.Format(Localization.RegisterReplaceFormat, sourceTexture.name,
                    AssetDatabase.GetAssetPath(replaced));
            // 流用するレシピ(別のアバターに登録済みの同じテクスチャのレシピなど)をほかでも使っていれば、
            // 登録した後の編集がそちらの色にも効くことを知らせる。
            var otherUsers = RecipeRegistration.OtherUsers(recipe, target);
            if (otherUsers.Count > 0)
                message += "\n\n" + string.Format(Localization.RegisterRecipeSharedFormat, NameList(otherUsers));
            // 0 = 登録、1 = キャンセル(閉じたときも)、2 = 共有のマテリアルは戻さずに登録。
            int choice = shared.Count > 0
                ? EditorUtility.DisplayDialogComplex(Localization.RegisterNonDestructive, message,
                    Localization.RegisterOk, Localization.Cancel, Localization.RegisterKeepShared)
                : EditorUtility.DisplayDialog(Localization.RegisterNonDestructive, message,
                    Localization.RegisterOk, Localization.Cancel) ? 0 : 1;
            if (choice == 1) return;
            if (choice == 2) exportUsers.RemoveAll(shared.Contains);

            // 作業用マスクを state へ同期してからレシピへ書く(マスク込みで保存される)。
            _maskView?.SyncBuffersToState();
            if (recipe == null)
                recipe = RecipeStore.Create(sourceTexture, _session);
            else
                RecipeStore.Save(recipe, _session);
            _boundRecipe = recipe;
            SessionPersistence.MarkRecipeSynced(recipe);

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

        // 確認画面に出すオブジェクト名の並び(多ければ 3 つと残りの数)。
        private static string NameList(List<IrocaRecolor> components)
        {
            const int shown = 3;
            var names = new List<string>();
            for (int i = 0; i < components.Count && i < shown; i++)
                names.Add(string.Format(Localization.NameQuoteFormat, components[i].gameObject.name));
            string list = string.Join(Localization.NameListSeparator, names);
            return components.Count > shown ? string.Format(Localization.NameListMoreFormat, list, components.Count - shown) : list;
        }
    }

    /// <summary>
    /// アセット(.asset)の取り込み(git の pull・checkout・バックアップからの復元の後など)を、開いているいろかの窓へ
    /// 知らせる(<see cref="IrocaWindow"/> の「外でのレシピの書き換え」)。取り込みの結果は変えない。
    /// </summary>
    internal sealed class RecipeImportWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            bool anyAsset = false;
            foreach (var path in importedAssets)
            {
                if (path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)) { anyAsset = true; break; }
            }
            if (!anyAsset) return;
            foreach (var e in EditingSessionRegistry.Snapshot())
                if (e is IrocaWindow w) w.OnRecipeAssetsImported();
        }
    }
}
