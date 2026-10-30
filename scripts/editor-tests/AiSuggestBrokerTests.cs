// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;

namespace Iroca.EditorTests
{
    /// <summary>
    /// AI 提案の待ち行列(<see cref="AiSuggestBroker"/>)。サービスを偽物に差し替えて(Sentis の無いホストでも走る)、
    /// 求めた順に受け取り先へ返すこと・取り消し・元の切り替え・エラー・手放し方を見る。
    /// 前はウィンドウごとのコントローラがそれぞれサービスを購読し、2 つ並ぶと先に購読した方が提案を取って捨てていた。
    /// </summary>
    public class AiSuggestBrokerTests
    {
        /// <summary>受理した順に 1 件ずつ返す、サービスの約束どおりの偽物(進めるのはテストの <see cref="Finish"/>)。</summary>
        private sealed class FakeService : IMaskSuggestService
        {
            public MaskSuggestPhase phase = MaskSuggestPhase.Idle;
            public bool modelsOk = true;
            // SetSource で新しい元を渡されたとき、埋め込みの計算に入る(false ならすぐ待機中)。
            public bool encodeOnNewSource;
            public string sourceKey;
            public readonly Queue<Vector2Like> queue = new Queue<Vector2Like>();
            public MaskSuggestProposal ready;
            public int flushes, cancelAlls, gpuReleases;

            public struct Vector2Like { public float u, v; }

            public MaskSuggestPhase Phase => phase;
            public float Progress => 0f;
            public string ErrorMessage => phase == MaskSuggestPhase.Error ? "boom" : null;
            public bool ErrorIsModelFile => false;
            public bool UsesCpuBackend => false;
            public bool TryEnsureModels() => modelsOk;
            public void RefreshModelFiles() { }
            public int PendingClickCount => queue.Count;
            public event System.Action StateChanged;

            public void SetSource(string cacheKey, NativePixels pixelsBottomUp)
            {
                if (cacheKey == sourceKey) return;
                // 本物と同じく、元を切り替えると待ち行列を黙って捨てる。
                queue.Clear();
                ready = null;
                sourceKey = cacheKey;
                Set(encodeOnNewSource ? MaskSuggestPhase.Encoding : MaskSuggestPhase.Idle);
            }

            public bool RequestProposal(float u, float v, MaskSuggestGranularity granularity)
            {
                if (!modelsOk || phase == MaskSuggestPhase.Error) return false;
                queue.Enqueue(new Vector2Like { u = u, v = v });
                if (phase == MaskSuggestPhase.Idle) Set(MaskSuggestPhase.Decoding);
                return true;
            }

            public void FlushPendingClicks()
            {
                flushes++;
                queue.Clear();
                Set(MaskSuggestPhase.Idle);
            }

            public bool TryTakeProposal(out MaskSuggestProposal proposal)
            {
                proposal = ready;
                if (phase != MaskSuggestPhase.ProposalReady || proposal == null) return false;
                ready = null;
                Set(queue.Count > 0 ? MaskSuggestPhase.Decoding : MaskSuggestPhase.Idle);
                return true;
            }

            public void CancelAll()
            {
                cancelAlls++;
                queue.Clear();
                ready = null;
                sourceKey = null;
                Set(MaskSuggestPhase.Idle);
            }

            public void ReleaseGpuResources()
            {
                gpuReleases++;
                CancelAll();
            }

            /// <summary>先頭の要求の推論が終わった(提案を出す)。</summary>
            public void Finish(MaskSuggestProposal p)
            {
                Assert.Greater(queue.Count, 0, "推論中の要求が無い");
                queue.Dequeue();
                ready = p;
                Set(MaskSuggestPhase.ProposalReady);
            }

            public void Set(MaskSuggestPhase p)
            {
                phase = p;
                StateChanged?.Invoke();
            }
        }

        private IMaskSuggestService _saved;
        private FakeService _svc;
        private readonly object _a = new object(), _b = new object();

        [SetUp]
        public void SetUp()
        {
            _saved = MaskSuggestBridge.Service;
            _svc = new FakeService();
            MaskSuggestBridge.Service = _svc;
            AiSuggestBroker.EnsureSubscribed();
        }

        [TearDown]
        public void TearDown()
        {
            AiSuggestBroker.Release(_a);
            AiSuggestBroker.Release(_b);
            MaskSuggestBridge.Service = _saved;
            AiSuggestBroker.EnsureSubscribed();
        }

        private static MaskSuggestProposal P(int w) => new MaskSuggestProposal { maskBottomUp = new bool[w], width = w, height = 1 };

        [Test]
        public void ProposalsGoBackToWhoAskedInOrder()
        {
            MaskSuggestProposal gotA = null, gotB = null;
            Assert.IsTrue(AiSuggestBroker.Enqueue(_a, "k", null, 0.1f, 0.1f, MaskSuggestGranularity.Auto, p => gotA = p));
            Assert.IsTrue(AiSuggestBroker.Enqueue(_b, "k", null, 0.2f, 0.2f, MaskSuggestGranularity.Auto, p => gotB = p));
            var p1 = P(1);
            var p2 = P(2);
            _svc.Finish(p1);
            Assert.AreSame(p1, gotA);
            Assert.IsNull(gotB, "2 件目はまだ");
            _svc.Finish(p2);
            Assert.AreSame(p2, gotB);
            Assert.IsFalse(AiSuggestBroker.HasLive);
        }

        [Test]
        public void LeavesAProposalItDidNotAskFor()
        {
            // サービスを直接使った誰か(テストなど)の提案は取らない。
            _svc.queue.Enqueue(default);
            _svc.Finish(P(1));
            Assert.AreEqual(MaskSuggestPhase.ProposalReady, _svc.Phase);
            Assert.IsNotNull(_svc.ready);
        }

        [Test]
        public void Cancel_FlushesWhenAlone_AndKeepsOthersWhenMixed()
        {
            bool aDone = false;
            MaskSuggestProposal a = P(9);
            AiSuggestBroker.Enqueue(_a, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, p => { aDone = true; a = p; });
            Assert.IsTrue(AiSuggestBroker.Cancel(_a));
            Assert.IsTrue(aDone, "取り消したら「来ない」を返す");
            Assert.IsNull(a);
            Assert.AreEqual(1, _svc.flushes, "自分の要求だけならサービスの待ち行列も捨てる");
            Assert.IsFalse(AiSuggestBroker.Cancel(_a), "二度目は何も無い");

            MaskSuggestProposal gotB = null;
            int aProposals = 0;
            AiSuggestBroker.Enqueue(_a, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, p => { if (p != null) aProposals++; });
            AiSuggestBroker.Enqueue(_b, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, p => gotB = p);
            AiSuggestBroker.Cancel(_a);
            Assert.AreEqual(1, _svc.flushes, "ほかの持ち主の要求があれば捨てない");
            _svc.Finish(P(1));   // 取り消した分は捨てる
            var pb = P(2);
            _svc.Finish(pb);
            Assert.AreSame(pb, gotB);
            Assert.AreEqual(0, aProposals, "取り消した分は届かない");
        }

        [Test]
        public void SwitchingTheSourceTellsTheOthersTheirsWillNotCome()
        {
            bool aDone = false;
            MaskSuggestProposal a = P(9);
            AiSuggestBroker.Enqueue(_a, "k1", null, 0f, 0f, MaskSuggestGranularity.Auto, p => { aDone = true; a = p; });
            MaskSuggestProposal gotB = null;
            AiSuggestBroker.Enqueue(_b, "k2", null, 0f, 0f, MaskSuggestGranularity.Auto, p => gotB = p);
            Assert.IsTrue(aDone);
            Assert.IsNull(a, "元を切り替えると、前の元の要求は届かない");
            Assert.AreEqual("k2", _svc.sourceKey);
            var pb = P(1);
            _svc.Finish(pb);
            Assert.AreSame(pb, gotB, "切り替えた後の 1 件目は切り替えた側のもの");
        }

        [Test]
        public void RequestWhenIdle_WaitsForOthersAndForTheEmbedding()
        {
            AiSuggestBroker.Enqueue(_a, "k1", null, 0f, 0f, MaskSuggestGranularity.Auto, _ => { });
            Assert.AreEqual(AiSuggestBroker.Request.Busy,
                AiSuggestBroker.RequestWhenIdle(_b, "k2", null, 0f, 0f, _ => { }), "ほかの元の要求は捨てずに待つ");
            Assert.AreEqual("k1", _svc.sourceKey);
            Assert.AreEqual(AiSuggestBroker.Request.Busy,
                AiSuggestBroker.RequestWhenIdle(_b, "k1", null, 0f, 0f, _ => { }), "同じ元でも、処理中は待つ");
            _svc.Finish(P(1));

            _svc.encodeOnNewSource = true;
            Assert.AreEqual(AiSuggestBroker.Request.Busy,
                AiSuggestBroker.RequestWhenIdle(_b, "k2", null, 0f, 0f, _ => { }), "埋め込みの計算を始めて待つ");
            Assert.AreEqual("k2", _svc.sourceKey);
            Assert.AreEqual(MaskSuggestPhase.Encoding, _svc.Phase);
            _svc.Set(MaskSuggestPhase.Idle);
            MaskSuggestProposal got = null;
            Assert.AreEqual(AiSuggestBroker.Request.Started,
                AiSuggestBroker.RequestWhenIdle(_b, "k2", null, 0f, 0f, p => got = p));
            var p2 = P(2);
            _svc.Finish(p2);
            Assert.AreSame(p2, got);

            _svc.modelsOk = false;
            Assert.AreEqual(AiSuggestBroker.Request.Unavailable,
                AiSuggestBroker.RequestWhenIdle(_b, "k2", null, 0f, 0f, _ => { }));
        }

        [Test]
        public void Error_FailsEveryWaiter_AndTheNextClickRetries()
        {
            int fails = 0;
            AiSuggestBroker.Enqueue(_a, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, p => { if (p == null) fails++; });
            AiSuggestBroker.Enqueue(_b, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, p => { if (p == null) fails++; });
            _svc.queue.Clear();
            _svc.Set(MaskSuggestPhase.Error);
            Assert.AreEqual(2, fails);
            Assert.IsFalse(AiSuggestBroker.HasLive);
            Assert.AreEqual(AiSuggestBroker.Request.Unavailable,
                AiSuggestBroker.RequestWhenIdle(_a, "k", null, 0f, 0f, _ => { }), "証拠はエラー中に求めない");
            Assert.IsTrue(AiSuggestBroker.Enqueue(_a, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, _ => { }),
                "右クリックはエラーを止めてやり直す");
            Assert.AreEqual(1, _svc.cancelAlls);
        }

        [Test]
        public void Release_StopsTheServiceOnlyWhenNobodyElseWaits()
        {
            AiSuggestBroker.Enqueue(_a, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, _ => { });
            AiSuggestBroker.Enqueue(_b, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, _ => { });
            AiSuggestBroker.Release(_a);
            Assert.AreEqual(0, _svc.cancelAlls, "ほかの持ち主が待っている");
            AiSuggestBroker.ReleaseGpuIfUnused();
            Assert.AreEqual(0, _svc.gpuReleases, "待っている要求があるうちは GPU も手放さない");
            AiSuggestBroker.Release(_b);
            Assert.AreEqual(1, _svc.cancelAlls);
            Assert.IsNull(_svc.sourceKey, "元の参照も手放させる");
            AiSuggestBroker.ReleaseGpuIfUnused();
            Assert.AreEqual(1, _svc.gpuReleases);
        }

        [Test]
        public void ReplacedService_FailsWhatWasWaitingOnTheOldOne()
        {
            MaskSuggestProposal a = P(9);
            AiSuggestBroker.Enqueue(_a, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, p => a = p);
            var old = _svc;
            _svc = new FakeService();
            MaskSuggestBridge.Service = _svc;
            AiSuggestBroker.EnsureSubscribed();
            Assert.IsNull(a, "前のサービスに並べた分は届かない");
            old.Finish(P(1));
            Assert.IsNotNull(old.ready, "前のサービスの提案は取らない(購読を外した)");

            MaskSuggestProposal got = null;
            AiSuggestBroker.Enqueue(_a, "k", null, 0f, 0f, MaskSuggestGranularity.Auto, p => got = p);
            var p2 = P(2);
            _svc.Finish(p2);
            Assert.AreSame(p2, got);
        }
    }
}
