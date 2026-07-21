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
        const string TempImportDir = "Assets/IrocaModelImportTemp";

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
                    return ModelLoader.Load(cachePath);
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
            string assetPath = TempImportDir + "/" + Path.GetFileName(onnxPath);
            try
            {
                Directory.CreateDirectory(TempImportDir);
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
                if (AssetDatabase.IsValidFolder(TempImportDir))
                    AssetDatabase.DeleteAsset(TempImportDir);
            }
        }
    }
}
#endif
