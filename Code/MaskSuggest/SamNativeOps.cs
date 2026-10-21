// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI 提案の画素処理(SamImageOps・SamMaskPostprocess・SamZoomOps)を、Mono のヒープの外に置いた画素
    /// (<see cref="NativePixels"/>)で呼ぶ入口。推論のサービス(Iroca.SentisIntegration)の asmdef は unsafe を
    /// 許していないので、ポインタを扱うのはここに閉じ込める。呼び出し側は読み終えるまで画素の参照を持つこと。
    /// </summary>
    internal static unsafe class SamNativeOps
    {
        public static float[] BuildEncoderInput(NativePixels px, CancellationToken token = default)
            => SamImageOps.BuildEncoderInput(px.Ptr, px.Width, px.Height, token);

        // 画素が無ければ何もしない(配列版の RefineInPlace と同じ約束)。
        public static void RefineInPlace(bool[] maskBottomUp, NativePixels px, CancellationToken token = default)
        {
            if (px == null) return;
            SamMaskPostprocess.RefineInPlace(maskBottomUp, px.Ptr, px.Width, px.Height, token);
        }

        public static Color32[] ExtractCrop(NativePixels px, int x0, int y0, int side)
            => SamZoomOps.ExtractCrop(px.Ptr, px.Width, px.Height, x0, y0, side);
    }
}
