// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using NUnit.Framework;

namespace Iroca.EditorTests
{
    /// <summary>受理した順に 1 件ずつ返す、サービスの約束どおりの偽物(進めるのはテストの <see cref="Finish"/>)。</summary>
    internal sealed class FakeMaskSuggestService : IMaskSuggestService
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
}
