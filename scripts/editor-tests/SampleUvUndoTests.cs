// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// スポイト位置（ColorZone.sampleUV）が Undo/Redo で sampleColor と対で戻るか。
    /// 位置は自動調整が AI 提案の証拠を取るアンカーで、消えると次の自動調整が黙って
    /// 「スポイト位置なし」の導出へ落ちる。以前は Undo の外に置いて色の食い違いで
    /// 無効化していたため、Ctrl+Y で色だけ戻って位置が失われていた。
    /// Undo の書き戻しは Unity のシリアライザが行うので、実機でしか検査できない。
    /// </summary>
    public class SampleUvUndoTests
    {
        private static readonly Color Red = new Color(0.8f, 0.2f, 0.2f);
        private static readonly Color Blue = new Color(0.2f, 0.3f, 0.9f);
        private static readonly Vector2 Uv1 = new Vector2(0.25f, 0.75f);
        private static readonly Vector2 Uv2 = new Vector2(0.6f, 0.1f);

        private SampleUvUndoHost _host;

        [SetUp]
        public void SetUp()
        {
            _host = ScriptableObject.CreateInstance<SampleUvUndoHost>();
            var z = new ColorZone { name = "服" };
            z.EnsureId();
            _host.session.zones.Add(z);
            Undo.ClearUndo(_host);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearUndo(_host);
            Object.DestroyImmediate(_host);
        }

        // 毎回リストから引き直す（Undo の書き戻しでインスタンスが作り直されても追える）。
        private ColorZone Zone => _host.session.zones[0];

        // 操作ごとに Undo グループを分ける（同じグループだと 1 ステップに合流する）。
        private void Pick(Color c, Vector2 uv)
        {
            Undo.IncrementCurrentGroup();
            PreviewView.ApplyEyedropperSample(_host, Zone, c, uv);
            Undo.IncrementCurrentGroup();
        }

        [Test]
        public void Redo_RestoresPositionWithColor()
        {
            Pick(Red, Uv1);
            Undo.PerformUndo();
            Assert.IsFalse(Zone.HasSampleUV, "Undo で位置も取る前へ戻る");
            Assert.IsFalse(Zone.sampleColorSet);

            Undo.PerformRedo();
            Assert.AreEqual(Red, Zone.sampleColor);
            Assert.IsTrue(Zone.HasSampleUV, "Redo で色と一緒に位置も戻る");
            Assert.AreEqual(Uv1, Zone.sampleUV);
        }

        [Test]
        public void Undo_RestoresPreviousPosition()
        {
            Pick(Red, Uv1);
            Pick(Blue, Uv2);
            Undo.PerformUndo();
            Assert.AreEqual(Red, Zone.sampleColor);
            Assert.AreEqual(Uv1, Zone.sampleUV, "ひとつ前のスポイト位置へ戻る（消えない）");
        }

        [Test]
        public void SameColorRepick_MovesPositionUndoably()
        {
            Pick(Red, Uv1);
            Pick(Red, Uv2); // 同じ色を別の島で拾い直す
            Assert.AreEqual(Uv2, Zone.sampleUV);
            Undo.PerformUndo();
            Assert.AreEqual(Uv1, Zone.sampleUV, "位置だけの変更も 1 ステップで戻る");
            Undo.PerformRedo();
            Assert.AreEqual(Uv2, Zone.sampleUV);
        }

        [Test]
        public void UnrelatedUndo_KeepsPosition()
        {
            Pick(Red, Uv1);
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(_host, "Iroca Edit");
            Zone.tolerance = 0.4f;
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            Assert.AreEqual(Uv1, Zone.sampleUV, "色に触れない Undo では位置が残る");
            Undo.PerformRedo();
            Assert.AreEqual(Uv1, Zone.sampleUV);
        }

        [Test]
        public void UndoZoneRemoval_RestoresPosition()
        {
            Pick(Red, Uv1);
            Undo.IncrementCurrentGroup();
            Undo.RegisterCompleteObjectUndo(_host, "Remove Zone");
            _host.session.zones.RemoveAt(0);
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            Assert.AreEqual(1, _host.session.zones.Count);
            Assert.AreEqual(Uv1, Zone.sampleUV, "削除を戻したゾーンに位置も戻る");
        }

        // ─── 永続化 ───

        [Test]
        public void Preset_DoesNotCarrySamplePosition()
        {
            // プリセットは別テクスチャへ読み込まれるので位置を書かない。編集中の位置は消さない。
            Pick(Red, Uv1);
            var copies = IrocaPresetData.WithoutSamplePositions(_host.session.zones);
            Assert.IsFalse(copies[0].HasSampleUV);
            Assert.AreEqual(Red, copies[0].sampleColor);
            Assert.AreEqual(Uv1, Zone.sampleUV, "複製からだけ外す");
        }

        [Test]
        public void PresetLoad_DropsSamplePositionWrittenInFile()
        {
            // 位置を外さずに書かれたプリセット（手書き・他ツール）でも読み込みで外す。
            var assets = TestAssets.Create();
            try
            {
                string path = Path.Combine(TestAssets.Abs(assets.Folder), "with-uv.json");
                var z = new ColorZone { sampleColor = Red, sampleUV = Uv1 };
                var data = new IrocaPresetData { zones = { z } };
                Assert.IsTrue(PresetStore.SaveToPath(path, data));
                StringAssert.Contains("sampleUV", File.ReadAllText(path));
                var loaded = PresetStore.Load(path);
                Assert.IsFalse(loaded.zones[0].HasSampleUV);
                Assert.AreEqual(Red, loaded.zones[0].sampleColor);
            }
            finally { assets.Dispose(); }
        }

        [Test]
        public void Session_KeepsSamplePosition()
        {
            // セッションはテクスチャ単位なので位置も持ち越す（開き直しても自動調整が AI 提案を使える）。
            var s = IrocaSessionState.CreateDefault();
            s.zones.Add(new ColorZone { sampleColor = Red, sampleUV = Uv1 });
            var back = JsonUtility.FromJson<IrocaSessionState>(JsonUtility.ToJson(s));
            Assert.AreEqual(Uv1, back.zones[0].sampleUV);

            // 位置を持たない旧セッション JSON は「位置なし」で読める。
            var legacy = JsonUtility.FromJson<IrocaSessionState>("{\"zones\":[{\"name\":\"z\"}]}");
            Assert.IsFalse(legacy.zones[0].HasSampleUV);
        }
    }
}
