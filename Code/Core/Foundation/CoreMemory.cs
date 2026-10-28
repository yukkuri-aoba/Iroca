// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading.Tasks;

namespace Iroca
{
    /// <summary>
    /// Core が持つヒープの外のメモリのうち、いま使っていない分をまとめて返す窓口(0.4.0 の計画の 3-4)。
    /// PixelProcessor の <c>ReleaseWorkBuffers</c> / <c>ReleaseWorkBuffersInBackground</c> は転送役。
    /// </summary>
    internal static class CoreMemory
    {
        /// <summary>
        /// 作業配列のうち、いま使っていない分と、色の表のキャッシュを全部解放する(OS へ戻る)。計算中の分は
        /// 計算が返すまで残る(使っている色の表はその計算が Release した時点で解放される)。
        /// ウィンドウを閉じる・テクスチャを替える・書き出しの後・リロードの前に UI 側から呼ぶ。次の計算は、
        /// 新しく確保した領域の最初の書き込みと、色の表の作り直しの分だけ遅くなる(4K で数十 ms)。
        /// </summary>
        internal static void ReleaseIdle()
        {
            PixelProcessor.ClearPaletteCache();
            WorkPools.ReleaseFree();   // AI 提案の精密化(SamMaskRefine)の分も同じプール
        }

        /// <summary>
        /// <see cref="ReleaseIdle"/> をスレッドプールで行う(呼んだスレッドはすぐ戻る)。触った大きい塊を OS へ返すのは
        /// 8K で数十〜数百 ms かかるので、書き出しの後・ウィンドウを閉じたときにメインスレッドを止めないよう、こちらを使う。
        /// プールと色の表のキャッシュはロックで守られているので、解放の途中で次の計算が借りに来ても壊れない
        /// (その計算が新しく確保するだけ)。スクリプトのリロードの前は、アンロードに間に合うよう同期の方を使う。
        /// </summary>
        internal static Task ReleaseIdleInBackground() => Task.Run(ReleaseIdle);
    }
}
