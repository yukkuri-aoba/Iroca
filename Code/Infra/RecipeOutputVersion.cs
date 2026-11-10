// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca {
    /// <summary>
    /// 仕上がり(色替えの出来上がり)の版。同じレシピでも、いろかの計算が変わると仕上がりが変わる(更新すると、登録済みの
    /// アバターの色が次の再生・アップロードから黙って変わる)。レシピに「どの版の仕上がりで確かめたか」
    /// (<see cref="IrocaRecipe.outputVersion"/>)を持たせ、今の版より古ければインスペクタ・いろかの窓・アップロードで知らせる。
    /// <para>
    /// 上げ方: 出力が変わるリリースごとに <see cref="Current"/> を 1 つ上げ、<see cref="FirstRelease"/> にその版を足す
    /// (`docs/RELEASING.md`)。キャッシュの鍵には入れない(コードが変われば鍵も変わる。「確かめた」の印を付けただけで
    /// 作り直さないため)。
    /// </para>
    /// <para>
    /// 印を今の版にする時: レシピを作ったとき・中身を変えて保存したとき・登録したとき・「確かめた」を押したとき・
    /// 確かめてアップロードしたとき(<see cref="RecipeStore.MarkOutputChecked"/>)。開いただけでは変えない。
    /// </para>
    /// </summary>
    internal static class RecipeOutputVersion {
        /// <summary>今の版。0 = 0.3.x 以前(印の無いレシピ)、1 = 0.4.0。</summary>
        internal const int Current = 1;

        // 版ごとの、その版になった最初のリリース(添え字 = 版。0 は印の無い古いレシピなので無い)。
        private static readonly string[] FirstRelease = { null, "0.4.0" };

        /// <summary>今の版より前の仕上がりで確かめたレシピか(今の版では仕上がりが変わることがある)。</summary>
        internal static bool IsOutdated(IrocaRecipe recipe) => recipe != null && recipe.outputVersion < Current;

        /// <summary>
        /// 版 <paramref name="version"/> の仕上がりだった、いろかのリリースの範囲(表示用。「0.4.0 より前」など)。
        /// </summary>
        internal static string ReleasesOf(int version) {
            if (version <= 0) {
                return string.Format(Localization.OutputVersionBeforeFormat, FirstRelease[1]);
            }
            if (version >= Current || version + 1 >= FirstRelease.Length) {
                return FirstRelease[System.Math.Min(version, FirstRelease.Length - 1)];
            }
            return string.Format(Localization.OutputVersionRangeFormat, FirstRelease[version], FirstRelease[version + 1]);
        }
    }
}
