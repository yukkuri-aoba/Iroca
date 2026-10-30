// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI 提案の画素処理(SamImageOps・SamMaskPostprocess・SamZoomOps)を、Mono のヒープの外に置いた画素
    /// (<see cref="NativePixels"/>)で呼ぶ入口。推論のサービス(Iroca.SentisIntegration)の asmdef は unsafe を
    /// 許していないので、ポインタを扱うのはここに閉じ込める。呼び出し側は読み終えるまで画素の参照を持つこと。
    /// エンコーダの入力と埋め込みはヒープの外の float の並び(<see cref="NativeBuffer"/>)で持ち、推論に渡すときだけ
    /// <see cref="NativeArray{T}"/> に見せる(<see cref="FloatView"/>。0.4.0 の計画の B4)。
    /// </summary>
    internal static unsafe class SamNativeOps
    {
        /// <summary>原本からエンコーダの入力(<see cref="SamImageOps.EncoderInputLength"/> 個の float。参照 1 で返す)を作る。</summary>
        public static NativeBuffer BuildEncoderInput(NativePixels px, CancellationToken token = default)
            => BuildEncoderInput(px.Ptr, px.Width, px.Height, token);

        private static NativeBuffer BuildEncoderInput(Color32* px, int w, int h, CancellationToken token)
        {
            var chw = new NativeBuffer((long)SamImageOps.EncoderInputLength * sizeof(float), NativeUsage.Other);
            try
            {
                SamImageOps.BuildEncoderInputInto(px, w, h, (float*)chw.Ptr, token);
                return chw;
            }
            catch
            {
                chw.Release();
                throw;
            }
        }

        /// <summary>
        /// float の並び(length 個)を <see cref="NativeArray{T}"/> に見せる(写さない)。見せている間は buffer の参照を
        /// 持つこと。使い終わったら Dispose する(並びは手放さない)。
        /// </summary>
        public static TextureIO.NativeView<float> FloatView(NativeBuffer buffer, int length)
        {
            if (buffer == null) throw new System.ArgumentNullException(nameof(buffer));
            if ((long)length * sizeof(float) > buffer.Bytes)
                throw new System.ArgumentOutOfRangeException(nameof(length), $"{length} 個は {buffer.Bytes} バイトに入らない");
            return TextureIO.NativeView<float>.Over((void*)buffer.Ptr, length);
        }

        /// <summary>
        /// 推論の読み出し(<see cref="NativeArray{T}"/>。CPU では Temp、GPU では読み出しの持ち物で、次のフレームまで
        /// しか使えない)を、持ち続けられるヒープの外の並びへ写す(参照 1 で返す。空なら null)。
        /// </summary>
        public static NativeBuffer CopyFloats(NativeArray<float> src)
        {
            if (!src.IsCreated || src.Length == 0) return null;
            long bytes = (long)src.Length * sizeof(float);
            var dst = new NativeBuffer(bytes, NativeUsage.Other);
            System.Buffer.MemoryCopy(NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(src), (void*)dst.Ptr, bytes, bytes);
            return dst;
        }

        /// <summary>float の並びの要素数(null は 0)。</summary>
        public static int FloatCount(NativeBuffer buffer) => buffer == null ? 0 : (int)(buffer.Bytes / sizeof(float));

        // 画素が無ければ何もしない(配列版の RefineInPlace と同じ約束)。
        public static void RefineInPlace(NativeBools mask, NativePixels px, CancellationToken token = default)
        {
            if (px == null || mask == null) return;
            SamMaskPostprocess.RefineInPlace(mask.Ptr, px.Ptr, px.Width, px.Height, token);
        }

        /// <summary>切り出し(参照 1。受け取った側が手放す)。</summary>
        public static NativePixels ExtractCrop(NativePixels px, int x0, int y0, int side)
            => SamZoomOps.ExtractCrop(px.Ptr, px.Width, px.Height, x0, y0, side);

        /// <summary>
        /// デコーダの出力から提案マスクを作る(<see cref="SamMaskPostprocess.SelectAndUpscale(float[], float[], int, int, float, Color32*, MaskSuggestGranularity, CancellationToken)"/>)。
        /// px を渡すと精密化もする(texW*texH 画素)。結果のマスクは受け取った側が手放す。
        /// </summary>
        public static SamMaskPostprocess.Result SelectAndUpscale(float[] logits, float[] scores, int texW, int texH,
            NativePixels px, MaskSuggestGranularity granularity, CancellationToken token = default)
            => SamMaskPostprocess.SelectAndUpscale(logits, scores, texW, texH, SamMaskPostprocess.DefaultFloodRejectFrac,
                px == null ? null : px.Ptr, granularity, token);

        /// <summary>クリック画素を含む連結成分の外接矩形の長辺(<see cref="SamZoomOps.ClickComponentBBoxLong(bool*, int, int, int, int)"/>)。</summary>
        public static int ClickComponentBBoxLong(NativeBools mask, int clickX, int clickY)
            => SamZoomOps.ClickComponentBBoxLong(mask.Ptr, mask.Width, mask.Height, clickX, clickY);

        /// <summary>切り出しのマスク(side 四方)を w*h へ貼り戻す(参照 1。受け取った側が手放す)。</summary>
        public static NativeBools PasteCrop(NativeBools crop, int w, int h, int x0, int y0, out int trueCount)
            => SamZoomOps.PasteCrop(crop.Ptr, crop.Width, w, h, x0, y0, out trueCount);
    }
}
