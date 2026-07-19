// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using UnityEditor;
using UnityEngine.Networking;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案モデル(ONNX ペア)の GitHub Release からのダウンロード。
    /// ユーザーの明示ボタン操作でのみ開始し、sha256 検証後に
    /// <see cref="MaskSuggestBridge.ModelsDirectory"/> へ配置する。
    /// ネットワーク不可・検証失敗時は部分ファイルを残さない(手動配置のフォールバックは
    /// フォルダを開くボタンと MANUAL の手順で案内する)。
    /// </summary>
    internal static class MaskSuggestModelDownload
    {
        // 配布リリース(モデルはバージョン独立のタグで配布し、本体リリースと分離する)
        public const string ReleaseTag = "models-mobilesam-v1";
        public const string BaseUrl =
            "https://github.com/yukkuri-aoba/Iroca/releases/download/" + ReleaseTag + "/";

        // export_meta.json (dev_safe/ml) で確定したエクスポート成果物のハッシュ。
        // リリースへは必ずこのハッシュのファイルをアップロードする。
        static readonly (string file, string sha256)[] Files =
        {
            ("mobile_sam_encoder.onnx", "25fb1f619027c2e81a7e54425c148e16e99990c8f0d83eefd29a0f4495a75865"),
            ("mobile_sam_decoder.onnx", "2196a2a4b153528d00383c7c6a363dd765725c10097bb06233917b171d8ceda4"),
        };

        static UnityWebRequest _request;
        static int _fileIndex = -1;

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
            BeginCurrentFile();
        }

        static void BeginCurrentFile()
        {
            var (file, _) = Files[_fileIndex];
            string tmp = TempPath(file);
            if (File.Exists(tmp)) File.Delete(tmp);
            _request = new UnityWebRequest(BaseUrl + file, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DownloadHandlerFile(tmp),
            };
            _request.SendWebRequest();
        }

        static void Tick()
        {
            if (_request == null || !_request.isDone) return;

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
            string actual = Sha256Of(tmp);
            if (!string.Equals(actual, sha, System.StringComparison.OrdinalIgnoreCase))
            {
                Fail($"{file}: sha256 が一致しません({actual})", tmp);
                return;
            }
            string dst = Path.Combine(MaskSuggestBridge.ModelsDirectory, file);
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(tmp, dst);

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
        }

        static string TempPath(string file) =>
            Path.Combine(MaskSuggestBridge.ModelsDirectory, file + ".download");

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
