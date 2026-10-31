// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 保存済みの編集状態(<see cref="IrocaSessionState"/>: ゾーン・処理設定・マスク)を
    /// そのまま画素へ適用する。UI を持たない経路(非破壊ビルド・自動化)の入口で、入力は「適用して保存」と
    /// 同じ組み立て(<see cref="RecolorInput.FromSaved"/>)を通して <see cref="PixelProcessor.ProcessPixelsArray"/> を呼ぶ。
    /// Texture2D も UnityEngine.Object も触らないので、バックグラウンドスレッドから呼んでよい。
    /// </summary>
    internal static class SessionRecolor
    {
        /// <summary>
        /// 画素をその場で再着色する。有効なゾーンが無ければ何もせず false を返す。
        /// </summary>
        public static bool Apply(Color32[] pixels, int width, int height,
            IrocaSessionState state, CancellationToken cancellationToken = default)
        {
            var input = RecolorInput.FromSaved(state);
            if (input == null) return false;
            try
            {
                PixelProcessor.ProcessPixelsArray(pixels, width, height, input.Masks, input.Zones,
                    input.Settings, cancellationToken);
            }
            finally
            {
                input.ReleaseMasks();
            }
            return true;
        }

        /// <summary>
        /// <see cref="Apply(Color32[], int, int, IrocaSessionState, CancellationToken)"/> のポインタ版。Mono のヒープの外に
        /// 置いた画素(非破壊ビルド・シーンのプレビュー)を写さずに処理する。画素は処理が返るまで呼び出し側が保つこと。
        /// </summary>
        public static unsafe bool Apply(Color32* pixels, int width, int height,
            IrocaSessionState state, CancellationToken cancellationToken = default)
        {
            var input = RecolorInput.FromSaved(state);
            if (input == null) return false;
            try
            {
                PixelProcessor.ProcessPixelsNative(pixels, width, height, input.Masks, input.Zones,
                    input.Settings, cancellationToken);
            }
            finally
            {
                input.ReleaseMasks();
            }
            return true;
        }
    }
}
