// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// プレビューの差分表示を、描いている変更前・変更後のテクスチャから GPU で描く(<c>PreviewDiff.shader</c>)。
    /// 以前は差分の画像を別のジョブで CPU で作ってから上げていたので、画像より 1 コマ以上遅れて届き、スライダーの
    /// ドラッグ中やパン・ズーム中に古い差分が重なっていた。差分のテクスチャ・管理配列も要らなくなる。
    /// 判定の定義(8 ビットの値で RGB の差の合計が 10 を超えたら塗る)はシェーダーにだけあり、EditMode テストが
    /// CPU で同じ定義を書いて比べる。持ち主(PreviewView)が Dispose でマテリアルを手放す。
    /// </summary>
    internal sealed class PreviewDiffDrawer : System.IDisposable
    {
        internal const string ShaderName = "Hidden/Iroca/PreviewDiff";

        private static readonly int BeforeTexId = Shader.PropertyToID("_BeforeTex");
        private static readonly int TexSizeId = Shader.PropertyToID("_TexSize");
        private static readonly int SmoothId = Shader.PropertyToID("_Smooth");

        private Material _material;
        private bool _missingLogged;

        /// <summary>
        /// 差分を描くマテリアル。変更後を mainTexture、変更前を _BeforeTex に置く(同じ寸法であること)。
        /// シェーダーが見つからないときは null(1 回だけ警告する)。
        /// </summary>
        internal Material MaterialFor(Texture before, Texture after)
        {
            if (_material == null)
            {
                var shader = Shader.Find(ShaderName);
                if (shader == null)
                {
                    if (!_missingLogged) Debug.LogWarning($"[Iroca] Shader not found: {ShaderName}. The diff view is not drawn.");
                    _missingLogged = true;
                    return null;
                }
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            _material.mainTexture = after;
            _material.SetTexture(BeforeTexId, before);
            _material.SetVector(TexSizeId, new Vector4(after.width, after.height, 0f, 0f));
            // 変更後のテクスチャの補間に合わせる(Point なら画素ごとの四角、Bilinear なら縁をなじませる)。
            _material.SetFloat(SmoothId, after.filterMode == FilterMode.Point ? 0f : 1f);
            return _material;
        }

        /// <summary>
        /// screenRect に、変更前・変更後(同じ寸法)の uv の範囲(下端が 0)の差分を描く。Repaint のときだけ描く。
        /// 2 枚の寸法が違う・どちらかが無いときは何もしない。
        /// </summary>
        internal void Draw(Rect screenRect, Texture before, Texture after, Rect uv)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            if (screenRect.width <= 0f || screenRect.height <= 0f) return;
            if (before == null || after == null || before.width != after.width || before.height != after.height) return;
            var mat = MaterialFor(before, after);
            if (mat == null) return;
            Graphics.DrawTexture(screenRect, after, uv, 0, 0, 0, 0, mat);
        }

        /// <summary>
        /// full(画像全体の矩形)に引き伸ばした差分のうち、hole の外側だけを描く。hole が空(幅 0)なら全体を描く。
        /// 拡大表示の切り出しの外側(新しく見えた端)を、縮小表示の 2 枚から描くのに使う。
        /// </summary>
        internal void DrawOutside(Rect full, Rect hole, Texture before, Texture after)
        {
            if (full.width <= 0f || full.height <= 0f) return;
            hole = hole.width > 0f && hole.height > 0f
                ? Rect.MinMaxRect(
                    Mathf.Clamp(hole.xMin, full.xMin, full.xMax), Mathf.Clamp(hole.yMin, full.yMin, full.yMax),
                    Mathf.Clamp(hole.xMax, full.xMin, full.xMax), Mathf.Clamp(hole.yMax, full.yMin, full.yMax))
                : default;
            if (hole.width <= 0f || hole.height <= 0f)
            {
                Draw(full, before, after, new Rect(0f, 0f, 1f, 1f));
                return;
            }
            // 上・下は全幅、左・右は hole の高さだけ。
            DrawPart(full, Rect.MinMaxRect(full.xMin, full.yMin, full.xMax, hole.yMin), before, after);
            DrawPart(full, Rect.MinMaxRect(full.xMin, hole.yMax, full.xMax, full.yMax), before, after);
            DrawPart(full, Rect.MinMaxRect(full.xMin, hole.yMin, hole.xMin, hole.yMax), before, after);
            DrawPart(full, Rect.MinMaxRect(hole.xMax, hole.yMin, full.xMax, hole.yMax), before, after);
        }

        // full に引き伸ばした差分のうち part(full の内側)に当たる範囲だけを part に描く。
        private void DrawPart(Rect full, Rect part, Texture before, Texture after)
        {
            if (part.width <= 0f || part.height <= 0f) return;
            var uv = new Rect(
                (part.xMin - full.xMin) / full.width,
                1f - (part.yMax - full.yMin) / full.height,
                part.width / full.width,
                part.height / full.height);
            Draw(part, before, after, uv);
        }

        public void Dispose()
        {
            if (_material == null) return;
            Object.DestroyImmediate(_material);
            _material = null;
        }
    }
}
