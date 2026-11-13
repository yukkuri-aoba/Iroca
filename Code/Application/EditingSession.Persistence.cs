// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0

namespace Iroca {
    // EditingSession のうち、保存と復元: テクスチャごとのファイル(マスク = MaskFileStore、ゾーンと設定 = SessionFileStore)と、
    // 結び付いたレシピ。どこから読み・どこへ書くかの規則は SessionPersistence と各ストアが持ち、ここは編集中の文書とマスクへ
    // 当てる順と、「ファイルを読めなかった」印を持つ(2026-11-13 まで、印と順はウィンドウとマスクの画面が持っていた)。
    // 画面の後始末(重ね表示の作り直し・AI 提案の取り消し・Undo の記録・知らせ)は呼ぶ側(ウィンドウ・マスクの画面)。
    internal sealed partial class EditingSession {
        /// <summary>今のテクスチャに結び付いたレシピ(無ければ null)。</summary>
        internal IrocaRecipe BoundRecipe => BoundRecipeOf(Document);

        /// <summary>
        /// 文書の今のテクスチャに結び付いたレシピ(無ければ null)。文書がレシピを覚えていても、そのレシピの元テクスチャが今の
        /// テクスチャと違えば(Undo でテクスチャだけ戻ったなど)結び付いていない。
        /// </summary>
        internal static IrocaRecipe BoundRecipeOf(IrocaEditingDocument document) =>
            document != null && document.boundRecipe != null && document.sourceTexture != null
            && document.boundRecipe.sourceTexture == document.sourceTexture
                ? document.boundRecipe
                : null;

        /// <summary>
        /// 今のテクスチャのマスクのファイル(MaskCache)が「存在するのに読めなかった」か。true の間は、空の保存での削除と、退避なしの
        /// 上書きをしない(一時的な読み込みの失敗が、データの恒久的な消失に化けないように)。中身のある保存ができたら下ろす。
        /// </summary>
        internal bool MaskLoadFailed { get; private set; }

        /// <summary>
        /// 今のテクスチャのセッションのファイル(SessionCache)が「存在するのに読めなかった」か。<see cref="MaskLoadFailed"/> と同じ守り。
        /// </summary>
        internal bool SessionLoadFailed { get; private set; }

        // テクスチャの編集内容のファイルを決める鍵(アセットでなければ null)。
        private string StorageKey => SessionPersistence.StorageKeyOf(Document.sourceTexture);

        // ─────────────── マスクのファイル ───────────────

        /// <summary>
        /// 保存形式にしたマスクを MaskCache ファイルへ書く。ディスクへの書き込みに失敗したときだけ false(呼ぶ側が知らせる)。
        /// テクスチャがアセットでなければ何もしない(true)。
        /// </summary>
        internal bool SaveMasksToFile(MaskState ms) {
            string path = StorageKey;
            if (path == null) {
                return true;
            }

            bool ok = MaskFileStore.SaveMask(path, ms, MaskLoadFailed);

            // 有効な内容を書き込めたら通常動作へ復帰する（空保存スキップの場合は
            // 解除しない: 解除すると次の空保存が未読ファイルを削除してしまう）。
            bool hasContent = ms != null &&
                (!string.IsNullOrEmpty(ms.commonMaskBase64)
                 || (ms.zones != null && ms.zones.Count > 0)
                 || (ms.zoneIncludes != null && ms.zoneIncludes.Count > 0));
            if (ok && hasContent) {
                MaskLoadFailed = false;
            }
            return ok;
        }

        /// <summary>
        /// MaskCache ファイルから全マスクを読んで、編集中のマスクを置き換える(Editor の再起動をまたぐ正規のストア)。
        /// ファイルから層を読めたら true(ドメインリロードの後に、履歴の版とファイルのどちらを採るかを決めるのに使う。
        /// ファイルを 2 回読まないように返す)。テクスチャがアセットでなければ何もしない(false)。
        /// </summary>
        internal bool RestoreMasksFromFile() {
            string path = StorageKey;
            if (path == null) {
                return false;
            }

            Masks.Clear();
            MaskLoadFailed = false;

            var fileState = MaskFileStore.LoadMask(path, out bool unreadable);
            if (fileState != null && fileState.width > 0 && fileState.height > 0) {
                // 展開規則(寸法不一致・壊れた項目は捨てる)は Core の MaskStateCodec と同じ(MaskWorkspace.DecodeFrom)。
                Masks.DecodeFrom(fileState, out int dropped);
                // 読めない項目があったら、読めた分で開くが、読めなかったファイルと同じく扱う(次の保存の前に元のファイルを .bak に
                // 残し、窓に帯を出す)。黙って捨てると、次の保存でファイルからも消える。レシピから読むときは .bak の仕組みが無いので、
                // 警告(DecodeFrom が出す)だけ。
                if (dropped > 0) {
                    MaskLoadFailed = true;
                }
                return Masks.HasLayers;
            }

            // ファイルは存在するのに使える状態を得られなかった（IO失敗 or 形状不正の JSON）。
            // このセッションでの空保存による削除・無退避上書きを抑止する。
            MaskLoadFailed = unreadable || fileState != null;

            // レシピが結び付いていれば、同じ内容がレシピにある。読めないまま空で進むと、次の保存でレシピのマスクまで空になる
            // (印が守るのはファイルだけ)。印は残すので、ファイルは次の保存で退避してから書き直す。
            if (MaskLoadFailed) {
                RestoreMasksFromBoundRecipe();
            }
            return false;
        }

        /// <summary>
        /// いまのテクスチャのマスクのファイルが読めるかを確かめ直す(Undo でテクスチャが戻ったとき。読めなかった印はテクスチャごと)。
        /// 読めても、編集中が空なのにファイルに中身があれば、編集中は読めなかったときのまま(空の保存でファイルは消えるので、ふつうは
        /// 食い違わない)。そのまま保存するとファイルを消すので、印を立てたままにする。
        /// </summary>
        internal void RecheckMaskFile() {
            string path = StorageKey;
            if (path == null) {
                MaskLoadFailed = false;
                return;
            }
            var fileState = MaskFileStore.LoadMask(path, out bool unreadable);
            MaskLoadFailed = unreadable || (fileState != null && (fileState.width <= 0 || fileState.height <= 0))
                || (fileState != null && !MaskFileStore.IsEmpty(fileState) && !Masks.HasLayers);
        }

        /// <summary>
        /// 結び付いたレシピのマスクを編集中のマスクへ展開する(ファイルが古い・読めないと分かっているとき)。レシピが無い・読めない・
        /// マスクが無ければ何もせず false。
        /// </summary>
        internal bool RestoreMasksFromBoundRecipe() {
            var recipe = BoundRecipe;
            var state = recipe != null ? RecipeStore.Load(recipe) : null;
            var masks = state?.maskState;
            if (masks == null || masks.width <= 0 || masks.height <= 0) {
                return false;
            }
            Masks.DecodeFrom(masks);
            return true;
        }

        /// <summary>
        /// レシピから読んだ編集状態(<c>maskState</c> にマスクが入っている)のマスクを編集中のマスクへ展開し、編集状態からは外す
        /// (文書に文字列を持たない)。MaskCache(UserSettings)は読まない(レシピが正。次の保存で MaskCache も同じ内容に揃う)。
        /// テクスチャがアセットでなければ何もしない(false)。
        /// </summary>
        internal bool RestoreMasksFromRecipeState() {
            if (StorageKey == null) {
                return false;
            }
            MaskLoadFailed = false;
            var session = State;
            if (session.maskState != null) {
                Masks.DecodeFrom(session.maskState);
            }
            session.maskState = new MaskState();
            return true;
        }

        // ─────────────── セッションのファイル ───────────────

        /// <summary>読めなかった印を下ろす(編集内容をまるごと読み直す前。読む元はこれから決まる)。</summary>
        internal void ForgetSessionLoadFailure() => SessionLoadFailed = false;

        /// <summary>
        /// セッションのファイルから編集状態(ゾーンと設定)を読んで差し替える。無ければ既定(空のゾーン)。マスクは空にしておく
        /// (呼ぶ側が続けてマスクのファイルから読む。無ければ空のまま = 正しい)。レシピが結び付いていれば、呼ぶ側が先にレシピから読む。
        /// </summary>
        internal void LoadSessionFromFile() {
            var loaded = SessionPersistence.LoadFromFile(Document.sourceTexture, out bool unreadable);
            SessionLoadFailed = unreadable;
            Document.session = loaded ?? IrocaSessionState.CreateDefault();
            Document.session.maskState = new MaskState();
            EnsureAllZoneIds();
        }

        /// <summary>
        /// セッションのファイルが読めるかを確かめ直す。読めても、編集中が空なのにファイルに中身があれば、編集中は読めなかったときのまま
        /// (空のセッションは保存でファイルごと消えるので、ふつうは食い違わない)。そのまま保存するとファイルを消すので、読めなかった印を
        /// 立てたままにする。<paramref name="adoptMissed"/> ならファイルの中身を読む。レシピが結び付いていればレシピが正なので見ない。
        /// </summary>
        internal void RecheckSessionFile(bool adoptMissed) {
            var loaded = SessionPersistence.LoadFromFile(Document.sourceTexture, out bool unreadable);
            bool missed = loaded != null && !SessionFileStore.IsEmpty(loaded) && SessionFileStore.IsEmpty(State) && BoundRecipe == null;
            if (missed && adoptMissed) {
                Document.session = loaded;
                Document.session.maskState = new MaskState();
                EnsureAllZoneIds();
                SessionLoadFailed = false;
                return;
            }
            SessionLoadFailed = unreadable || missed;
        }

        // ─────────────── 保存 ───────────────

        /// <summary>
        /// 今のテクスチャの編集内容(マスク + ゾーン・色・処理設定)を、ファイルと、結び付いたレシピへ保存する。
        /// ファイルに書けなかったら false(呼ぶ側が利用者に知らせる)。
        /// <paramref name="saveMasks"/> が false ならマスクのファイルには書かない(マスクをまだ読み込んでいないとき。空のマスクを
        /// 書くとファイルを消す)。<paramref name="recipeChangedOutside"/> は、結び付いたレシピが外(git など)で書き換わっていて
        /// 書かなかったか(窓の古い内容で上書きしない。呼ぶ側がレシピから読み直す)。
        /// </summary>
        internal bool Save(bool saveMasks, out bool recipeChangedOutside) {
            var saved = ToSaveState();
            bool maskOk = !saveMasks || SaveMasksToFile(saved.maskState);
            // 中身を書けたら印を下ろす(立てたままだと、次の保存で .bak を新しい中身で上書きして元のファイルを失う)。
            bool sessionOk = SessionPersistence.SaveToFile(Document.sourceTexture, saved, SessionLoadFailed, out bool wroteContent);
            if (wroteContent) {
                SessionLoadFailed = false;
            }
            // 非破壊のレシピが結び付いていればそちらにも書く。
            recipeChangedOutside = !SaveToBoundRecipe(saved);
            return maskOk && sessionOk;
        }

        /// <summary>
        /// 結び付いたレシピへ編集内容を書く。<paramref name="saved"/> はマスク込みのもの(<see cref="ToSaveState"/>)。
        /// レシピが外(git など)で書き換わっていて書かなかったら false(呼ぶ側がレシピから読み直す)。結び付いていなければ何もしない(true)。
        /// </summary>
        internal bool SaveToBoundRecipe(IrocaSessionState saved) {
            var recipe = BoundRecipe;
            return recipe == null || SessionPersistence.SaveToRecipe(recipe, saved);
        }
    }
}
