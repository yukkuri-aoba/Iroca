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
            // 設定列の下限幅。設定行(日本語ラベル＋スライダー＋数値フィールド)が右端で
            // 切れずに収まる幅。SettingsLabelWidth は内容幅の 45% をラベルに割くので、
            // 最長ラベル(「連続領域モード (Flood Fill)」「境界クリーンアップ（α分解）」)の
            // 実測必要幅 ~138px を満たすにはカラム幅 320px が要る。280 時代は labelWidth が
            // 120px まで縮み、実ウィンドウのキャプチャでこれらのラベルが右端で欠けていた。
            public const float LeftColumnMin      = 320f;
            public const float LeftColumnMax      = 450f;
            // 横並び時に右(プレビュー)カラムへ優先確保する幅。等倍(100%)の MaxSize px
            // プレビューが横スクロールバー無しで収まる幅＋外側縦バー(~13px)と丸めの余裕。
            // 左カラムは LeftColumnMin までの範囲でこの幅に譲る(テクスチャ非依存の固定値に
            // して、切替時に設定列の幅が動かないようにする)。
            // MaxSize=384 では 408 となり、既定ウィンドウ幅(728)＝左カラム下限 320 + 408 で
            // ちょうど収まる。512 時代は予約 536 が左カラムを潰し、設定列の右端(数値
            // フィールド・ボタン)がプレビューの下に隠れて見えていた。
            public const float PreviewColumnReserve = Preview.MaxSize + 24f;
            public const float RemoveButtonWidth  = 22f;
            public const float SmallButtonWidth   = 48f;
            // サンプルカラー欄と同一行に置くスポイトボタンの幅。ラベル短縮版
            // (EyedropperIdle/Active)が JA/EN とも収まり、左カラム下限(280px)でも
            // カラーフィールドの操作幅を残せる値。
            public const float EyedropperButtonWidth = 96f;
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
            // 512→448 (2026-07-23): 既定ウィンドウ幅(800)では「左カラム内容の最小幅
            // (~300px)」と「512px 等倍プレビュー＋余裕(536px)」が合計 849px となり両立
            // 不可能で、左カラムが下限まで潰されて設定 UI の右端がプレビューの下に隠れて
            // いた。448 なら 472+320=792 ≤ 800 で既定サイズでもスクロールバー・隠れ無しに
            // 収まる。表示が一回り小さくなる代償は、ズーム(Ctrl+スクロール)と詳細
            // プレビュー(ソース画素直接描画)で補える。
            // 448→384 (2026-07-25): 上記のとおり既定幅 800 は「設定列 320＋等倍 448＋余白」
            // でほぼ必要最小に張り付いており、ウィンドウが実作業に対して大きすぎた。
            // 384 なら予約 408＋設定列 320＝728 で開けて、等倍表示・横バー無しは維持できる。
            // 全体像の把握には十分な大きさで、細部の確認は同じくズームと詳細プレビューが担う。
            public const int MaxSize = 384;

            // 段階的リファインのプロキシ解像度（長辺）。ソースが MaxSize を超える場合、
            // 操作確定後にまずこの解像度で概要を即表示し、続けてフル解像度で確定して差し替える。
            // 全フェーズが画素数に比例するため、プロキシは数十倍速い（4K=1677万画素→約26万画素）。
            // 小さいほど高速だが、後段の pixel 半径系（デコンタミ/ブラー/穴埋め）の相対 footprint が
            // 大きくなり細線が潰れやすい。MaxSize より大きい場合は処理後に表示解像度へ再縮小される
            // (ScheduleProxyPreview が対応済み。処理品質を保つため MaxSize 縮小後も 512 を維持)。
            // ＝確定表示はフル解像度のままなので最終結果はバイト不変。視覚検証で 512↔1024 を調整する。
            public const int ProxyMaxSize = 512;

            // 比較モードで Before/After パネルの間に空ける横方向の間隔(px)。
            public const float PanelSpacing = 8f;
            // プレビュー枠の縦/横に確保するスクロールバー等の余白(px)。
            public const float ViewportMargin = 16f;
            // 動的高さ調整でプレビュー枠を縮める際の下限高(px)。これ未満まで縮めても
            // プレビューとして実用にならないため、不足分は外側 ScrollView のスクロールに任せる。
            public const float MinViewportHeight = 160f;
            // プレビュー枠をカラム幅へ合わせる際の下限幅(px)。カラムがこれより狭くても
            // 枠はここで止める(横並びの最小ウィンドウ幅でも右カラムは 280px 前後あるので、
            // 通常はこの下限に当たらない。異常に狭い実測値が来たときの保険)。
            public const float MinViewportWidth = 120f;
        }

        public static class ExperimentalFeatures
        {
            // 連続領域モード(連結成分アンカリング)を有効化。useFloodFill=true のゾーンのみ作動し、
            // 既存ゾーン(useFloodFill=false)は内側ゲートで skip され出力ビット不変。
            public const bool EnableFloodFill = true;
        }
    }
}
