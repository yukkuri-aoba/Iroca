// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // SamMaskRefine: 別解像度への被覆保存の転写(TransferCoverage)。クラスの説明と共通の定数・下回りは SamMaskRefine.cs。
    internal static unsafe partial class SamMaskRefine {
        /// <summary>
        /// 提案マスクを別解像度のキャンバスへ「被覆保存」で転写する(フットプリント内に
        /// 1 画素でも提案があれば true)。最近傍サンプリングだと縮小時(実ファイル解像度で
        /// 仕上げた提案 → インポート解像度のマスクキャンバス)に境界の被覆が 1 セル単位で
        /// 欠け、取り残し画素が再着色されて点ノイズになる。保護マスクは「少しでも掛かる
        /// セルは保護」が安全側。拡大方向(ブロック=1px)では最近傍と同値。
        /// 座標系は行方向が一貫していれば上/下原点いずれでも正しい。
        /// </summary>
        public static bool[] TransferCoverage(bool[] src, int sw, int sh, int dw, int dh) {
            if (src == null || sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0 ||
                src.Length != sw * sh) {
                return null;
            }
            var dst = new bool[dw * dh];
            fixed (bool* s = src)
            fixed (bool* t = dst) {
                TransferCoverage(s, sw, sh, t, dw, dh);
            }
            return dst;
        }

        /// <summary>
        /// <see cref="TransferCoverage(bool[], int, int, int, int)"/> の、元(sw*sh 個)と書き先(dw*dh 個。全部に書く)を
        /// ポインタで受ける版。
        /// </summary>
        public static void TransferCoverage(bool* src, int sw, int sh, bool* dst, int dw, int dh) {
            if (src == null || dst == null || sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) {
                return;
            }
            // 各出力画素は自分のフットプリントだけを読む(行並列で決定的)。
            // コミット経路のメインスレッドで呼ばれるため、ここの短縮は UI の引っかかりに直接効く。
            Parallel.For(0, dh, MakeParallelOptions(), my => {
                int sy0 = (int)((long)my * sh / dh);
                int sy1 = (int)((long)(my + 1) * sh / dh);
                if (sy1 <= sy0) {
                    sy1 = Mathf.Min(sh, sy0 + 1);
                }
                int dRow = my * dw;
                for (int mx = 0; mx < dw; mx++) {
                    int sx0 = (int)((long)mx * sw / dw);
                    int sx1 = (int)((long)(mx + 1) * sw / dw);
                    if (sx1 <= sx0) {
                        sx1 = Mathf.Min(sw, sx0 + 1);
                    }
                    bool any = false;
                    for (int sy = sy0; sy < sy1 && !any; sy++) {
                        int sRow = sy * sw;
                        for (int sx = sx0; sx < sx1; sx++) {
                            if (src[sRow + sx]) { any = true; break; }
                        }
                    }
                    dst[dRow + mx] = any;
                }
            });
        }
    }
}
