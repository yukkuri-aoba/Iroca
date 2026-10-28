// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Iroca
{
    /// <summary>
    /// 新しい版の確認。VCC / ALCOM が読むのと同じ公開リスト(<see cref="ListingUrl"/>)を読み、
    /// 今の Unity で入れられるいろかの新しい版があるかを調べる。知らせるのは <see cref="UpdateNotice"/> の帯で、
    /// 更新そのものは VCC / ALCOM に任せる(プロジェクトのファイルは書き換えない)。
    /// <para>
    /// 読みに行くのはいろかのウィンドウを描いたときだけで、Unity を起動するごとに 1 回まで、さらに前回読めてから
    /// 1 日たつまでは読み直さない。失敗しても何も表示しない(オフラインの人に毎回エラーを見せない)。
    /// 送るのは公開ファイルへの GET だけで、利用者や作業内容の情報は含めない。Preferences の「いろか」でオフにできる。
    /// </para>
    /// <para>
    /// リストに版を載せた時点で、古い版の利用者に通知が出る。資産を付ける前に載せないこと(docs/RELEASING.md)。
    /// 版の <c>unity</c> 欄は「その版が入る Unity の最低版」として読むので、上げたら必ず直す。
    /// </para>
    /// </summary>
    internal static partial class UpdateCheck
    {
        public const string ListingUrl = "https://yukkuri-aoba.github.io/Iroca/index.json";
        public const string PackageId = "com.yukkuri-aoba.iroca";
        // 版をまたいで更新する人にも途中の変更が全部見えるよう、版ごとのリリースページでなく通しの変更履歴を開く。
        public const string ChangelogUrl = "https://github.com/yukkuri-aoba/Iroca/blob/main/CHANGELOG.md";

        const string EnabledKey = IrocaPrefs.UpdateCheckEnabled;
        const string DismissedKey = IrocaPrefs.UpdateCheckDismissed;
        // 結果は Unity の版(major.minor)と、先行版を含めるかごとに分けて覚える。同じ PC で Unity 2022 と 6 の
        // プロジェクトを開くと、入れられる最新版が違い得るため。
        const string LatestKeyPrefix = IrocaPrefs.UpdateCheckLatestPrefix;
        const string CheckedAtKeyPrefix = IrocaPrefs.UpdateCheckCheckedAtUtcPrefix;
        // この Unity の起動中にもう試したか(ドメインリロードをまたいで残る)。
        const string TriedThisSessionKey = IrocaPrefs.UpdateCheckTried;
        const int TimeoutSeconds = 20;

        static UnityWebRequest s_request;
        static bool? s_enabled;
        static string s_latest;
        static string s_dismissed;
        static string s_current;
        static bool s_currentResolved;

        public static bool Enabled
        {
            get => s_enabled ??= EditorPrefs.GetBool(EnabledKey, true);
            set
            {
                s_enabled = value;
                EditorPrefs.SetBool(EnabledKey, value);
                RepaintWindows();
            }
        }

        /// <summary>入っているいろかの版(package.json)。パッケージとして入っていなければ null。</summary>
        public static string CurrentVersion
        {
            get
            {
                if (!s_currentResolved)
                {
                    s_currentResolved = true;
                    try
                    {
                        s_current = UnityEditor.PackageManager.PackageInfo
                            .FindForAssembly(typeof(UpdateCheck).Assembly)?.version;
                    }
                    catch (Exception)
                    {
                        s_current = null;
                    }
                }
                return s_current;
            }
        }

        /// <summary>
        /// 知らせる版。オフ・版が分からない・新しい版が無い・その版(かそれより新しい版)を閉じた、のどれかなら null。
        /// OnGUI ごとに呼ばれるので EditorPrefs は最初の 1 回だけ読む。
        /// </summary>
        public static string AvailableVersion
        {
            get
            {
                if (!Enabled) return null;
                string current = CurrentVersion;
                if (current == null || !SemVer.TryParse(current, out var cur)) return null;
                s_latest ??= EditorPrefs.GetString(LatestKey(cur.IsPrerelease), "");
                if (!SemVer.TryParse(s_latest, out var latest) || latest.CompareTo(cur) <= 0) return null;
                s_dismissed ??= EditorPrefs.GetString(DismissedKey, "");
                if (SemVer.TryParse(s_dismissed, out var dismissed) && latest.CompareTo(dismissed) <= 0) return null;
                return s_latest;
            }
        }

        /// <summary>その版のお知らせを閉じる。もっと新しい版が出たらまた知らせる。</summary>
        public static void Dismiss(string version)
        {
            s_dismissed = version ?? "";
            EditorPrefs.SetString(DismissedKey, s_dismissed);
            RepaintWindows();
        }

        /// <summary>
        /// 読みに行く頃合いなら、裏で公開リストを読み始める。待たずに戻る。結果は次の描画から <see cref="AvailableVersion"/> に出る。
        /// </summary>
        public static void CheckIfDue()
        {
            if (s_request != null || !Enabled) return;
            if (SessionState.GetBool(TriedThisSessionKey, false)) return;
            SessionState.SetBool(TriedThisSessionKey, true);

            string current = CurrentVersion;
            if (current == null || !SemVer.TryParse(current, out var cur)) return;
            if (!IsDue(EditorPrefs.GetString(CheckedAtKey(cur.IsPrerelease), ""), DateTime.UtcNow)) return;

            s_request = UnityWebRequest.Get(ListingUrl);
            s_request.timeout = TimeoutSeconds;
            // 読み込み中にドメインリロードが来ると完了の通知が届かず、要求が宙に浮く。止めて、次の描画で試し直す。
            AssemblyReloadEvents.beforeAssemblyReload += AbortForReload;
            s_request.SendWebRequest().completed += _ => Finish(cur.IsPrerelease);
        }

        static void AbortForReload()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= AbortForReload;
            // 先に手放す。Abort の中で完了の通知(Finish)が走っても、二重に後始末しない。
            var request = s_request;
            s_request = null;
            if (request == null) return;
            request.Abort();
            request.Dispose();
            SessionState.SetBool(TriedThisSessionKey, false);
        }

        static void Finish(bool includePrerelease)
        {
            AssemblyReloadEvents.beforeAssemblyReload -= AbortForReload;
            var request = s_request;
            s_request = null;
            if (request == null) return;
            try
            {
                // 読めなかった・中身が壊れていたときは、前に覚えた結果をそのまま使う(次の起動で試し直す)。
                if (request.result != UnityWebRequest.Result.Success) return;
                if (!TryFindLatest(request.downloadHandler.text, PackageId, includePrerelease,
                        Application.unityVersion, out string latest))
                    return;
                s_latest = latest ?? "";
                EditorPrefs.SetString(LatestKey(includePrerelease), s_latest);
                EditorPrefs.SetString(CheckedAtKey(includePrerelease),
                    DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                if (AvailableVersion != null) RepaintWindows();
            }
            catch (Exception)
            {
                // お知らせのために色替えの作業を止めない。
            }
            finally
            {
                request.Dispose();
            }
        }

        static string UnityKey => UnityMajorMinor(Application.unityVersion);
        static string LatestKey(bool includePrerelease) => LatestKeyPrefix + UnityKey + (includePrerelease ? ":pre" : "");
        static string CheckedAtKey(bool includePrerelease) => CheckedAtKeyPrefix + UnityKey + (includePrerelease ? ":pre" : "");

        static void RepaintWindows() => EditingSessionRegistry.RepaintAll();
    }
}
