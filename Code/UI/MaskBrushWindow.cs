// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マスク編集ウィンドウ。**マスク編集の操作と状態はすべてここにある**:
    /// 対象（共通 / 各ゾーン）× 種類（除外 / 含める）× ツール（塗る / 消す）と、
    /// ブラシサイズ・AI 提案（プレビューの右クリックメニューで使う）の設定・取り消し・クリア。
    ///
    /// メインウィンドウのマスク欄に残るのは、ここを開く入口・読み取り専用サマリ・
    /// 右クリックのパーツ操作に使うメッシュの欄だけ。AI の一度きりの有効化（Sentis 導入と
    /// モデル取得）はメインウィンドウ上部のバナー（MaskSuggestSection.DrawSetupBanner）にある。
    /// 編集中に見ているのは「このウィンドウ + プレビュー」なので、編集中に触るものが
    /// 左カラムに残っていると視線と操作がウィンドウ間を往復することになる
    /// （2026-08-22 のユーザー指摘で集約）。
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
        private Vector2 _scroll;

        // このプロジェクトで一度置いたら、あとは Unity が覚えている位置・大きさに任せる。
        private static string PlacedKey => IrocaPrefs.MaskBrushWindowPlacedPrefix + Application.dataPath;
        private const float DefaultWidth = 320f;
        private const float DefaultHeight = 560f;

        internal static void Open(IrocaWindow host)
        {
            bool wasOpen = HasOpenInstances<MaskBrushWindow>();
            var win = GetWindow<MaskBrushWindow>(utility: false, title: Localization.MaskEditWindowTitle, focus: true);
            win._host = host;
            // 中身はスクロールできるので、下限は操作の頭が見える大きさでよい。
            win.minSize = new Vector2(280, 320);
            win.Show();
            // 初めて開いたときは Unity の既定(画面の左上・約 330×320)になり、AI 提案のエラーが出ていると
            // 下端の操作まで届かない。いろかのウィンドウの隣に、中身が収まる大きさで置く。
            // Show の前に入れると、Show が既定の位置で上書きする。2 回目からは Unity が閉じたときの位置を戻す。
            if (!wasOpen && !EditorPrefs.GetBool(PlacedKey, false))
            {
                win.position = PlaceBeside(host);
                EditorPrefs.SetBool(PlacedKey, true);
            }
        }

        /// <summary>いろかのウィンドウの右(はみ出すなら左)に並べる位置。Unity のメインウィンドウの中に収める。</summary>
        private static Rect PlaceBeside(IrocaWindow host)
        {
            Rect screen = EditorGUIUtility.GetMainWindowPosition();
            float h = Mathf.Min(DefaultHeight, screen.height);
            if (host == null) return new Rect(screen.center.x - DefaultWidth / 2, screen.center.y - h / 2, DefaultWidth, h);
            Rect hp = host.position;
            float x = hp.xMax + 4f;
            if (x + DefaultWidth > screen.xMax) x = hp.x - DefaultWidth - 4f;
            x = Mathf.Clamp(x, screen.x, Mathf.Max(screen.x, screen.xMax - DefaultWidth));
            float y = Mathf.Clamp(hp.y, screen.y, Mathf.Max(screen.y, screen.yMax - h));
            return new Rect(x, y, DefaultWidth, h);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(Localization.MaskEditWindowTitle,
                // "d_" は付けない(ダークスキンでは IconContent が自動で付ける。IrocaWindow 参照)
                EditorGUIUtility.IconContent("Grid.PaintTool").image);
            // シリアライズ参照が切れていたら(ドメインリロード・本体の作り直しなど)、既存の本体へつなぎ直す。
            if (_host == null) _host = IrocaWindow.FindAnyInstance();
        }

        // 最後に描いた時点の本体の控え(OnInspectorUpdate で、変わったときだけ描き直す)。
        [System.NonSerialized] private IrocaWindow.DrawStamp _drawn;
        // 本体を見失っている間に、次に探してよい時刻。
        [System.NonSerialized] private double _nextHostSearchAt;

        /// <summary>
        /// ホスト参照を解決する。シリアライズ参照が切れていたら（ドメインリロード・
        /// ウィンドウ再作成など）既存の IrocaWindow を探して再接続する。
        /// </summary>
        private IrocaWindow ResolveHost()
        {
            if (_host == null) _host = IrocaWindow.FindAnyInstance();
            return _host;
        }

        private void OnGUI()
        {
            // 本体は探さない(探すのは OnInspectorUpdate。同じフレームの Layout と Repaint で見え方を変えない)。
            var host = _host != null ? _host : null;
            if (Event.current.type == EventType.Repaint) _drawn.Record(host);
            if (host == null)
            {
                if (IrocaWindow.DrawMissingHostNotice()) _nextHostSearchAt = 0;
                return;
            }
            // ここでの操作(対象・ブラシ・粒度の切り替えなど)を、本体と別ウィンドウのプレビューに知らせる。
            if (IrocaWindow.MayChangeState(Event.current.type)) host.BumpUiState();
            if (host.SourceTexture == null)
            {
                EditorGUILayout.HelpBox(Localization.SetTexture, MessageType.Info);
                return;
            }
            // エクスポート中・手動の自動調整中は本体・別窓プレビューと同じく操作を止める
            // (IrocaPreviewWindow と同条件)。ここを開けておくと、ジョブの適用待ちの最中に
            // 「マスクを元に戻す」= Undo.PerformUndo だけが通り、apply と競合する。
            // 窓を小さくしても下端の操作(取り消し・クリア)に届くよう、中身はスクロールできるようにする。
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUI.DisabledScope(host.IsJobBlockingUI))
            {
                host._maskView.DrawBrushPalette();
            }
            EditorGUILayout.EndScrollView();
        }

        // ブラシ/AI の状態はメインウィンドウ側で変わる（Esc＝ClearPreviewInteractionModes による
        // ブラシ解除や、AI 提案の推論状態の変化など）ため、低頻度ポーリング（OnInspectorUpdate は
        // 約 10 回/秒）で、本体の状態が最後に描いたときから変わっていたら描き直す（IrocaWindow.DrawStamp）。
        // 操作していない間は描き直さない。
        private void OnInspectorUpdate()
        {
            if (_host == null && EditorApplication.timeSinceStartup >= _nextHostSearchAt)
            {
                _nextHostSearchAt = EditorApplication.timeSinceStartup + IrocaWindow.HostSearchIntervalSeconds;
                _host = IrocaWindow.FindAnyInstance();
            }
            if (_drawn.Changed(_host != null ? _host : null)) Repaint();
        }

        private void OnDestroy()
        {
            // このウィンドウを閉じる = ブラシでの編集を終える、と一対一にする。
            // AI 提案はモードを持たない(プレビューの右クリックメニューから都度選ぶ)ので触らない。
            var host = ResolveHost();
            if (host != null && host._maskView != null)
            {
                host._maskView.DeactivateBrush();
                host.RequestRepaint();
            }
        }
    }
}
