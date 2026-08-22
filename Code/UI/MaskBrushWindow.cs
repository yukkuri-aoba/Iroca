// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マスク編集ウィンドウ。**マスク編集の操作と状態はすべてここにある**:
    /// 対象（共通 / 各ゾーン）× 種類（除外 / 含める）× ツール（塗る / 消す / AI 提案）と、
    /// ツール別の設定・取り消し・クリア。
    ///
    /// メインウィンドウのマスク欄に残るのは、ここを開く入口・読み取り専用サマリ・
    /// AI の一度きりの有効化（Sentis 導入とモデル取得）だけ。編集中に見ているのは
    /// 「このウィンドウ + プレビュー」なので、編集中に触るものが左カラムに残っていると
    /// 視線と操作がウィンドウ間を往復することになる（2026-08-22 のユーザー指摘で集約）。
    ///
    /// 描画内容は <see cref="MaskPaintView.DrawBrushPalette"/> へ委譲する薄い殻で、
    /// 状態は従来どおり IrocaWindow 側の MaskPaintView に集約される。
    /// 塗る/クリックの作業自体はプレビュー上で行う。
    /// MenuItem は持たない（到達経路はメインの「マスクを編集...」ボタンのみ。
    /// ドメインリロード後にレイアウトへ残った場合は ResolveHost で再接続する）。
    /// </summary>
    internal sealed class MaskBrushWindow : EditorWindow
    {
        [SerializeField] private IrocaWindow _host;

        internal static void Open(IrocaWindow host)
        {
            var win = GetWindow<MaskBrushWindow>(utility: false, title: Localization.MaskEditWindowTitle, focus: true);
            win._host = host;
            // 高さは対象プルダウン・種類・ツール・ツール別設定・取り消し/クリア・ヒントが
            // 収まる値（AI 提案の状態表示が出るぶんを含む）。
            win.minSize = new Vector2(280, 320);
            win.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(Localization.MaskEditWindowTitle,
                // "d_" は付けない(ダークスキンでは IconContent が自動で付ける。IrocaWindow 参照)
                EditorGUIUtility.IconContent("Grid.PaintTool").image);
        }

        /// <summary>
        /// ホスト参照を解決する。シリアライズ参照が切れていたら（ドメインリロード・
        /// ウィンドウ再作成など）既存の IrocaWindow を探して再接続する。
        /// </summary>
        private IrocaWindow ResolveHost()
        {
            if (_host == null)
            {
                var hosts = Resources.FindObjectsOfTypeAll<IrocaWindow>();
                if (hosts.Length > 0) _host = hosts[0];
            }
            return _host;
        }

        private void OnGUI()
        {
            var host = ResolveHost();
            if (host == null)
            {
                EditorGUILayout.HelpBox(Localization.BrushPaletteNoHost, MessageType.Info);
                if (GUILayout.Button(new GUIContent(Localization.OpenIrocaWindow, Localization.OpenIrocaWindowTooltip)))
                    IrocaWindow.ShowWindow();
                return;
            }
            if (host.SourceTexture == null)
            {
                EditorGUILayout.HelpBox(Localization.SetTexture, MessageType.Info);
                return;
            }
            // エクスポート中・手動の自動調整中は本体・別窓プレビューと同じく操作を止める
            // (IrocaPreviewWindow と同条件)。ここを開けておくと、ジョブの適用待ちの最中に
            // 「マスクを元に戻す」= Undo.PerformUndo だけが通り、apply と競合する。
            using (new EditorGUI.DisabledScope(host.IsJobBlockingUI))
            {
                host._maskView.DrawBrushPalette();
            }
        }

        // ブラシ/AI の状態はメインウィンドウ側で変わる（AI 開始でブラシ解除など）ため、
        // 低頻度ポーリングで表示を追従させる（OnInspectorUpdate は約 10fps）。
        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private void OnDestroy()
        {
            // このウィンドウを閉じる = マスク編集を終える、と一対一にする。
            // ブラシだけでなく AI 提案も解除するのは、状態表示と終了導線がこのウィンドウにしか
            // 無くなったため（以前はメインウィンドウのマスク欄に AI の終了導線があったので
            // ブラシだけ解除していた）。見えない操作モードが残ると、プレビューの右クリックが
            // 黙ってマスクへ反映され続けることになる。
            var host = ResolveHost();
            if (host != null && host._maskView != null)
            {
                host._maskView.DeactivateBrush();
                host._maskView.SuggestControllerIfCreated?.SetActive(false);
                host.RequestRepaint();
            }
        }
    }
}
