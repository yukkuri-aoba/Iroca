// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Effect = Iroca.MaterialColorSettings.Effect;

namespace Iroca
{
    /// <summary>
    /// プレビューの画像に、マテリアルの色の設定(<see cref="MaterialColorSettings"/>)を掛けた「アバター上の色」を作る。
    /// 光や影は入れない(シーンのプレビューが受け持つ)。
    ///
    /// lilToon は自前で計算し直さず、lilToon 自身の焼き込みシェーダー(インスペクタの「焼き込み」が使う
    /// Hidden/ltsother_baker)へマテリアルの値を写して描く。メインカラー・色調補正・グラデーションマップ・
    /// 2nd / 3rd の重ね描きまで、lilToon の焼き込みと同じ結果になる。それ以外は色の掛け算だけを
    /// <see cref="ColorTintLut"/> で当てる(標準・URP・VRChat の Mobile 系などはこれが albedo の全部)。
    ///
    /// 拡大表示の切り出しは、テクスチャの上の位置で値が変わるもの(重ね描きの層・色調補正のマスク)が
    /// あるとき、切り出しを元の位置に貼った作業用の RenderTexture を経由して、テクスチャ全体と同じ座標で描く。
    /// </summary>
    internal sealed class AppearanceRenderer : System.IDisposable
    {
        internal enum Kind
        {
            /// <summary>プレビューに掛けるものが無い(テクスチャのまま)。</summary>
            None,
            /// <summary>色の掛け算だけ。</summary>
            Tint,
            /// <summary>lilToon の焼き込みシェーダーで描く。</summary>
            LilToonBaker,
        }

        private const string LilToonBakerName = "Hidden/ltsother_baker";
        private static Shader s_lilToonBaker;

        /// <summary>lilToon の焼き込みシェーダー。lilToon が入っていない・古くて無いときは null。</summary>
        internal static Shader LilToonBaker
        {
            get
            {
                if (s_lilToonBaker == null) s_lilToonBaker = Shader.Find(LilToonBakerName);
                return s_lilToonBaker;
            }
        }

        /// <summary>この読み取り結果をプレビューにどう掛けるか。</summary>
        internal static Kind KindOf(MaterialColorSettings.Report r)
        {
            if (r == null || !r.AffectsColor) return Kind.None;
            if (r.slot == MaterialColorSettings.Slot.Main && r.family == MaterialColorSettings.Family.LilToon
                && LilToonBaker != null && (r.effects & ~Effect.LayerOtherUv) != 0)
                return Kind.LilToonBaker;
            return (r.effects & Effect.Tint) != 0 ? Kind.Tint : Kind.None;
        }

        /// <summary>見つけた色の設定のうち、プレビューに入っていないもの。</summary>
        internal static Effect NotPreviewed(MaterialColorSettings.Report r)
        {
            if (r == null) return Effect.None;
            const Effect poiyomi = Effect.PoiColorAdjust | Effect.PoiDecal | Effect.PoiRgbMask;
            switch (KindOf(r))
            {
                case Kind.LilToonBaker: return r.effects & (Effect.LayerOtherUv | poiyomi);
                case Kind.Tint: return r.effects & ~Effect.Tint;
                default: return r.effects;
            }
        }

        // lilToon の焼き込み用のマテリアル(写した値の要約が変わったら作り直す)。
        private Material _baker;
        private string _bakerFor;
        // 切り出しを元の位置に貼る作業用(テクスチャ全体の寸法。拡大表示に重ね描きの層があるときだけ)と、貼るのに使うマテリアル。
        private RenderTexture _scratch;
        private Material _copy;
        // 色の掛け算の表(チャンネルごと)と、その作り元。
        private byte[] _lutR, _lutG, _lutB;
        private Color _lutTint;
        private bool _lutLinear, _lutBuilt;

        /// <summary>
        /// テクスチャ全体の画像 src(UV の 0〜1 が画像全体)に色の設定を掛けて dst へ書く。
        /// 掛けるものが無ければ false(dst は触らない)。
        /// </summary>
        public bool Render(MaterialColorSettings.Report r, Texture2D src, ref Texture2D dst)
            => RenderRegion(r, src, new Rect(0f, 0f, 1f, 1f), 0, 0, ref dst);

        /// <summary>
        /// テクスチャの一部の画像 crop に色の設定を掛けて dst へ書く。uvRect は crop が覆う範囲(テクスチャ全体を
        /// 0〜1 とした UV、下原点)、fullW / fullH はテクスチャ全体の画素数(作業用の寸法を決めるのに使う)。
        /// </summary>
        public bool RenderRegion(MaterialColorSettings.Report r, Texture2D crop, Rect uvRect, int fullW, int fullH,
                                 ref Texture2D dst)
        {
            if (crop == null) return false;
            switch (KindOf(r))
            {
                case Kind.Tint:
                    ApplyTint(r.tint, crop, ref dst);
                    return true;
                case Kind.LilToonBaker:
                    PrepareBaker(r);
                    if (IsWhole(uvRect) || !DependsOnUv(r.material))
                        BlitBaker(crop, ref dst);
                    else
                        BakeRegion(crop, uvRect, fullW, fullH, ref dst);
                    return true;
                default:
                    return false;
            }
        }

        // ── 色の掛け算 ─────────────────────────────────────────────

        private unsafe void ApplyTint(Color tint, Texture2D src, ref Texture2D dst)
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            if (!_lutBuilt || _lutTint != tint || _lutLinear != linear)
            {
                _lutR = ColorTintLut.Build(tint.r, linear);
                _lutG = ColorTintLut.Build(tint.g, linear);
                _lutB = ColorTintLut.Build(tint.b, linear);
                _lutTint = tint;
                _lutLinear = linear;
                _lutBuilt = true;
            }
            TextureSlot.Resize(ref dst, src.width, src.height);
            dst.filterMode = src.filterMode;
            var s = src.GetRawTextureData<Color32>();
            var d = dst.GetRawTextureData<Color32>();
            ColorTintLut.Apply((Color32*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(s),
                               (Color32*)NativeArrayUnsafeUtility.GetUnsafePtr(d), s.Length, _lutR, _lutG, _lutB);
            dst.Apply(false);
        }

        // ── lilToon ───────────────────────────────────────────────

        /// <summary>
        /// マテリアルの値を焼き込みシェーダーへ写す。焼き込みシェーダーにある名前のうち、マテリアルにもあるものを
        /// 全部写す(lilToon のインスペクタの焼き込みが写すものを含む。版が変わって増えた値も追える)。
        /// UV0 以外で重ねる層は、テクスチャの上の位置が決まらないので切る。
        /// </summary>
        private void PrepareBaker(MaterialColorSettings.Report r)
        {
            if (_baker != null && _bakerFor == r.signature) return;
            DestroyNow(ref _baker);
            var shader = LilToonBaker;
            _baker = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var src = r.material;
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = shader.GetPropertyName(i);
                if (name == "_MainTex" || !src.HasProperty(name)) continue;
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color: _baker.SetColor(name, src.GetColor(name)); break;
                    case ShaderPropertyType.Vector: _baker.SetVector(name, src.GetVector(name)); break;
                    case ShaderPropertyType.Int: _baker.SetInteger(name, src.GetInteger(name)); break;
                    case ShaderPropertyType.Texture:
                        _baker.SetTexture(name, src.GetTexture(name));
                        _baker.SetTextureScale(name, src.GetTextureScale(name));
                        _baker.SetTextureOffset(name, src.GetTextureOffset(name));
                        break;
                    default: _baker.SetFloat(name, src.GetFloat(name)); break;
                }
            }
            DisableOtherUvLayer(src, "_UseMain2ndTex", "_Main2ndTex_UVMode");
            DisableOtherUvLayer(src, "_UseMain3rdTex", "_Main3rdTex_UVMode");
            _bakerFor = r.signature;
        }

        private void DisableOtherUvLayer(Material src, string use, string uvMode)
        {
            if (src.HasProperty(uvMode) && Mathf.RoundToInt(src.GetFloat(uvMode)) != 0 && _baker.HasProperty(use))
                _baker.SetFloat(use, 0f);
        }

        /// <summary>
        /// テクスチャの上の位置で値が変わるものを描くか(重ね描きの層・色調補正のマスク)。無ければ、どの切り出しも
        /// 画素ごとの変換なので、切り出しをそのまま焼き込みシェーダーに通せばよい。
        /// </summary>
        private bool DependsOnUv(Material src)
        {
            if (_baker.HasProperty("_UseMain2ndTex") && _baker.GetFloat("_UseMain2ndTex") > 0.5f) return true;
            if (_baker.HasProperty("_UseMain3rdTex") && _baker.GetFloat("_UseMain3rdTex") > 0.5f) return true;
            return src.HasProperty("_MainColorAdjustMask") && src.GetTexture("_MainColorAdjustMask") != null;
        }

        // 画像全体(UV の 0〜1)を焼き込みシェーダーに通す。lilToon のインスペクタの焼き込みと同じ手順
        // (sRGB のテクスチャ → 既定の読み書きの RenderTexture → ReadPixels)。
        private void BlitBaker(Texture2D src, ref Texture2D dst)
        {
            // Blit は描き先を rt にしたまま返るので、前の描き先は先に控えて戻す(ウィンドウの描画中に呼ばれる)。
            var prev = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Graphics.Blit(src, rt, _baker);
            ReadBack(rt, src.filterMode, ref dst);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }

        /// <summary>
        /// 切り出しを、作業用の RenderTexture(テクスチャ全体を切り出しと同じ密度で持つ)の元の位置に貼り、
        /// そこを UV で指して焼き込みシェーダーに通す。焼き込みシェーダーはメインテクスチャと重ね描きの層を
        /// 同じ UV で読むので、切り出しだけを渡すと層の位置がずれる。
        /// </summary>
        private void BakeRegion(Texture2D crop, Rect uvRect, int fullW, int fullH, ref Texture2D dst)
        {
            int sw = Mathf.Max(1, Mathf.RoundToInt(crop.width / Mathf.Max(1e-6f, uvRect.width)));
            int sh = Mathf.Max(1, Mathf.RoundToInt(crop.height / Mathf.Max(1e-6f, uvRect.height)));
            // 拡大しているときは元の画素数で頭打ち(それ以上細かくしても値は増えない)。
            if (fullW > 0) sw = Mathf.Min(sw, fullW);
            if (fullH > 0) sh = Mathf.Min(sh, fullH);
            EnsureScratch(sw, sh);
            // 貼るのは Graphics.Blit が使う写しのシェーダー(裏面も描く)。RenderTexture へ描くと上下が返る環境で、
            // 裏面を落とすシェーダーだと四角が消える。
            if (_copy == null)
                _copy = new Material(Shader.Find("Hidden/BlitCopy") ?? Shader.Find("Unlit/Texture"))
                        { hideFlags = HideFlags.HideAndDontSave };
            _copy.mainTexture = crop;
            DrawQuad(_scratch, _copy, uvRect, new Rect(0f, 0f, 1f, 1f));

            var rt = RenderTexture.GetTemporary(crop.width, crop.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            _baker.mainTexture = _scratch;
            DrawQuad(rt, _baker, new Rect(0f, 0f, 1f, 1f), uvRect);
            _baker.mainTexture = null;
            ReadBack(rt, crop.filterMode, ref dst);
            RenderTexture.ReleaseTemporary(rt);
        }

        private void EnsureScratch(int w, int h)
        {
            if (_scratch != null && _scratch.width == w && _scratch.height == h && _scratch.IsCreated()) return;
            ReleaseScratch();
            _scratch = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _scratch.Create();
        }

        /// <summary>
        /// target の pos(0〜1、下原点)の範囲に、UV が uv の範囲を指す四角を mat で描く
        /// (Graphics.Blit と同じ描き方で、UV だけを好きな範囲にしたもの)。
        /// </summary>
        private static void DrawQuad(RenderTexture target, Material mat, Rect pos, Rect uv)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();
            GL.LoadOrtho();
            mat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.TexCoord2(uv.xMin, uv.yMin); GL.Vertex3(pos.xMin, pos.yMin, 0f);
            GL.TexCoord2(uv.xMin, uv.yMax); GL.Vertex3(pos.xMin, pos.yMax, 0f);
            GL.TexCoord2(uv.xMax, uv.yMax); GL.Vertex3(pos.xMax, pos.yMax, 0f);
            GL.TexCoord2(uv.xMax, uv.yMin); GL.Vertex3(pos.xMax, pos.yMin, 0f);
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        private static void ReadBack(RenderTexture rt, FilterMode filter, ref Texture2D dst)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            TextureSlot.Resize(ref dst, rt.width, rt.height);
            dst.filterMode = filter;
            dst.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            dst.Apply(false);
            RenderTexture.active = prev;
        }

        private static bool IsWhole(Rect uv)
            => uv.xMin <= 0f && uv.yMin <= 0f && uv.xMax >= 1f && uv.yMax >= 1f;

        /// <summary>作業用の RenderTexture を手放す(拡大表示をやめた・テクスチャを替えたとき)。</summary>
        public void ReleaseScratch()
        {
            if (_scratch == null) return;
            _scratch.Release();
            Object.DestroyImmediate(_scratch);
            _scratch = null;
        }

        public void Dispose()
        {
            ReleaseScratch();
            DestroyNow(ref _baker);
            DestroyNow(ref _copy);
            _bakerFor = null;
        }

        private static void DestroyNow(ref Material m)
        {
            if (m == null) return;
            Object.DestroyImmediate(m);
            m = null;
        }
    }
}
