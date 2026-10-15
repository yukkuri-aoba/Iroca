// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// フル段プレビューの出力バッファ(フル解像度の Color32[]。4K で 64MB)を使い回す。
    /// 毎回 new すると、置き換えられた前の出力が 1 回ごとに 64MB のゴミになる。Unity の Mono(Boehm)は
    /// ゴミが溜まるとヒープを広げ、広げたヒープは OS に返らないので、編集を続けるほどエディタのメモリが増える。
    ///
    /// 使い回してよいのは「もう誰も読み書きしない」バッファだけ:
    /// <list type="bullet">
    /// <item>置き換えられた前の出力・取り消されたジョブの作業バッファだけを <see cref="Retire"/> で受け取る
    /// (ジョブは自分の作業を抜けてから返すので、書き込み中のものは入らない)。</item>
    /// <item>拡大表示のジョブが切り出し中のバッファは <see cref="AddLease"/>/<see cref="ReleaseLease"/> で印を付け、
    /// 印がある間は渡さない。印は弱参照の表で持つので、開始前に取り消されて外し損ねても配列を握り続けない。</item>
    /// <item>シーンへの反映(LivePreview)が持っている画素は、取り出す側が heldElsewhere で除く。</item>
    /// </list>
    /// 置いておくのは 1 本だけ(それ以上は捨てて GC に任せる)。出力の内容はどのバッファでも同じ。
    /// </summary>
    internal sealed class PreviewOutputBuffers
    {
        private const int MaxRetired = 1;

        private sealed class LeaseCount { public int n; }

        private readonly object _gate = new object();
        private readonly List<Color32[]> _retired = new List<Color32[]>();
        private readonly ConditionalWeakTable<Color32[], LeaseCount> _leases = new ConditionalWeakTable<Color32[], LeaseCount>();

        /// <summary>長さ len の使い回せるバッファを取り出す。無ければ null(呼び出し側が new する)。</summary>
        public Color32[] Take(int len, Func<Color32[], bool> heldElsewhere)
        {
            lock (_gate)
            {
                for (int i = 0; i < _retired.Count; i++)
                {
                    var b = _retired[i];
                    if (b.Length != len) continue;
                    if (_leases.TryGetValue(b, out var lc) && lc.n > 0) continue;
                    if (heldElsewhere != null && heldElsewhere(b)) continue;
                    _retired.RemoveAt(i);
                    return b;
                }
            }
            return null;
        }

        /// <summary>もう書き込まれないバッファを置く(どのスレッドからでもよい)。</summary>
        public void Retire(Color32[] buffer)
        {
            if (buffer == null) return;
            lock (_gate)
            {
                foreach (var b in _retired)
                    if (ReferenceEquals(b, buffer)) return;
                _retired.Add(buffer);
                while (_retired.Count > MaxRetired) _retired.RemoveAt(0);
            }
        }

        /// <summary>読み取り中の印を付ける(メインスレッドでジョブを予約するとき)。</summary>
        public void AddLease(Color32[] buffer)
        {
            if (buffer == null) return;
            lock (_gate) { _leases.GetOrCreateValue(buffer).n++; }
        }

        /// <summary>読み取り中の印を外す(ジョブの作業の最後。どのスレッドからでもよい)。</summary>
        public void ReleaseLease(Color32[] buffer)
        {
            if (buffer == null) return;
            lock (_gate)
            {
                if (_leases.TryGetValue(buffer, out var lc) && lc.n > 0) lc.n--;
            }
        }

        /// <summary>置いてあるバッファを捨てる(元テクスチャが変わったとき)。</summary>
        public void Clear()
        {
            lock (_gate) { _retired.Clear(); }
        }
    }
}
