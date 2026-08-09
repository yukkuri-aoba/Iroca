// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using UnityEditor;
using UnityEngine.Networking;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案モデル(ONNX ペア)のモデル専用リポジトリ(raw ブランチ取得)からのダウンロード。
    /// ユーザーの明示ボタン操作でのみ開始し、sha256 検証後に
    /// <see cref="MaskSuggestBridge.ModelsDirectory"/> へ配置する。
    /// ネットワーク不可・検証失敗時は部分ファイルを残さない(手動配置のフォールバックは
    /// フォルダを開くボタンと MANUAL の手順で案内する)。
    /// </summary>
    internal static class MaskSuggestModelDownload
    {
        // AI モデルは本体と分離した「モデル専用リポジトリ」の main ブランチ直下に置き、
        // raw で取得する。GitHub Release の儀式(タグ/資産アップロード)が不要で、main へ
        // push した時点で即配布される。
        // 配布先を変えるときは owner/repo をここだけ直す。
        // 注意: sha256 が本体側(下記 Files)に焼き込まれているため、モデルの差し替えは
        // 「モデルリポジトリへの push + 本体のハッシュ更新リリース」を必ずセットで行う。
        // モデルだけ差し替えると全既存クライアントがハッシュ不一致で失敗する(安全側だが配布は止まる)。
        public const string ModelRepo = "yukkuri-aoba/Iroca-Models"; // owner/repo
        public const string ModelBranch = "main";
        public const string BaseUrl =
            "https://raw.githubusercontent.com/" + ModelRepo + "/" + ModelBranch + "/";

        // export_meta.json (dev_safe/ml) で確定したエクスポート成果物のハッシュ。
        // リリースへは必ずこのハッシュのファイルをアップロードする。
        static readonly (string file, string sha256)[] Files =
        {
            ("mobile_sam_encoder.onnx", "25fb1f619027c2e81a7e54425c148e16e99990c8f0d83eefd29a0f4495a75865"),
            ("mobile_sam_decoder.onnx", "2196a2a4b153528d00383c7c6a363dd765725c10097bb06233917b171d8ceda4"),
        };

        static UnityWebRequest _request;
        static int _fileIndex = -1;

        // ストール検知: UnityWebRequest.timeout(全体時間)だと大きいモデルの正当な低速 DL まで
        // 切ってしまうため、「downloadedBytes が一定時間進まない」ことをタイムアウト条件にする。
        const double StallTimeoutSeconds = 30.0;
        static double _lastProgressTime;
        static ulong _lastProgressBytes;

        public static bool InProgress => _fileIndex >= 0;
        public static string Error { get; private set; }

        /// <summary>全体進捗 0..1(ファイル数で均等割り)。</summary>
        public static float Progress
        {
            get
            {
                if (!InProgress) return 0f;
                float cur = _request != null ? _request.downloadProgress : 0f;
                return (_fileIndex + UnityEngine.Mathf.Clamp01(cur)) / Files.Length;
            }
        }

        /// <summary>ダウンロードを開始する(進行中なら no-op)。完了/失敗は状態プロパティで観測する。</summary>
        public static void Start()
        {
            if (InProgress) return;
            Error = null;
            Directory.CreateDirectory(MaskSuggestBridge.ModelsDirectory);
            _fileIndex = 0;
            EditorApplication.update += Tick;
            // ドメインリロードで static 状態(進捗・購読)は消えるため、ネイティブの
            // UnityWebRequest と部分ファイルだけはリロード前に後始末する。これが無いと
            // ダウンロード中のスクリプトコンパイルでリクエストがリークし .download が残る。
            AssemblyReloadEvents.beforeAssemblyReload += CleanupBeforeReload;
            BeginCurrentFile();
        }

        /// <summary>進行中のダウンロードを中止する(部分ファイルは削除・エラー表示なし)。</summary>
        public static void Cancel()
        {
            if (!InProgress) return;
            string tmp = _fileIndex < Files.Length ? TempPath(Files[_fileIndex].file) : null;
            AbortRequest();
            Error = null;
            try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            Finish();
        }

        static void BeginCurrentFile()
        {
            var (file, _) = Files[_fileIndex];
            string tmp = TempPath(file);
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                _request = new UnityWebRequest(BaseUrl + file, UnityWebRequest.kHttpVerbGET)
                {
                    downloadHandler = new DownloadHandlerFile(tmp),
                };
                _request.SendWebRequest();
                _lastProgressBytes = 0;
                _lastProgressTime = EditorApplication.timeSinceStartup;
            }
            catch (System.Exception e)
            {
                // .download が別プロセス/AV にロックされている等。例外を漏らすと
                // InProgress=true のままリクエスト無しで固まるため、必ず Fail に落とす。
                Fail($"{file}: {e.Message}", tmp);
            }
        }

        static void Tick()
        {
            if (_request == null) return;

            if (!_request.isDone)
            {
                // ストール検知(進捗が止まったままの接続を打ち切る)
                ulong got = _request.downloadedBytes;
                if (got != _lastProgressBytes)
                {
                    _lastProgressBytes = got;
                    _lastProgressTime = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - _lastProgressTime > StallTimeoutSeconds)
                {
                    var (stalled, _) = Files[_fileIndex];
                    string stalledTmp = TempPath(stalled);
                    AbortRequest();
                    Fail($"{stalled}: {StallTimeoutSeconds:F0} 秒間応答がありません(接続を確認して再試行してください)", stalledTmp);
                }
                return;
            }

            var (file, sha) = Files[_fileIndex];
            string tmp = TempPath(file);
            bool ok = _request.result == UnityWebRequest.Result.Success;
            string netError = _request.error;
            _request.Dispose();
            _request = null;

            if (!ok)
            {
                Fail($"{file}: {netError}", tmp);
                return;
            }
            // ハッシュ検証〜配置は IO 例外(AV の一時ロック・別インスタンスとの競合等)が現実的に
            // 起きる区間。例外が Tick から漏れると _request=null のまま InProgress=true が永続し、
            // 進捗バーが出続けたままエラーも出ない詰み状態になるため、必ず Fail に落とす。
            try
            {
                string actual = Sha256Of(tmp);
                if (!string.Equals(actual, sha, System.StringComparison.OrdinalIgnoreCase))
                {
                    Fail($"{file}: sha256 が一致しません({actual})", tmp);
                    return;
                }
                string dst = Path.Combine(MaskSuggestBridge.ModelsDirectory, file);
                // 別インスタンスが同じ onnx をロード中かもしれない。Delete→Move だと
                // 「一瞬ファイルが存在しない」窓ができ、読み込み中のファイルを消すことにもなる。
                // Replace は原子的に差し替えるので、読み手は旧か新のどちらかを必ず見る
                // （ロックされていれば例外 → Fail に落ちる。黙って壊すより良い）。
                if (File.Exists(dst)) File.Replace(tmp, dst, null);
                else File.Move(tmp, dst);
            }
            catch (System.Exception e)
            {
                Fail($"{file}: {e.Message}", tmp);
                return;
            }

            _fileIndex++;
            if (_fileIndex >= Files.Length)
            {
                Finish();
                // 配置完了 → サービスにロードさせる(次の GUI フレームで反映)
                MaskSuggestBridge.Service?.TryEnsureModels();
            }
            else
            {
                BeginCurrentFile();
            }
        }

        static void Fail(string message, string tmpToDelete)
        {
            Error = message;
            try { if (File.Exists(tmpToDelete)) File.Delete(tmpToDelete); } catch { }
            Finish();
        }

        static void Finish()
        {
            _fileIndex = -1;
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= CleanupBeforeReload;
        }

        static void AbortRequest()
        {
            if (_request == null) return;
            try { _request.Abort(); } catch { }
            _request.Dispose();
            _request = null;
        }

        static void CleanupBeforeReload()
        {
            AbortRequest();
            if (_fileIndex >= 0 && _fileIndex < Files.Length)
            {
                try
                {
                    string tmp = TempPath(Files[_fileIndex].file);
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch { }
            }
        }

        // 一時ファイル名は Unity プロセスごとに分ける。モデル置き場は LOCALAPPDATA の
        // 全プロジェクト共有なので、"<file>.download" 固定だと、別インスタンスが落としている
        // 最中の一時ファイルを開始時の無条件削除で壊していた（レビュー §5 中）。
        // プロセス ID はドメインリロードを跨いでも同じなので、リロード時の後始末とも整合する。
        static readonly string TempSuffix =
            ".download." + System.Diagnostics.Process.GetCurrentProcess().Id;

        static string TempPath(string file) =>
            Path.Combine(MaskSuggestBridge.ModelsDirectory, file + TempSuffix);

        static string Sha256Of(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
