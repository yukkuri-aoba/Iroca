// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Iroca
{
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
    /// </summary>
    internal static class LivePreview
    {
        /// <summary>ウィンドウが編集している元テクスチャと、結び付いたレシピ(無ければ null)。</summary>
        internal sealed class Target : IEquatable<Target>
        {
            public readonly Texture2D source;
            public readonly IrocaRecipe boundRecipe;

            public Target(Texture2D source, IrocaRecipe boundRecipe)
            {
                this.source = source;
                this.boundRecipe = boundRecipe;
            }

            public bool Equals(Target other) =>
                other != null && ReferenceEquals(source, other.source) && ReferenceEquals(boundRecipe, other.boundRecipe);

            public override bool Equals(object obj) => Equals(obj as Target);

            public override int GetHashCode() =>
                (source != null ? source.GetInstanceID() : 0) * 397 ^ (boundRecipe != null ? boundRecipe.GetInstanceID() : 0);
        }

        private sealed class Slot
        {
            public RenderTexture rt;
            public int refs;
            // 最後に届いた画素(行 0 = 画像下端)。借り手が現れたとき・作り直したときに書き込む。
            public Color32[] pixels;
            public int width, height;
            public Texture2D staging;
        }

        private static readonly Dictionary<Texture2D, Slot> Slots = new Dictionary<Texture2D, Slot>();

        /// <summary>いま編集中のもの(何も編集していなければ null)。</summary>
        internal static Target Current { get; private set; }

        /// <summary><see cref="Current"/> が変わった(テクスチャの切り替え・レシピの結び付け・ウィンドウを閉じた)。</summary>
        internal static event Action TargetChanged;

        [InitializeOnLoadMethod]
        private static void Install()
        {
            // HideAndDontSave の RenderTexture はドメインリロードで静的な参照だけが消えて残るので、先に捨てる。
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAll;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
        }

        /// <summary>
        /// いま編集しているものを知らせる。<paramref name="source"/> が null なら「何も映さない」。
        /// 同じ値なら何もしない(ウィンドウは毎フレーム呼んでよい)。
        /// </summary>
        public static void SetTarget(Texture2D source, IrocaRecipe boundRecipe)
        {
            var next = source != null ? new Target(source, boundRecipe) : null;
            if (Equals(Current, next)) return;
            var previous = Current;
            Current = next;
            // 前の元テクスチャの画素は、借り手が残っていなければもう使わない。
            if (previous != null && (next == null || previous.source != next.source))
                DropIfUnused(previous.source);
            TargetChanged?.Invoke();
        }

        /// <summary>
        /// 色替えの結果を置く。<paramref name="source"/> がいま編集中のものでなければ捨てる
        /// (テクスチャを切り替えた直後に届いた前のテクスチャの結果)。借り手がいればすぐ書き込む。
        /// </summary>
        public static void Push(Texture2D source, Color32[] pixels, int width, int height)
        {
            if (source == null || pixels == null || Current == null || Current.source != source) return;
            if (pixels.Length != width * height) return;
            if (!Slots.TryGetValue(source, out var slot))
            {
                slot = new Slot();
                Slots[source] = slot;
            }
            slot.pixels = pixels;
            slot.width = width;
            slot.height = height;
            if (slot.rt != null) Upload(slot);
        }

        /// <summary>
        /// <paramref name="source"/> の色替え結果を映す RenderTexture を借りる。返したら
        /// <see cref="Release"/> で返す。まだ結果が無ければ元テクスチャの見た目のまま。
        /// </summary>
        public static Texture Acquire(Texture2D source)
        {
            if (source == null) return null;
            if (!Slots.TryGetValue(source, out var slot))
            {
                slot = new Slot();
                Slots[source] = slot;
            }
            if (slot.rt == null || !slot.rt.IsCreated())
            {
                // GPU 側だけ失われたときは同じオブジェクトのまま作り直す(貸し出し中のマテリアルが参照している)。
                if (slot.rt == null) CreateTarget(slot, source);
                else slot.rt.Create();
                if (slot.pixels != null) Upload(slot);
                else BlitKeepingActive(source, slot.rt);
            }
            slot.refs++;
            return slot.rt;
        }

        public static void Release(Texture2D source)
        {
            if (source == null || !Slots.TryGetValue(source, out var slot)) return;
            if (slot.refs > 0) slot.refs--;
            DropIfUnused(source);
        }

        /// <summary>
        /// <paramref name="pixels"/> をシーンへの反映用に持っているか(再アップロードで読むので、持っている間は
        /// 書き換えてはいけない)。メインスレッドから呼ぶ。
        /// </summary>
        internal static bool HoldsPixels(Color32[] pixels)
        {
            if (pixels == null) return false;
            foreach (var slot in Slots.Values)
                if (ReferenceEquals(slot.pixels, pixels)) return true;
            return false;
        }

        /// <summary>テスト用: 借りられている数。</summary>
        internal static int RefCount(Texture2D source) =>
            source != null && Slots.TryGetValue(source, out var slot) ? slot.refs : 0;

        private static void DropIfUnused(Texture2D source)
        {
            if (source == null || !Slots.TryGetValue(source, out var slot) || slot.refs > 0) return;
            if (Current != null && Current.source == source)
            {
                // 編集中のものは画素を残す(また借りられたらすぐ映せるように)。GPU 側だけ手放す。
                DestroyGpu(slot);
                return;
            }
            DestroyGpu(slot);
            Slots.Remove(source);
        }

        private static void ReleaseAll()
        {
            foreach (var slot in Slots.Values) DestroyGpu(slot);
            Slots.Clear();
            Current = null;
        }

        private static void CreateTarget(Slot slot, Texture2D source)
        {
            DestroyGpu(slot);
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat);
            var desc = new RenderTextureDescriptor(Mathf.Max(1, source.width), Mathf.Max(1, source.height),
                RenderTextureFormat.ARGB32, 0)
            {
                sRGB = srgb,
                useMipMap = source.mipmapCount > 1,
                autoGenerateMips = source.mipmapCount > 1,
            };
            slot.rt = new RenderTexture(desc)
            {
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
        private static void Upload(Slot slot)
        {
            if (slot.rt == null || slot.pixels == null) return;
            bool srgb = slot.rt.sRGB;
            if (slot.staging == null || slot.staging.width != slot.width || slot.staging.height != slot.height
                || GraphicsFormatUtility.IsSRGBFormat(slot.staging.graphicsFormat) != srgb)
            {
                if (slot.staging != null) UnityEngine.Object.DestroyImmediate(slot.staging);
                slot.staging = new Texture2D(slot.width, slot.height, TextureFormat.RGBA32, false, !srgb)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            slot.staging.SetPixelData(slot.pixels, 0);
            slot.staging.Apply(false, false);

            bool shrink = slot.width > slot.rt.width * 2 || slot.height > slot.rt.height * 2;
            if (shrink)
            {
                var desc = new RenderTextureDescriptor(slot.width, slot.height, RenderTextureFormat.ARGB32, 0)
                {
                    sRGB = srgb,
                    useMipMap = true,
                    autoGenerateMips = true,
                };
                var tmp = RenderTexture.GetTemporary(desc);
                tmp.filterMode = FilterMode.Trilinear;
                BlitKeepingActive(slot.staging, tmp);
                BlitKeepingActive(tmp, slot.rt);
                RenderTexture.ReleaseTemporary(tmp);
            }
            else
            {
                BlitKeepingActive(slot.staging, slot.rt);
            }
            SceneView.RepaintAll();
        }

        // Graphics.Blit は描画先を RenderTexture.active に残す。そのままだと他の処理の描画先を奪い、
        // 後で捨てるときに「active のまま解放した」警告も出るので、元に戻す。
        private static void BlitKeepingActive(Texture from, RenderTexture to)
        {
            var previous = RenderTexture.active;
            Graphics.Blit(from, to);
            RenderTexture.active = previous;
        }

        private static void DestroyGpu(Slot slot)
        {
            if (slot.rt != null)
            {
                slot.rt.Release();
                UnityEngine.Object.DestroyImmediate(slot.rt);
                slot.rt = null;
            }
            if (slot.staging != null)
            {
                UnityEngine.Object.DestroyImmediate(slot.staging);
                slot.staging = null;
            }
        }
    }
}
