// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI 提案の待ち行列の唯一の持ち主(0.4.0 の計画の 4-3)。サービス(<see cref="MaskSuggestBridge.Service"/>)の状態の
    /// 変化を購読するのはここだけで、求めた順に受け取り先を積み(サービスは受理した順に 1 件ずつ返す)、届いた提案を
    /// その受け取り先へ返す。求めるのは、ウィンドウの右クリックの提案(<see cref="MaskSuggestController"/>)と、
    /// 自動調整の証拠(ウィンドウと自動化)。
    ///
    /// 以前はウィンドウごとのコントローラがそれぞれサービスを購読し、自動化(MCP)の自動調整はウィンドウのコントローラを
    /// 借りていた(窓が無ければサービスを直接使った)。購読が 2 つ並ぶと、先に購読した方が提案を取って捨てる
    /// (閉じたウィンドウの購読が残ったとき。自動化と窓が同時に使うときは、自動化が窓のコントローラを探して借りていた)。
    ///
    /// 元(ソース画像)は 1 つだけ: サービスは元を切り替えると待ち行列を黙って捨てるので、切り替えるときは並んでいる
    /// 受け取り先にも「来ない」(null)を返してそろえる。メインスレッド専用。
    /// 計画では Application の層だったが、右クリックのコントローラ(この段)からも使うので、サービスの窓口
    /// (<see cref="MaskSuggestBridge"/>)と同じ段に置いた。
    /// </summary>
    internal static class AiSuggestBroker
    {
        private sealed class Ticket
        {
            public object owner;
            // null = 取り消した(届いたら捨てる。サービスの待ち行列からは選んで抜けないので、順番は残す)。
            public Action<MaskSuggestProposal> onResult;
        }

        // サービスの待ち行列(処理中の 1 件を含む)と同じ順。
        private static readonly List<Ticket> s_queue = new List<Ticket>();
        private static IMaskSuggestService s_subscribed;
        // ここが最後にサービスへ渡した元のキー(null = 渡していない・手放させた)。
        private static string s_sourceKey;

        /// <summary>サービスの状態が変わった(描き直しに使う)。</summary>
        internal static event Action StateChanged;

        /// <summary><see cref="RequestWhenIdle"/> の結果。</summary>
        internal enum Request
        {
            /// <summary>受理した。結果は onResult で 1 回だけ返る。</summary>
            Started,
            /// <summary>AI が準備中(モデルの読み込み・埋め込みの計算・ほかの提案の処理)。後で求め直す。</summary>
            Busy,
            /// <summary>AI が使えない(Sentis・モデルが無い・エラー)。</summary>
            Unavailable,
        }

        /// <summary>
        /// サービス(無ければ null)。登録し直されたら(ドメインリロード・テストの差し替え)、購読も付け替える。
        /// </summary>
        private static IMaskSuggestService Service
        {
            get
            {
                var svc = MaskSuggestBridge.Service;
                if (!ReferenceEquals(svc, s_subscribed))
                {
                    if (s_subscribed != null) s_subscribed.StateChanged -= OnServiceStateChanged;
                    s_subscribed = svc;
                    if (svc != null) svc.StateChanged += OnServiceStateChanged;
                    // 前のサービスに並べた分はもう届かない。
                    FailAll();
                    s_sourceKey = null;
                }
                return svc;
            }
        }

        /// <summary>
        /// 提案を 1 件求める(右クリックの提案)。処理中の要求の後ろに並ぶ。元が今の元と違えば、並んでいる要求を
        /// 取り消して切り替える(ユーザーが今押したものを先にする)。結果は onResult で 1 回だけ返る
        /// (取り消し・破棄・エラーのときは null)。戻り値 false = 受理できなかった(モデルを読めないなど)。
        /// pixels は下原点。サービスは自分の参照を足して持つ。
        /// </summary>
        internal static bool Enqueue(object owner, string sourceKey, NativePixels pixels, float u, float v,
            MaskSuggestGranularity granularity, Action<MaskSuggestProposal> onResult)
        {
            var svc = Service;
            if (svc == null) return false;
            // エラー表示のまま選び直したときは、ここが再試行の導線になる
            // (自動再試行は原因が直らないまま走り続けるので入れない)。
            if (svc.Phase == MaskSuggestPhase.Error) CancelAllOn(svc);
            if (!svc.TryEnsureModels()) return false;
            UseSource(svc, sourceKey, pixels);
            return Submit(svc, owner, u, v, granularity, onResult);
        }

        /// <summary>
        /// AI が待機中のときだけ提案を 1 件求める(自動調整の証拠)。準備中・ほかの元の要求が並んでいる間は
        /// <see cref="Request.Busy"/> で、呼ぶ側が後で求め直す(埋め込みの計算は、ここで元を渡したときに始まる)。
        /// 結果は onResult で 1 回だけ返る(取り消し・破棄のときは null)。
        /// </summary>
        internal static Request RequestWhenIdle(object owner, string sourceKey, NativePixels pixels, float u, float v,
            Action<MaskSuggestProposal> onResult)
        {
            var svc = Service;
            if (svc == null || onResult == null) return Request.Unavailable;
            if (!svc.TryEnsureModels()) return Request.Unavailable;
            if (svc.Phase == MaskSuggestPhase.Error) return Request.Unavailable;
            // ほかの元の要求を、元を切り替えて捨てない。空くまで待つ。
            if (sourceKey != s_sourceKey && HasLive) return Request.Busy;
            UseSource(svc, sourceKey, pixels);
            if (svc.Phase != MaskSuggestPhase.Idle) return Request.Busy;
            return Submit(svc, owner, u, v, MaskSuggestGranularity.Auto, onResult) ? Request.Started : Request.Unavailable;
        }

        /// <summary>
        /// 求める前に元の埋め込みの計算を始めておく(右クリックのメニューを開いたとき)。待機中だけ
        /// (モデルの読み込みは重いのでここでは始めない。エラー中も再試行しない)。
        /// </summary>
        internal static void Prepare(string sourceKey, NativePixels pixels)
        {
            var svc = Service;
            if (svc == null || svc.Phase != MaskSuggestPhase.Idle) return;
            if (sourceKey != s_sourceKey && HasLive) return;
            UseSource(svc, sourceKey, pixels);
        }

        /// <summary>サービスの状態の変化を受け取り始める(描き直しのために <see cref="StateChanged"/> を使う側が呼ぶ)。</summary>
        internal static void EnsureSubscribed() => _ = Service;

        /// <summary>
        /// 持ち主の要求を取り消す(Esc・Undo・期限切れ・中止)。受け取り先には「来ない」(null)を返す。並んでいるのが
        /// この持ち主の要求だけなら、サービスの待ち行列も捨てる(処理中の 1 件は完走後に捨てられる)。ほかの持ち主の
        /// 要求が混ざっているときは、届いた分を捨てる。戻り値 true = 取り消すものがあった。
        /// </summary>
        internal static bool Cancel(object owner)
        {
            List<Ticket> mine = null;
            bool others = false;
            foreach (var t in s_queue)
            {
                if (t.onResult == null) continue;
                if (ReferenceEquals(t.owner, owner)) (mine ??= new List<Ticket>()).Add(t);
                else others = true;
            }
            if (!others && s_queue.Count > 0)
            {
                MaskSuggestBridge.Service?.FlushPendingClicks();
                s_queue.Clear();
            }
            if (mine == null) return false;
            foreach (var t in mine) Deliver(t, null);
            return true;
        }

        /// <summary>
        /// 持ち主が手を引く(テクスチャの切り替え・ウィンドウを閉じる)。要求を取り消し(受け取り先には null)、ほかの
        /// 持ち主の要求が無ければサービスの処理も止めて、元の画素と埋め込みの参照を手放させる。
        /// </summary>
        internal static void Release(object owner)
        {
            Cancel(owner);
            if (HasLive) return;
            var svc = MaskSuggestBridge.Service;
            if (svc != null) CancelAllOn(svc);
        }

        /// <summary>
        /// ほかに要求が無ければ、推論のワーカー(GPU のメモリ)も手放させる(ウィンドウを閉じた・窓の無い自動化が
        /// 終わった)。次に使うときはサービスが作り直す。
        /// </summary>
        internal static void ReleaseGpuIfUnused()
        {
            if (HasLive) return;
            var svc = MaskSuggestBridge.Service;
            if (svc == null) return;
            s_queue.Clear();
            s_sourceKey = null;
            svc.ReleaseGpuResources();
        }

        /// <summary>まだ受け取り先のある要求があるか。</summary>
        internal static bool HasLive
        {
            get
            {
                foreach (var t in s_queue)
                    if (t.onResult != null) return true;
                return false;
            }
        }

        /// <summary>持ち主のまだ届いていない要求の数(テスト・表示用)。</summary>
        internal static int PendingCount(object owner)
        {
            int n = 0;
            foreach (var t in s_queue)
                if (t.onResult != null && ReferenceEquals(t.owner, owner)) n++;
            return n;
        }

        private static void UseSource(IMaskSuggestService svc, string sourceKey, NativePixels pixels)
        {
            if (sourceKey != s_sourceKey)
            {
                // サービスは元を切り替えると待ち行列を黙って捨てる。受け取り先にも「来ない」を返してそろえる。
                FailAll();
                s_sourceKey = sourceKey;
            }
            svc.SetSource(sourceKey, pixels);   // 同じ元なら何もしない(埋め込みのキャッシュ)
        }

        private static bool Submit(IMaskSuggestService svc, object owner, float u, float v,
            MaskSuggestGranularity granularity, Action<MaskSuggestProposal> onResult)
        {
            // 受け取り先を先に積む(サービスがその場で提案を出しても、誰のものか決まっているように)。
            var t = new Ticket { owner = owner, onResult = onResult };
            s_queue.Add(t);
            if (svc.RequestProposal(u, v, granularity)) return true;
            s_queue.Remove(t);
            return false;
        }

        private static void CancelAllOn(IMaskSuggestService svc)
        {
            FailAll();
            s_sourceKey = null;
            svc.CancelAll();
        }

        // 並んでいる受け取り先に「来ない」を返して空にする(サービスが待ち行列を捨てたときと同じ所で呼ぶ)。
        private static void FailAll()
        {
            if (s_queue.Count == 0) return;
            var pending = s_queue.ToArray();
            s_queue.Clear();
            foreach (var t in pending) Deliver(t, null);
        }

        private static void OnServiceStateChanged()
        {
            var svc = MaskSuggestBridge.Service;
            if (svc == null || !ReferenceEquals(svc, s_subscribed)) return;
            // 自分が求めた分だけを受け取る(並んでいないのに出ている提案は、サービスを直接使った誰かのもの)。
            if (svc.Phase == MaskSuggestPhase.ProposalReady && s_queue.Count > 0)
            {
                // 取り出すと、サービスは次の処理へ進む(状態の変化でここへ入り直すが、そのときは ProposalReady ではない)。
                if (svc.TryTakeProposal(out var proposal))
                {
                    var t = s_queue[0];
                    s_queue.RemoveAt(0);
                    Deliver(t, proposal);
                }
            }
            else if (svc.Phase == MaskSuggestPhase.Error)
            {
                // エラーでは、処理待ちがサービス側で捨てられる(復帰は右クリックからの選び直し)。
                FailAll();
            }
            StateChanged?.Invoke();
        }

        private static void Deliver(Ticket t, MaskSuggestProposal proposal)
        {
            var cb = t.onResult;
            t.onResult = null;
            if (cb == null) return;
            // 受け取り先の例外で、待ち行列とサービスの対応を崩さない。
            try { cb(proposal); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}
