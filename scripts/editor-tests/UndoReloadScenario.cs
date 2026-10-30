// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// スクリプトの再読み込み・Play をまたいだときの編集と Undo の確認。
    /// Unity は再読み込みのときに、スクリプトのオブジェクト(ウィンドウ・ScriptableObject)を対象にした Undo を捨てる
    /// (シーンの物の Undo だけが残る。2026-10-30 に実験で確かめた)。なので、それより前のいろかの編集は Ctrl+Z で戻せない。
    /// ここで見るのは、編集内容(マスク・ゾーン)が失われないこと、空振りの Ctrl+Z で何も変わらないこと、再読み込みの後の
    /// 編集は普通に取り消せること。Unity の振る舞いが変わって履歴が残るようになったら、ここが落ちて分かる。
    /// テストランナーの中と batchmode では、シーンの物の Undo も含めて履歴が空になるので、[UnityTest] では見られない。
    /// そこでランナーを使わずに、再読み込みのたびに続きから進む台本にした。環境変数 IROCA_UNDO_RELOAD_REPORT があるときだけ
    /// 動く(<c>Run-EditorTests.ps1 -UndoReload</c>)。結果は 1 行ずつ報告のファイルへ書き、失敗の数を終了コードにする。
    /// </summary>
    [InitializeOnLoad]
    internal static class UndoReloadScenario
    {
        private const string Key = "Iroca.Tests.UndoReload.";
        private const int W = 4, H = 4;

        private static string Report => Environment.GetEnvironmentVariable("IROCA_UNDO_RELOAD_REPORT");

        static UndoReloadScenario()
        {
            if (string.IsNullOrEmpty(Report)) return;
            // 窓の OnEnable とエディタの更新が済んでから進める。Play の前後は、切り替えが終わるまで待つ。
            EditorApplication.update -= WaitAndRun;
            EditorApplication.update += WaitAndRun;
        }

        private static void WaitAndRun()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            bool wantPlay = SessionState.GetBool(Key + "wantPlay", false);
            if (EditorApplication.isPlaying != wantPlay) return;
            EditorApplication.update -= WaitAndRun;
            RunStep();
        }

        private static IrocaWindow Win =>
            EditorUtility.InstanceIDToObject(SessionState.GetInt(Key + "win", 0)) as IrocaWindow;

        // 手順。どれも最後に再読み込みか Play の切り替えを頼む(最後の手順は終わる)。
        private static readonly Action[] Steps =
        {
            BeforeReload,
            AfterReload,
            InPlayMode,
            AfterPlayMode,
        };

        private static void RunStep()
        {
            int step = SessionState.GetInt(Key + "step", 0);
            SessionState.SetInt(Key + "step", step + 1);
            try
            {
                Line($"step {step}: {Steps[step].Method.Name}");
                Steps[step]();
            }
            catch (Exception ex)
            {
                Fail($"step {step}: {ex}");
                Finish();
            }
        }

        private static void Line(string s) => File.AppendAllText(Report, s + "\n");

        private static void Fail(string s)
        {
            SessionState.SetInt(Key + "fails", SessionState.GetInt(Key + "fails", 0) + 1);
            Line("FAIL " + s);
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Line("ok   " + what);
            else Fail(what);
        }

        private static void Finish()
        {
            Cleanup();
            int fails = SessionState.GetInt(Key + "fails", 0);
            Line($"done fails={fails}");
            EditorApplication.Exit(fails);
        }

        private static void Cleanup()
        {
            var win = Win;
            if (win != null) UnityEngine.Object.DestroyImmediate(win);

            string prefKey = IrocaWindow.LastTextureGuidPrefKey;
            if (SessionState.GetBool(Key + "hadPref", false)) EditorPrefs.SetString(prefKey, SessionState.GetString(Key + "pref", ""));
            else EditorPrefs.DeleteKey(prefKey);

            string guid = SessionState.GetString(Key + "guid", "");
            if (guid.Length > 0)
                foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, guid + "*")) File.Delete(f);
                }
            string folder = SessionState.GetString(Key + "folder", "");
            if (folder.Length > 0) AssetDatabase.DeleteAsset(folder);
        }

        // ブラシの 1 ストローク(押し始めの記録 → 塗る → 離す)。操作ごとに Undo グループを分ける。
        private static void Stroke(int pixel)
        {
            var win = Win;
            Undo.IncrementCurrentGroup();
            win._maskView.BeginStroke();
            win.Editing.Masks.WritableCommon().Set(pixel, true);
            win._maskView.EndStroke();
            win.Editing.NotifyChanged(EditChange.Masks);
            Undo.IncrementCurrentGroup();
        }

        private static void CheckCommon(string because, params int[] pixels)
        {
            var common = Win.Editing.Masks.Common;
            var got = new List<int>();
            for (int i = 0; i < W * H; i++)
                if (common != null && common.Get(i)) got.Add(i);
            Check(string.Join(",", got) == string.Join(",", pixels),
                $"{because}: 共通のマスク 期待 [{string.Join(",", pixels)}] 実際 [{string.Join(",", got)}]");
        }

        private static void CheckZones(string because, int count)
        {
            int got = Win.Session.zones.Count;
            Check(got == count, $"{because}: ゾーン 期待 {count} 実際 {got}");
        }

        // ─────────────── 手順 ───────────────

        private static void BeforeReload()
        {
            var assets = TestAssets.Create();
            string tex = assets.WritePng("tex", TestAssets.Solid(W, H, new Color32(200, 50, 50, 255)), W, H);
            string guid = AssetDatabase.AssetPathToGUID(tex);
            SessionState.SetString(Key + "folder", assets.Folder);
            SessionState.SetString(Key + "guid", guid);

            // ウィンドウは開いたときに「前回のテクスチャ」を読むので、テスト用のテクスチャを指しておく。
            string prefKey = IrocaWindow.LastTextureGuidPrefKey;
            SessionState.SetBool(Key + "hadPref", EditorPrefs.HasKey(prefKey));
            SessionState.SetString(Key + "pref", EditorPrefs.GetString(prefKey, ""));
            EditorPrefs.SetString(prefKey, guid);

            var win = ScriptableObject.CreateInstance<IrocaWindow>();
            SessionState.SetInt(Key + "win", win.GetInstanceID());
            Check(win.SourceTexture != null && AssetDatabase.GetAssetPath(win.SourceTexture) == tex, "窓がテスト用のテクスチャを開いた");
            win.Editing.Masks.Reset(W, H);
            Undo.ClearAll();

            Stroke(1);
            Undo.IncrementCurrentGroup();
            win.Editing.AddZone();
            Undo.IncrementCurrentGroup();
            Stroke(2);
            CheckCommon("再読み込みの前", 1, 2);
            CheckZones("再読み込みの前", 1);
            Check(UndoRecords().Count == 3, "再読み込みの前は 3 回分の履歴がある");

            EditorUtility.RequestScriptReload();
            EditorApplication.update += WaitAndRun;
        }

        // Undo の履歴の名前(Unity の internal の口。結果の読み解き用)。
        private static List<string> UndoRecords()
        {
            var undo = new List<string>();
            var m = typeof(Undo).GetMethod("GetRecords",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, new[] { typeof(List<string>), typeof(List<string>) }, null);
            m?.Invoke(null, new object[] { undo, new List<string>() });
            return undo;
        }

        private static void AfterReload()
        {
            Line("     履歴: " + string.Join(" | ", UndoRecords()));
            Check(Win != null, "再読み込みで窓が残る");
            CheckCommon("再読み込みの後", 1, 2);
            CheckZones("再読み込みの後", 1);

            Check(UndoRecords().Count == 0, "Unity が再読み込みの前の編集の Undo を捨てている");
            Undo.PerformUndo();
            CheckCommon("空振りの Ctrl+Z", 1, 2);
            CheckZones("空振りの Ctrl+Z", 1);

            Stroke(3);
            CheckCommon("再読み込みの後のストローク", 1, 2, 3);
            Undo.PerformUndo();
            CheckCommon("再読み込みの後のストロークを戻す", 1, 2);
            Undo.PerformRedo();
            CheckCommon("やり直す", 1, 2, 3);
            Undo.PerformUndo();

            SessionState.SetBool(Key + "wantPlay", true);
            EditorApplication.EnterPlaymode();
            EditorApplication.update += WaitAndRun;
        }

        private static void InPlayMode()
        {
            CheckCommon("Play を始めた後", 1, 2);
            CheckZones("Play を始めた後", 1);
            Stroke(4);
            CheckCommon("Play 中のストローク", 1, 2, 4);
            Undo.PerformUndo();
            CheckCommon("Play 中に戻す", 1, 2);
            Undo.PerformRedo();
            CheckCommon("Play 中にやり直す", 1, 2, 4);

            SessionState.SetBool(Key + "wantPlay", false);
            EditorApplication.ExitPlaymode();
            EditorApplication.update += WaitAndRun;
        }

        private static void AfterPlayMode()
        {
            CheckCommon("Play を止めた後", 1, 2, 4);
            CheckZones("Play を止めた後", 1);
            Stroke(5);
            Undo.PerformUndo();
            CheckCommon("Play を止めた後に戻す", 1, 2, 4);
            Finish();
        }
    }
}
