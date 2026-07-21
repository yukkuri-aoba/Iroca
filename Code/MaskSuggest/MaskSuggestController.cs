// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案の入力→反映を仲介するコントローラ。
    ///
    /// UX は「クリック 1 回 = 1 反映」に一本化している:
    ///   プレビューでパーツをクリック → SAM が領域を推定 → その領域を即、編集対象の除外マスクへ
    ///   追加する(= 1 つの Undo ストローク)。積み上げ・確定ボタンは無く、間違えたら Ctrl+Z で
    ///   1 手ずつ戻す(手描きブラシと同じ操作感)。複数の島に分かれたパーツは、島を順にクリックすれば
    ///   それぞれが別ストロークとしてマスクへ足される。
    ///
    /// サービス(Sentis 側)とは MaskSuggestBridge 経由で結合し、Sentis 不在時はインスタンスも
    /// 作られない(MaskSuggestSection 参照)。
    /// </summary>
    internal sealed class MaskSuggestController
    {
        IrocaWindow _host;
        MaskPaintView _maskView;
        bool _subscribed;

        // クリック後、推論結果を待っている間 true。結果が来たら即マスクへ反映する。
        bool _awaitingProposal;
        // 待っている間に Undo/Redo が割り込んだら、遅れて届く提案を古い状態へ誤って足さないよう捨てる。
        bool _dropNextProposal;

        /// <summary>AI 提案モードが有効か(プレビュークリックを提案に使う)。</summary>
        public bool Active { get; private set; }

        /// <summary>提案の粒度(次のクリックから適用)。</summary>
        public MaskSuggestGranularity Granularity = MaskSuggestGranularity.Auto;

        /// <summary>直近のクリックが背景まで広がった可能性(粒度を下げる/やり直しの誘導に使う)。</summary>
        public bool LastClickFloodWarning { get; private set; }

        public void Initialize(IrocaWindow host, MaskPaintView maskView)
        {
            _host = host;
            _maskView = maskView;
            var svc = MaskSuggestBridge.Service;
            if (svc != null && !_subscribed)
            {
                svc.StateChanged += OnServiceStateChanged;
                _subscribed = true;
            }
        }

        public void SetActive(bool active)
        {
            if (Active == active) return;
            Active = active;
            if (active) MaskSuggestBridge.Service?.TryEnsureModels();
            else LastClickFloodWarning = false;
            _host?.RequestRepaint();
        }

        // ─────────────────── クリック → 推論 → 即マスク反映 ───────────────────

        /// <summary>
        /// プレビュークリック。pixels は実フル解像度ソース(下原点)。
        /// 推論は非同期で、結果は <see cref="OnServiceStateChanged"/> がマスクへ直接反映する。
        /// </summary>
        public void OnPreviewClick(float u, float v, Color32[] pixelsBottomUp,
                                   int width, int height, string sourceKey)
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null || !Active) return;
            if (!svc.TryEnsureModels()) return;

            svc.SetSource(sourceKey, pixelsBottomUp, width, height);
            svc.RequestProposal(u, v, Granularity);
            _awaitingProposal = true;
            _dropNextProposal = false;
        }

        void OnServiceStateChanged()
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) return;
            if (svc.Phase == MaskSuggestPhase.ProposalReady &&
                svc.TryTakeProposal(out var proposal))
            {
                _awaitingProposal = false;
                // モードを抜けた・Undo が割り込んだ・空提案、のいずれかなら反映しない。
                if (!_dropNextProposal && Active && proposal != null)
                    CommitProposalToMask(proposal);
                _dropNextProposal = false;
            }
            _host?.RequestRepaint();
        }

        /// <summary>
        /// 提案領域を編集対象の除外マスク(共通 or ゾーン)へ OR 合成し、1 つの Undo ストロークとして反映する。
        /// 選んだ部分を「色替えしない範囲」へ加える = そのパーツを色替えから保護する。
        /// 反映後は通常のマスクとしてブラシ修正・Ctrl+Z(1 ストローク扱い)が効く。
        /// </summary>
        void CommitProposalToMask(MaskSuggestProposal proposal)
        {
            if (_maskView == null) return;
            var src = proposal.maskBottomUp;
            int sw = proposal.width, sh = proposal.height;
            if (src == null || sw <= 0 || sh <= 0) return;

            _maskView.EnsureMasks();
            var mask = _maskView.GetActiveMaskArray();
            int mw = _maskView.maskWidth, mh = _maskView.maskHeight;
            if (mask == null || mw <= 0 || mh <= 0) return;

            _maskView.BeginStroke();
            if (mw == sw && mh == sh)
            {
                for (int i = 0; i < mask.Length; i++)
                    if (src[i]) mask[i] = true;
            }
            else
            {
                // マスク解像度がソースと異なる場合は最近傍で転写(EnsureMasks のリスケールと同方針)。
                for (int my = 0; my < mh; my++)
                {
                    int sy = (int)((long)my * sh / mh);
                    int srcRow = sy * sw, dstRow = my * mw;
                    for (int mx = 0; mx < mw; mx++)
                    {
                        int sx = (int)((long)mx * sw / mw);
                        if (src[srcRow + sx]) mask[dstRow + mx] = true;
                    }
                }
            }
            _maskView.EndStroke();
            _maskView.maskDirty = true;
            LastClickFloodWarning = proposal.floodWarning;
            _host?.MarkPreviewDirty();
            _host?.RequestRepaint();
        }

        /// <summary>Unity Undo/Redo 実行時: 反映待ちの提案は古い状態に重なるため、届いても捨てる。</summary>
        public void OnUndoRedoPerformed()
        {
            if (_awaitingProposal) _dropNextProposal = true;
            LastClickFloodWarning = false;
        }

        /// <summary>テクスチャ切替・ウィンドウ破棄時の後始末。</summary>
        public void OnSourceChangedOrClosing()
        {
            MaskSuggestBridge.Service?.CancelAll();
            _awaitingProposal = false;
            _dropNextProposal = false;
            LastClickFloodWarning = false;
        }
    }
}
