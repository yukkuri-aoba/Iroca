// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 編集内容の保存と復元(0.4.0 の計画の 4-2 の 5)。テクスチャごとの編集状態を、どこから読み・どこへ書くかを決める。
    /// <list type="bullet">
    /// <item>復元の優先順: 結び付いたレシピ → テクスチャごとのセッションのファイル(SessionFileStore) → 既定。</item>
    /// <item>前回編集していたテクスチャ(EditorPrefs。Unity を起動して最初に開いたときに読む)。</item>
    /// <item>外(git など)でのレシピの書き換えの検出(下の「外でのレシピの書き換え」)。</item>
    /// <item>開いただけでは古い形のレシピを書き直さない(下の「開いただけでは書かない」)。</item>
    /// </list>
    /// 編集状態を差し替える・Undo に積む・マスクの作業用バッファと同期する・利用者に知らせるのは、呼び出し側
    /// (いろかのウィンドウ)。マスクのファイル(MaskFileStore)は今はマスクの編集(MaskPaintView)が読み書きする。
    /// メインスレッド専用。
    /// </summary>
    internal static class SessionPersistence {
        /// <summary>テクスチャのアセットのパス(アセットでなければ null)。</summary>
        internal static string TexturePathOf(Texture2D tex) {
            if (tex == null) {
                return null;
            }
            string path = AssetDatabase.GetAssetPath(tex);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// テクスチャの編集内容のファイルを決める鍵(MaskFileStore・SessionFileStore に渡す。アセットでなければ null)。
        /// ふつうのテクスチャはパスそのもので、1 つのファイルに複数のテクスチャがあるときはサブアセットの番号を足す。
        /// </summary>
        internal static string StorageKeyOf(Texture2D tex) {
            string path = TexturePathOf(tex);
            if (path == null || AssetDatabase.IsMainAsset(tex)) {
                return path;
            }
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(tex, out string _, out long localId)
                ? path + GuidFileCache.SubAssetSeparator + localId
                : path;
        }

        /// <summary>テクスチャのアセットの GUID(アセットでなければ空)。</summary>
        internal static string GuidOf(Texture2D tex) {
            string path = TexturePathOf(tex);
            return path != null ? AssetDatabase.AssetPathToGUID(path) : "";
        }

        // ─────────────── レシピとセッションのファイル ───────────────

        /// <summary>
        /// 開いたテクスチャのレシピを決める。既に結び付いているレシピ(<paramref name="bound"/>。レシピから開いた・
        /// ドメインリロード)を優先し、無ければそのテクスチャのレシピがちょうど 1 つならそれ。複数あれば、このテクスチャで
        /// 最後に結び付いたレシピ(開き直すと前と違う所へ保存しないように)。どれでもなければ null(どれに書くか決められないため。
        /// インスペクタの「いろかで開く」で選ぶ。窓が案内を出す)。
        /// </summary>
        internal static IrocaRecipe RecipeFor(Texture2D tex, IrocaRecipe bound) {
            if (tex == null) {
                return null;
            }
            if (bound != null) {
                return bound;
            }
            var found = RecipeStore.FindForTexture(tex);
            if (found.Count == 1) {
                return found[0];
            }
            if (found.Count == 0) {
                return null;
            }
            string guid = SessionFileStore.LoadBoundRecipeGuid(StorageKeyOf(tex));
            if (string.IsNullOrEmpty(guid)) {
                return null;
            }
            foreach (var recipe in found) {
                if (AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(recipe)) == guid) {
                    return recipe;
                }
            }
            return null;
        }

        /// <summary>テクスチャに結び付いたレシピを覚える(<see cref="RecipeFor"/> が開き直したときに使う)。null なら忘れる。</summary>
        internal static void RememberBoundRecipe(Texture2D tex, IrocaRecipe recipe) {
            string path = StorageKeyOf(tex);
            if (path == null) {
                return;
            }
            string recipePath = recipe != null ? AssetDatabase.GetAssetPath(recipe) : null;
            SessionFileStore.SaveBoundRecipeGuid(path, string.IsNullOrEmpty(recipePath) ? null : AssetDatabase.AssetPathToGUID(recipePath));
        }

        /// <summary>
        /// レシピから編集内容(マスク込み)を読む。読めなければ null。読めたら同期した中身として覚える。
        /// 壊れたレシピは読まない(保存もしない)。空のレシピは書いてよいので、同期した中身として覚える
        /// (覚えていないレシピには保存しない。下の「外でのレシピの書き換え」)。
        /// </summary>
        internal static IrocaSessionState LoadFromRecipe(IrocaRecipe recipe) {
            var state = RecipeStore.Load(recipe);
            if (state == null) {
                if (string.IsNullOrEmpty(recipe.sessionJson)) {
                    MarkRecipeSynced(recipe);
                }
                return null;
            }
            MarkRecipeSynced(recipe);
            return state;
        }

        /// <summary>
        /// 結び付いたレシピへ編集内容(マスク込み)を書く。マスクは呼び出し側で state へ同期済みであること。
        /// 外(git など)で書き換わったレシピは窓の古い内容で上書きせず false(呼び出し側がレシピから読み直す)。
        /// 読めないレシピ(新しい版で作られた等)は上書きしない。開いたときの内容のまま(編集していない)なら書かない
        /// (<see cref="MarkRecipeOpened"/>)。
        /// </summary>
        internal static bool SaveToRecipe(IrocaRecipe recipe, IrocaSessionState state) {
            if (RecipeChangedOutside(recipe)) {
                return false;
            }
            if (RecipeStore.SaveUnlessUnreadable(recipe, state, json => UnchangedSinceOpened(recipe, json))) {
                // 書いた後のレシピは今の形(次からはいつもの比べ方で足りる)。
                ForgetRecipeOpened(recipe);
            }
            MarkRecipeSynced(recipe);
            return true;
        }

        /// <summary>テクスチャのセッションのファイルを読む。無い・テクスチャがアセットでなければ null。</summary>
        internal static IrocaSessionState LoadFromFile(Texture2D tex, out bool unreadable) {
            unreadable = false;
            string path = StorageKeyOf(tex);
            return path != null ? SessionFileStore.LoadSession(path, out unreadable) : null;
        }

        /// <summary>
        /// テクスチャのセッションのファイルへ書く。<paramref name="loadFailed"/> はファイルが「存在するのに読めなかった」
        /// 印で、立っている間は空保存での削除・無退避の上書きをしない。書けなかったときだけ false。
        /// <paramref name="wroteContent"/> は中身を書けた(ファイルはもう読めるので、印を下ろしてよい)とき true。
        /// </summary>
        internal static bool SaveToFile(Texture2D tex, IrocaSessionState state, bool loadFailed, out bool wroteContent) {
            wroteContent = false;
            string path = StorageKeyOf(tex);
            if (path == null || state == null) {
                return true;
            }
            bool ok = SessionFileStore.SaveSession(path, state, loadFailed);
            wroteContent = ok && !SessionFileStore.IsEmpty(state);
            return ok;
        }

        // ─────────────── 前回編集していたテクスチャ ───────────────

        // 最後に編集していたテクスチャの GUID を保存する EditorPrefs キー（Editor 再起動を跨ぐ）。
        // EditorPrefs は Unity インストール単位(全プロジェクト共有)なので、プロジェクトパス
        // (Application.dataPath)の安定ハッシュを付与してプロジェクトごとに分離する。これが無いと
        // コピーした別プロジェクトが前プロジェクトの GUID を誤って自動ロードしうる。string.GetHashCode
        // はプロセス間で不安定(乱択化)なため FNV-1a を使う(再起動を跨いで同一キー)。
        internal static string LastTextureGuidPrefKey =>
            IrocaPrefs.LastTextureGuidPrefix + StableHashHex(Application.dataPath);

        private static string StableHashHex(string s) {
            uint h = 2166136261u; // FNV-1a 32bit
            if (s != null) {
                foreach (char c in s) { h ^= c; h *= 16777619u; }
            }
            return h.ToString("X8");
        }

        /// <summary>テクスチャの GUID を「前回編集テクスチャ」として記憶する（テクスチャ未設定なら空）。</summary>
        internal static void RememberLastEdited(Texture2D tex) {
            string path = TexturePathOf(tex);
            string guid = path != null ? AssetDatabase.AssetPathToGUID(path) : "";
            EditorPrefs.SetString(LastTextureGuidPrefKey, guid ?? "");
        }

        /// <summary>記憶した前回編集テクスチャ。記憶が無い・解決できなければ null。</summary>
        internal static Texture2D LastEdited() {
            string guid = EditorPrefs.GetString(LastTextureGuidPrefKey, "");
            if (string.IsNullOrEmpty(guid)) {
                return null;
            }
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) {
                return null;
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ─────────────── 外(git など)でのレシピの書き換え ───────────────
        // 窓は編集内容をレシピへ書き戻す(自動保存・テクスチャの切り替え・閉じる・再コンパイル・ビルドの直前)。
        // git の pull・checkout やバックアップからの復元でレシピが外から書き換わった後に書き戻すと、取り込んだ
        // 変更が黙って消え、再生・アップロードにも古い色替えが使われていた。
        // そこで、窓が最後にレシピと同期した(読んだ・書いた)中身を覚えておき、レシピの中身がそれと違えば外からの
        // 変更とみなして書き戻さず、レシピから読み直す(窓の Undo に積むので、Ctrl+Z で読み直す前に戻せる)。
        // 覚える場所は窓の直列化ではなく static と SessionState(ドメインリロードをまたぎ、Unity を閉じると消える。
        // 起動後はレシピを正として読み直すので要らない)。窓の直列化に置くと、Undo で窓の内容と一緒に巻き戻り、
        // 同期済みのレシピを「外で変わった」と取り違える。
        // 同期したことの無いレシピ(開いたときに読めなかった)には書かない。迷ったら書かずに読み直す側に倒す
        // (読み直しは Undo で戻せるが、上書きしたレシピは戻せない)。

        // レシピ(instanceID) → 最後に同期した sessionJson。中身が変わっていなければ参照の比較だけで済む。
        private static readonly Dictionary<int, string> s_recipeSyncedJson = new Dictionary<int, string>();
        // SessionState に残す、同期した中身のハッシュの数(新しい順の最後が今の同期)。古いものは Undo で
        // レシピが前の中身へ戻ったとき(自動化 API の色替えの Ctrl+Z)に、外からの変更と見分けるのに使う。
        private const int RecipeSyncHistory = 8;

        private static string RecipeSyncKey(IrocaRecipe recipe) {
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(recipe));
            return string.IsNullOrEmpty(guid) ? null : IrocaPrefs.RecipeSyncedPrefix + guid;
        }

        private static string RecipeJsonHash(string json) => Hash128.Compute(json ?? "").ToString();

        private static string[] RecipeSyncedHashes(IrocaRecipe recipe) {
            string key = RecipeSyncKey(recipe);
            string value = key != null ? SessionState.GetString(key, "") : "";
            return value.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>レシピの今の中身を、窓と同期した中身として覚える(窓がレシピを読んだ・書いた直後に呼ぶ)。</summary>
        internal static void MarkRecipeSynced(IrocaRecipe recipe) {
            if (recipe == null) {
                return;
            }
            string json = recipe.sessionJson ?? "";
            int id = recipe.GetInstanceID();
            if (s_recipeSyncedJson.TryGetValue(id, out var known) && ReferenceEquals(known, json)) {
                return;
            }
            s_recipeSyncedJson[id] = json;
            string key = RecipeSyncKey(recipe);
            if (key == null) {
                return;
            }
            string hash = RecipeJsonHash(json);
            var hashes = new List<string>(RecipeSyncedHashes(recipe));
            hashes.Remove(hash);
            hashes.Add(hash);
            if (hashes.Count > RecipeSyncHistory) {
                hashes.RemoveRange(0, hashes.Count - RecipeSyncHistory);
            }
            SessionState.SetString(key, string.Join(",", hashes));
        }

        /// <summary>
        /// 窓が最後に同期してから、レシピの中身が外で変わったか。同期したことの無いレシピも true。
        /// </summary>
        internal static bool RecipeChangedOutside(IrocaRecipe recipe) {
            if (recipe == null) {
                return false;
            }
            string json = recipe.sessionJson ?? "";
            int id = recipe.GetInstanceID();
            if (s_recipeSyncedJson.TryGetValue(id, out var known) && ReferenceEquals(known, json)) {
                return false;
            }
            // 参照が違っても(同じ中身で取り込み直された・ドメインリロードの後)中身が同じなら変わっていない。
            var hashes = RecipeSyncedHashes(recipe);
            if (hashes.Length == 0 || hashes[hashes.Length - 1] != RecipeJsonHash(json)) {
                return true;
            }
            s_recipeSyncedJson[id] = json;
            return false;
        }

        /// <summary>レシピの今の中身が、窓が前に同期したことのある中身か(最後に同期したものより前も含む)。</summary>
        internal static bool WasRecipeSyncedBefore(IrocaRecipe recipe)
            => recipe != null && System.Array.IndexOf(RecipeSyncedHashes(recipe), RecipeJsonHash(recipe.sessionJson)) >= 0;

        // ─────────────── 開いただけでは書かない ───────────────
        // 前の版で作った・ゾーンに id が無い・自動化や手で書いた JSON のレシピは、読んで書き直すと文字列が変わる(新しい形になる。
        // 同じ版で書いたレシピは変わらない)。開いただけで(編集しなくても)自動保存・閉じる・再コンパイル・ビルドの直前の保存が新しい形で
        // 書き、git で管理している .asset に差分が出ていた。
        // そこで、窓がレシピから読んだ直後に「そのときのレシピの文字列」と「読んだ内容をそのまま書いたらこうなる JSON」のハッシュを覚え、
        // 書こうとする JSON が後者のままで、レシピも前者のままなら書かない。編集すれば書き、編集して戻したら(一度書いた後なら)戻した中身を
        // 書く。覚える場所は同期の印と同じ SessionState(ドメインリロードをまたぐ。Unity を閉じたら、次に開いたときに読み直して覚え直す)。

        private static string RecipeOpenedKey(IrocaRecipe recipe) {
            string guid = recipe != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(recipe)) : null;
            return string.IsNullOrEmpty(guid) ? null : IrocaPrefs.RecipeOpenedPrefix + guid;
        }

        /// <summary>
        /// 窓がレシピから読んだ直後に呼ぶ。<paramref name="asLoaded"/> は読んだ内容をそのまま保存するときの形(マスク込み。
        /// <see cref="EditingSession.ToSaveState"/>。ゾーンの id など、窓が読むときに足したものも入っている)。
        /// </summary>
        internal static void MarkRecipeOpened(IrocaRecipe recipe, IrocaSessionState asLoaded) {
            string key = RecipeOpenedKey(recipe);
            if (key == null || asLoaded == null) {
                return;
            }
            SessionState.SetString(key, RecipeJsonHash(recipe.sessionJson) + "," + RecipeJsonHash(JsonUtility.ToJson(asLoaded)));
        }

        private static void ForgetRecipeOpened(IrocaRecipe recipe) {
            string key = RecipeOpenedKey(recipe);
            if (key != null) {
                SessionState.EraseString(key);
            }
        }

        // 書こうとする JSON が、開いたときの内容のままで、レシピも開いたときのままか。
        private static bool UnchangedSinceOpened(IrocaRecipe recipe, string json) {
            string key = RecipeOpenedKey(recipe);
            string value = key != null ? SessionState.GetString(key, "") : "";
            int comma = value.IndexOf(',');
            if (comma < 0) {
                return false;
            }
            return value.Substring(comma + 1) == RecipeJsonHash(json)
                && value.Substring(0, comma) == RecipeJsonHash(recipe.sessionJson);
        }
    }
}
