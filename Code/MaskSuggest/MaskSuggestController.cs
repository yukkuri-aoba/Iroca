// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案の UI 状態機械。クリック提案の積み上げ(和集合)・1手戻し・
    /// 既存マスクへの確定を担う。サービス(Sentis 側)とは MaskSuggestBridge 経由で結合し、
    /// Sentis 不在時はインスタンスも作られない(MaskSuggestSection 参照)。
    ///
    /// 計測(dev_safe/ml)で確定した UX 要件を実装する:
    ///   1クリック=1提案 → オーバーレイ確認 → 追加/やり直し → 和集合を積み上げ →
    ///   悪い採用は 1 手戻し(和集合は自動では回復しないため) → まとめて既存マスクへ確定。
    /// </summary>
    internal sealed class MaskSuggestController
    {
        const int OverlayMaxSize = 1024;

        // 未採用の提案(クリック毎に 1 つ)
        MaskSuggestProposal _pending;
        // 採用済みピースの和集合(テクスチャ実寸・下原点)
        bool[] _union;
        int _unionW, _unionH;
        // 1 手戻し用の採用ピース(RLE で保持しメモリを抑える)
        readonly List<string> _acceptedPieces = new List<string>();

        Texture2D _overlayTexture;
        bool _overlayDirty;

        IrocaWindow _host;
        MaskPaintView _maskView;
        bool _subscribed;

        /// <summary>AI 提案モードが有効か(プレビュークリックを提案に使う)。</summary>
        public bool Active { get; private set; }

        public MaskSuggestProposal Pending => _pending;
        public int AcceptedCount => _acceptedPieces.Count;
        public bool HasUnion => _union != null && _acceptedPieces.Count > 0;

        /// <summary>プレビューへ重ねる提案オーバーレイ(なければ null)。</summary>
        public Texture2D OverlayTexture => _overlayTexture;

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
            if (active)
            {
                MaskSuggestBridge.Service?.TryEnsureModels();
            }
            else
            {
                // モードを抜けても採用済み和集合は保持する(誤操作で積み上げを失わない)。
                DiscardPending();
            }
            _host?.RequestRepaint();
        }

        // ─────────────────────── クリック → 提案 ───────────────────────

        /// <summary>
        /// プレビュークリック。pixels は実フル解像度ソース(下原点)。
        /// </summary>
        public void OnPreviewClick(float u, float v, Color32[] pixelsBottomUp,
                                   int width, int height, string sourceKey)
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null || !Active) return;
            if (!svc.TryEnsureModels()) return;

            // テクスチャが変わっていたら積み上げをリセット
            if (_union != null && (width != _unionW || height != _unionH))
                ClearAccumulation();

            svc.SetSource(sourceKey, pixelsBottomUp, width, height);
            svc.RequestProposal(u, v);
        }

        void OnServiceStateChanged()
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null) return;
            if (svc.Phase == MaskSuggestPhase.ProposalReady &&
                svc.TryTakeProposal(out var proposal))
            {
                _pending = proposal;
                _overlayDirty = true;
            }
            _host?.RequestRepaint();
        }

        // ─────────────────────── 採用/やり直し/戻し ───────────────────────

        /// <summary>表示中の提案を和集合へ追加する。</summary>
        public void AcceptPending()
        {
            if (_pending == null) return;
            EnsureUnion(_pending.width, _pending.height);
            var mask = _pending.maskBottomUp;
            _acceptedPieces.Add(MaskRle.Encode(mask, _unionW, _unionH));
            for (int i = 0; i < _union.Length; i++)
                if (mask[i]) _union[i] = true;
            _pending = null;
            _overlayDirty = true;
            _host?.RequestRepaint();
        }

        /// <summary>表示中の提案を破棄する(やり直し = 別の場所をクリック)。</summary>
        public void DiscardPending()
        {
            if (_pending == null) return;
            _pending = null;
            _overlayDirty = true;
            _host?.RequestRepaint();
        }

        /// <summary>最後に追加したピースを取り消し、和集合を組み直す。</summary>
        public void UndoLastAccepted()
        {
            if (_acceptedPieces.Count == 0) return;
            _acceptedPieces.RemoveAt(_acceptedPieces.Count - 1);
            RebuildUnionFromPieces();
            _overlayDirty = true;
            _host?.RequestRepaint();
        }

        /// <summary>積み上げと提案をすべて破棄する。</summary>
        public void ClearAccumulation()
        {
            _pending = null;
            _union = null;
            _acceptedPieces.Clear();
            _overlayDirty = true;
            _host?.RequestRepaint();
        }

        /// <summary>
        /// 積み上げた和集合を既存の除外マスク(編集対象に従う)へ確定する。
        /// invert=false: 選択部分を除外に追加 / true: 選択部分「以外」を除外に追加。
        /// 確定後は通常のマスクとしてブラシ修正・Ctrl+Z(1 ストローク扱い)が効く。
        /// </summary>
        public void CommitToMask(bool invert)
        {
            if (!HasUnion || _maskView == null) return;
            _maskView.EnsureMasks();
            var mask = _maskView.GetActiveMaskArray();
            int mw = _maskView.maskWidth, mh = _maskView.maskHeight;
            if (mask == null || mw <= 0 || mh <= 0) return;

            _maskView.BeginStroke();
            if (mw == _unionW && mh == _unionH)
            {
                for (int i = 0; i < mask.Length; i++)
                    if (_union[i] != invert) mask[i] = true;
            }
            else
            {
                // マスク解像度がソースと異なる場合は最近傍で転写(EnsureMasks のリスケールと同方針)
                for (int my = 0; my < mh; my++)
                {
                    int sy = (int)((long)my * _unionH / mh);
                    int srcRow = sy * _unionW, dstRow = my * mw;
                    for (int mx = 0; mx < mw; mx++)
                    {
                        int sx = (int)((long)mx * _unionW / mw);
                        if (_union[srcRow + sx] != invert) mask[dstRow + mx] = true;
                    }
                }
            }
            _maskView.EndStroke();
            _maskView.maskDirty = true;
            ClearAccumulation();
            _host?.MarkPreviewDirty();
            _host?.RequestRepaint();
        }

        /// <summary>Unity Undo/Redo 実行時: 保留中提案は古い状態に重なるため破棄する。</summary>
        public void OnUndoRedoPerformed() => DiscardPending();

        /// <summary>テクスチャ切替・ウィンドウ破棄時の後始末。</summary>
        public void OnSourceChangedOrClosing()
        {
            MaskSuggestBridge.Service?.CancelAll();
            ClearAccumulation();
            ReleaseOverlay();
        }

        // ─────────────────────── オーバーレイ ───────────────────────

        static readonly Color32 PendingColor = new Color32(80, 200, 255, 110);   // 提案(未採用)
        static readonly Color32 AcceptedColor = new Color32(80, 230, 140, 80);   // 採用済み和集合

        /// <summary>必要ならオーバーレイテクスチャを組み直す(メインスレッド・毎 GUI 呼び出し可)。</summary>
        public void UpdateOverlayIfNeeded()
        {
            if (!_overlayDirty) return;
            _overlayDirty = false;

            bool hasPending = _pending != null;
            bool hasUnion = HasUnion;
            if (!hasPending && !hasUnion)
            {
                ReleaseOverlay();
                return;
            }

            int srcW = hasPending ? _pending.width : _unionW;
            int srcH = hasPending ? _pending.height : _unionH;
            if (srcW <= 0 || srcH <= 0) { ReleaseOverlay(); return; }

            // 実寸テクスチャは重い(4096²=64MB)ので長辺 1024 に落として引き伸ばし描画する
            // (既存マスクオーバーレイと同じ「低解像度を全体へ引き伸ばす」方針)。
            float s = Mathf.Min(1f, OverlayMaxSize / (float)Mathf.Max(srcW, srcH));
            int ow = Mathf.Max(1, Mathf.RoundToInt(srcW * s));
            int oh = Mathf.Max(1, Mathf.RoundToInt(srcH * s));

            if (_overlayTexture == null || _overlayTexture.width != ow || _overlayTexture.height != oh)
            {
                ReleaseOverlay();
                _overlayTexture = new Texture2D(ow, oh, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }

            var px = new Color32[ow * oh];
            var clear = new Color32(0, 0, 0, 0);
            for (int y = 0; y < oh; y++)
            {
                int sy = (int)((long)y * srcH / oh);
                int srcRow = sy * srcW, dstRow = y * ow;
                for (int x = 0; x < ow; x++)
                {
                    int sx = (int)((long)x * srcW / ow);
                    int si = srcRow + sx;
                    if (hasPending && _pending.maskBottomUp[si]) px[dstRow + x] = PendingColor;
                    else if (hasUnion && _union[si]) px[dstRow + x] = AcceptedColor;
                    else px[dstRow + x] = clear;
                }
            }
            _overlayTexture.SetPixels32(px);
            _overlayTexture.Apply(false);
        }

        void ReleaseOverlay()
        {
            if (_overlayTexture != null)
            {
                Object.DestroyImmediate(_overlayTexture);
                _overlayTexture = null;
            }
        }

        void EnsureUnion(int w, int h)
        {
            if (_union != null && _unionW == w && _unionH == h) return;
            _union = new bool[w * h];
            _unionW = w;
            _unionH = h;
            _acceptedPieces.Clear();
        }

        void RebuildUnionFromPieces()
        {
            if (_union == null) return;
            System.Array.Clear(_union, 0, _union.Length);
            foreach (var rle in _acceptedPieces)
            {
                var piece = MaskRle.Decode(rle, out int w, out int h);
                if (piece == null || w != _unionW || h != _unionH) continue;
                for (int i = 0; i < _union.Length; i++)
                    if (piece[i]) _union[i] = true;
            }
        }
    }
}
