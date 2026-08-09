// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_SENTIS_PRESENT
using System;
using System.IO;
using Unity.Sentis;
using UnityEditor;
using UnityEngine;

namespace Iroca.SentisIntegration
{
    /// <summary>
    /// MobileSAM の ONNX モデルを発見し、Sentis の Model へ変換・キャッシュ・ロードする。
    ///
    /// Sentis 2.x の ONNX 変換器(ONNXModelConverter)は internal のため、public API のみで
    /// 変換する: .onnx を Assets 内の一時フォルダへコピー → AssetDatabase インポート
    /// (ONNX ScriptedImporter が ModelAsset を生成) → ModelWriter.Save で .sentis を
    /// UserSettings 側へキャッシュ → 一時アセット削除。2 回目以降は .sentis を
    /// ModelLoader.Load(path) で直接ロードする(高速・Assets 非汚染)。
    /// キャッシュ名に Sentis パッケージ版を含め、版が変わったら自動再変換する。
    /// </summary>
    internal static class SentisModelRepository
    {
        public const string EncoderFileName = MaskSuggestBridge.EncoderFileName;
        public const string DecoderFileName = MaskSuggestBridge.DecoderFileName;
        // 実行ごとに一意名を作る。固定名フォルダを finally でフォルダごと消していたため、
        // ユーザーが偶然同名フォルダを持っていると中身ごと失われた。
        const string TempImportDirPrefix = "Assets/IrocaModelImportTemp_";

        static string ModelsDir => MaskSuggestBridge.ModelsDirectory;
        static string CacheDir => Path.Combine(ModelsDir, "cache");

        public static string EncoderOnnxPath => Path.Combine(ModelsDir, EncoderFileName);
        public static string DecoderOnnxPath => Path.Combine(ModelsDir, DecoderFileName);

        /// <summary>両モデルの .onnx が配置済みか(軽量チェック)。</summary>
        public static bool ModelsPresent() =>
            File.Exists(EncoderOnnxPath) && File.Exists(DecoderOnnxPath);

        static string SentisVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Worker).Assembly);
            return info != null ? info.version : "unknown";
        }

        static string CachePathFor(string onnxPath) =>
            Path.Combine(CacheDir,
                Path.GetFileNameWithoutExtension(onnxPath) + "." + SentisVersion() + ".sentis");

        /// <summary>
        /// .onnx を Model としてロードする(.sentis キャッシュ経由)。失敗時は null + error。
        /// メインスレッド専用(AssetDatabase を使うため)。
        /// </summary>
        public static Model LoadOrConvert(string onnxPath, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(onnxPath))
                {
                    error = $"モデルファイルがありません: {onnxPath}";
                    return null;
                }
                string cachePath = CachePathFor(onnxPath);
                if (File.Exists(cachePath) &&
                    File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(onnxPath))
                {
                    try
                    {
                        return ModelLoader.Load(cachePath);
                    }
                    catch (Exception cacheEx)
                    {
                        // Save 中クラッシュ・空き容量枯渇等で壊れたキャッシュは「新しいタイムスタンプ」で
                        // 残り続け、従来はユーザーが手動で消すまで AI 機能が永続エラーだった。
                        // 削除して ONNX からの再変換にフォールバックする。
                        UnityEngine.Debug.LogWarning(
                            $"[Iroca] .sentis キャッシュの読込に失敗したため削除して再変換します: {cachePath}\n{cacheEx.Message}");
                        try { File.Delete(cachePath); } catch { /* 削除失敗でも下の Save が上書きする */ }
                    }
                }

                var model = ConvertViaAssetPipeline(onnxPath, out error);
                if (model == null) return null;

                Directory.CreateDirectory(CacheDir);
                ModelWriter.Save(cachePath, model);
                // Save 直後の Load で「キャッシュから読めること」まで検証しておく
                // (次回起動時に壊れたキャッシュで失敗するより今失敗する方が診断しやすい)
                return ModelLoader.Load(cachePath);
            }
            catch (Exception e)
            {
                error = $"モデルのロードに失敗しました: {e.Message}";
                return null;
            }
        }

        static Model ConvertViaAssetPipeline(string onnxPath, out string error)
        {
            error = null;
            string tempDir = TempImportDirPrefix + Guid.NewGuid().ToString("N");
            string assetPath = tempDir + "/" + Path.GetFileName(onnxPath);
            bool createdTempDir = false;
            try
            {
                if (Directory.Exists(tempDir))
                {
                    // GUID 衝突は現実には起きないが、起きたなら他人のフォルダなので触らない。
                    error = "一時フォルダ名が衝突しました: " + tempDir;
                    return null;
                }
                Directory.CreateDirectory(tempDir);
                createdTempDir = true;
                File.Copy(onnxPath, assetPath, overwrite: true);
                AssetDatabase.ImportAsset(assetPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(assetPath);
                if (asset == null)
                {
                    error = "ONNX のインポートに失敗しました(Console のログを確認してください): " + onnxPath;
                    return null;
                }
                return ModelLoader.Load(asset);
            }
            finally
            {
                // 一時アセットは成功・失敗を問わず必ず消す(ユーザープロジェクトを汚さない)
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null)
                    AssetDatabase.DeleteAsset(assetPath);
                // 自分が作ったフォルダのときだけ消す（既存フォルダを巻き込まない）
                if (createdTempDir && AssetDatabase.IsValidFolder(tempDir))
                    AssetDatabase.DeleteAsset(tempDir);
            }
        }
    }
}
#endif
