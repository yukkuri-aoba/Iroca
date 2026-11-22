// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// 塗り先の色を変えたら、自動調整の導いた模様保持(valueBlend)を今の式で導き直す(Core の ZoneEdits.SetTarget)。
    /// スポイトのクリックで自動調整するときの塗り先は既定の白なので、導き直さないと「クリック → 塗り先 → 自動調整」と違う値が残る。
    /// 手で触った模様保持・自動調整していないゾーン・この項目の無い古いデータは変えない。AI は使わない(従来の解析)。
    /// </summary>
    public class PickValueBlendTests {
        private const int W = 64, H = 64;
        private NativePixels _pixels;
        private static readonly Color Dark = new Color(0.05f, 0.05f, 0.1f);

        [SetUp]
        public void SetUp() => _pixels = NativePixels.CopyOf(Halves(), W, H);

        [TearDown]
        public void TearDown() => _pixels.Release();

        // 左半分は陰影のある赤、右半分は青(下原点)。AutoTuneServiceTests と同じ絵。
        private static Color32[] Halves() {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    byte shade = (byte)(150 + (x * 7 + y * 3) % 70);
                    px[y * W + x] = x < W / 2 ? new Color32(shade, 30, 30, 255) : new Color32(30, 40, 200, 255);
                }
            }
            return px;
        }

        private static ColorZone RedZone() {
            var z = EditingSession.NewZone(new System.Collections.Generic.List<ColorZone>());
            z.sampleColor = new Color32(185, 30, 30, 255);
            z.sampleColorSet = true;
            z.EnsureId();
            return z;
        }

        private unsafe ZoneAutoTuner.TuneResult Tune(ColorZone zone) {
            var r = ZoneAutoTuner.Analyze(_pixels.Ptr, W, H, zone.Clone());
            r.ApplyTo(zone);
            return r;
        }

        [Test]
        public void TargetAfterAutoTune_GivesTheSameValuesAsTargetFirst() {
            var first = RedZone();
            ZoneEdits.SetTarget(first, Dark);
            var expected = Tune(first);

            var later = RedZone();   // 塗り先は既定の白のまま調整する(スポイトのクリックで自動調整したとき)
            Tune(later);
            Assume.That(later.valueBlend, Is.Not.EqualTo(first.valueBlend), "テストの前提: 白で調整すると模様保持が違う");

            ZoneEdits.SetTarget(later, Dark);
            Assert.AreEqual(first.valueBlend, later.valueBlend, "塗り先を選んだ後に模様保持を導き直していない");
            Assert.IsTrue(AutoTuneService.ValuesIntact(later, expected), "順番で導出値が違う");
            Assert.IsTrue(later.ValueBlendIsDerived, "導き直した値も、自動調整の値として続けて導き直す");

            // 色の欄から選んでも同じ(窓・--script の set)。
            ZoneEdits.SetTargetFromField(later, Color.white);
            Assert.AreEqual(ZoneAutoTuner.ValueBlendFor(later.sampleColor, Color.white), later.valueBlend);
        }

        [Test]
        public void ValueBlendChangedByHand_IsKept() {
            var zone = RedZone();
            Tune(zone);
            zone.valueBlend = 0.8f;   // 模様保持のスライダー
            Assert.IsFalse(zone.ValueBlendIsDerived);
            ZoneEdits.SetTarget(zone, Dark);
            Assert.AreEqual(0.8f, zone.valueBlend, "手で触った模様保持を書き換えた");
            ZoneEdits.SetTargetFromField(zone, Color.white);
            Assert.AreEqual(0.8f, zone.valueBlend);
        }

        [Test]
        public void ZoneNeverAutoTuned_KeepsTheValueBlend() {
            var zone = RedZone();
            Assert.Less(zone.derivedValueBlend, 0f, "新しいゾーンは自動調整の値を持たない");
            ZoneEdits.SetTarget(zone, Dark);
            Assert.AreEqual(1f, zone.valueBlend, "自動調整していないゾーンの模様保持を変えた(スポイトの直後に塗る出力が変わる)");
        }

        [Test]
        public void ResettingTheTuning_IsNotAnAutoTunedValue() {
            var zone = RedZone();
            ZoneEdits.SetTarget(zone, Dark);
            Tune(zone);
            zone.ResetTuningToDefault();
            ZoneEdits.SetTarget(zone, Color.white);
            ZoneEdits.SetTarget(zone, Dark);
            Assert.AreEqual(1f, zone.valueBlend, "既定へ戻した模様保持を導き直した");
        }

        // この項目の無い古いデータ(保存したセッション・プリセット)は、自動調整の値を持たないものとして読む。
        [Test]
        public void OldDataWithoutTheField_IsReadAsNotDerived() {
            var zone = JsonUtility.FromJson<ColorZone>("{\"name\":\"a\",\"valueBlend\":0.7}");
            Assert.Less(zone.derivedValueBlend, 0f);
            Assert.IsFalse(zone.ValueBlendIsDerived);
            var state = JsonUtility.FromJson<IrocaSessionState>("{\"zones\":[{\"name\":\"a\",\"valueBlend\":0.7}]}");
            Assert.AreEqual(1, state.zones.Count);
            Assert.Less(state.zones[0].derivedValueBlend, 0f, "リストの中のゾーンに既定が入っていない");
            ZoneEdits.SetTarget(state.zones[0], Dark);
            Assert.AreEqual(0.7f, state.zones[0].valueBlend, "古いデータの模様保持を書き換えた");

            // 書いて読み直すと残る(Undo・保存)。
            var tuned = RedZone();
            Tune(tuned);
            var back = JsonUtility.FromJson<ColorZone>(JsonUtility.ToJson(tuned));
            Assert.AreEqual(tuned.derivedValueBlend, back.derivedValueBlend);
            Assert.IsTrue(back.ValueBlendIsDerived);
        }

        // 塗り先を変えて模様保持を導き直したゾーンは、手で触っていない(上書きの確認を出さない・取り直しで調整し直す)。
        [Test]
        public void UntouchedByUser_AfterTheTargetRederivedTheValueBlend() {
            var zone = RedZone();
            zone.sampleUV = new Vector2(-1f, -1f);
            var run = AutoTuneService.Start(zone, _pixels, "k", null, 0, 0, RecolorSettings.Default, null);
            var sw = Stopwatch.StartNew();
            while (run.Status == AutoTuneService.Status.Analyzing) {
                Assert.Less(sw.Elapsed.TotalSeconds, 60, "解析が終わらない");
                run.Advance();
                Thread.Sleep(5);
            }
            Assert.AreEqual(AutoTuneService.Status.Done, run.Status, run.Failure + " " + run.FailureDetail);
            run.ApplyTo(zone);
            var input = run.ToInput();
            ZoneEdits.SetTarget(zone, Dark);
            Assume.That(zone.valueBlend, Is.Not.EqualTo(input.applied.valueBlend), "テストの前提: 導き直すと値が変わる");
            Assert.IsTrue(AutoTuneService.UntouchedByUser(zone, input), "導き直した模様保持を手調整と見た");
            zone.valueBlend = 0.95f;
            Assert.IsFalse(AutoTuneService.UntouchedByUser(zone, input), "手で触った模様保持");
        }
    }

    /// <summary>
    /// スポイトのクリックで自動調整まで済ませてから塗る(2026-11-23。IrocaWindow.AutoTune の AddZoneByEyedropper・
    /// ApplyEyedropperSample)。待つ間は塗らず(ゾーンを足さない・色を替えない)、届いたら「元に戻す」1 回分で書く。
    /// 設定がオフ・AI が無い・失敗・期限切れなら、自動調整しないときと同じに塗る。取り消し・Undo・テクスチャの切り替え・
    /// 窓を閉じたら何も残さない。既にあるゾーンの取り直しは、手で調整していなければ調整し直す。
    /// AI は偽物(<see cref="FakeMaskSuggestService"/>)なので Sentis の無いホストでも走る。絵は漏れの確かめの合成の絵(LeakScene)。
    /// </summary>
    public class PickAutoTuneWindowTests {
        private const int W = LeakScene.W, H = LeakScene.H;
        private TestAssets _assets;
        private string _guid;
        private string _prefKey;
        private string _prefBefore;
        private bool _pickHadKey;
        private bool _pickBefore;
        private IrocaWindow _win;
        private IMaskSuggestService _saved;
        private FakeMaskSuggestService _svc;
        private readonly System.Collections.Generic.List<string> _otherTextures = new System.Collections.Generic.List<string>();

        [SetUp]
        public void SetUp() {
            _pickHadKey = EditorPrefs.HasKey(IrocaPrefs.AutoTuneOnPick);
            _pickBefore = IrocaWindow.AutoTuneOnPick;
            IrocaWindow.AutoTuneOnPick = true;
            _assets = TestAssets.Create();
            string tex = _assets.WritePng("pick", LeakScene.Image(), W, H);
            _guid = AssetDatabase.AssetPathToGUID(tex);
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);
            _saved = MaskSuggestBridge.Service;
            _svc = new FakeMaskSuggestService();
            MaskSuggestBridge.Service = _svc;
            AiSuggestBroker.EnsureSubscribed();
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreEqual(tex, AssetDatabase.GetAssetPath(_win.SourceTexture), "窓がテスト用のテクスチャを開いていない");
            _win.Editing.Masks.Reset(W, H);
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown() {
            Undo.ClearAll();
            AutoTuneService.EvidenceWaitSecondsForTests = 0;
            if (_win != null) {
                Object.DestroyImmediate(_win);
            }
            MaskSuggestBridge.Service = _saved;
            AiSuggestBroker.EnsureSubscribed();
            IrocaWindow.AutoTuneOnPick = _pickBefore;
            if (!_pickHadKey) {
                EditorPrefs.DeleteKey(IrocaPrefs.AutoTuneOnPick);
            }
            if (_prefBefore == null) {
                EditorPrefs.DeleteKey(_prefKey);
            } else {
                EditorPrefs.SetString(_prefKey, _prefBefore);
            }
            var guids = new System.Collections.Generic.List<string> { _guid };
            foreach (var p in _otherTextures) {
                guids.Add(AssetDatabase.AssetPathToGUID(p));
            }
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir }) {
                if (!Directory.Exists(dir)) {
                    continue;
                }
                foreach (var g in guids) {
                    foreach (var f in Directory.GetFiles(dir, g + "*")) {
                        File.Delete(f);
                    }
                }
            }
            _otherTextures.Clear();
            _assets.Dispose();
        }

        private static Color ColorAt(int x, int y) {
            Color32 c = LeakScene.Image()[y * W + x];
            return new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
        }

        private static Vector2 UvAt(int x, int y) => new Vector2((x + 0.5f) / W, (y + 0.5f) / H);

        private System.Collections.Generic.List<ColorZone> Zones => _win.Session.zones;

        private int Excluded(string zoneId) {
            var layer = _win.Editing.Masks.Layer(false, zoneId);
            if (layer == null) {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < W * H; i++) {
                if (layer.Get(i)) {
                    n++;
                }
            }
            return n;
        }

        // 偽物の AI に答えて終わるまで進め(1 つ目の要求 = 証拠に部品 A、後の要求 = 漏れの確かめに部品 B)、窓に当てさせる。
        private AutoTuneService.Run DriveAndApply() {
            var run = _win.RunningAutoTune;
            Assert.IsNotNull(run, "自動調整が走っていない");
            Assert.AreEqual(AutoTuneService.Status.WaitingForEvidence, run.Status, run.Failure + " " + run.FailureDetail);
            int answered = 0;
            var sw = Stopwatch.StartNew();
            while (run.Status != AutoTuneService.Status.Done && run.Status != AutoTuneService.Status.Failed) {
                Assert.Less(sw.Elapsed.TotalSeconds, 60, "終わらない: " + run.Status);
                if (_svc.queue.Count > 0 && _svc.phase == MaskSuggestPhase.Decoding) {
                    _svc.Finish(LeakScene.Proposal(answered == 0 ? (System.Func<int, int, bool>)LeakScene.InA : LeakScene.InB));
                    answered++;
                    continue;
                }
                Thread.Sleep(5);
                run.Advance();
            }
            Assert.AreEqual(AutoTuneService.Status.Done, run.Status, run.Failure + " " + run.FailureDetail);
            Assert.IsTrue(_win.PickAutoTunePending, "テストの前提: 届くまでは当てていない");
            _win.TickAutoTune();
            Assert.IsFalse(_win.IsJobBlockingUI, "当てた後も待ちの印が残った");
            Assert.IsFalse(_win.PickAutoTunePending);
            return run;
        }

        [Test]
        public void PickOn_PaintsNothingWhileWaiting_ThenAddsTheTunedZoneAsOneUndo() {
            var picked = ColorAt(LeakScene.ClickX, LeakScene.ClickY);
            var uv = LeakScene.ClickUv;
            Undo.IncrementCurrentGroup();
            _win.AddZoneByEyedropper(picked, uv);
            Assert.IsTrue(_win.IsJobBlockingUI, "自動調整を待っていない: " + _win.RunningAutoTune?.Failure);
            Assert.IsTrue(_win.PickAutoTunePending);
            Assert.AreEqual(0, Zones.Count, "待っている間にゾーンを足した(塗った)");

            var run = DriveAndApply();
            Assert.AreEqual(1, Zones.Count, "ゾーンができない");
            var zone = Zones[0];
            string id = zone.id;
            Assert.AreEqual(uv, zone.sampleUV, "スポイトの位置");
            Assert.IsTrue(AutoTuneService.ValuesIntact(zone, run.Result), "調整の値が入っていない");
            Assert.IsTrue(_win.TryGetAutoTuneInput(id, out _), "自動調整の入力を覚えていない(上書きの確認・取り直しの判定に使う)");
            Assert.IsFalse(_win.IsUntunedNewZone(zone), "「自動調整を押す」の案内が残る");
            Assert.AreEqual(0, _win._maskView.activeMaskTarget, "作ったゾーンを直す対象にしていない");
            int excluded = run.LeakExcludedPixels > 0 ? LeakScene.Count(LeakScene.Mask(LeakScene.InB)) : 0;
            Assert.AreEqual(excluded, Excluded(id), "外す範囲がゾーンの「塗らない」に入っていない");

            Undo.PerformUndo();
            Assert.AreEqual(0, Zones.Count, "1 回の Undo でゾーンの追加ごと戻らない");
            Assert.AreEqual(0, Excluded(id), "1 回の Undo でマスクが戻らない");

            Undo.PerformRedo();
            Assert.AreEqual(1, Zones.Count, "やり直しでゾーンが戻らない");
            Assert.IsTrue(AutoTuneService.ValuesIntact(Zones[0], run.Result), "やり直しで値が戻らない");
            Assert.AreEqual(excluded, Excluded(id), "やり直しでマスクが戻らない");
        }

        [Test]
        public void PickOff_PaintsAtOnceWithTheInitialTolerance() {
            IrocaWindow.AutoTuneOnPick = false;
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            Assert.IsFalse(_win.IsJobBlockingUI, "設定がオフなのに待った");
            Assert.AreEqual(1, Zones.Count);
            Assert.AreEqual(EditingSession.NewZoneInitialTolerance, Zones[0].tolerance, 1e-6f);
            Assert.AreEqual(0, _svc.requests, "設定がオフなのに AI に求めた");
        }

        [TestCase(true, TestName = "PickWithoutAi_PaintsAtOnceWithTheInitialTolerance(NoSentis)")]
        [TestCase(false, TestName = "PickWithoutAi_PaintsAtOnceWithTheInitialTolerance(NoModel)")]
        public void PickWithoutAi_PaintsAtOnceWithTheInitialTolerance(bool noSentis) {
            if (noSentis) {
                MaskSuggestBridge.Service = null;
                AiSuggestBroker.EnsureSubscribed();
            } else {
                _svc.modelsOk = false;   // モデルが読めない(Unavailable)
            }
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            Assert.IsFalse(_win.IsJobBlockingUI, "AI が無いのに待った");
            Assert.AreEqual(1, Zones.Count, "AI が無いときに塗らなかった");
            Assert.AreEqual(EditingSession.NewZoneInitialTolerance, Zones[0].tolerance, 1e-6f);
        }

        // 待っている間に AI が失敗・期限切れになったら、自動調整しないときと同じに塗る(自分の「元に戻す」1 回分)。
        [TestCase(false, TestName = "PickFailsWhileWaiting_PaintsWithTheInitialTolerance(Timeout)")]
        [TestCase(true, TestName = "PickFailsWhileWaiting_PaintsWithTheInitialTolerance(AiError)")]
        public void PickFailsWhileWaiting_PaintsWithTheInitialTolerance(bool aiError) {
            AutoTuneService.EvidenceWaitSecondsForTests = 0.05;
            Undo.IncrementCurrentGroup();
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            Assert.IsTrue(_win.IsJobBlockingUI, "テストの前提: 待っている");
            if (aiError) {
                _svc.Set(MaskSuggestPhase.Error);
            } else {
                Thread.Sleep(150);
            }
            _win.TickAutoTune();
            Assert.IsFalse(_win.IsJobBlockingUI, "失敗の後も待ちの印が残った");
            Assert.AreEqual(1, Zones.Count, "失敗したら塗る");
            Assert.AreEqual(EditingSession.NewZoneInitialTolerance, Zones[0].tolerance, 1e-6f);
            Assert.AreEqual(LeakScene.ClickUv, Zones[0].sampleUV);
            Assert.IsFalse(_win.TryGetAutoTuneInput(Zones[0].id, out _), "自動調整していないのに入力を覚えた");
            Undo.PerformUndo();
            Assert.AreEqual(0, Zones.Count, "1 回の Undo で戻らない");
        }

        [Test]
        public void PickCancelled_LeavesNoZone() {
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            Assert.IsTrue(_win.IsJobBlockingUI, "テストの前提: 待っている");
            _win.CancelAutoTune();   // 進み具合の「キャンセル」
            Assert.IsFalse(_win.IsJobBlockingUI);
            Assert.IsFalse(_win.PickAutoTunePending, "取り消した後も当て先が残った");
            Assert.IsFalse(AiSuggestBroker.HasLive, "AI への要求が残った");
            _win.TickAutoTune();
            Assert.AreEqual(0, Zones.Count, "取り消したのにゾーンができた");
        }

        [Test]
        public void PickInterruptedByUndo_LeavesNoZone() {
            Undo.IncrementCurrentGroup();
            _win.Editing.AddZone();
            Undo.IncrementCurrentGroup();
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            Assert.IsTrue(_win.IsJobBlockingUI, "テストの前提: 待っている");
            Undo.PerformUndo();
            Assert.IsFalse(_win.IsJobBlockingUI, "Undo で止まっていない");
            Assert.IsFalse(_win.PickAutoTunePending);
            Assert.AreEqual(0, Zones.Count, "Undo の後にゾーンが残った・できた");
        }

        [Test]
        public void PickInterruptedByTextureSwitch_LeavesNoZone() {
            string other = _assets.WritePng("pick-other", TestAssets.Solid(W, H, new Color32(50, 200, 50, 255)), W, H);
            _otherTextures.Add(other);
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            Assert.IsTrue(_win.IsJobBlockingUI, "テストの前提: 待っている");
            _win.SwitchSourceTexture(AssetDatabase.LoadAssetAtPath<Texture2D>(other));
            Assert.IsFalse(_win.IsJobBlockingUI, "テクスチャの切り替えで止まっていない");
            Assert.IsFalse(_win.PickAutoTunePending);
            Assert.AreEqual(0, Zones.Count, "切り替えた先にゾーンができた");
        }

        [Test]
        public void WindowClosedWhileWaiting_LeavesNoRequest() {
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            Assert.IsTrue(_win.IsJobBlockingUI, "テストの前提: 待っている");
            var run = _win.RunningAutoTune;
            Object.DestroyImmediate(_win);
            _win = null;
            Assert.AreEqual(AutoTuneService.Status.Failed, run.Status, "閉じても自動調整が止まっていない");
            Assert.IsFalse(AiSuggestBroker.HasLive, "AI への要求が残った");
        }

        // 手で調整していないゾーンでスポイトを取り直したら、取り直した色で調整し直す(届くまで色も替えない。1 回の Undo で戻る)。
        [Test]
        public void Repick_UntouchedZone_TunesAgainAsOneUndo() {
            IrocaWindow.AutoTuneOnPick = false;
            Undo.IncrementCurrentGroup();
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            Undo.IncrementCurrentGroup();
            var zone = Zones[0];
            var before = zone.Clone();
            IrocaWindow.AutoTuneOnPick = true;

            var picked = ColorAt(40, 10);
            var uv = UvAt(40, 10);
            Assume.That(picked, Is.Not.EqualTo(before.sampleColor), "テストの前提: 色が変わる");
            _win.ApplyEyedropperSample(zone, picked, uv);
            Assert.IsTrue(_win.IsJobBlockingUI, "手で調整していないのに調整し直さない: " + _win.RunningAutoTune?.Failure);
            Assert.AreEqual(before.sampleColor, zone.sampleColor, "待っている間に色を替えた");
            Assert.AreEqual(before.sampleUV, zone.sampleUV);

            var run = DriveAndApply();
            Assert.AreEqual(1, Zones.Count);
            Assert.AreEqual(uv, zone.sampleUV, "取り直した位置");
            Assert.IsTrue(AutoTuneService.ValuesIntact(zone, run.Result), "調整し直した値が入っていない");

            Undo.PerformUndo();
            zone = Zones[0];
            Assert.AreEqual(before.sampleColor, zone.sampleColor, "1 回の Undo で色が戻らない");
            Assert.AreEqual(before.sampleUV, zone.sampleUV, "1 回の Undo で位置が戻らない");
            Assert.AreEqual(before.tolerance, zone.tolerance, 1e-6f, "1 回の Undo で値が戻らない");
        }

        // 手で調整したゾーンは、今までどおり色と位置だけを取り直す(調整し直さない)。
        [Test]
        public void Repick_ZoneAdjustedByHand_OnlyChangesTheColor() {
            IrocaWindow.AutoTuneOnPick = false;
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            var zone = Zones[0];
            zone.SetToleranceManually(0.35f);   // 許容範囲のスライダー
            IrocaWindow.AutoTuneOnPick = true;

            var picked = ColorAt(40, 10);
            var uv = UvAt(40, 10);
            _win.ApplyEyedropperSample(zone, picked, uv);
            Assert.IsFalse(_win.IsJobBlockingUI, "手で調整したゾーンを調整し直した");
            Assert.AreEqual(picked, zone.sampleColor, "色を取り直していない");
            Assert.AreEqual(uv, zone.sampleUV);
            Assert.AreEqual(0.35f, zone.tolerance, 1e-6f, "手で入れた値を変えた");
            Assert.AreEqual(0, _svc.requests, "手で調整したゾーンで AI に求めた");
        }

        // 自動調整したゾーンに塗り先の色を選ぶと、模様保持を導き直す(窓の色の欄と同じ ZoneEdits)。
        [Test]
        public void TargetPickedAfterThePick_RederivesTheValueBlend() {
            _win.AddZoneByEyedropper(ColorAt(LeakScene.ClickX, LeakScene.ClickY), LeakScene.ClickUv);
            DriveAndApply();
            var zone = Zones[0];
            var dark = new Color(0.05f, 0.05f, 0.05f);
            Assert.IsTrue(ZoneEdits.SetTargetFromField(zone, dark));
            Assert.AreEqual(ZoneAutoTuner.ValueBlendFor(zone.sampleColor, dark), zone.valueBlend, "塗り先で導き直していない");
            Assert.IsTrue(_win.TryGetAutoTuneInput(zone.id, out var input));
            Assert.IsTrue(AutoTuneService.UntouchedByUser(zone, input), "導き直した模様保持を手調整と見た");
        }

        // スポイトのボタンを押したら(設定がオン)、次のエディタの更新で元の画像を AI に渡しておく(埋め込みの計算が先に始まる)。
        [UnityTest]
        public IEnumerator ArmingTheEyedropper_HandsTheSourceToTheAi() {
            _win.ArmEyedropper(IrocaWindow.NewZoneEyedropperId);
            _win.WarmAiForPick();
            for (int i = 0; i < 5 && _svc.sourceKey == null; i++) {
                yield return null;
            }
            Assert.IsNotNull(_svc.sourceKey, "元の画像を渡していない");
            Assert.AreEqual(_win.Preview.TrueSourceCacheKey(), _svc.sourceKey, "自動調整と同じ鍵で渡していない");
            Assert.AreEqual(0, _svc.requests, "温めるだけで提案を求めた");
        }

        [UnityTest]
        public IEnumerator ArmingTheEyedropper_DoesNothing_WhenOffOrTheAiFailed() {
            IrocaWindow.AutoTuneOnPick = false;
            _win.ArmEyedropper(IrocaWindow.NewZoneEyedropperId);
            _win.WarmAiForPick();
            for (int i = 0; i < 3; i++) {
                yield return null;
            }
            Assert.IsNull(_svc.sourceKey, "設定がオフなのに渡した");

            IrocaWindow.AutoTuneOnPick = true;
            _svc.Set(MaskSuggestPhase.Error);
            _win.WarmAiForPick();
            for (int i = 0; i < 3; i++) {
                yield return null;
            }
            Assert.IsNull(_svc.sourceKey, "AI がエラーなのに渡した");
            Assert.AreEqual(MaskSuggestPhase.Error, _svc.phase, "温めるだけでエラーを解いた");
        }
    }
}
