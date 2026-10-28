// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca
{
    // ウィンドウの寸法とメニューの場所(UI だけが使う値)。表示文字列の正は Localization に残し、ここでは
    // 多言語化しない値だけを持つ。Core と共有するプレビューの寸法は Core の IrocaConsts.Preview
    // (0.4.0 の計画の 3-3 で IrocaConsts から分けた)。
    internal static class IrocaUiConsts
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
            public const float PreviewColumnReserve = IrocaConsts.Preview.MaxSize + 24f;
            public const float RemoveButtonWidth  = 22f;
            public const float SmallButtonWidth   = 48f;
            // サンプルカラー欄と同一行に置くスポイトボタンの幅。ラベル短縮版
            // (EyedropperIdle/Active)が JA/EN とも収まり、左カラム下限(LeftColumnMin)でも
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
    }
}
