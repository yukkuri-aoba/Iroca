// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;

namespace Iroca
{
    /// <summary>
    /// フル段プレビューの出力バッファ(フル解像度。4K で 64MB)を使い回す。毎回確保すると、置き換えられた前の出力の
    /// 解放と新しい確保が 1 回ごとに起きる(0.3.0 より前は管理配列で、Unity の Mono(Boehm)はゴミが溜まると
    /// ヒープを広げ、広げたヒープは OS に返らないので、編集を続けるほどエディタのメモリが増えていた)。
    /// 0.3.0 から出力は Mono のヒープの外(<see cref="NativePixels"/>)に置き、ここも参照の数で持つ。
    ///
    /// 使い回してよいのは「もう誰も読み書きしない」バッファだけ:
    /// <list type="bullet">
    /// <item>置き換えられた前の出力・取り消されたジョブの作業バッファだけを <see cref="Retire"/> で受け取る
    /// (渡した側の参照をこちらが引き取る。ジョブは自分の作業を抜けてから返すので、書き込み中のものは入らない)。</item>
    /// <item>ほかの持ち主(拡大表示のジョブが切り出し中・シーンへの反映が保持中)はそれぞれ参照を持っているので、
    /// 参照がこちらの 1 つだけのものしか <see cref="Take"/> で渡さない。</item>
    /// </list>
    /// 置いておくのは 1 本だけ(それ以上は手放す)。出力の内容はどのバッファでも同じ。
    /// </summary>
    internal sealed class PreviewOutputBuffers
    {
        private const int MaxRetired = 1;

        private readonly object _gate = new object();
        private readonly List<NativePixels> _retired = new List<NativePixels>();
        // 閉じた後(Close)に戻ってきたバッファは置かずにすぐ手放す(閉じる前に走り出したジョブが後から返す)。
        private bool _closed;

        /// <summary>
        /// 寸法が同じで、ほかに持ち主のいないバッファを取り出す(参照を 1 つ持たせて返す)。無ければ null(呼び出し側が確保する)。
        /// 参照はメインスレッドでしか足されないので、メインスレッドから呼べば、取り出した後に別の持ち主が現れることはない。
        /// </summary>
        public NativePixels Take(int width, int height)
        {
            lock (_gate)
            {
                for (int i = 0; i < _retired.Count; i++)
                {
                    var b = _retired[i];
                    if (b.Width != width || b.Height != height) continue;
                    if (b.RefCount != 1) continue;
                    _retired.RemoveAt(i);
                    return b;
                }
            }
            return null;
        }

        /// <summary>もう書き込まれないバッファを置く(渡した側の参照を引き取る。どのスレッドからでもよい)。</summary>
        public void Retire(NativePixels buffer)
        {
            if (buffer == null) return;
            NativePixels drop = null;
            lock (_gate)
            {
                // 閉じた後か、同じものを 2 回置かれた(参照も 2 つ渡された)ときは置かない(置くのは 1 回分だけ)。
                if (_closed || _retired.Contains(buffer)) drop = buffer;
                else
                {
                    _retired.Add(buffer);
                    if (_retired.Count > MaxRetired)
                    {
                        drop = _retired[0];
                        _retired.RemoveAt(0);
                    }
                }
            }
            drop?.Release();
        }

        /// <summary>置いてあるバッファを手放す(元テクスチャが変わったとき)。</summary>
        public void Clear() => Drain(close: false);

        /// <summary>置いてあるバッファを手放し、以後に戻ってきたものもすぐ手放す(ウィンドウを閉じるとき)。</summary>
        public void Close() => Drain(close: true);

        private void Drain(bool close)
        {
            NativePixels[] drop;
            lock (_gate)
            {
                if (close) _closed = true;
                drop = _retired.ToArray();
                _retired.Clear();
            }
            foreach (var b in drop) b.Release();
        }
    }
}
