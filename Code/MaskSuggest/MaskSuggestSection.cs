// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 除外マスク UI 内に描く「AI マスク提案」セクション。
    /// Sentis 統合(MaskSuggestBridge.Service)が不在なら一切描画しない = 既存 UI 完全不変。
    /// </summary>
    internal static class MaskSuggestSection
    {
        public static void Draw(IrocaWindow host, MaskPaintView maskView)
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) return;
            var ctl = maskView.SuggestController;
            if (ctl == null) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Localization.AiSuggest, EditorStyles.boldLabel);

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

            if (!ctl.Active)
            {
                // 非アクティブでも積み上げが残っていれば操作は出す(誤ってモードを閉じた場合の救済)
                DrawAccumulationControls(ctl);
                ctl.UpdateOverlayIfNeeded();
                return;
            }

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
                        var pr = EditorGUILayout.GetControlRect(false, 18f);
                        EditorGUI.ProgressBar(pr, MaskSuggestModelDownload.Progress,
                                              Localization.AiSuggestDownloading);
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
                    break;

                default:
                    EditorGUILayout.HelpBox(Localization.AiSuggestHintIdle, MessageType.Info);
                    break;
            }

            // ─── 表示中の提案 ───
            if (ctl.Pending != null)
            {
                if (ctl.Pending.floodWarning)
                    EditorGUILayout.HelpBox(Localization.AiSuggestAreaWarning, MessageType.Warning);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent(Localization.AiSuggestAccept,
                                                    Localization.AiSuggestAcceptTooltip)))
                    ctl.AcceptPending();
                if (GUILayout.Button(new GUIContent(Localization.AiSuggestRetry,
                                                    Localization.AiSuggestRetryTooltip)))
                    ctl.DiscardPending();
                EditorGUILayout.EndHorizontal();
            }

            DrawAccumulationControls(ctl);
            ctl.UpdateOverlayIfNeeded();
        }

        static void DrawAccumulationControls(MaskSuggestController ctl)
        {
            if (!ctl.HasUnion) return;
            EditorGUILayout.LabelField(
                string.Format(Localization.AiSuggestPiecesFormat, ctl.AcceptedCount),
                EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(Localization.AiSuggestUndoPiece,
                                                Localization.AiSuggestUndoPieceTooltip)))
                ctl.UndoLastAccepted();
            if (GUILayout.Button(new GUIContent(Localization.AiSuggestClear,
                                                Localization.AiSuggestClearTooltip)))
                ctl.ClearAccumulation();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(Localization.AiSuggestCommitExclude,
                                                Localization.AiSuggestCommitExcludeTooltip)))
                ctl.CommitToMask(invert: false);
            if (GUILayout.Button(new GUIContent(Localization.AiSuggestCommitKeep,
                                                Localization.AiSuggestCommitKeepTooltip)))
                ctl.CommitToMask(invert: true);
            EditorGUILayout.EndHorizontal();
        }
    }
}
