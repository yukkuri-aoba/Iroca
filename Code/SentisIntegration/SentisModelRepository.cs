// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_SENTIS_PRESENT
using System;
using System.IO;
#if IROCA_INFERENCE_ENGINE
using Unity.InferenceEngine;
#else
using Unity.Sentis;
#endif
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
    /// モデル置き場へキャッシュ → 一時アセット削除。2 回目以降は .sentis を
    /// ModelLoader.Load で直接ロードする(高速・Assets 非汚染)。
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
                    error = string.Format(Localization.AiErrModelMissingFormat, onnxPath);
                    return null;
                }
                string cachePath = CachePathFor(onnxPath);
                if (File.Exists(cachePath) &&
                    File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(onnxPath))
                {
                    var cached = TryLoadCache(cachePath, out bool locked, out string why);
                    if (cached != null)
                    {
                        MarkUsed(cachePath, onnxPath);
                        DeleteStaleCaches(onnxPath, cachePath);
                        DeleteLeftoverTempCaches(onnxPath);
                        return cached;
                    }
                    if (locked)
                    {
                        // 別の Unity がつかんだまま離さない。中身は壊れていないので消さずに、今回だけ変換し直す。
                        UnityEngine.Debug.LogWarning(
                            $"[Iroca] .sentis キャッシュを別のプログラムが使っていて開けないため、今回は ONNX から変換します: {cachePath}\n{why}");
                    }
                    else if (why != null)
                    {
                        // Save 中クラッシュ・空き容量枯渇等で壊れたキャッシュは「新しいタイムスタンプ」で
                        // 残り続け、従来はユーザーが手動で消すまで AI 機能が永続エラーだった。
                        // 削除して ONNX からの再変換にフォールバックする。
                        UnityEngine.Debug.LogWarning(
                            $"[Iroca] .sentis キャッシュの読込に失敗したため削除して再変換します: {cachePath}\n{why}");
                        try { File.Delete(cachePath); } catch { /* 削除失敗でも下の置き換えが上書きする */ }
                    }
                }

                var model = ConvertViaAssetPipeline(onnxPath, out error);
                if (model == null) return null;

                var loaded = SaveAndLoad(cachePath, model, out error);
                if (loaded == null) return null;
                DeleteStaleCaches(onnxPath, cachePath);
                DeleteLeftoverTempCaches(onnxPath);
                return loaded;
            }
            catch (Exception e)
            {
                error = string.Format(Localization.AiErrModelLoadFormat, e.Message);
                return null;
            }
        }

        // キャッシュは全プロジェクト共有のフォルダにあるので、別の Unity が同じファイルを読み書きしていることがある
        // (読み手どうしは共有で開けるが、旧版のいろかは共有なしで開く)。どちらも 1 秒前後で終わるので、
        // 開けないときは少し待って開き直す(合計 1.5 秒ほど)。
        static readonly int[] OpenRetryDelaysMs = { 50, 100, 200, 400, 750 };

        /// <summary>
        /// キャッシュを読む。読めなければ null で、locked = 別のプログラムが使っていて最後まで開けなかった
        /// (中身は壊れていない)、why = 理由(ファイルが無くなっていたときは null)。
        /// </summary>
        static Model TryLoadCache(string path, out bool locked, out string why)
        {
            locked = false;
            why = null;
            for (int attempt = 0; ; attempt++)
            {
                FileStream fs;
                try
                {
                    fs = OpenForRead(path);
                }
                catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
                {
                    return null;   // 開くまでの間に別の Unity が消した。変換し直せばよい
                }
                catch (Exception e) when (IsTransient(e))
                {
                    if (attempt < OpenRetryDelaysMs.Length)
                    {
                        System.Threading.Thread.Sleep(OpenRetryDelaysMs[attempt]);
                        continue;
                    }
                    locked = true;
                    why = e.Message;
                    return null;
                }
                try
                {
                    using (fs)
                    {
                        // 中身が壊れていると Sentis はエラーをログに出して null を返す(版によっては例外)。
                        var model = ModelLoader.Load(fs);
                        if (model == null) why = Localization.AiUnknownError;
                        return model;
                    }
                }
                catch (Exception e)
                {
                    why = e.Message;
                    return null;
                }
            }
        }

        // 読み手どうしはぶつからないように共有で開く。Delete も共有しておくと、別の Unity が変換し直した
        // キャッシュで置き換えるのも妨げない(開いている側は置き換え前の中身を最後まで読める)。
        static FileStream OpenForRead(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        // 共有違反・アクセス拒否(消しかけのファイル)は待てば直ることがある。パスの誤りなどは待っても直らない。
        static bool IsTransient(Exception e) =>
            e is UnauthorizedAccessException
            || (e is IOException
                && !(e is FileNotFoundException || e is DirectoryNotFoundException
                     || e is PathTooLongException || e is DriveNotFoundException));

        /// <summary>
        /// 変換したモデルをキャッシュへ保存し、保存したものを読み直して返す(キャッシュから読めることまで
        /// 今確かめる。次回起動時に壊れたキャッシュで失敗するより今失敗する方が診断しやすい)。
        /// 一時ファイルに書き切ってから置き換えるので、別の Unity が書きかけのキャッシュを読むことはない。
        /// 置き換えられなかった(別の Unity が古いキャッシュを使っている)ときも、読み直したモデルは使える。
        /// </summary>
        static Model SaveAndLoad(string cachePath, Model model, out string error)
        {
            error = null;
            Directory.CreateDirectory(CacheDir);
            // 一時ファイルは Unity ごとに分ける(同じ瞬間に 2 つの Unity が変換しても互いの書きかけを壊さない)。
            string tmp = cachePath + TempSuffix + System.Diagnostics.Process.GetCurrentProcess().Id;
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    ModelWriter.Save(fs, model);
                Model loaded;
                using (var fs = OpenForRead(tmp))
                    loaded = ModelLoader.Load(fs);
                if (loaded == null)
                {
                    error = string.Format(Localization.AiErrModelLoadFormat, cachePath);
                    return null;
                }
                ReplaceBestEffort(tmp, cachePath);
                return loaded;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 次の掃除で消す */ }
            }
        }

        const string TempSuffix = ".tmp.";

        static void ReplaceBestEffort(string tmp, string dst)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    // 試すたびに置き換え先の有無を見直す(同じ瞬間に別の Unity が作ったり消したりする)。
                    if (File.Exists(dst)) File.Replace(tmp, dst, null);
                    else File.Move(tmp, dst);
                    return;
                }
                catch (Exception e) when (IsTransient(e) && attempt < OpenRetryDelaysMs.Length)
                {
                    System.Threading.Thread.Sleep(OpenRetryDelaysMs[attempt]);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[Iroca] 変換した AI モデルをキャッシュに保存できませんでした（次に使うときにもう一度変換します）: {dst}\n{e.Message}");
                    return;
                }
            }
        }

        /// <summary>
        /// 保存の途中で Unity が落ちて残った一時ファイル(数十 MB)を消す。保存は数秒で終わるので、
        /// 1 日以上前のものは持ち主がもういない。失敗は無視する。
        /// </summary>
        static void DeleteLeftoverTempCaches(string onnxPath)
        {
            try
            {
                string stem = Path.GetFileNameWithoutExtension(onnxPath);
                foreach (string f in Directory.GetFiles(CacheDir, stem + ".*.sentis" + TempSuffix + "*"))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromDays(1)) continue;
                    try { File.Delete(f); } catch { /* 次の機会に消す */ }
                }
            }
            catch { /* CacheDir 不在・列挙失敗も無視 */ }
        }

        // ほかの Sentis 版のキャッシュは、これだけの日数使われていないものだけ消す。
        static readonly TimeSpan UnusedCacheLifetime = TimeSpan.FromDays(30);

        /// <summary>
        /// 読み込んだキャッシュの更新時刻を今にする(使った印。<see cref="DeleteStaleCaches"/> が見る)。
        /// 有効判定は「キャッシュの時刻 ≥ ONNX の時刻」なので、新しくしても判定は変わらない。ただし読んでいる間に
        /// ONNX が置き換わっていたら(ダウンロードし直した)、古いキャッシュを有効に見せてしまうので触らない。
        /// 失敗は無視する(別の Unity が読んでいる最中など。その Unity が読み終えたときに新しくする)。
        /// </summary>
        static void MarkUsed(string cachePath, string onnxPath)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(cachePath) < File.GetLastWriteTimeUtc(onnxPath)) return;
                File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
            }
            catch { /* 使った印が付かないだけ */ }
        }

        /// <summary>
        /// 同じ .onnx から作られたほかの Sentis 版の .sentis キャッシュのうち、長く使われていないものを削除する。
        ///
        /// キャッシュ名は &lt;stem&gt;.&lt;version&gt;.sentis で、Sentis の版が変わると別ファイルになる。
        /// 掃除しないと版を上げるたびに 45MB 級の孤児が %LOCALAPPDATA% に積み上がる。
        /// ただしこのフォルダは全プロジェクトで共有なので、ほかの版は別のプロジェクト(Unity 2022 の Sentis と
        /// Unity 6 の Inference Engine など)が今も使っていることがある。無条件に消すと、行き来するたびに
        /// 互いのキャッシュを消し合って変換し直していた。使うたびに更新時刻を新しくし(<see cref="MarkUsed"/>)、
        /// <see cref="UnusedCacheLifetime"/> より長く使われていない版だけを消す。
        /// 現行版を読み込めた後にだけ呼ぶこと。失敗は無視する(掃除は best-effort で、
        /// 消せなくても AI マスク提案の動作には影響しない)。
        /// </summary>
        static void DeleteStaleCaches(string onnxPath, string keepPath)
        {
            try
            {
                string stem = Path.GetFileNameWithoutExtension(onnxPath);
                foreach (string f in Directory.GetFiles(CacheDir, stem + ".*.sentis"))
                {
                    // GetFiles の検索パターンは環境によって拡張子の前方一致まで拾うことがあるため、
                    // 消す前に本当に .sentis かを明示的に確認する。
                    if (!f.EndsWith(".sentis", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(f, keepPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < UnusedCacheLifetime) continue;
                    try { File.Delete(f); }
                    catch { /* 別インスタンスが読込中などは掴まれたまま。次回の掃除で消せばよい */ }
                }
            }
            catch { /* CacheDir 不在・列挙失敗も無視 */ }
        }

        /// <summary>
        /// 前の変換の途中で Unity が落ちると、一時フォルダ(数十 MB の ONNX 入り)が Assets に残る。
        /// 名前が一時フォルダの形(接頭辞 + GUID 32 桁)で、中身がいろかのモデル(と .meta)だけのものに限って消す
        /// (ほかのファイルが入っていれば利用者のものとして触らない)。失敗しても変換は続ける。
        /// </summary>
        static void DeleteLeftoverTempDirs()
        {
            try
            {
                string prefix = Path.GetFileName(TempImportDirPrefix);
                foreach (string dir in Directory.GetDirectories(Application.dataPath, prefix + "*"))
                {
                    string name = Path.GetFileName(dir);
                    if (!IsTempDirName(name.Substring(prefix.Length))) continue;
                    if (Directory.GetDirectories(dir).Length > 0) continue;
                    bool onlyModels = true;
                    foreach (string f in Directory.GetFiles(dir))
                        onlyModels &= IsModelFileName(Path.GetFileName(f));
                    if (!onlyModels) continue;

                    string assetPath = "Assets/" + name;
                    if (AssetDatabase.IsValidFolder(assetPath))
                    {
                        AssetDatabase.DeleteAsset(assetPath);
                    }
                    else
                    {
                        Directory.Delete(dir, recursive: true);
                        if (File.Exists(dir + ".meta")) File.Delete(dir + ".meta");
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[Iroca] AI モデルの変換で残った一時フォルダを消せませんでした: {e.Message}");
            }
        }

        static bool IsTempDirName(string guidPart)
        {
            if (guidPart.Length != 32) return false;
            foreach (char c in guidPart)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        static bool IsModelFileName(string file)
        {
            foreach (string model in new[] { EncoderFileName, DecoderFileName })
                if (file == model || file == model + ".meta") return true;
            return false;
        }

        static Model ConvertViaAssetPipeline(string onnxPath, out string error)
        {
            error = null;
            DeleteLeftoverTempDirs();
            string tempDir = TempImportDirPrefix + Guid.NewGuid().ToString("N");
            string assetPath = tempDir + "/" + Path.GetFileName(onnxPath);
            bool createdTempDir = false;
            try
            {
                if (Directory.Exists(tempDir))
                {
                    // GUID 衝突は現実には起きないが、起きたなら他人のフォルダなので触らない。
                    error = string.Format(Localization.AiErrTempDirCollisionFormat, tempDir);
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
                    error = string.Format(Localization.AiErrOnnxImportFormat, onnxPath);
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
