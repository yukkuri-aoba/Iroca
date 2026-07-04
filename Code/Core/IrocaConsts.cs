// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca
{
    // Iroca で散在するマジックナンバー・メニューパスを集約する。
    // 表示文字列の正は Localization に残し、ここでは多言語化しない値だけを持つ。
    internal static class IrocaConsts
    {
        public const string MenuPath = "Tools/いろか";

        public static class Layout
        {
            public const float SideBySideMinWidth = 600f;
            public const float LeftColumnRatio    = 0.4f;
            public const float LeftColumnMin      = 280f;
            public const float LeftColumnMax      = 450f;
            public const float RemoveButtonWidth  = 22f;
            public const float SmallButtonWidth   = 48f;
            // EditorWindow.position はタブバー（ウィンドウクローム）の高さを含むが、
            // GUI 描画領域はそれより小さい。エクスポートを常にウィンドウ内に収めるための安全マージン。
            // タブバーは ~21px。この差分がエクスポート下端の空白として見えるため、
            // クリップしない範囲で詰める（過大だと下に大きな余白が出る）。
            public const float WindowChromeMargin = 26f;
            // スクロール対象の中央領域に確保する最低高さ。
            // ウィンドウが極端に低い時もこの高さは確保され、内部スクロールで残りを閲覧する。
            public const float MiddleAreaMinHeight = 120f;
        }

        public static class Preview
        {
            // メインプレビューの最大寸法（長辺）。
            // 処理結果はこのサイズへ等比縮小されてから表示される。
            public const int MaxSize = 512;

            // 段階的リファインのプロキシ解像度（長辺）。ソースが MaxSize を超える場合、
            // 操作確定後にまずこの解像度で概要を即表示し、続けてフル解像度で確定して差し替える。
            // 全フェーズが画素数に比例するため、プロキシは数十倍速い（4K=1677万画素→約26万画素）。
            // 小さいほど高速だが、後段の pixel 半径系（デコンタミ/ブラー/穴埋め）の相対 footprint が
            // 大きくなり細線が潰れやすい。MaxSize と同値なら表示解像度で直接処理（再縮小なし）。
            // ＝確定表示はフル解像度のままなので最終結果はバイト不変。視覚検証で 512↔1024 を調整する。
            public const int ProxyMaxSize = 512;

            // 比較モードで Before/After パネルの間に空ける横方向の間隔(px)。
            public const float PanelSpacing = 8f;
            // プレビュー枠の縦/横に確保するスクロールバー等の余白(px)。
            public const float ViewportMargin = 16f;
            // 動的高さ調整でプレビュー枠を縮める際の下限高(px)。これ未満まで縮めても
            // プレビューとして実用にならないため、不足分は外側 ScrollView のスクロールに任せる。
            public const float MinViewportHeight = 160f;
        }

        public static class ExperimentalFeatures
        {
            // 連続領域モード(連結成分アンカリング)を有効化。useFloodFill=true のゾーンのみ作動し、
            // 既存ゾーン(useFloodFill=false)は内側ゲートで skip され出力ビット不変。
            public const bool EnableFloodFill = true;
        }
    }
}
