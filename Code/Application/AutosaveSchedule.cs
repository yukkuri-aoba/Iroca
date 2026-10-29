// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using UnityEditor;

namespace Iroca
{
    /// <summary>
    /// 編集内容(ゾーン・色・処理設定・マスク、結び付いたレシピ)の自動保存の予定。
    /// <para>
    /// 以前は保存がテクスチャの切り替え・ウィンドウを閉じる・ドメインリロード・非破壊ビルドの直前だけで、
    /// Unity が落ちると直前の編集が消えた。非破壊ではレシピが成果物そのものなので、編集が止まって
    /// 少ししたら保存する。編集の合図は <see cref="EditingSession.Changed"/>。スライダーのドラッグ中・マスクを
    /// 塗っている途中・書き出しなどの最中(<c>busy</c>)は保存しない。
    /// 保存先はどれも中身が変わっていなければ書き込まないか、同じ中身を書くだけなので、空振りしても害は無い。
    /// </para>
    /// メインスレッド専用。
    /// </summary>
    internal sealed class AutosaveSchedule
    {
        // 最後の編集から保存するまでの待ち。ドラッグ中は編集のたびに延びるので、止まってから 1 回になる。
        private const double DelaySeconds = 2.0;
        // 忙しいときに見直すまでの間。
        private const double BusyRetrySeconds = 0.5;

        private readonly Func<bool> _busy;
        private readonly Action _save;
        private EditingSession _session;
        // 0 = 予定なし。
        private double _due;

        /// <param name="busy">今は保存しない(終わってから 1 回保存する)なら true。</param>
        /// <param name="save">保存する。</param>
        internal AutosaveSchedule(Func<bool> busy, Action save)
        {
            _busy = busy;
            _save = save;
        }

        /// <summary>保存の予定があるか(テスト用)。</summary>
        internal bool IsPending => _due > 0;

        /// <summary><paramref name="session"/> の編集を見始める(前に見ていたものはやめる)。</summary>
        internal void Start(EditingSession session)
        {
            Stop();
            _session = session;
            _session.Changed += OnChanged;
            EditorApplication.update += Tick;
        }

        /// <summary>見るのをやめ、予定も消す(呼ぶ側がこの後で保存する)。</summary>
        internal void Stop()
        {
            if (_session != null) _session.Changed -= OnChanged;
            _session = null;
            EditorApplication.update -= Tick;
            _due = 0;
        }

        private void OnChanged(EditChange _) => _due = EditorApplication.timeSinceStartup + DelaySeconds;

        // EditorApplication.update から毎フレーム呼ばれる。Unity はそこで例外を受け止めないので、投げると
        // 同じフレームで後ろに並んだ他のツールの更新が飛ばされる。ここで受け止め、次の編集まで保存を待つ。
        private void Tick()
        {
            try { TickCore(); }
            catch (Exception e)
            {
                _due = 0;
                UnityEngine.Debug.LogException(e);
            }
        }

        private void TickCore()
        {
            if (_due <= 0 || EditorApplication.timeSinceStartup < _due) return;
            if (_busy())
            {
                _due = EditorApplication.timeSinceStartup + BusyRetrySeconds;
                return;
            }
            _due = 0;
            _save();
        }
    }
}
