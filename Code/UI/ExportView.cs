// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Iroca
{
    /// <summary>
    /// 単体出力 / 一括適用の UI 描画と PNG 書き出しを担当する。
    /// プレビュー生成・プリセット管理・マスク編集には関与しない。
    /// </summary>
    [System.Serializable]
    internal class ExportView
    {
        public bool batchFoldout;
        public List<Texture2D> batchTextures = new List<Texture2D>();

        public bool saveAsNewFile = true;
        public string newFileName = "";
        public bool inheritImportSettings = true;

        [System.NonSerialized] private Vector2 _batchScrollPos;
        [System.NonSerialized] private IrocaWindow _host;

        // メインスレッドで pixels を取得し、PixelProcessor 計算 + PNG エンコード + 書き込みを
        // Task.Run(バックグラウンド)で実行する。完了後、メインスレッドでは AssetDatabase 操作のみ。
        // (旧: エンコード/書き込みもメインスレッドで行い 4K で終了時にフリーズしていた)
        [System.NonSerialized] private readonly PreviewJob<ExportPayload> _exportJob = new PreviewJob<ExportPayload>();
        [System.NonSerialized] private readonly PreviewJobProgress _exportProgress = new PreviewJobProgress();

        private struct ExportPayload
        {
            public string outputPath;
            public string srcPath;
            public bool inheritImportSettings;
            // エンコード済み PNG。ディスクへの書き込みは apply(メインスレッド)で行う。
            // BG で書くと「ファイルは書けたがキャンセルで apply が走らない」中途半端な状態
            // (ImportAsset も InvalidateSourceAndRepaint も未実行)が作れてしまうため。
            public byte[] pngData;
        }

        /// <summary>エクスポート処理中。true の間はウィンドウ全体を Disabled に。</summary>
        public bool IsExporting => _exportJob.IsRunning;

        public void Initialize(IrocaWindow host)
        {
            _host = host;
        }

        public void Dispose()
        {
            _exportJob.Dispose();
        }

        public void DrawExportSection()
        {
            EditorGUILayout.LabelField(Localization.StepPrefixExport + Localization.Export, EditorStyles.boldLabel);

            // 外側の DisabledScope（IrocaWindow.OnGUI で囲まれる）を壊さないよう、
            // GUI.enabled の直接代入ではなく BeginDisabledGroup を使う。
            EditorGUI.BeginDisabledGroup(_host.SourceTexture == null);

            saveAsNewFile = EditorGUILayout.Toggle(
                new GUIContent(Localization.SaveAsNewFile, Localization.SaveAsNewFileTooltip),
                saveAsNewFile);

            inheritImportSettings = EditorGUILayout.Toggle(
                new GUIContent(Localization.InheritImportSettings, Localization.InheritImportSettingsTooltip),
                inheritImportSettings);

            if (saveAsNewFile)
            {
                newFileName = EditorGUILayout.TextField(
                    new GUIContent(Localization.FileName, Localization.FileNameTooltip),
                    newFileName);
            }

            if (GUILayout.Button(new GUIContent(Localization.ApplyAndSave, Localization.ApplyAndSaveTooltip), GUILayout.Height(32)))
            {
                ApplyRecolor();
            }

            if (GUILayout.Button(new GUIContent(Localization.OpenFolder, Localization.OpenFolderTooltip)))
            {
                string path = AssetDatabase.GetAssetPath(_host.SourceTexture);
                if (!string.IsNullOrEmpty(path))
                    EditorUtility.RevealInFinder(path);
            }

            EditorGUI.EndDisabledGroup();
        }

        public void SetSourceTextureBaseName(string baseNameWithoutExtension)
        {
            newFileName = baseNameWithoutExtension + "_recolored";
        }

        /// <summary>
        /// エクスポートセクションの描画想定高さを返す。
        /// IrocaWindow の横並びレイアウトで「上部 + プレビュー領域」の高さ計算に使う。
        /// 折りたたみは廃止したので常に内部コントロールの合計を返す。
        /// </summary>
        public float GetSectionHeight()
        {
            float lineH = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            // 内訳: 見出しラベル + 新規保存トグル + (新規時のみ)ファイル名 +
            //       インポート設定継承トグル + 適用ボタン(高さ32) + フォルダを開くボタン
            float h = lineH;          // 見出しラベル
            h += lineH;               // saveAsNewFile トグル
            if (saveAsNewFile)
                h += lineH;           // ファイル名フィールド
            h += lineH;               // inheritImportSettings トグル
            h += 32f + EditorGUIUtility.standardVerticalSpacing; // ApplyAndSave ボタン
            h += lineH;               // OpenFolder ボタン
            return h;
        }

        private void ApplyRecolor()
        {
            if (_exportJob.IsRunning) return;

            // 有効なゾーンが無いと無変更ファイルを書き出して「完了」表示になり誤解を生むため、
            // 処理に入る前に止めてユーザーへ誘導する。
            if (_host.Session == null || !_host.Session.zones.Any(z => z.enabled))
            {
                _host?.ShowNotification(new GUIContent(Localization.NoEnabledZones));
                return;
            }

            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null || !IrocaWindow.IsReadable(sourceTexture))
            {
                NotifyError(Localization.TextureReadError);
                return;
            }

            string srcPath = AssetDatabase.GetAssetPath(sourceTexture);
            if (string.IsNullOrEmpty(srcPath))
            {
                NotifyError(Localization.PathNotFound);
                return;
            }

            // 重い処理のあとでキャンセルされると計算が全て無駄になるので、
            // 確認はユーザー入力の時点（＝処理前）に行う。
            string outputPath;
            if (saveAsNewFile)
            {
                string dir = Path.GetDirectoryName(srcPath);
                string safeName = string.IsNullOrWhiteSpace(newFileName) ? "recolored" : newFileName;
                // セキュリティ: ファイル名部分のみを取得してパストラバーサルを防ぐ
                safeName = Path.GetFileName(safeName);
                foreach (char c in Path.GetInvalidFileNameChars())
                    safeName = safeName.Replace(c.ToString(), "_");
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "recolored";
                outputPath = Path.Combine(dir, safeName + ".png");

                if (File.Exists(outputPath))
                {
                    if (!EditorUtility.DisplayDialog(Localization.Confirm,
                        Localization.FileExistsConfirm(outputPath), Localization.Overwrite, Localization.Cancel))
                    {
                        return;
                    }
                }
            }
            else
            {
                outputPath = Path.ChangeExtension(srcPath, ".png");
                // ソースが PNG なら真の上書き。非 PNG（.jpg/.tga 等）だと ChangeExtension は
                // 元ファイルを上書きせず隣に .png を新規作成するだけで、マテリアルは旧ファイル参照の
                // まま＝「色が変わらない」ように見える。ダイアログ文言も「上書き」で矛盾するので、
                // 非 PNG のときは新規作成＋非自動切替を明示する専用ダイアログを出す。
                bool srcIsPng = string.Equals(Path.GetExtension(srcPath), ".png", System.StringComparison.OrdinalIgnoreCase);
                bool confirmed = srcIsPng
                    ? EditorUtility.DisplayDialog(Localization.Confirm,
                        Localization.OverwriteConfirm, Localization.Overwrite, Localization.Cancel)
                    : EditorUtility.DisplayDialog(Localization.Confirm,
                        Localization.OverwriteNonPngConfirm(Path.GetFileName(outputPath)), Localization.OK, Localization.Cancel);
                if (!confirmed)
                {
                    return;
                }
            }

            // Texture2D API はメインスレッド必須なのでここで全て済ませ、計算本体だけ Task.Run へ渡す。
            Texture2D loadTex = null;
            Color32[] pixels;
            int texW, texH;
            try
            {
                byte[] srcBytes = File.ReadAllBytes(srcPath);
                loadTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!loadTex.LoadImage(srcBytes))
                {
                    NotifyError(Localization.TextureLoadError);
                    return;
                }
                pixels = loadTex.GetPixels32();
                texW = loadTex.width;
                texH = loadTex.height;
            }
            finally
            {
                if (loadTex != null) Object.DestroyImmediate(loadTex);
            }

            var session = _host.Session;
            // リスト先頭のゾーンほど先に処理され、重なった領域を占有する。
            // ゾーンは Clone してから BG へ渡す(プレビュー系と同じ防御コピー)。かんたんモードの
            // 自動調整はエクスポート中も裏で走るため、生参照だと apply や Undo でゾーンが変異し
            // 一部ゾーンだけ新旧混在の出力になり得る。Clone は値等価コピーで出力ビット不変。
            var sorted = session.zones.Where(z => z.enabled).Select(z => z.Clone()).ToList();
            var maskSnap = (sorted.Count > 0) ? _host.BuildMaskSnapshot() : null;

            // 計算に必要な値を全てローカル変数に退避（Task.Run の中から session を直接触らない）
            float edgeFeather = session.edgeFeather;
            int antiAliasCleanup = session.antiAliasCleanup;
            int holeFillPasses = session.holeFillPasses;
            int holeFillMinNeighbors = session.holeFillMinNeighbors;
            float relaxedSatMin = session.relaxedSatMin;
            float relaxedSatRamp = session.relaxedSatRamp;
            bool useDecontamination = session.useDecontamination;
            int decontaminationRadius = session.decontaminationRadius;
            bool inheritFlag = inheritImportSettings;

            _exportProgress.Reset();
            _exportProgress.Report(0.05f);

            _exportJob.Schedule(
                work: ct =>
                {
                    _exportProgress.Report(0.10f);
                    if (sorted.Count > 0)
                    {
                        // CancellationToken 対応オーバーロード: 内部で ThrowIfCancellationRequested を呼ぶ
                        PixelProcessor.ProcessPixelsArray(pixels, texW, texH,
                            maskSnap, sorted, edgeFeather, antiAliasCleanup,
                            holeFillPasses, holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp,
                            0, 0, 0, 0, ct,
                            useDecontamination: useDecontamination,
                            decontaminationRadius: decontaminationRadius);
                    }
                    ct.ThrowIfCancellationRequested();
                    _exportProgress.Report(0.80f);

                    // PNG エンコード + ファイル書き込みもバックグラウンドで行う(メインスレッドの
                    // 終了時フリーズを解消。旧版は Texture2D + EncodeToPNG をメインスレッドで実行し
                    // 4K で秒単位ブロックしていた)。Texture2D を介さない ImageConversion.EncodeArrayToPNG
                    // は Unity 2022.3(本プロジェクト/VRChat の対象)ではスレッドセーフ。Color32[] は
                    // sRGB バイト値なので R8G8B8A8_SRGB を指定し、旧 Texture2D(RGBA32).EncodeToPNG と
                    // 同じ画素を書き出す。
                    // ※ Unity 6+ では EncodeArrayToPNG がメインスレッド必須に変わったため、将来 Unity 6
                    //    以降へ移行する場合はこのエンコードをメインスレッド(apply 側)へ戻すこと。
                    byte[] rgba = new byte[pixels.Length * 4];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        int o = i * 4;
                        rgba[o]     = pixels[i].r;
                        rgba[o + 1] = pixels[i].g;
                        rgba[o + 2] = pixels[i].b;
                        rgba[o + 3] = pixels[i].a;
                    }
                    byte[] pngData = ImageConversion.EncodeArrayToPNG(
                        rgba, GraphicsFormat.R8G8B8A8_SRGB, (uint)texW, (uint)texH);
                    if (pngData == null || pngData.Length == 0)
                        throw new System.Exception("EncodeArrayToPNG が空のデータを返しました");
                    _exportProgress.Report(0.95f);

                    // ★ここでディスクへ書かない★ — 書いてしまうと、この直後〜apply までの間に
                    // キャンセルされた場合に「PNG は置き換わったのに ImportAsset も
                    // InvalidateSourceAndRepaint も走っていない」状態が残る。上書きモードでは
                    // A-2 で塞いだ二重適用経路(プレビューが古い原本画素を握り続ける)が復活する。
                    // 重いのはエンコードでありファイル書き込みではないので、書き込みは apply で行う。
                    return new ExportPayload
                    {
                        outputPath = outputPath,
                        srcPath = srcPath,
                        inheritImportSettings = inheritFlag,
                        pngData = pngData,
                    };
                },
                apply: payload =>
                {
                    // メインスレッド: 書き込み + AssetDatabase 操作(重いエンコードは BG で完了済み)。
                    // ここまで来たらキャンセルされないので、書き込みと取り込みは必ず対で走る。
                    //
                    // PreviewJob は apply で投げた例外を onError へ回さない(BG の例外だけを拾う)。
                    // 書き込みは権限不足・ディスクフル等で普通に失敗し得るので、ここで受けて
                    // BG 失敗時と同じ経路へ流す。放置すると失敗が UI に一切出ない。
                    try
                    {
                        // 書き込み自体はアトミック(一時ファイル→rename)にする。書き潰す相手が
                        // 元テクスチャそのものであり得るため、途中で落ちた書き込みで原本を失わない。
                        AtomicFile.WriteAllBytes(payload.outputPath, payload.pngData);
                        _exportProgress.Report(0.97f);

                        string relativePath = PathUtils.ToAssetsRelativeOrNull(payload.outputPath);
                        if (relativePath != null)
                        {
                            if (payload.inheritImportSettings)
                            {
                                string srcRel = PathUtils.ToAssetsRelativeOrNull(payload.srcPath);
                                if (srcRel != null)
                                    PreApplyImportSettings(srcRel, relativePath);
                            }
                            AssetDatabase.ImportAsset(relativePath);
                        }

                        // ソース自身を書き換えたなら、プレビューが握っている「ディスク原本の画素」は
                        // もう古い。捨てないと、次のエクスポートが再着色済みファイルを読み直して
                        // 二重適用になる（プレビューは旧画素を表示し続けるので画面では気づけない）。
                        if (IsSameFile(payload.outputPath, payload.srcPath))
                            _host?.InvalidateSourceAndRepaint();

                        _exportProgress.Report(1.0f);
                        Debug.Log($"[Iroca] Saved: {payload.outputPath}");
                        // 非モーダル通知: ファイル名のみウィンドウ右下に短時間表示。詳細パスは Debug.Log。
                        _host?.ShowNotification(new GUIContent($"{Localization.Complete}: {Path.GetFileName(payload.outputPath)}"));
                    }
                    catch (System.Exception ex)
                    {
                        ReportExportFailure(ex);
                    }
                },
                onError: ReportExportFailure);
        }

        /// <summary>エクスポート失敗をログ＋UI 通知へ流す（BG 側と apply 側で共有）。</summary>
        private void ReportExportFailure(System.Exception ex)
        {
            Debug.LogError($"[Iroca] Export failed: {ex.Message}\n{ex.StackTrace}");
            NotifyError(ex.Message);
        }

        /// <summary>
        /// 2 つのパスが同一ファイルを指すか。書き出し先がプレビューのソース自身かの判定に使う。
        /// AssetDatabase のパスは '/' 区切り、Path.Combine は '\' を混ぜるため、素の文字列比較では
        /// 取りこぼす。フルパスへ正規化してから比較する。判定に迷ったら「同一」と答える側が安全
        /// （余分なキャッシュ破棄で済み、逆は二重適用を見逃す）。
        /// </summary>
        private static bool IsSameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                    System.StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// エラーを Console に出しつつ Iroca ウィンドウ内に非モーダル通知を表示する。
        /// EditorUtility.DisplayDialog は Editor 全体をブロックするため避ける。
        /// </summary>
        private void NotifyError(string message)
        {
            Debug.LogError($"[Iroca] {message}");
            _host?.ShowNotification(new GUIContent($"{Localization.Error}: {message}"));
        }

        /// <summary>
        /// エクスポート実行中の進捗バーとキャンセルボタンを描画する。
        /// IrocaWindow.OnGUI の DisabledScope の外で呼び出すことで、ウィンドウ全体が
        /// 無効化されている状況でもキャンセルだけは押せるようにする。
        /// </summary>
        public void DrawJobOverlay()
        {
            if (!_exportJob.IsRunning) return;

            EditorGUILayout.Space(2);
            var rect = EditorGUILayout.GetControlRect(false, 18f);
            float pct = _exportProgress.Value;
            string label = $"{Localization.ApplyAndSave}  {Mathf.RoundToInt(pct * 100f)}%";
            EditorGUI.ProgressBar(rect, pct, label);

            if (GUILayout.Button(new GUIContent(Localization.Cancel, Localization.CancelActionTooltip), GUILayout.Height(22)))
            {
                _exportJob.Cancel();
            }
            EditorGUILayout.Space(2);
        }

        public void DrawBatchSection()
        {
            batchFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(batchFoldout, Localization.BatchApply);
            if (!batchFoldout)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            EditorGUILayout.HelpBox(Localization.BatchHint, MessageType.Info);

            _batchScrollPos = EditorGUILayout.BeginScrollView(_batchScrollPos, GUILayout.MaxHeight(120));
            int removeIdx = -1;
            for (int i = 0; i < batchTextures.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                batchTextures[i] = (Texture2D)EditorGUILayout.ObjectField(
                    batchTextures[i], typeof(Texture2D), false);
                if (GUILayout.Button(new GUIContent("×", Localization.RemoveBatchTextureTooltip),
                        GUILayout.Width(IrocaConsts.Layout.RemoveButtonWidth)))
                    removeIdx = i;
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            if (removeIdx >= 0) batchTextures.RemoveAt(removeIdx);

            if (GUILayout.Button(new GUIContent(Localization.AddBatchTexture, Localization.AddBatchTextureTooltip)))
                batchTextures.Add(null);

            var session = _host.Session;
            EditorGUI.BeginDisabledGroup(batchTextures.Count == 0 || session.zones.Count == 0);
            if (GUILayout.Button(new GUIContent(Localization.BatchApplyAndSave, Localization.BatchApplyAndSaveTooltip), GUILayout.Height(28)))
                RunBatchApply();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(4);
        }

        // .meta ファイルをインポート前に書き込んでおくことで、
        // ImportAsset の 1 回の圧縮パスで正しい設定が適用される（SaveAndReimport 不要）。
        private static void PreApplyImportSettings(string srcRelPath, string dstRelPath)
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            string sep  = Path.DirectorySeparatorChar.ToString();
            string srcMeta = Path.Combine(root, srcRelPath.Replace("/", sep)) + ".meta";
            string dstMeta = Path.Combine(root, dstRelPath.Replace("/", sep)) + ".meta";

            if (!File.Exists(srcMeta)) return;

            string content = File.ReadAllText(srcMeta, System.Text.Encoding.UTF8);

            // 上書きなら既存 GUID を維持、新規ファイルなら新 GUID を生成
            string guid = AssetDatabase.AssetPathToGUID(dstRelPath);
            if (string.IsNullOrEmpty(guid))
                guid = System.Guid.NewGuid().ToString("N");

            content = Regex.Replace(content, @"(?m)^guid: [0-9a-f]+$", $"guid: {guid}");
            File.WriteAllText(dstMeta, content, System.Text.Encoding.UTF8);
        }

        private void RunBatchApply()
        {
            var session = _host.Session;
            var plannedOutputs = new List<string>();
            foreach (var tex in batchTextures)
            {
                if (tex == null) continue;
                string srcPath = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(srcPath)) continue;
                string dir      = Path.GetDirectoryName(srcPath);
                string baseName = Path.GetFileNameWithoutExtension(srcPath) + "_recolored";
                plannedOutputs.Add(Path.Combine(dir, baseName + ".png"));
            }

            bool anyExists = plannedOutputs.Any(File.Exists);
            if (anyExists)
            {
                if (!EditorUtility.DisplayDialog(
                        Localization.Confirm,
                        Localization.OverwriteConfirm,
                        Localization.Overwrite,
                        Localization.Cancel))
                {
                    return;
                }
            }

            int success = 0;
            // 一括対象に現在プレビュー中のテクスチャの出力先が含まれると、ソース画素が書き換わる。
            // 単体エクスポートと同じくキャッシュを捨てないと二重適用になるので、書き出し先を照合する。
            string previewSrcPath = _host.SourceTexture != null
                ? AssetDatabase.GetAssetPath(_host.SourceTexture) : null;
            bool previewSourceOverwritten = false;
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < batchTextures.Count; i++)
                {
                    var tex = batchTextures[i];
                    if (tex == null) continue;
                    Texture2D fullTex = null;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            Localization.BatchProgress,
                            tex.name,
                            (float)i / Mathf.Max(1, batchTextures.Count)))
                    {
                        break;
                    }

                    if (!IrocaWindow.IsReadable(tex)) IrocaWindow.EnableReadWrite(tex);
                    if (!IrocaWindow.IsReadable(tex)) continue;

                    string srcPath = AssetDatabase.GetAssetPath(tex);
                    if (string.IsNullOrEmpty(srcPath)) continue;

                    try
                    {
                        byte[] srcBytes = File.ReadAllBytes(srcPath);
                        fullTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!fullTex.LoadImage(srcBytes)) continue;

                        Color32[] pixels = fullTex.GetPixels32();
                        int texW = fullTex.width, texH = fullTex.height;
                        // リスト先頭のゾーンほど先に処理され、重なった領域を占有する。
            var sorted = session.zones.Where(z => z.enabled).ToList();

                        if (sorted.Count > 0)
                        {
                            var maskSnap = _host.BuildMaskSnapshot();
                            PixelProcessor.ProcessPixelsArray(pixels, texW, texH,
                                maskSnap, sorted, session.edgeFeather, session.antiAliasCleanup,
                                session.holeFillPasses, session.holeFillMinNeighbors, session.relaxedSatMin, session.relaxedSatRamp,
                                useDecontamination: session.useDecontamination,
                                decontaminationRadius: session.decontaminationRadius);
                        }

                        fullTex.SetPixels32(pixels);
                        // Apply()(CPU→GPU アップロード)は CPU 側を読む EncodeToPNG には不要。
                        byte[] pngData = fullTex.EncodeToPNG();

                        string dir      = Path.GetDirectoryName(srcPath);
                        string baseName = Path.GetFileNameWithoutExtension(srcPath) + "_recolored";
                        string outPath  = Path.Combine(dir, baseName + ".png");
                        File.WriteAllBytes(outPath, pngData);
                        if (IsSameFile(outPath, previewSrcPath)) previewSourceOverwritten = true;
                        string relOutPath = PathUtils.ToAssetsRelativeOrNull(outPath);
                        if (relOutPath != null)
                        {
                            if (inheritImportSettings)
                                PreApplyImportSettings(srcPath, relOutPath);
                            AssetDatabase.ImportAsset(relOutPath);
                        }
                        success++;
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[Iroca] Batch apply failed for {tex.name}: {ex.Message}");
                    }
                    finally
                    {
                        if (fullTex != null)
                            Object.DestroyImmediate(fullTex);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            if (previewSourceOverwritten) _host?.InvalidateSourceAndRepaint();

            EditorUtility.DisplayDialog(Localization.Complete,
                Localization.BatchComplete(success), Localization.OK);
        }
    }
}
