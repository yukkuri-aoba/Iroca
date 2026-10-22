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
    /// 2nd / 3rd の重ね描きまで、lilToon の本番の描画で照明を 1 にしたときと同じ色になる(層を照明の前後に
    /// 分けて重ねる設定は 2 回に分けて描く。lilToon のインスペクタの焼き込みはそこを 1 回で描く)。それ以外は色の掛け算だけを
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

        /// <summary>
        /// 焼き込みシェーダーが読むのに Properties に宣言していない値(lilToon のインスペクタの焼き込み
        /// lilEditorTextureBaker.cs も名指しで写している)。写さないと 0 のまま描かれ、重ね描きの UV が
        /// DecalAnimation の 0 割りで壊れる(デカールでない層も同じ関数を通る)。
        /// </summary>
        internal static readonly string[] UndeclaredBakerVectors =
        {
            "_Main2ndTexDecalAnimation", "_Main2ndTexDecalSubParam", "_Main3rdTexDecalAnimation", "_Main3rdTexDecalSubParam",
        };
        internal static readonly string[] UndeclaredBakerFloats = { "_Main2ndTexAlphaMode", "_Main3rdTexAlphaMode" };

        /// <summary>焼き込みシェーダーは読まないが、層の重ね方を決めるのに見る値(照明の前後・表裏)。</summary>
        internal static readonly string[] LayerOrderFloats =
        {
            "_Main2ndEnableLighting", "_Main3rdEnableLighting", "_Main2ndTex_Cull", "_Main3rdTex_Cull",
        };

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

        // lilToon の焼き込み用のマテリアル(写した値の要約が変わったら作り直す)。_bakerAfter は照明の後に重ねる
        // 層の分(無ければ null)。
        private Material _baker;
        private Material _bakerAfter;
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
        /// 照明の後に重ねる層があれば、その分を描く 2 つ目(<see cref="_bakerAfter"/>)も作る(<see cref="SplitByLighting"/>)。
        /// </summary>
        private void PrepareBaker(MaterialColorSettings.Report r)
        {
            if (_baker != null && _bakerFor == r.signature) return;
            DestroyNow(ref _baker);
            DestroyNow(ref _bakerAfter);
            _baker = NewBaker(r.material);
            SplitByLighting(r.material);
            _bakerFor = r.signature;
        }

        // UV0 以外で重ねる層は、テクスチャの上の位置が決まらないので切る。表(front)で消える層は、
        // アバターを外から見たときに出ないので切る。
        private static Material NewBaker(Material src)
        {
            var shader = LilToonBaker;
            var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = shader.GetPropertyName(i);
                if (name == "_MainTex" || !src.HasProperty(name)) continue;
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color: m.SetColor(name, src.GetColor(name)); break;
                    case ShaderPropertyType.Vector: m.SetVector(name, src.GetVector(name)); break;
                    case ShaderPropertyType.Int: m.SetInteger(name, src.GetInteger(name)); break;
                    case ShaderPropertyType.Texture:
                        m.SetTexture(name, src.GetTexture(name));
                        m.SetTextureScale(name, src.GetTextureScale(name));
                        m.SetTextureOffset(name, src.GetTextureOffset(name));
                        break;
                    default: m.SetFloat(name, src.GetFloat(name)); break;
                }
            }
            foreach (var name in UndeclaredBakerVectors)
                if (src.HasProperty(name)) m.SetVector(name, src.GetVector(name));
            foreach (var name in UndeclaredBakerFloats)
                if (src.HasProperty(name)) m.SetFloat(name, src.GetFloat(name));
            HideLayer(m, src, "_UseMain2ndTex", "_Main2ndTex_UVMode", "_Main2ndTex_Cull");
            HideLayer(m, src, "_UseMain3rdTex", "_Main3rdTex_UVMode", "_Main3rdTex_Cull");
            return m;
        }

        private static void HideLayer(Material m, Material src, string use, string uvMode, string cull)
        {
            bool otherUv = src.HasProperty(uvMode) && Mathf.RoundToInt(src.GetFloat(uvMode)) != 0;
            bool hiddenOnFront = src.HasProperty(cull) && Mathf.RoundToInt(src.GetFloat(cull)) == 1;
            if ((otherUv || hiddenOnFront) && m.HasProperty(use)) m.SetFloat(use, 0f);
        }

        /// <summary>
        /// lilToon の本番の描画は、層の不透明度のうち EnableLighting の割合を照明の前に、残りを照明の後に重ねる
        /// (2nd → 3rd → 照明 → 2nd → 3rd)。焼き込みシェーダーは 2nd → 3rd を 1 回ずつ重ねるだけなので、照明の後に
        /// 重ねる分がある層があれば、照明の前の分(<see cref="_baker"/>)と後の分(<see cref="_bakerAfter"/>、メインの
        /// 色の設定は外す)の 2 回に分ける。分けないと、2nd だけ照明の後のとき 2nd と 3rd の上下が逆になる。
        /// </summary>
        private void SplitByLighting(Material src)
        {
            float l2 = LayerLighting(src, "_UseMain2ndTex", "_Main2ndEnableLighting");
            float l3 = LayerLighting(src, "_UseMain3rdTex", "_Main3rdEnableLighting");
            if (l2 >= 1f && l3 >= 1f) return;
            // 2 回目はメインの段を素通りさせる。色調補正は無設定でも HSV を経て明るさを 1 で頭打ちにするので
            // (加算で重ねた層は 1 を超える)、色調補正のマスクを黒にして補正前の値をそのまま使わせる。
            _bakerAfter = NewBaker(src);
            _bakerAfter.SetColor("_Color", Color.white);
            _bakerAfter.SetTexture("_MainColorAdjustMask", Texture2D.blackTexture);
            SplitLayer("_UseMain2ndTex", "_Color2nd", l2);
            SplitLayer("_UseMain3rdTex", "_Color3rd", l3);
        }

        // 層の EnableLighting(描かない層・値が無いときは 1 = 全部を照明の前に重ねる)。
        private float LayerLighting(Material src, string use, string lighting)
        {
            if (!_baker.HasProperty(use) || _baker.GetFloat(use) < 0.5f || !src.HasProperty(lighting)) return 1f;
            return Mathf.Clamp01(src.GetFloat(lighting));
        }

        // 層の色の不透明度を、照明の前の分と後の分に割る(焼き込みシェーダーは色の a を層の不透明度に掛ける)。
        private void SplitLayer(string use, string color, float lighting)
        {
            if (lighting >= 1f) { _bakerAfter.SetFloat(use, 0f); return; }
            var c = _baker.GetColor(color);
            if (lighting <= 0f) _baker.SetFloat(use, 0f);
            else _baker.SetColor(color, new Color(c.r, c.g, c.b, c.a * lighting));
            _bakerAfter.SetColor(color, new Color(c.r, c.g, c.b, c.a * (1f - lighting)));
        }

        /// <summary>
        /// テクスチャの上の位置で値が変わるものを描くか(重ね描きの層・色調補正のマスク)。無ければ、どの切り出しも
        /// 画素ごとの変換なので、切り出しをそのまま焼き込みシェーダーに通せばよい。
        /// </summary>
        private bool DependsOnUv(Material src)
        {
            if (_bakerAfter != null) return true;
            if (_baker.HasProperty("_UseMain2ndTex") && _baker.GetFloat("_UseMain2ndTex") > 0.5f) return true;
            if (_baker.HasProperty("_UseMain3rdTex") && _baker.GetFloat("_UseMain3rdTex") > 0.5f) return true;
            return src.HasProperty("_MainColorAdjustMask") && src.GetTexture("_MainColorAdjustMask") != null;
        }

        // 照明の前の結果を置く作業用。8 ビットに丸めず、色空間の変換もしない(2 回目がそのままの値で読む)。
        // 焼き込みシェーダーは層のマスクをメインテクスチャのサンプラーで読むので、読み方は 1 回目の入力に合わせる。
        private static RenderTexture GetMidTemporary(int w, int h, Texture like)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            rt.filterMode = like.filterMode;
            rt.wrapMode = like.wrapMode;
            return rt;
        }

        // 画像全体(UV の 0〜1)を焼き込みシェーダーに通す。lilToon のインスペクタの焼き込みと同じ手順
        // (sRGB のテクスチャ → 既定の読み書きの RenderTexture → ReadPixels)。
        private void BlitBaker(Texture2D src, ref Texture2D dst)
        {
            // Blit は描き先を rt にしたまま返るので、前の描き先は先に控えて戻す(ウィンドウの描画中に呼ばれる)。
            var prev = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            if (_bakerAfter == null)
                Graphics.Blit(src, rt, _baker);
            else
            {
                var mid = GetMidTemporary(src.width, src.height, src);
                Graphics.Blit(src, mid, _baker);
                Graphics.Blit(mid, rt, _bakerAfter);
                RenderTexture.ReleaseTemporary(mid);
            }
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
            if (_bakerAfter == null)
                DrawQuad(rt, _baker, new Rect(0f, 0f, 1f, 1f), uvRect);
            else
            {
                // 照明の前の分を作業用と同じ並び(元の位置)に描き、そこを指して照明の後の分を描く。
                var mid = GetMidTemporary(sw, sh, _scratch);
                DrawQuad(mid, _baker, uvRect, uvRect);
                _bakerAfter.mainTexture = mid;
                DrawQuad(rt, _bakerAfter, new Rect(0f, 0f, 1f, 1f), uvRect);
                _bakerAfter.mainTexture = null;
                RenderTexture.ReleaseTemporary(mid);
            }
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
            DestroyNow(ref _bakerAfter);
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
