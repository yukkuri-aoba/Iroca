// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Iroca {
    /// <summary>
    /// いろかウィンドウで編集中の色替えを、シーンのアバターへその場で映すための受け渡し口(NDMF 非依存)。
    /// <para>
    /// ウィンドウは「いま何を編集しているか」(<see cref="SetTarget"/>)と、プレビューを作るたびにその画素
    /// (<see cref="Push"/>)を置く。NDMF のプレビュー(Iroca.NdmfIntegration の IrocaPreviewFilter)は
    /// 元テクスチャごとの RenderTexture を借りて(<see cref="Acquire"/>)マテリアルの複製に差し込む。
    /// 画素が届くたびに同じ RenderTexture の中身を書き換えるので、NDMF 側の作り直しは要らない
    /// (スライダーを動かしている間も、ウィンドウのプレビューと同じ速さで追従する)。
    /// </para>
    /// <para>
    /// 色替えの計算はウィンドウのプレビューの結果をそのまま使い、ここでは増やさない。ドラッグ中は縮小版
    /// (表示解像度)、手を止めたらフル解像度が届く(ドラッグでない操作はフル解像度だけ。縮小版を挟むと
    /// 一瞬粗く見える)。RenderTexture の寸法は取り込み済みの元テクスチャに
    /// そろえる(シーンで普段見えている解像度。非破壊ビルドの差し替えテクスチャも同じ寸法)。
    /// </para>
    /// <para>
    /// ミップと圧縮は、再生・アップロードのときの差し替えテクスチャ(<see cref="RecipeTextureBuilder"/>)と同じではない。
    /// こちらは非圧縮で、ミップは GPU の単純な縮小(取り込み設定の Kaiser・Preserve Coverage・Fadeout は効かない)。
    /// 取り込みと同じ処理は 4K で 0.3 秒ほどかかり、スライダーに追従できないため。遠景や、アルファで切り抜いた髪の
    /// 見え方は、再生・アップロードのときと少し違いうる(編集中の表示の仕様)。
    /// </para>
    /// </summary>
    internal static class LivePreview {
        /// <summary>ウィンドウが編集している元テクスチャと、結び付いたレシピ(無ければ null)。</summary>
        internal sealed class Target : IEquatable<Target> {
            public readonly Texture2D source;
            public readonly IrocaRecipe boundRecipe;

            public Target(Texture2D source, IrocaRecipe boundRecipe) {
                this.source = source;
                this.boundRecipe = boundRecipe;
            }

            public bool Equals(Target other) =>
                other != null && ReferenceEquals(source, other.source) && ReferenceEquals(boundRecipe, other.boundRecipe);

            public override bool Equals(object obj) => Equals(obj as Target);

            public override int GetHashCode() =>
                (source != null ? source.GetInstanceID() : 0) * 397 ^ (boundRecipe != null ? boundRecipe.GetInstanceID() : 0);
        }

        private sealed class Slot {
            public RenderTexture rt;
            public int refs;
            // 最後に届いた画素(行 0 = 画像下端。ドラッグ中の縮小版か、確定のフル解像度)。借り手が現れたとき・作り直したときに
            // 書き込む。Mono のヒープの外にあり、参照を 1 つ持つ。
            public NativePixels native;
            public int width, height;
            // 画素を GPU へ上げる中継(RGBA32・ミップなし)。ドラッグの追従(表示寸法。長辺 384 以下)と確定(作業解像度)で
            // 寸法が替わるので、直近の 2 つの寸法を 1 枚ずつ持って使い回す(staging が直近に使った方、stagingSpare がもう片方)。
            // 1 枚だけだと、ドラッグを始めるたび・離すたびに確定の寸法(4K で 64MB)を作り直していた。
            // 中身は次に上げるときに CPU から書き直す(LoadRawTextureData・Alpha Source)ので、CPU 側の写しは手放せない。
            // 使い回すのは編集が続いている間だけで足りるので、手を止めたら捨てる(TrimStaging。4K で CPU と GPU に 64MB ずつ)。
            public Texture2D staging;
            public Texture2D stagingSpare;
            // Alpha Source を当てるときの取り込み設定(最後に届いた画素のテクスチャのもの)と、その画素が原本ファイルを読んだものか
            // (取り込み済みテクスチャの画素なら、Unity が当て済みなので当てない)。
            public TextureImporter importer;
            public bool fromSourceFile;

            public bool HasPixels => native != null;

            // 持っている画素を手放す(native は参照を返す)。
            public void DropPixels() {
                var n = native;
                native = null;
                n?.Release();
            }
        }

        private static readonly Dictionary<Texture2D, Slot> Slots = new Dictionary<Texture2D, Slot>();

        /// <summary>いま編集中のもの(何も編集していなければ null)。</summary>
        internal static Target Current { get; private set; }

        /// <summary><see cref="Current"/> が変わった(テクスチャの切り替え・レシピの結び付け・ウィンドウを閉じた)。</summary>
        internal static event Action TargetChanged;

        [InitializeOnLoadMethod]
        private static void Install() {
            // HideAndDontSave の RenderTexture はドメインリロードで静的な参照だけが消えて残るので、先に捨てる。
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAll;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
            // 手を止めて作業配列を返す時期(計算が 10 秒無い)に、中継も返す。
            NativeMemoryLifecycle.IdleTrimmed -= OnIdleTrimmed;
            NativeMemoryLifecycle.IdleTrimmed += OnIdleTrimmed;
        }

        // タイマーのスレッドから来る。テクスチャを捨てられるのはメインスレッドだけなので、次のエディタの更新へ回す。
        private static void OnIdleTrimmed() => PreviewJobMainThread.Post(TrimStaging);

        /// <summary>
        /// 画素を GPU へ上げる中継を捨てる(手を止めたとき)。RenderTexture と最後に届いた画素は残すので、映っているものは
        /// 変わらず、次に画素が届いたときに中継を作り直す。internal はテストが呼ぶため。
        /// </summary>
        internal static void TrimStaging() {
            foreach (var slot in Slots.Values) {
                DestroyStaging(slot);
            }
        }

        /// <summary>テスト用: 持っている中継の数(0〜2)。</summary>
        internal static int StagingCount(Texture2D source) {
            if (source == null || !Slots.TryGetValue(source, out var slot)) {
                return 0;
            }
            return (slot.staging != null ? 1 : 0) + (slot.stagingSpare != null ? 1 : 0);
        }

        /// <summary>
        /// いま編集しているものを知らせる。<paramref name="source"/> が null なら「何も映さない」。
        /// 同じ値なら何もしない(ウィンドウは毎フレーム呼んでよい)。
        /// </summary>
        public static void SetTarget(Texture2D source, IrocaRecipe boundRecipe) {
            // 同じなら比べるための Target を作らない(ウィンドウが描画のたびに呼ぶ)。
            var current = Current;
            bool same = source == null
                ? current == null
                : current != null && ReferenceEquals(current.source, source) && ReferenceEquals(current.boundRecipe, boundRecipe);
            if (same) {
                return;
            }
            var next = source != null ? new Target(source, boundRecipe) : null;
            PurgeDestroyedSources();
            var previous = Current;
            Current = next;
            // 前の元テクスチャの画素は、借り手が残っていなければもう使わない。
            if (previous != null && (next == null || previous.source != next.source)) {
                DropIfUnused(previous.source);
            }
            TargetChanged?.Invoke();
        }

        /// <summary>
        /// 編集中の色(<see cref="MaterialShadowColors.TryGetColors(IList{ColorZone}, out Color, out Color)"/>)を知らせる
        /// (ウィンドウは毎フレーム呼んでよい)。シーンのプレビューは、変わったらマテリアルの複製の影の色を塗り直す
        /// (色を動かしている間に NDMF の作り直しを起こさないよう、<see cref="ShadowColorsVersion"/> で見る)。
        /// </summary>
        public static void SetShadowColors(bool has, Color sample, Color target) {
            if (has == HasShadowColors && (!has || (sample == ShadowSample && target == ShadowTarget))) {
                return;
            }
            HasShadowColors = has;
            ShadowSample = sample;
            ShadowTarget = target;
            ShadowColorsVersion++;
        }

        internal static bool HasShadowColors { get; private set; }
        internal static Color ShadowSample { get; private set; }
        internal static Color ShadowTarget { get; private set; }
        internal static int ShadowColorsVersion { get; private set; }

        /// <summary>
        /// 色替えの結果(Mono のヒープの外の画素。ドラッグ中の縮小版か、確定のフル解像度)を置く。<paramref name="source"/> が
        /// いま編集中のものでなければ捨てる(テクスチャを切り替えた直後に届いた前のテクスチャの結果)。借り手がいればすぐ書き込む。
        /// 置いている間は参照を 1 つ持つ(次の Push・テクスチャの切り替え・閉じたときに返す)。
        /// <paramref name="fromSourceFile"/> は、色替えの元の画素が原本ファイルを読んだものか(<see cref="RecipeTextureBuilder.ApplyAlphaSource"/>)。
        /// </summary>
        public static void Push(Texture2D source, NativePixels pixels, bool fromSourceFile) {
            if (source == null || pixels == null || Current == null || Current.source != source) {
                return;
            }
            var slot = SlotFor(source);
            pixels.AddRef();
            slot.DropPixels();
            slot.native = pixels;
            slot.width = pixels.Width;
            slot.height = pixels.Height;
            slot.importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)) as TextureImporter;
            slot.fromSourceFile = fromSourceFile;
            if (slot.rt != null) {
                Upload(slot);
            }
        }

        private static Slot SlotFor(Texture2D source) {
            if (!Slots.TryGetValue(source, out var slot)) {
                slot = new Slot();
                Slots[source] = slot;
            }
            return slot;
        }

        /// <summary>
        /// <paramref name="source"/> の色替え結果を映す RenderTexture を借りる。返したら
        /// <see cref="Release"/> で返す。まだ結果が無ければ元テクスチャの見た目のまま。
        /// </summary>
        public static Texture Acquire(Texture2D source) {
            if (source == null) {
                return null;
            }
            if (!Slots.TryGetValue(source, out var slot)) {
                slot = new Slot();
                Slots[source] = slot;
            }
            if (slot.rt == null || !slot.rt.IsCreated()) {
                Restore(slot, source);
            }
            slot.refs++;
            UpdateTargetWatch();
            return slot.rt;
        }

        // RenderTexture を作り(GPU 側だけ失われたときは同じオブジェクトのまま作り直す。貸し出し中のマテリアルが参照している)、
        // 最後に届いた画素を書く。画素がまだ無ければ元テクスチャの見た目にする。
        private static void Restore(Slot slot, Texture2D source) {
            if (slot.rt == null) {
                CreateTarget(slot, source);
            } else {
                slot.rt.Create();
            }
            if (slot.HasPixels) {
                Upload(slot);
            } else {
                BlitKeepingActive(source, slot.rt);
            }
        }

        // ── 貸している RenderTexture の見張り ──
        // RenderTexture の GPU 側は、画面のロックやドライバーの作り直しなどで失われることがある。以前は借りるときにしか
        // 見ていなかったので、借りたまま失われると、次に色を変えるまでシーンのアバターが黒く映った。借り手がいる間だけ、
        // 時々見て、失われていたら上げ直す。
        private const double TargetWatchSeconds = 1.0;
        private static bool s_watchingTargets;
        private static double s_targetsWatchedAt;

        private static void UpdateTargetWatch() {
            bool borrowed = false;
            foreach (var slot in Slots.Values) {
                if (slot.refs > 0) {
                    borrowed = true;
                    break;
                }
            }
            if (borrowed == s_watchingTargets) {
                return;
            }
            s_watchingTargets = borrowed;
            EditorApplication.update -= WatchTargets;
            if (borrowed) {
                EditorApplication.update += WatchTargets;
            }
        }

        private static void WatchTargets() {
            double now = EditorApplication.timeSinceStartup;
            if (now - s_targetsWatchedAt < TargetWatchSeconds) {
                return;
            }
            s_targetsWatchedAt = now;
            RestoreLostTargets();
        }

        /// <summary>貸している RenderTexture のうち、GPU 側が失われたものを上げ直す。internal はテストが呼ぶため。</summary>
        internal static void RestoreLostTargets() {
            foreach (var pair in Slots) {
                var slot = pair.Value;
                if (slot.refs <= 0 || slot.rt == null || slot.rt.IsCreated() || pair.Key == null) {
                    continue;
                }
                Restore(slot, pair.Key);
            }
        }

        public static void Release(Texture2D source) {
            if (source == null || !Slots.TryGetValue(source, out var slot)) {
                return;
            }
            if (slot.refs > 0) {
                slot.refs--;
            }
            DropIfUnused(source);
            UpdateTargetWatch();
        }

        /// <summary>
        /// 元テクスチャが破棄された(Project で消した)スロットを手放す。破棄された後は null と比べて真になるので、
        /// <see cref="Release"/> と <see cref="DropIfUnused"/> からは見つけられず、次の再読み込みまで画素と RenderTexture が残っていた
        /// (8K で最大約 768MB)。
        /// </summary>
        internal static void PurgeDestroyedSources() {
            if (Current != null && Current.source == null) {
                Current = null;
                TargetChanged?.Invoke();
            }
            List<Texture2D> gone = null;
            foreach (var key in Slots.Keys) {
                if (key == null) {
                    (gone ??= new List<Texture2D>()).Add(key);
                }
            }
            if (gone == null) {
                return;
            }
            foreach (var key in gone) {
                var slot = Slots[key];
                DestroyGpu(slot);
                slot.DropPixels();
                Slots.Remove(key);
            }
            UpdateTargetWatch();
        }

        /// <summary>テスト用: スロットの数。</summary>
        internal static int SlotCount => Slots.Count;

        /// <summary>テスト用: 借りられている数。</summary>
        internal static int RefCount(Texture2D source) =>
            source != null && Slots.TryGetValue(source, out var slot) ? slot.refs : 0;

        private static void DropIfUnused(Texture2D source) {
            if (source == null || !Slots.TryGetValue(source, out var slot) || slot.refs > 0) {
                return;
            }
            if (Current != null && Current.source == source) {
                // 編集中のものは画素を残す(また借りられたらすぐ映せるように)。GPU 側だけ手放す。
                DestroyGpu(slot);
                return;
            }
            DestroyGpu(slot);
            slot.DropPixels();
            Slots.Remove(source);
        }

        // リロードの直前に、他のツールの後始末と同じ並びで Unity が呼ぶ。Unity はここで例外を受け止めないので、
        // 投げると後ろに並んだ他のツールの後始末が飛ばされる。スロットごと・手放すものごとに受け止め、
        // 表は必ず空にする。
        private static void ReleaseAll() {
            try {
                foreach (var slot in Slots.Values) {
                    try { DestroyGpu(slot); } catch (Exception e) { Debug.LogException(e); }
                    try { slot.DropPixels(); } catch (Exception e) { Debug.LogException(e); }
                }
            } catch (Exception e) { Debug.LogException(e); } finally {
                Slots.Clear();
                Current = null;
                EditorApplication.update -= WatchTargets;
                s_watchingTargets = false;
            }
        }

        private static void CreateTarget(Slot slot, Texture2D source) {
            DestroyGpu(slot);
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);
            var desc = new RenderTextureDescriptor(Mathf.Max(1, source.width), Mathf.Max(1, source.height),
                RenderTextureFormat.ARGB32, 0) {
                sRGB = srgb,
                useMipMap = source.mipmapCount > 1,
                autoGenerateMips = source.mipmapCount > 1,
            };
            slot.rt = new RenderTexture(desc) {
                name = source.name + " (Iroca preview)",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = source.filterMode,
                wrapModeU = source.wrapModeU,
                wrapModeV = source.wrapModeV,
                anisoLevel = source.anisoLevel,
            };
            slot.rt.Create();
        }

        // 画素をいったん Texture2D に載せ、GPU で RenderTexture の寸法へ合わせて書き込む。
        // 縮小のときは、ミップ付きの一時 RenderTexture を挟んで GPU にミップを作らせる(そのまま縮小すると
        // 4 倍以上でちらつく)。CPU でのミップ生成(Texture2D.Apply(true))は 4K で重いので使わない。
        private static unsafe void Upload(Slot slot) {
            if (slot.rt == null || !slot.HasPixels) {
                return;
            }
            bool srgb = slot.rt.sRGB;
            SelectStaging(slot, srgb);
            // RGBA32 でミップなしなので、Color32 の並びがそのままテクスチャのデータになる(管理配列へ写さない)。
            slot.staging.LoadRawTextureData((IntPtr)slot.native.Ptr, slot.native.Length * 4);
            // ビルド(RecipeTextureBuilder)と同じく、出来上がりに取り込み設定の Alpha Source を当てる。届いた画素は窓のプレビューも
            // 使うので、写した先で当てる。
            RecipeTextureBuilder.ApplyAlphaSource(slot.staging, slot.importer, slot.fromSourceFile);
            slot.staging.Apply(false, false);

            bool shrink = slot.width > slot.rt.width * 2 || slot.height > slot.rt.height * 2;
            if (shrink) {
                var desc = new RenderTextureDescriptor(slot.width, slot.height, RenderTextureFormat.ARGB32, 0) {
                    sRGB = srgb,
                    useMipMap = true,
                    autoGenerateMips = true,
                };
                var tmp = RenderTexture.GetTemporary(desc);
                tmp.filterMode = FilterMode.Trilinear;
                BlitKeepingActive(slot.staging, tmp);
                BlitKeepingActive(tmp, slot.rt);
                RenderTexture.ReleaseTemporary(tmp);
            } else {
                BlitKeepingActive(slot.staging, slot.rt);
            }
            SceneView.RepaintAll();
        }

        // 今の画素の寸法と色空間に合う中継を slot.staging に置く。控えに合うものがあれば入れ替えて使い、無ければ作る
        // (直近に使った方を控えへ回し、合わない控えは捨てる。持つのは 2 枚まで)。
        private static void SelectStaging(Slot slot, bool srgb) {
            if (StagingFits(slot.staging, slot.width, slot.height, srgb)) {
                return;
            }
            if (StagingFits(slot.stagingSpare, slot.width, slot.height, srgb)) {
                (slot.staging, slot.stagingSpare) = (slot.stagingSpare, slot.staging);
                return;
            }
            if (slot.stagingSpare != null) {
                UnityEngine.Object.DestroyImmediate(slot.stagingSpare);
            }
            slot.stagingSpare = null;
            // 色空間が替わった(別の元テクスチャの読み方)中継は、もう使わないので控えにも残さない。
            if (slot.staging != null && GraphicsFormatUtility.IsSRGBFormat(slot.staging.graphicsFormat) != srgb) {
                UnityEngine.Object.DestroyImmediate(slot.staging);
            } else {
                slot.stagingSpare = slot.staging;
            }
            slot.staging = new Texture2D(slot.width, slot.height, TextureFormat.RGBA32, false, !srgb) {
                name = TextureSlot.OwnedName("live preview staging"),
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        private static bool StagingFits(Texture2D t, int w, int h, bool srgb)
            => t != null && t.width == w && t.height == h && GraphicsFormatUtility.IsSRGBFormat(t.graphicsFormat) == srgb;

        // Graphics.Blit は描画先を RenderTexture.active に残す。そのままだと他の処理の描画先を奪い、
        // 後で捨てるときに「active のまま解放した」警告も出るので、元に戻す。
        private static void BlitKeepingActive(Texture from, RenderTexture to) {
            var previous = RenderTexture.active;
            Graphics.Blit(from, to);
            RenderTexture.active = previous;
        }

        private static void DestroyGpu(Slot slot) {
            if (slot.rt != null) {
                slot.rt.Release();
                UnityEngine.Object.DestroyImmediate(slot.rt);
                slot.rt = null;
            }
            DestroyStaging(slot);
        }

        private static void DestroyStaging(Slot slot) {
            if (slot.staging != null) {
                UnityEngine.Object.DestroyImmediate(slot.staging);
                slot.staging = null;
            }
            if (slot.stagingSpare != null) {
                UnityEngine.Object.DestroyImmediate(slot.stagingSpare);
                slot.stagingSpare = null;
            }
        }
    }
}
