// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マスクブラシの操作パレット（サイズ・除外/含める・元に戻す）。
    /// 描画内容は <see cref="MaskPaintView.DrawBrushPalette"/> へ委譲する薄い殻で、
    /// 状態は従来どおり IrocaWindow 側の MaskPaintView に集約される。
    /// 塗る作業自体はメインウィンドウのプレビュー上で行う。
    /// MenuItem は持たない（到達経路はメインの「ブラシで編集」ボタンのみ。
    /// ドメインリロード後にレイアウトへ残った場合は ResolveHost で再接続する）。
    /// </summary>
    internal sealed class MaskBrushWindow : EditorWindow
    {
        [SerializeField] private IrocaWindow _host;

        internal static void Open(IrocaWindow host)
        {
            var win = GetWindow<MaskBrushWindow>(utility: false, title: Localization.BrushPaletteTitle, focus: true);
            win._host = host;
            win.minSize = new Vector2(260, 200);
            win.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(Localization.BrushPaletteTitle,
                EditorGUIUtility.IconContent("d_Grid.PaintTool").image);
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
            host._maskView.DrawBrushPalette();
        }

        // ブラシ/AI の状態はメインウィンドウ側で変わる（AI 開始でブラシ解除など）ため、
        // 低頻度ポーリングで表示を追従させる（OnInspectorUpdate は約 10fps）。
        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private void OnDestroy()
        {
            // パレットを閉じたらペイントモードも終了する。
            // 「ブラシ操作 UI が見えないのに塗れる」状態を残さない。
            var host = ResolveHost();
            if (host != null && host._maskView != null)
            {
                host._maskView.DeactivateBrush();
                host.RequestRepaint();
            }
        }
    }
}
