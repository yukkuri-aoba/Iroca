// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 除外マスク UI 内に描く「AI マスク提案」セクション。
    /// Sentis 統合(MaskSuggestBridge.Service)が不在のときは、AI 機能の存在を知らせて
    /// ワンクリックで有効化する導線(Sentis 導入ボタン)だけを描く。これが無いと
    /// Sentis を手動導入した開発環境でしか AI 機能に到達できない。
    /// </summary>
    internal static class MaskSuggestSection
    {
        public static void Draw(IrocaWindow host, MaskPaintView maskView)
        {
            // 導入直後の「Unity 再起動」案内は、サービスが載ったか(Burst 失敗で載らない場合も含む)に
            // かかわらず出したいので、サービス分岐より前に描く。
            DrawPostInstallRestartNoticeIfNeeded();

            var svc = MaskSuggestBridge.Service;
            if (svc == null)
            {
                DrawSentisSetup();
                return;
            }
            var ctl = maskView.SuggestController;
            if (ctl == null) return;

            // Burst が失敗した世代では推論が空を返すだけで、エラーも出ずに「動かない」ように
            // 見える。クリックを試す前に気づけるよう、モードに入る前から知らせる。
            DrawBurstFailureNoticeIfNeeded();

            // 見出しラベルは置かない（ボタン文言が自明で、詳細は AiSuggestToggleTooltip にある。
            // マスク欄インラインの行数を抑えるため）。
            EditorGUILayout.Space(2);

            // ─── モード切替(ペイントと排他) ───
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = ctl.Active ? IrocaColors.IncludeButton : Color.white;
            string toggleLabel = ctl.Active ? Localization.AiSuggestActive : Localization.AiSuggestStart;
            if (GUILayout.Button(new GUIContent(toggleLabel, Localization.AiSuggestToggleTooltip)))
            {
                bool next = !ctl.Active;
                if (next) maskView.maskPaintActive = false; // ブラシとは排他
                ctl.SetActive(next);
            }
            GUI.backgroundColor = prevBg;

            if (!ctl.Active) return;

            // 提案の粒度(SAM はクリック 1 点に粒度違いの候補を同時出力する)
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                new GUIContent(Localization.AiSuggestGranularity, Localization.AiSuggestGranularityTooltip),
                GUILayout.Width(70));
            var granOptions = new[]
            {
                new GUIContent(Localization.AiSuggestGranularityAuto, Localization.AiSuggestGranularityAutoTooltip),
                new GUIContent(Localization.AiSuggestGranularityFine, Localization.AiSuggestGranularityFineTooltip),
                new GUIContent(Localization.AiSuggestGranularityCoarse, Localization.AiSuggestGranularityCoarseTooltip),
            };
            int gran = GUILayout.Toolbar((int)ctl.Granularity, granOptions);
            if (gran != (int)ctl.Granularity)
                ctl.Granularity = (MaskSuggestGranularity)gran;
            EditorGUILayout.EndHorizontal();

            // ─── サービス状態 ───
            switch (svc.Phase)
            {
                case MaskSuggestPhase.NoModel:
                    EditorGUILayout.HelpBox(Localization.AiSuggestNoModel, MessageType.Info);
                    if (MaskSuggestModelDownload.InProgress)
                    {
                        EditorGUILayout.BeginHorizontal();
                        var pr = EditorGUILayout.GetControlRect(false, 18f);
                        EditorGUI.ProgressBar(pr, MaskSuggestModelDownload.Progress,
                                              Localization.AiSuggestDownloading);
                        if (GUILayout.Button(new GUIContent(Localization.AiSuggestDownloadCancel,
                                                            Localization.AiSuggestDownloadCancelTooltip),
                                             GUILayout.Width(48f)))
                            MaskSuggestModelDownload.Cancel();
                        EditorGUILayout.EndHorizontal();
                    }
                    else
                    {
                        if (MaskSuggestModelDownload.Error != null)
                            EditorGUILayout.HelpBox(
                                string.Format(Localization.AiSuggestDownloadFailed,
                                              MaskSuggestModelDownload.Error),
                                MessageType.Warning);
                        EditorGUILayout.BeginHorizontal();
                        if (GUILayout.Button(new GUIContent(Localization.AiSuggestDownload,
                                string.Format(Localization.AiSuggestDownloadTooltip,
                                              MaskSuggestModelDownload.BaseUrl))))
                            MaskSuggestModelDownload.Start();
                        if (GUILayout.Button(new GUIContent(Localization.AiSuggestOpenModelFolder,
                                                            Localization.AiSuggestOpenModelFolderTooltip)))
                        {
                            System.IO.Directory.CreateDirectory(MaskSuggestBridge.ModelsDirectory);
                            EditorUtility.RevealInFinder(MaskSuggestBridge.ModelsDirectory);
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    // 手動配置後の再チェックはモード再有効化ではなくここで拾う
                    if (Event.current.type == EventType.Layout) svc.TryEnsureModels();
                    break;

                case MaskSuggestPhase.LoadingModel:
                    EditorGUILayout.HelpBox(Localization.AiSuggestLoadingModel, MessageType.Info);
                    break;

                case MaskSuggestPhase.Encoding:
                {
                    var r = EditorGUILayout.GetControlRect(false, 18f);
                    EditorGUI.ProgressBar(r, Mathf.Clamp01(svc.Progress), Localization.AiSuggestEncoding);
                    break;
                }

                case MaskSuggestPhase.Decoding:
                    EditorGUILayout.LabelField(Localization.AiSuggestDecoding, EditorStyles.miniLabel);
                    break;

                case MaskSuggestPhase.Error:
                    EditorGUILayout.HelpBox(svc.ErrorMessage ?? "error", MessageType.Error);
                    // 推論の失敗は Burst のコールドスタート失敗が原因のことが多く、その世代では
                    // 何度クリックしても直らない。再起動導線をエラーと同じ場所に出す。
                    EditorGUILayout.HelpBox(Localization.AiSuggestErrorRestartHint, MessageType.Info);
                    DrawRestartButton();
                    break;

                default:
                    EditorGUILayout.HelpBox(Localization.AiSuggestHintIdle, MessageType.Info);
                    break;
            }

            DrawTargetAndWarnings(host, maskView, ctl);
        }

        /// <summary>
        /// Sentis 導入直後に「Unity 再起動」を促す。導入時のドメインリロードで Burst の
        /// コールドスタート失敗を踏むと、その世代だけ AI が動かない。再起動でクリーンな
        /// Burst 初期化になるため、確実に有効化したいユーザー向けの導線。
        /// SessionState はエディタ再起動で消えるので、再起動すれば自動的に消える。
        /// </summary>
        static void DrawPostInstallRestartNoticeIfNeeded()
        {
            if (!SessionState.GetBool(MaskSuggestInstall.RestartRecommendedKey, false)) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(Localization.AiSuggestRestartRecommended, MessageType.Info);
            EditorGUILayout.BeginHorizontal();
            DrawRestartButton();
            if (GUILayout.Button(new GUIContent(Localization.AiSuggestRestartLater,
                                                Localization.AiSuggestRestartLaterTooltip),
                                 GUILayout.Width(80)))
                SessionState.SetBool(MaskSuggestInstall.RestartRecommendedKey, false);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// このセッションで Burst のコンパイル失敗を観測していたら、AI が使えない状態だと知らせる。
        /// Unity を再起動するまで直らないため、クリックを試す前に出す。
        /// </summary>
        static void DrawBurstFailureNoticeIfNeeded()
        {
            if (!MaskSuggestBurstWatch.FailedThisSession) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(Localization.AiSuggestBurstFailed, MessageType.Warning);
            DrawRestartButton();
        }

        /// <summary>Unity を再起動する(現在のプロジェクトを開き直す)ボタン。</summary>
        static void DrawRestartButton()
        {
            if (!GUILayout.Button(new GUIContent(Localization.AiSuggestRestartNow,
                                                 Localization.AiSuggestRestartNowTooltip)))
                return;
            SessionState.SetBool(MaskSuggestInstall.RestartRecommendedKey, false);
            // 現在のプロジェクトを開き直す = エディタ再起動(Burst をクリーンに初期化)。
            EditorApplication.OpenProject(System.IO.Directory.GetCurrentDirectory());
        }

        /// <summary>
        /// Sentis 未導入時の導線。AI 機能の存在を知らせ、Package Manager 経由の
        /// ワンクリック導入ボタンを出す(導入後 Unity が再コンパイルして機能が有効になる)。
        /// </summary>
        static void DrawSentisSetup()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localization.AiSuggest, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(Localization.AiSuggestSentisRequired, MessageType.Info);

            if (MaskSuggestInstall.InProgress)
            {
                EditorGUILayout.HelpBox(Localization.AiSuggestInstalling, MessageType.Info);
                return;
            }

            if (MaskSuggestInstall.Error != null)
                EditorGUILayout.HelpBox(
                    string.Format(Localization.AiSuggestInstallFailed, MaskSuggestInstall.Error),
                    MessageType.Warning);

            if (GUILayout.Button(new GUIContent(
                    Localization.AiSuggestInstallSentis,
                    string.Format(Localization.AiSuggestInstallSentisTooltip,
                                  MaskSuggestInstall.SentisPackageId,
                                  MaskSuggestInstall.SentisPackageVersion))))
                MaskSuggestInstall.StartInstall();
        }

        /// <summary>
        /// クリックがどのマスクへ足されるかの明示と、効果が出ない/外した場合の注意書き。
        /// クリック 1 回で即マスクへ反映されるため(積み上げ・確定ボタンは無い)、取り違えや
        /// 「反映されない」に先に気づけるよう常時表示する。
        /// </summary>
        static void DrawTargetAndWarnings(IrocaWindow host, MaskPaintView maskView, MaskSuggestController ctl)
        {
            // 追加先マスク(共通 or ゾーン)を明示する。編集対象が想定と違うゾーンだと
            // 「足しても対象ゾーンに効かず変化なし」に見えるため、取り違えを防ぐ。
            var zones = host.Session?.zones;
            int t = maskView.activeMaskTarget;
            string target = (t >= 0 && zones != null && t < zones.Count)
                ? (string.IsNullOrEmpty(zones[t].name) ? Localization.UnnamedZone : zones[t].name)
                : Localization.MaskTargetCommon;
            EditorGUILayout.LabelField(
                string.Format(Localization.AiSuggestCommitTargetFormat, target),
                EditorStyles.miniLabel);

            // マスクは色ゾーンの色替え範囲を制限する機能。有効な色ゾーンが無いと足しても
            // 出力は変わらない(=「反映されない」の主因の一つ)。ここで先に気づけるようにする。
            bool anyEnabledZone = zones != null && zones.Exists(z => z != null && z.enabled);
            if (!anyEnabledZone)
                EditorGUILayout.HelpBox(Localization.AiSuggestNoZoneWarning, MessageType.Warning);

            // 直近クリックが背景まで広がった可能性があれば、Ctrl+Z で戻す誘導を出す。
            if (ctl.LastClickFloodWarning)
                EditorGUILayout.HelpBox(Localization.AiSuggestAreaWarning, MessageType.Warning);

            // 推論が完走してもマスクが変わらないと、画面上は「何も起きない」としか見えない。
            // 「AI が領域を返していない(エンジンの異常を疑う)」と「すでに追加済みだった(正常)」を
            // 区別して出し、Unity 再起動が要る状況かどうかがその場で分かるようにする。
            if (ctl.LastProposalEmpty)
            {
                EditorGUILayout.HelpBox(Localization.AiSuggestEmptyProposal, MessageType.Warning);
                DrawRestartButton();
            }
            else if (ctl.LastCommitEmpty)
            {
                EditorGUILayout.HelpBox(Localization.AiSuggestEmptyCommit, MessageType.Info);
            }
        }
    }
}
