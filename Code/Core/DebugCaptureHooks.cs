using System;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// パイプライン透明化機能の静的接続点。実装が同梱されていなくても
    /// 本体は型としてこのクラスだけを参照する。
    ///
    /// Code.Debug/ asmdef が存在し、かつ <c>[InitializeOnLoadMethod]</c> で
    /// <see cref="Factory"/> と <see cref="OnDrawFoldout"/> を登録したときだけ
    /// デバッグ機能が有効化される。フォルダごと削除すれば本クラスは
    /// 単なる「null 入れ物」として静かに無効化される。
    /// </summary>
    internal static class DebugCaptureHooks
    {
        /// <summary>
        /// プレビュー / エクスポートのジョブが <see cref="PixelProcessor"/> に渡す
        /// <see cref="IDebugCapture"/> を取得するためのファクトリ。
        /// null（= 機能未導入 or トグル OFF）の場合、本体は何もキャプチャしない。
        /// </summary>
        internal static Func<IDebugCapture> Factory;

        /// <summary>
        /// VACCWindow の OnGUI から発火される foldout 描画イベント。
        /// subscriber がいないときは何も描画されない（本体 UI に影響なし）。
        /// </summary>
        internal static event Action<VACCWindow> OnDrawFoldout;

        /// <summary>
        /// VACCWindow から foldout イベントを発火する薄いラッパ。
        /// </summary>
        internal static void RaiseDrawFoldout(VACCWindow window)
        {
            OnDrawFoldout?.Invoke(window);
        }
    }
}
