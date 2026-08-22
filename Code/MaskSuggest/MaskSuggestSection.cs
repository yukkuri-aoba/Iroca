// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 「AI マスク提案」の UI。置き場は役割で 2 つに分かれる:
    ///
    ///   <see cref="DrawSetup"/>        … メインウィンドウのマスク欄。**一度きりの有効化**
    ///                                    (Sentis 導入・モデル取得・要再起動の案内)だけを持つ。
    ///                                    ここに無いと、Sentis を手動導入した環境でしか AI 機能に
    ///                                    到達できず、機能の存在にも気づけない。
    ///   <see cref="DrawToolControls"/> … マスク編集ウィンドウ。AI 提案ツールを選んでいる間の
    ///                                    **操作と状態**(粒度・進捗・警告)。
    ///
    /// 分け方の基準は「編集中に触るものか」。編集中に触るものはすべてマスク編集ウィンドウへ寄せる
    /// (対象ゾーン・マスクの種類・ツール・粒度が別ウィンドウに散っていると取り違えが起きる。
    ///  2026-08-22 のユーザー指摘)。
    /// </summary>
    internal static class MaskSuggestSection
    {
        /// <summary>
        /// AI 提案ツールが今すぐ使えるか。false ならマスク編集ウィンドウ側でツールボタンを
        /// 無効表示にし、有効化はメインウィンドウのマスク欄(<see cref="DrawSetup"/>)へ誘導する。
        /// </summary>
        public static bool ToolReady
        {
            get
            {
                var svc = MaskSuggestBridge.Service;
                return svc != null && svc.Phase != MaskSuggestPhase.NoModel;
            }
        }

        /// <summary>
        /// メインウィンドウのマスク欄に描く有効化導線。セットアップが済んでいる間は何も描かない
        /// (常設の説明は編集ウィンドウ側のツールチップが持つ)。
        /// </summary>
        public static void DrawSetup(IrocaWindow host)
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

            // Burst が失敗した世代では推論が空を返すだけで、エラーも出ずに「動かない」ように
            // 見える。クリックを試す前に気づけるよう、モードに入る前から知らせる。
            DrawBurstFailureNoticeIfNeeded(svc);

            if (svc.Phase != MaskSuggestPhase.NoModel) return;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localization.AiSuggest, EditorStyles.boldLabel);
            DrawModelDownload(svc);
        }

        /// <summary>
        /// マスク編集ウィンドウで AI 提案ツールを選んでいる間の操作・状態表示。
        /// 「マスクの種類」と対象ゾーンは呼び出し側(パレット上部)が既に描いているのでここには置かない。
        /// </summary>
        public static void DrawToolControls(IrocaWindow host, MaskPaintView maskView)
        {
            var svc = MaskSuggestBridge.Service;
            var ctl = maskView.SuggestControllerIfCreated;
            if (svc == null || ctl == null) return;

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

            switch (svc.Phase)
            {
                case MaskSuggestPhase.NoModel:
                    // 取得操作そのものはメインウィンドウ側(DrawSetup)が持つ。ここへ複製すると
                    // 「同じボタンが 2 か所」になり、今回まとめた意味が無くなる。
                    EditorGUILayout.HelpBox(Localization.AiSuggestNoModelInEditor, MessageType.Info);
                    break;

                case MaskSuggestPhase.LoadingModel:
                    EditorGUILayout.HelpBox(Localization.AiSuggestLoadingModel, MessageType.Info);
                    break;

                case MaskSuggestPhase.Encoding:
                {
                    var r = EditorGUILayout.GetControlRect(false, 18f);
                    EditorGUI.ProgressBar(r, Mathf.Clamp01(svc.Progress), Localization.AiSuggestEncoding);
                    DrawPendingClickCount(svc, minCount: 1);
                    break;
                }

                case MaskSuggestPhase.Decoding:
                    EditorGUILayout.LabelField(Localization.AiSuggestDecoding, EditorStyles.miniLabel);
                    DrawPendingClickCount(svc, minCount: 2);
                    break;

                case MaskSuggestPhase.Error:
                    EditorGUILayout.HelpBox(svc.ErrorMessage ?? "error", MessageType.Error);
                    // 推論の失敗は Burst のコールドスタート失敗が原因のことが多く、その世代では
                    // 何度クリックしても直らない。再起動導線をエラーと同じ場所に出す。
                    EditorGUILayout.HelpBox(Localization.AiSuggestErrorRestartHint, MessageType.Info);
                    DrawRestartButton();
                    break;

                default:
                    // 待機中の操作説明は出さない。同じウィンドウの最下部に
                    // ツール共通のヒント(Localization.MaskHintAi)が出ており、二重になる。
                    break;
            }

            DrawTargetAndWarnings(host, maskView, ctl);
        }

        /// <summary>
        /// AI モデル(2 ファイル・約 45MB)の取得導線。ユーザー共通フォルダへ 1 か所だけ置くので
        /// プロジェクトごとの再取得は要らない。Sentis 導入と対で「一度きりの有効化」に属する。
        /// </summary>
        static void DrawModelDownload(IMaskSuggestService svc)
        {
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
            if (Event.current.type == EventType.Layout) svc.TryEnsureModels();
        }

        /// <summary>
        /// 反映待ちクリックの件数表示。クリックは FIFO で全て順に反映されるため、
        /// 「押した分が消えていない」ことをここで明示する(プレビュー上のマーカーと対)。
        /// </summary>
        static void DrawPendingClickCount(IMaskSuggestService svc, int minCount)
        {
            int pending = svc.PendingClickCount;
            if (pending < minCount) return;
            EditorGUILayout.LabelField(
                string.Format(Localization.AiSuggestPendingClicksFormat, pending),
                EditorStyles.miniLabel);
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
        ///
        /// ただし出すのは推論が CPU バックエンドで走るときだけ。Burst は Sentis アセンブリの
        /// 関数ポインタをドメインロード時に一括で先行コンパイルするので、GPU バックエンドしか
        /// 使わない環境でも Unity.Sentis.CPUBackend の失敗ログは出る。そこで警告すると
        /// 「正常に動いているのに再起動を促される」誤検出になる。
        /// </summary>
        static void DrawBurstFailureNoticeIfNeeded(IMaskSuggestService svc)
        {
            if (!MaskSuggestBurstWatch.FailedThisSession) return;
            if (!svc.UsesCpuBackend) return;
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
            // CWD はプロジェクトルートである保証がない（batchmode 起動やスクリプトからの
            // Directory.SetCurrentDirectory で変わる）。ズレていると別のフォルダを
            // プロジェクトとして開こうとするので、dataPath から確実に導出する。
            EditorApplication.OpenProject(
                System.IO.Path.GetDirectoryName(Application.dataPath));
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

            // この導線は「Sentis 統合アセンブリが不在」で出るため、未導入だけでなく
            // 対応範囲外の版が既に入っている場合も含む。押すと他ツールが入れた Sentis を
            // 差し替えることになるので、黙って実行せず先に伝える。
            string replaced = MaskSuggestInstall.VersionThatWouldBeReplaced();
            if (replaced != null)
                EditorGUILayout.HelpBox(
                    string.Format(Localization.AiSuggestSentisVersionReplace,
                                  replaced, MaskSuggestInstall.SentisPackageVersion),
                    MessageType.Warning);

            string label = replaced != null
                ? string.Format(Localization.AiSuggestReplaceSentis, MaskSuggestInstall.SentisPackageVersion)
                : Localization.AiSuggestInstallSentis;
            if (GUILayout.Button(new GUIContent(
                    label,
                    string.Format(Localization.AiSuggestInstallSentisTooltip,
                                  MaskSuggestInstall.SentisPackageId,
                                  MaskSuggestInstall.SentisPackageVersion))))
                MaskSuggestInstall.StartInstall();
        }

        /// <summary>
        /// 効果が出ない/外した場合の注意書き。クリック 1 回で即マスクへ反映されるため
        /// (積み上げ・確定ボタンは無い)、「反映されない」に先に気づけるよう常時表示する。
        ///
        /// 「追加先: ゾーン / 種類」の明示ラベルはここには置かない。同じウィンドウの数行上に
        /// 対象プルダウンと種類ボタンが現物として見えており、文字での言い直しは冗長なため
        /// (別ウィンドウに散っていた頃は取り違え防止に必要だった)。
        /// </summary>
        static void DrawTargetAndWarnings(IrocaWindow host, MaskPaintView maskView, MaskSuggestController ctl)
        {
            var zones = host.Session?.zones;

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
