// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// プレビュー専用のサブウィンドウ。描画は IrocaWindow が持つ <see cref="PreviewView"/> へ
    /// 委譲する薄い殻で、状態（ズーム・比較/差分・生成ジョブ・詳細プレビュー）は従来どおり
    /// メインウィンドウ側に集約される（<see cref="MaskBrushWindow"/> と同じ方針）。
    ///
    /// 用途: ウィンドウ幅が狭く縦積みレイアウトになるとき、本体にプレビューを描くと設定列の
    /// 下へ押し出されて実用にならない。そのときは本体にプレビューを描かず、この窓へ切り出す。
    /// 同一 PreviewView を 2 つのウィンドウが同フレームでレイアウトすると、実測値
    /// （ビューポート幅・プレビュー枠より上の chrome 高）を互いに奪い合って枠サイズが
    /// 発振するため、この窓が開いている間は本体側は幅に関わらずプレビューを描かない。
    ///
    /// MenuItem は持たない（到達経路は本体の「プレビューを別ウィンドウで開く」ボタンのみ。
    /// ドメインリロード後にレイアウトへ残った場合は ResolveHost で再接続する）。
    /// </summary>
    internal sealed class IrocaPreviewWindow : EditorWindow
    {
        [SerializeField] private IrocaWindow _host;

        // 縦オーバーフロー用の外側スクロール（本体の右カラムと同じ構成）。
        [System.NonSerialized] private Vector2 _scrollPos;

        // 開閉判定のキャッシュ。IsOpen は本体の OnGUI から毎イベント呼ばれるため、
        // 探索(FindObjectsOfTypeAll=ロード済み全オブジェクト走査)を毎フレーム走らせない。
        // 窓が生まれるとき(新規作成・レイアウト復元・ドメインリロード)は必ず OnEnable が
        // 通るのでここが張り直され、閉じたら OnDestroy で外れる。static はドメインリロードで
        // 消えるため、その直後の 1 回だけ保険の探索を行う(OnEnable より先に本体が
        // 描かれた場合に「開いていない」と誤判定しないように)。
        // Unity の Object null 判定は破棄済みインスタンスを null 扱いするので、
        // 破棄済み参照が残っていても IsOpen は false になる。
        [System.NonSerialized] private static IrocaPreviewWindow _instance;
        [System.NonSerialized] private static bool _instanceSearched;

        internal static bool IsOpen => FindInstance() != null;

        private static IrocaPreviewWindow FindInstance()
        {
            if (_instance != null) return _instance;
            if (!_instanceSearched)
            {
                _instanceSearched = true;
                var wins = Resources.FindObjectsOfTypeAll<IrocaPreviewWindow>();
                _instance = wins.Length > 0 ? wins[0] : null;
            }
            return _instance;
        }

        internal static void Open(IrocaWindow host)
        {
            var win = GetWindow<IrocaPreviewWindow>(
                utility: false, title: Localization.PreviewWindowTitle, focus: true);
            win._host = host;
            // 等倍(100%)の MaxSize プレビューが極端に潰れない下限。これ未満でも
            // PreviewView 側が枠を MinViewportHeight まで縮めてスクロールへ逃がす。
            win.minSize = new Vector2(320, 320);
            win.Show();
        }

        internal static void FocusIfOpen()
        {
            var win = FindInstance();
            if (win != null) win.Focus();
        }

        internal static void CloseIfOpen()
        {
            var win = FindInstance();
            if (win != null) win.Close();
        }

        /// <summary>
        /// 本体側（PreviewView からの追い再描画要求を含む）から呼ぶ再描画フック。
        /// プレビューの実測値収束・ジョブ完了の反映は、この窓が描画されないと進まない。
        /// </summary>
        internal static void RepaintIfOpen()
        {
            var win = FindInstance();
            if (win != null) win.Repaint();
        }

        private void OnEnable()
        {
            _instance = this;
            _instanceSearched = true;
            titleContent = new GUIContent(Localization.PreviewWindowTitle,
                EditorGUIUtility.IconContent("d_Image Icon").image);
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

            var preview = host.Preview;
            if (preview == null) return;

            // エクスポート中・手動の自動調整中は本体と同じく操作を止める。ここを開けておくと
            // 本体が固まっている間にプレビュー上のペイント/スポイトだけ通ってしまう。
            using (new EditorGUI.DisabledScope(host.IsJobBlockingUI))
            {
                // 横バーは無効化(GUIStyle.none)。この外側 ScrollView は縦オーバーフロー専用で、
                // 横スクロールは内側プレビューに任せる（本体の右カラムと同じ理由）。
                _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos,
                    false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView,
                    GUILayout.ExpandHeight(true));

                // プレビュー枠がウィンドウ高に収まるよう動的に縮むためのカラム高を渡す
                // （本体の横並びレイアウトが horizH を渡すのと同じ役割）。
                preview.availableColumnHeight = position.height - IrocaConsts.Layout.WindowChromeMargin;
                preview.Draw();

                EditorGUILayout.EndScrollView();
            }
        }

        // プレビュー状態は本体ウィンドウ側の操作（色・設定・マスク変更）で変わるため、
        // 低頻度ポーリングで表示を追従させる（OnInspectorUpdate は約 10fps）。
        // 生成ジョブの進行やパン/ズームの追い再描画は RequestRepaint 経由で即時に届く。
        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            // 本体側は「別ウィンドウで表示中」の案内を出しているので、
            // 閉じたら通常表示（本体内プレビュー）へ戻す。
            var host = ResolveHost();
            if (host != null) host.Repaint();
        }
    }
}
