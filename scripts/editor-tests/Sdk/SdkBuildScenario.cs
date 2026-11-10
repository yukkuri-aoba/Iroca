// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace Iroca.EditorTests.Sdk {
    /// <summary>
    /// VRChat SDK のビルド経路(SDK の検証・ビルド前コールバック・NDMF・アセットバンドル化・テクスチャ圧縮)と、Private でのアップロードを
    /// 通して、いろかの非破壊の色替えがビルド成果物に入ることを確かめる台本。<c>AvatarProcessor.ProcessAvatar</c> を直接呼ぶ
    /// NdmfProcessAvatarTests と Play に入る UndoReloadScenario では、この経路は通らない。
    /// SDK のパネル(IVRCSdkAvatarBuilderApi の持ち主)が要るので batchmode では動かない。UndoReloadScenario と同じく、ランナーなし・
    /// 画面あり(最小化)で起動し、環境変数があるときだけ動いて、結果を 1 行ずつ報告のファイルへ書き、終了コードで終わる
    /// (<c>Run-EditorTests.ps1 -SdkBuild</c> / <c>-SdkUpload</c>)。
    ///
    /// 環境変数:
    ///   IROCA_SDK_BUILD_REPORT        報告のファイル(これがあるときだけ動く)
    ///   IROCA_SDK_BUILD_MODE          build(既定: ビルドしてバンドルを読み戻す。アップロードしない) / upload(Private でアップロード)
    ///   IROCA_SDK_UPLOAD_STATE        upload の blueprint ID を覚える JSON のパス(公開リポの外。無ければ新規作成して書く)
    ///   IROCA_SDK_UPLOAD_WAIT_CONSENT 1 のとき、SDK が出す権利の同意の確認を人が押すのを待つ(下の「権利の同意」)
    ///   IROCA_SDK_BUILD_TIMEOUT_SEC   台本全体の制限時間(既定 1200 秒)
    ///
    /// 終了コード: 0=通った、1 以上=失敗の数、<see cref="SkipExit"/>=前提が足りず測っていない(失敗ではない。報告に skip の行が出る)。
    ///
    /// 権利の同意: SDK は BuildAndUpload の途中で「アップロードする権利を持っている」という同意(VRCCopyrightAgreement)を求め、
    /// Unity を起動するたびに 1 回、人が OK を押す作りになっている(サーバーの記録に加えて SessionState にも同意の印が要る)。
    /// ここでは同意を偽装しない(内部の口を呼ばない・SessionState を書かない)。同意が無ければ skip で止まる。
    /// 人が押す前提で回すなら WAIT_CONSENT=1 にして、SDK の出す確認を人が押す。
    /// </summary>
    [InitializeOnLoad]
    internal static class SdkBuildScenario {
        public const int SkipExit = 90;
        private const string Key = "Iroca.Tests.SdkBuild.";

        // 圧縮(DXT/BC7 など)の誤差の許容。バンドルのテクスチャを読み戻した色と、期待した色の最大チャンネル差(0〜255)。
        // 実測(2026-11-10・Unity 2022.3.22f1・Windows・既定の取り込み設定で DXT1 に圧縮)では、平らな 2 面とも最大差 1。
        // 平らな面は DXT1 の 4x4 ブロックの端点の量子化(5-6-5 ビット)の誤差だけで、1〜数段階に収まる。許容 24 は、
        // BC7・ASTC など別の圧縮や、sRGB の往復の丸めで数段階ずれても落とさず、色替えが入っていない(元との差 170 前後)ことは
        // 確実に見分けられる幅(実測の約 20 倍・色替えの差の 1/7)。細かい模様のテクスチャにはこの比べ方は使わない(平らな面だけ)。
        private const int ToleranceChannels = 24;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static int _fails;
        private static bool _finished;
        private static SdkBuildFixture _fixture;
        private static string _bundlePath;
        private static string _tempDir;
        private static AssetBundle _bundle;

        private static string Report => Environment.GetEnvironmentVariable("IROCA_SDK_BUILD_REPORT");
        private static bool UploadMode => string.Equals(Environment.GetEnvironmentVariable("IROCA_SDK_BUILD_MODE"), "upload", StringComparison.OrdinalIgnoreCase);

        static SdkBuildScenario() {
            if (string.IsNullOrEmpty(Report)) {
                return;
            }
            EditorApplication.update -= WaitAndRun;
            EditorApplication.update += WaitAndRun;
        }

        private static void WaitAndRun() {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) {
                return;
            }
            EditorApplication.update -= WaitAndRun;
            if (SessionState.GetBool(Key + "started", false)) {
                // 途中でスクリプトが再読み込みされた(台本は続きから進まない)。
                _fails++;
                Line("FAIL 台本の途中でドメインが再読み込みされた(アセットの取り込みやコンパイルが走った?)");
                _finished = true;
                SessionState.SetBool(Key + "started", false);
                EditorApplication.Exit(_fails);
                return;
            }
            SessionState.SetBool(Key + "started", true);
            Clock.Restart();
            EditorApplication.update += Watchdog;
            Run();
        }

        private static void Watchdog() {
            double limit;
            if (!double.TryParse(Environment.GetEnvironmentVariable("IROCA_SDK_BUILD_TIMEOUT_SEC"), out limit) || limit <= 0) {
                limit = 1200;
            }
            if (!_finished && Clock.Elapsed.TotalSeconds > limit) {
                Fail($"制限時間({limit} 秒)を超えた(モーダルのダイアログや、サーバーの応答待ちで止まった?)");
                Exit();
            }
        }

        private static async void Run() {
            try {
                Line($"start mode={(UploadMode ? "upload" : "build")} unity={Application.unityVersion}");
                await Scenario();
            } catch (SkipException skip) {
                if (_finished) {
                    return;
                }
                Line("SKIP " + skip.Message);
                SafeCleanup();
                Line($"done skipped=1 ({Clock.Elapsed.TotalSeconds:F0}s)");
                _finished = true;
                SessionState.SetBool(Key + "started", false);
                EditorApplication.Exit(SkipExit);
                return;
            } catch (Exception ex) {
                Fail("例外: " + Brief(ex));
            }
            Exit();
        }

        private sealed class SkipException : Exception {
            public SkipException(string message) : base(message) { }
        }

        private static string Brief(Exception ex) {
            string s = ex.GetType().FullName + ": " + ex.Message;
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException) {
                s += " <- " + inner.GetType().Name + ": " + inner.Message;
            }
            string stack = ex.StackTrace ?? "";
            if (stack.Length > 600) {
                stack = stack.Substring(0, 600);
            }
            return (s + " | " + stack).Replace("\r", "").Replace("\n", " / ");
        }

        private static void Line(string s) {
            File.AppendAllText(Report, s + "\n");
        }

        private static void Fail(string s) {
            _fails++;
            Line("FAIL " + s);
        }

        private static void Check(bool ok, string what) {
            if (ok) {
                Line("ok   " + what);
            } else {
                Fail(what);
            }
        }

        private static void Exit() {
            if (_finished) {
                return;
            }
            _finished = true;
            SafeCleanup();
            Line($"done fails={_fails} ({Clock.Elapsed.TotalSeconds:F0}s)");
            SessionState.SetBool(Key + "started", false);
            EditorApplication.Exit(_fails);
        }

        private static void SafeCleanup() {
            try {
                Cleanup();
            } catch (Exception ex) {
                Line("warn cleanup: " + ex.Message);
            }
        }

        private static void Cleanup() {
            if (_bundle != null) {
                _bundle.Unload(true);
                _bundle = null;
            }
            if (VRCSdkControlPanel.window != null) {
                VRCSdkControlPanel.window.Close();
            }
            if (_fixture != null) {
                // 一時アセットのシーンを開いたままフォルダを消さない(空のシーンへ替えてから消す)。
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                _fixture.Dispose();
                _fixture = null;
            }
            if (!string.IsNullOrEmpty(_bundlePath) && File.Exists(_bundlePath)
                && Path.GetFullPath(_bundlePath).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) {
                File.Delete(_bundlePath);
            }
            if (_tempDir != null && Directory.Exists(_tempDir)) {
                Directory.Delete(_tempDir, true);
            }
            AssetDatabase.Refresh();
        }

        private static async Task<bool> WaitFor(Func<bool> condition, double seconds) {
            var sw = Stopwatch.StartNew();
            while (!condition()) {
                if (sw.Elapsed.TotalSeconds > seconds) {
                    return false;
                }
                await Task.Delay(200);
            }
            return true;
        }

        // ─────────────── 台本 ───────────────

        private static async Task Scenario() {
            bool upload = UploadMode;
            _tempDir = Path.Combine(Path.GetTempPath(), "iroca-sdk-smoke-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(_tempDir);

            // 合成アバター。シーンは空の新しいものにする(ホストの開いていたシーンを巻き込まない)。
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _fixture = SdkBuildFixture.Create();
            var go = _fixture.Avatar;
            // SDK の検証を通る最小の形: Animator(無いと「This avatar does not contain an Animator」で検証が落ちる)・
            // VRCAvatarDescriptor(視点の位置)・PipelineManager。
            if (go.GetComponent<Animator>() == null) {
                go.AddComponent<Animator>();
            }
            var descriptor = go.AddComponent<VRCAvatarDescriptor>();
            descriptor.ViewPosition = new Vector3(0, 0.6f, 0.1f);
            var pm = go.GetComponent<PipelineManager>();
            if (pm == null) {
                pm = go.AddComponent<PipelineManager>();
            }
            // シーンを一時アセットとして保存しておく。名前の無いシーンのままだと、ビルドの途中でシーンを保存する道具
            // (SDK・ビルド前コールバック)があったときに「名前を付けて保存」の画面が出て、人が押すまで止まる。
            bool saved = EditorSceneManager.SaveScene(go.scene, _fixture.Folder + "/SdkSmoke.unity");
            Check(saved, "合成アバターのシーンを一時アセットとして保存できた");
            Line($"fixture: samples original={Fmt(_fixture.Original)} expected={Fmt(_fixture.Expected)}");

            // SDK のパネルを開く(IVRCSdkAvatarBuilderApi はパネルが持つ)。
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            if (!await WaitFor(() => VRCSdkControlPanel.window != null, 90)) {
                throw new SkipException("SDK のパネルが開かない(リモート設定の取得に失敗?ネットワークを確かめる)");
            }
            IVRCSdkAvatarBuilderApi builder = null;
            for (int i = 0; i < 10 && builder == null; i++) {
                if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out builder)) {
                    await Task.Delay(1000);
                }
            }
            Check(builder != null, "SDK のパネルから IVRCSdkAvatarBuilderApi を取れた");
            if (builder == null) {
                return;
            }

            // パネルを開くと、保存済みの資格情報での自動ログインが非同期に走る。少し待って判定する。
            bool loggedIn = await WaitFor(() => APIUser.IsLoggedIn && APIUser.CurrentUser != null, upload ? 60 : 20);
            Line($"login: {(loggedIn ? "ログイン済み" : "ログインしていない")}");
            if (upload) {
                if (!loggedIn) {
                    throw new SkipException("SDK にログインしていない(Unity の VRChat SDK パネルで人がログインしておく。資格情報はこの台本では扱わない)");
                }
                if (!APIUser.CurrentUser.canPublishAvatars) {
                    throw new SkipException("ログイン中のユーザーにアバターを公開する権限が無い(canPublishAvatars=false)");
                }
            }

            if (!upload) {
                await BuildStage(builder, go);
            } else {
                await UploadStage(builder, go, pm);
            }
        }

        private static string Fmt(Color32[] c) {
            var parts = new List<string>();
            foreach (var x in c) {
                parts.Add($"({x.r},{x.g},{x.b})");
            }
            return string.Join("", parts);
        }

        // ─────────────── 段階 2: ビルドして読み戻す ───────────────

        private static async Task BuildStage(IVRCSdkAvatarBuilderApi builder, GameObject go) {
            var sw = Stopwatch.StartNew();
            _bundlePath = await builder.Build(go);
            Line($"build: {sw.Elapsed.TotalSeconds:F1}s -> {_bundlePath}");
            Check(!string.IsNullOrEmpty(_bundlePath) && File.Exists(_bundlePath), "(a) Build が例外なく終わり、バンドルのファイルができた");
            if (string.IsNullOrEmpty(_bundlePath) || !File.Exists(_bundlePath)) {
                return;
            }
            Line($"bundle: {new FileInfo(_bundlePath).Length} bytes");
            VerifyBundle(_bundlePath);
            var changes = _fixture.FindChanges();
            Check(changes.Count == 0, "(d) 元のマテリアル・テクスチャのアセットとシーン上のアバターがビルドの後も変わっていない"
                + (changes.Count > 0 ? ": " + string.Join(" / ", changes) : ""));
        }

        private static void VerifyBundle(string path) {
            _bundle = AssetBundle.LoadFromFile(path);
            Check(_bundle != null, "バンドルを AssetBundle.LoadFromFile で読めた");
            if (_bundle == null) {
                return;
            }
            GameObject avatar = null;
            foreach (var g in _bundle.LoadAllAssets<GameObject>()) {
                if (g.GetComponentInChildren<Renderer>(true) != null) {
                    avatar = g;
                    break;
                }
            }
            Check(avatar != null, "バンドルの中にレンダラーつきのアバターがある");
            if (avatar == null) {
                return;
            }

            // (c) いろかのコンポーネントが残っていない。読み込めないスクリプト(null)も数える(消し忘れの疑い)。
            int leftovers = 0, missing = 0;
            foreach (var c in avatar.GetComponentsInChildren<Component>(true)) {
                if (c == null) {
                    missing++;
                } else if (c.GetType().Namespace != null && c.GetType().Namespace.StartsWith("Iroca", StringComparison.Ordinal)) {
                    leftovers++;
                }
            }
            Check(leftovers == 0 && missing == 0, $"(c) バンドルの中のアバターにいろかのコンポーネントが残っていない(残り {leftovers}・読めないスクリプト {missing})");

            // (b) テクスチャが元でなく、いろかの色替え結果になっている。
            var renderer = avatar.GetComponentInChildren<Renderer>(true);
            var tex = renderer.sharedMaterial != null ? renderer.sharedMaterial.mainTexture : null;
            Check(tex != null, "(b) バンドルのマテリアルにテクスチャがある");
            if (tex == null) {
                return;
            }
            Line($"texture: {tex.name} {tex.width}x{tex.height} {(tex is Texture2D t2 ? t2.format.ToString() : tex.GetType().Name)}");
            var got = ReadMeans(tex, SdkBuildFixture.SampleUv);
            int worstChanged = MaxDiff(got[0], _fixture.Expected[0]);
            int worstSame = MaxDiff(got[1], _fixture.Expected[1]);
            int fromOriginal = MaxDiff(got[0], _fixture.Original[0]);
            Line($"diff: 色替えした面 期待との最大差={worstChanged} 元との最大差={fromOriginal} / 対象外の面 期待との最大差={worstSame}"
                + $" (読み戻し {Fmt(got)}、許容 {ToleranceChannels})");
            Check(worstChanged <= ToleranceChannels, $"(b) 色替えした面が、レシピから作った期待の色に圧縮の誤差の範囲で合う(差 {worstChanged})");
            Check(fromOriginal > ToleranceChannels * 2, $"(b) 色替えした面が元の色のままではない(元との差 {fromOriginal})");
            Check(worstSame <= ToleranceChannels, $"(b) 対象外の面は元の色のまま(差 {worstSame})");
        }

        private static int MaxDiff(Color32 a, Color32 b) {
            return Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
        }

        // テクスチャを GPU で画素にして(圧縮されていて読めなくても読める)、UV の位置の 8x8 の平均を返す。
        private static Color32[] ReadMeans(Texture tex, Vector2[] uvs) {
            int w = tex.width, h = tex.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var px = read.GetPixels32();
                var result = new Color32[uvs.Length];
                for (int i = 0; i < uvs.Length; i++) {
                    int cx = (int)(uvs[i].x * w), cy = (int)(uvs[i].y * h);
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int y = cy - 4; y < cy + 4; y++) {
                        for (int x = cx - 4; x < cx + 4; x++) {
                            var p = px[Mathf.Clamp(y, 0, h - 1) * w + Mathf.Clamp(x, 0, w - 1)];
                            r += p.r;
                            g += p.g;
                            b += p.b;
                            n++;
                        }
                    }
                    result[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
                return result;
            } finally {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(read);
            }
        }

        // ─────────────── 段階 3: Private でアップロード ───────────────

        [Serializable]
        private sealed class UploadState {
            public string avatarId;
        }

        private static async Task UploadStage(IVRCSdkAvatarBuilderApi builder, GameObject go, PipelineManager pm) {
            string statePath = Environment.GetEnvironmentVariable("IROCA_SDK_UPLOAD_STATE");
            if (string.IsNullOrEmpty(statePath)) {
                throw new SkipException("IROCA_SDK_UPLOAD_STATE(blueprint ID を覚える JSON のパス)が無い");
            }
            var state = new UploadState();
            if (File.Exists(statePath)) {
                state = JsonUtility.FromJson<UploadState>(File.ReadAllText(statePath)) ?? new UploadState();
            }
            string id = state.avatarId;
            Line($"state: {(string.IsNullOrEmpty(id) ? "blueprint ID なし(新規に作る)" : "blueprint ID を使い回す " + id)}");

            // 権利の同意。SDK は Unity を起動するたびに人の OK を求める。同意の印(SessionState)が無ければ、偽装せずに止まる。
            bool waitConsent = Environment.GetEnvironmentVariable("IROCA_SDK_UPLOAD_WAIT_CONSENT") == "1";
            const string consentList = "VRCSdkControlPanel.CopyrightAgreement.ContentList";
            bool sessionAgreed = !string.IsNullOrEmpty(id)
                && new List<string>((SessionState.GetString(consentList, "") ?? "").Split(';')).Contains(id);
            if (!string.IsNullOrEmpty(id)) {
                try {
                    var server = await VRCApi.CheckContentUploadConsent(new VRCAgreementCheckRequest {
                        AgreementCode = "content.copyright.owned", ContentId = id, Version = 1,
                    });
                    Line($"consent: サーバーの記録 agreed={server.Agreed} / この Unity の同意の印={sessionAgreed}");
                } catch (Exception ex) {
                    Line("consent: サーバーの記録を確かめられない: " + ex.Message);
                }
            }
            if (!sessionAgreed) {
                if (!waitConsent) {
                    throw new SkipException("権利の同意(content.copyright.owned)が要る。SDK は Unity を起動するたびに人の OK を求めるので、"
                        + "自動では通せない(同意の偽装はしない)。人が OK を押せるときは IROCA_SDK_UPLOAD_WAIT_CONSENT=1(-SdkUploadWaitConsent)で回す");
                }
                Line("consent: SDK の「Copyright ownership agreement」の確認が出る。人が内容を読んで OK を押す(押さなければ制限時間で失敗)");
            }

            string thumb = Path.Combine(_tempDir, "thumb.png");
            WriteThumbnail(thumb);
            if (!string.IsNullOrEmpty(id)) {
                pm.blueprintId = id;
            }
            var startedUtc = DateTime.UtcNow;
            var avatar = new VRCAvatar {
                ID = id,
                Name = "Iroca SDK smoke",
                Description = "Automated smoke test of a recolor build. Not a real avatar.",
                ReleaseStatus = "private",
                Tags = new List<string>(),
            };
            var sw = Stopwatch.StartNew();
            await builder.BuildAndUpload(go, avatar, thumb);
            Line($"upload: {sw.Elapsed.TotalSeconds:F1}s");

            string newId = pm.blueprintId;
            Check(!string.IsNullOrEmpty(newId), "アップロードの後、PipelineManager に blueprint ID が入っている");
            if (string.IsNullOrEmpty(newId)) {
                return;
            }
            if (newId != id) {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath)));
                File.WriteAllText(statePath, JsonUtility.ToJson(new UploadState { avatarId = newId }));
                Line($"state: blueprint ID を {statePath} へ書いた({newId})");
            }

            var remote = await VRCApi.GetAvatar(newId, forceRefresh: true);
            Line($"remote: id={remote.ID} release={remote.ReleaseStatus} version={remote.Version} updated={remote.UpdatedAt:o} author={remote.AuthorId}");
            Check(remote.ID == newId, "サーバーにその ID のアバターがある");
            Check(string.Equals(remote.ReleaseStatus, "private", StringComparison.OrdinalIgnoreCase), $"サーバー側で Private(実際 {remote.ReleaseStatus})");
            Check(remote.AuthorId == APIUser.CurrentUser.id, "作者がログイン中のユーザー");
            // 端末とサーバーの時計のずれを 2 分まで許す。
            Check(remote.UpdatedAt.ToUniversalTime() >= startedUtc.AddMinutes(-2), "サーバー側で今回の更新が反映されている(UpdatedAt)");
            Check(!string.IsNullOrEmpty(remote.GetLatestAssetUrlForCurrentPlatform()), "この PC のプラットフォーム向けのバンドルの URL が付いている");
            var changes = _fixture.FindChanges();
            Check(changes.Count == 0, "元のアセットとシーン上のアバターが変わっていない" + (changes.Count > 0 ? ": " + string.Join(" / ", changes) : ""));
        }

        private static void WriteThumbnail(string path) {
            var t = new Texture2D(512, 512, TextureFormat.RGB24, false);
            try {
                var px = new Color32[512 * 512];
                for (int y = 0; y < 512; y++) {
                    for (int x = 0; x < 512; x++) {
                        px[y * 512 + x] = x < 256 ? new Color32(30, 150, 60, 255) : new Color32(40, 40, 200, 255);
                    }
                }
                t.SetPixels32(px);
                File.WriteAllBytes(path, t.EncodeToPNG());
            } finally {
                UnityEngine.Object.DestroyImmediate(t);
            }
        }
    }
}
