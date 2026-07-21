// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>AI マスク提案サービスの状態。</summary>
    internal enum MaskSuggestPhase
    {
        /// <summary>モデルファイル未配置(UI は導入案内を出す)。</summary>
        NoModel,
        /// <summary>モデルの変換・ロード中。</summary>
        LoadingModel,
        /// <summary>待機(クリック受付可)。</summary>
        Idle,
        /// <summary>画像埋め込みを計算中(テクスチャ毎 1 回)。</summary>
        Encoding,
        /// <summary>クリックに対する提案を推論中。</summary>
        Decoding,
        /// <summary>提案が取得可能(TryTakeProposal で受け取る)。</summary>
        ProposalReady,
        /// <summary>エラー(ErrorMessage 参照)。</summary>
        Error,
    }

    /// <summary>1 クリック分の提案マスク。</summary>
    internal sealed class MaskSuggestProposal
    {
        /// <summary>下原点(GetPixels32 順) width*height の提案領域。</summary>
        public bool[] maskBottomUp;
        public int width;
        public int height;
        /// <summary>モデルの予測 IoU スコア(参考表示用)。</summary>
        public float score;
        /// <summary>キャンバス面積比。</summary>
        public float areaFrac;
        /// <summary>背景に流れた可能性(面積が棄却しきい以上のチャンネルしか無かった)。</summary>
        public bool floodWarning;
    }

    /// <summary>
    /// AI マスク提案サービス。実装は Sentis 統合アセンブリ(Iroca.SentisIntegration)が提供し、
    /// [InitializeOnLoad] で <see cref="MaskSuggestBridge.Service"/> に登録する。
    /// Sentis パッケージ不在時は Service == null のままで、UI は何も表示しない(バイト不変)。
    ///
    /// スレッド規約: 全メソッドはメインスレッドから呼ぶ。重い処理は実装側が内部で
    /// 分割実行し、状態変化を StateChanged で通知する(購読側は Repaint に使う)。
    /// </summary>
    internal interface IMaskSuggestService
    {
        MaskSuggestPhase Phase { get; }
        /// <summary>Encoding/LoadingModel 中の進捗 0..1(不明時は 0)。</summary>
        float Progress { get; }
        /// <summary>Phase == Error のときの表示用メッセージ。</summary>
        string ErrorMessage { get; }

        /// <summary>モデルファイルの存在確認と非同期ロード開始。false = 未配置(NoModel)。</summary>
        bool TryEnsureModels();

        /// <summary>
        /// 提案対象のソース画像を設定し、必要なら埋め込み計算を開始する。
        /// cacheKey が前回と同一なら no-op(埋め込みキャッシュ)。
        /// pixels は下原点(GetPixels32 順)。呼び出し後に配列を書き換えないこと。
        /// </summary>
        void SetSource(string cacheKey, Color32[] pixelsBottomUp, int width, int height);

        /// <summary>プレビュー UV(下原点)のクリックに対する提案を要求する。</summary>
        void RequestProposal(float u, float v, MaskSuggestGranularity granularity);

        /// <summary>ProposalReady の提案を取り出す(取り出すと Idle に戻る)。</summary>
        bool TryTakeProposal(out MaskSuggestProposal proposal);

        /// <summary>進行中の処理を破棄する(テクスチャ切替・ウィンドウ破棄時)。</summary>
        void CancelAll();

        /// <summary>Phase/Progress が変化したとき(メインスレッド)。UI の Repaint 用。</summary>
        event System.Action StateChanged;
    }

    /// <summary>
    /// 本体アセンブリと Sentis 統合アセンブリの結合点。
    /// 依存方向は SentisIntegration → 本体のみ(逆参照なし)。
    /// </summary>
    internal static class MaskSuggestBridge
    {
        /// <summary>登録済みサービス(null = Sentis 不在 = 機能 OFF)。</summary>
        public static IMaskSuggestService Service;

        public static bool Available => Service != null;

        /// <summary>
        /// モデル配置ディレクトリ。モデル(ONNX と .sentis 変換キャッシュ)はプロジェクトに
        /// 依存しない同一バイナリなので、プロジェクトごとに複製せず「ユーザー単位の共有フォルダ」に
        /// 1 か所だけ置く。こうするとプロジェクトを増やしても AI モデルがストレージを圧迫せず、
        /// 一度ダウンロードすれば別プロジェクトでも再ダウンロード不要になる。
        /// 保存先: Windows は %LOCALAPPDATA%\Iroca\Models、mac/Linux は ~/.local/share 等。
        /// (取得できない特殊環境ではプロジェクト内 UserSettings/Iroca/Models へフォールバック)
        /// </summary>
        public static string ModelsDirectory
        {
            get
            {
                string root = System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(root))
                    return System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(root, "Iroca", "Models"));
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    Application.dataPath, "..", "UserSettings", "Iroca", "Models"));
            }
        }
    }
}
