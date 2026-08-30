// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// いろかを開いた時点で AI(Sentis + 配布モデル)の準備を求めるダイアログ。
    ///
    /// 自動調整は「スポイト位置の AI 提案セグメント」を証拠にして導出する(ZoneAutoTuner.
    /// AnalyzeWithEvidence)。以前は AI が無い/温まっていないとき黙って従来導出へ落としていたが、
    /// その従来導出がハイライトを取りこぼす当の経路で、ユーザーからは「自動調整が壊れる」と
    /// しか見えなかった(2026-08-29)。落とすのをやめ、AI が無ければここで導入・ダウンロードを
    /// 求め、自動調整側は AI が使えなければ中止して案内する(IrocaWindow.AutoTune.cs)。
    ///
    /// 「あとで」はセッション中は再表示しない(SessionState。エディタ再起動で消える)。
    /// 自動調整を押した時点で無ければ、そのときに改めて出す(force)。
    /// </summary>
    internal static class MaskSuggestSetupPrompt
    {
        const string PromptedSentisKey = "Iroca.AiSetup.PromptedSentis";
        const string PromptedModelKey = "Iroca.AiSetup.PromptedModel";

        /// <summary>AI が今すぐ使える(Sentis あり・モデルファイルあり)か。</summary>
        public static bool Ready => MaskSuggestBridge.Available && MaskSuggestBridge.ModelFilesPresent;

        /// <summary>EditorApplication.delayCall 用(ウィンドウ OnEnable 中のモーダルを避ける)。</summary>
        public static void PromptIfNeeded() => PromptIfNeeded(force: false);

        /// <summary>
        /// 必要なら導入/ダウンロードのダイアログを出す。force=false ならセッション中 1 回だけ。
        /// 戻り値 = 何らかの準備(導入・ダウンロード)を開始したか。
        /// </summary>
        public static bool PromptIfNeeded(bool force)
        {
            if (Application.isBatchMode) return false;

            if (!MaskSuggestBridge.Available)
            {
                if (MaskSuggestInstall.InProgress) return false;
                if (!force && SessionState.GetBool(PromptedSentisKey, false)) return false;
                SessionState.SetBool(PromptedSentisKey, true);
                string replaced = MaskSuggestInstall.VersionThatWouldBeReplaced();
                string body = string.Format(Localization.AiSetupSentisBody, MaskSuggestInstall.SentisPackageVersion);
                if (replaced != null)
                    body += "\n\n" + string.Format(Localization.AiSuggestSentisVersionReplace,
                        replaced, MaskSuggestInstall.SentisPackageVersion);
                bool go = EditorUtility.DisplayDialog(Localization.AiSetupTitle, body,
                    Localization.AiSetupInstall, Localization.AiSetupLater);
                if (go) MaskSuggestInstall.StartInstall();
                return go;
            }

            MaskSuggestBridge.MigrateLegacyModelsIfNeeded();
            if (MaskSuggestBridge.ModelFilesPresent) return false;
            if (MaskSuggestModelDownload.InProgress) return false;
            if (!force && SessionState.GetBool(PromptedModelKey, false)) return false;
            SessionState.SetBool(PromptedModelKey, true);
            bool dl = EditorUtility.DisplayDialog(Localization.AiSetupTitle,
                string.Format(Localization.AiSetupModelBody,
                    MaskSuggestBridge.ModelsDirectory, MaskSuggestModelDownload.BaseUrl),
                Localization.AiSetupDownload, Localization.AiSetupLater);
            if (dl) MaskSuggestModelDownload.Start();
            return dl;
        }
    }
}
