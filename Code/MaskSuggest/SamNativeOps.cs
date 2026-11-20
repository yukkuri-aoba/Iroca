// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// AI 提案の画素処理(SamImageOps・SamMaskPostprocess・SamZoomOps)を、Mono のヒープの外に置いた画素
    /// (<see cref="NativePixels"/>)で呼ぶ入口。推論のサービス(Iroca.SentisIntegration)の asmdef は unsafe を
    /// 許していないので、ポインタを扱うのはここに閉じ込める。呼び出し側は読み終えるまで画素の参照を持つこと。
    /// エンコーダの入力と埋め込みはヒープの外の float の並び(<see cref="NativeBuffer"/>)で持ち、推論に渡すときだけ
    /// <see cref="NativeArray{T}"/> に見せる(<see cref="FloatView"/>。0.4.0 の計画の B4)。
    /// </summary>
    internal static unsafe class SamNativeOps {
        /// <summary>原本からエンコーダの入力(<see cref="SamImageOps.EncoderInputLength"/> 個の float。参照 1 で返す)を作る。</summary>
        public static NativeBuffer BuildEncoderInput(NativePixels px, CancellationToken token = default)
            => BuildEncoderInput(px.Ptr, px.Width, px.Height, token);

        private static NativeBuffer BuildEncoderInput(Color32* px, int w, int h, CancellationToken token) {
            var chw = new NativeBuffer((long)SamImageOps.EncoderInputLength * sizeof(float), NativeUsage.Other);
            try {
                SamImageOps.BuildEncoderInputInto(px, w, h, (float*)chw.Ptr, token);
                return chw;
            } catch {
                chw.Release();
                throw;
            }
        }

        /// <summary>
        /// float の並び(length 個)を <see cref="NativeArray{T}"/> に見せる(写さない)。見せている間は buffer の参照を
        /// 持つこと。使い終わったら Dispose する(並びは手放さない)。
        /// </summary>
        public static TextureIO.NativeView<float> FloatView(NativeBuffer buffer, int length) {
            if (buffer == null) {
                throw new System.ArgumentNullException(nameof(buffer));
            }
            if ((long)length * sizeof(float) > buffer.Bytes) {
                throw new System.ArgumentOutOfRangeException(nameof(length), $"{length} 個は {buffer.Bytes} バイトに入らない");
            }
            return TextureIO.NativeView<float>.Over((void*)buffer.Ptr, length);
        }

        /// <summary>
        /// 推論の読み出し(<see cref="NativeArray{T}"/>。CPU では Temp、GPU では読み出しの持ち物で、次のフレームまで
        /// しか使えない)を、持ち続けられるヒープの外の並びへ写す(参照 1 で返す。空なら null)。
        /// </summary>
        public static NativeBuffer CopyFloats(NativeArray<float> src) {
            if (!src.IsCreated || src.Length == 0) {
                return null;
            }
            long bytes = (long)src.Length * sizeof(float);
            var dst = new NativeBuffer(bytes, NativeUsage.Other);
            System.Buffer.MemoryCopy(NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(src), (void*)dst.Ptr, bytes, bytes);
            return dst;
        }

        /// <summary>float の並びの要素数(null は 0)。</summary>
        public static int FloatCount(NativeBuffer buffer) => buffer == null ? 0 : (int)(buffer.Bytes / sizeof(float));

        // 以下の後処理の口は、中身を Core の SamProposalSteps(Ops。ハーネスも同じ段を呼ぶ)へ移した。口は残し、そちらを呼ぶ。

        /// <summary>精密化 4 段(<see cref="SamProposalSteps.RefineInPlace"/>)。画素が無ければ何もしない(配列版の RefineInPlace と同じ約束)。</summary>
        public static void RefineInPlace(NativeBools mask, NativePixels px, CancellationToken token = default)
            => SamProposalSteps.RefineInPlace(mask, px, token);

        /// <summary>
        /// クリックした塊と色の違う離れた塊を外す(<see cref="SamProposalSteps.DropForeignComponents"/>)。
        /// クリックは px の画素座標(下原点)。画素が無ければ何もしない。外した画素数を返す。
        /// </summary>
        public static int DropForeignComponents(NativeBools mask, NativePixels px, int clickX, int clickY,
            CancellationToken token = default)
            => SamProposalSteps.DropForeignComponents(mask, px, clickX, clickY, token);

        /// <summary>切り出し(参照 1。受け取った側が手放す。<see cref="SamProposalSteps.ExtractCrop"/>)。</summary>
        public static NativePixels ExtractCrop(NativePixels px, int x0, int y0, int side)
            => SamProposalSteps.ExtractCrop(px, x0, y0, side);

        /// <summary>
        /// デコーダの出力から提案マスクを作る(<see cref="SamProposalSteps.SelectAndUpscale"/>)。
        /// px を渡すと精密化もする(texW*texH 画素)。結果のマスクは受け取った側が手放す。
        /// </summary>
        public static SamMaskPostprocess.Result SelectAndUpscale(float[] logits, float[] scores, int texW, int texH,
            NativePixels px, MaskSuggestGranularity granularity, CancellationToken token = default)
            => SamProposalSteps.SelectAndUpscale(logits, scores, texW, texH, px, granularity, token);

        /// <summary>
        /// 右クリックの提案(用途 Region・粒度 自動)の候補選びと拡大(<see cref="SamProposalSteps.SelectAndUpscaleByColor"/>。
        /// 精密化はしない)。px は候補の色を比べるのに読む(texW*texH 画素。null なら既定の自動と同じ)。結果のマスクは受け取った側が手放す。
        /// </summary>
        public static SamMaskPostprocess.Result SelectAndUpscaleByColor(float[] logits, float[] scores, int texW, int texH,
            NativePixels px, CancellationToken token = default)
            => SamProposalSteps.SelectAndUpscaleByColor(logits, scores, texW, texH, px, token);

        /// <summary>クリック画素を含む連結成分の外接矩形の長辺(<see cref="SamProposalSteps.ClickComponentBBoxLong"/>)。</summary>
        public static int ClickComponentBBoxLong(NativeBools mask, int clickX, int clickY)
            => SamProposalSteps.ClickComponentBBoxLong(mask, clickX, clickY);

        /// <summary>切り出しのマスク(side 四方)を w*h へ貼り戻す(参照 1。受け取った側が手放す。<see cref="SamProposalSteps.PasteCrop"/>)。</summary>
        public static NativeBools PasteCrop(NativeBools crop, int w, int h, int x0, int y0, out int trueCount)
            => SamProposalSteps.PasteCrop(crop, w, h, x0, y0, out trueCount);
    }
}
