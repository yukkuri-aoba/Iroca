// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca
{
    // Iroca で散在するマジックナンバーを集約する(Core と UI・ハーネスが共有するプレビューの寸法)。
    // ウィンドウの寸法とメニューの場所は UI の IrocaUiConsts(0.4.0 の計画の 3-3 で分けた)。
    internal static class IrocaConsts
    {
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
    }
}
