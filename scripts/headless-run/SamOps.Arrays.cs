// AI マスク提案の純計算(Code/MaskSuggest/Ops)の、管理配列(bool[]・Color32[])を受ける口。
// 製品はヒープの外の版(ポインタ)だけを呼ぶので、配列の口は製品の配布物に入れず、ハーネスがここで partial として足す。
// どれも大きさを確かめて fixed し、ポインタの版を呼ぶだけ(計算の本体と説明はポインタの版にある)。
// Harness.cs の samops 系のモードと、abcheck の反射 probe(SelectAndUpscale・RefineInPlace を配列で呼ぶ)が使う。
// AI 提案の結果キャッシュの鍵には、このファイルの中身も入る(tools/harness_build.py の _samops_digest)。
using UnityEngine;

namespace Iroca {
    internal static unsafe partial class SamMaskPostprocess {
        /// <summary>
        /// テスト・ハーネス用。<see cref="SelectAndUpscale(float[], float[], int, int, float, Color32*, MaskSuggestGranularity, System.Threading.CancellationToken)"/>
        /// の画素が配列の版(null か texW*texH に足りなければ精密化しない)。
        /// </summary>
        public static Result SelectAndUpscale(float[] logits, float[] scores, int texW, int texH,
                                              float floodRejectFrac = DefaultFloodRejectFrac,
                                              Color32[] pixelsBottomUp = null,
                                              MaskSuggestGranularity granularity = MaskSuggestGranularity.Auto,
                                              System.Threading.CancellationToken token = default) {
            // 各段は画素が足りなければ何もしない(配列版の約束)ので、足りないときは精密化しないのと同じ。
            if (pixelsBottomUp != null && pixelsBottomUp.Length < texW * texH) {
                pixelsBottomUp = null;
            }
            fixed (Color32* p = pixelsBottomUp) {
                return SelectAndUpscale(logits, scores, texW, texH, floodRejectFrac, p, granularity, token);
            }
        }

        /// <summary>
        /// テスト・ハーネス用。<see cref="RefineInPlace(bool*, Color32*, int, int, System.Threading.CancellationToken)"/> の配列版
        /// (マスクが texW*texH 個でないか、画素が足りなければ何もしない)。
        /// </summary>
        public static void RefineInPlace(bool[] maskBottomUp, Color32[] pixelsBottomUp,
                                         int texW, int texH,
                                         System.Threading.CancellationToken token = default) {
            // 各段は画素が足りなければ何もしない(配列版の約束)ので、足りないときは丸ごと飛ばしても同じ。
            if (maskBottomUp == null || maskBottomUp.Length != texW * texH) {
                return;
            }
            if (pixelsBottomUp == null || pixelsBottomUp.Length < texW * texH) {
                return;
            }
            fixed (bool* m = maskBottomUp)
            fixed (Color32* p = pixelsBottomUp) {
                RefineInPlace(m, p, texW, texH, token);
            }
        }
    }

    internal static unsafe partial class SamMaskRefine {
        /// <summary>テスト・ハーネス用。<see cref="IncludeAaTransition(bool*, Color32*, int, int, System.Threading.CancellationToken, int)"/> の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static void IncludeAaTransition(bool[] mask, Color32[] pixelsBottomUp, int w, int h,
                                               System.Threading.CancellationToken token = default,
                                               int dOverride = 0) {
            if (mask == null || mask.Length != w * h) {
                return;
            }
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) {
                return;
            }
            fixed (bool* m = mask)
            fixed (Color32* p = pixelsBottomUp) {
                IncludeAaTransition(m, p, w, h, token, dOverride);
            }
        }

        /// <summary>テスト・ハーネス用。<see cref="TryDeriveAaCropRect(bool*, int, int, out int, out int, out int, out int, out int)"/> の配列版(mask が w*h 個でなければ false)。</summary>
        public static bool TryDeriveAaCropRect(bool[] mask, int w, int h,
                                               out int rx0, out int ry0, out int rw, out int rh,
                                               out int d) {
            rx0 = ry0 = rw = rh = 0;
            d = BandHalfWidth(w, h);
            if (mask == null || mask.Length != w * h) {
                return false;
            }
            fixed (bool* m = mask) {
                return TryDeriveAaCropRect(m, w, h, out rx0, out ry0, out rw, out rh, out d);
            }
        }

        /// <summary>
        /// テスト・ハーネス用。<see cref="IncludeAaTransitionCropped(bool*, int, int, Color32*, int, int, int, int, int, System.Threading.CancellationToken)"/>
        /// の配列版(大きさが合わなければ何もしない)。
        /// </summary>
        public static void IncludeAaTransitionCropped(bool[] fullMask, int w, int h,
                                                      Color32[] cropPixels,
                                                      int rx0, int ry0, int rw, int rh, int d,
                                                      System.Threading.CancellationToken token = default) {
            if (fullMask == null || cropPixels == null || fullMask.Length != w * h ||
                cropPixels.Length < rw * rh || rw <= 0 || rh <= 0) {
                return;
            }
            fixed (bool* m = fullMask)
            fixed (Color32* p = cropPixels) {
                IncludeAaTransitionCropped(m, w, h, p, rx0, ry0, rw, rh, d, token);
            }
        }

        /// <summary>テスト・ハーネス用。<see cref="TryDeriveRefineCropRect(bool*, int, int, System.Threading.CancellationToken, out bool, out int, out int, out int, out int)"/> の配列版(mask が w*h 個でなければ false)。</summary>
        internal static bool TryDeriveRefineCropRect(bool[] mask, int w, int h, System.Threading.CancellationToken token,
            out bool empty, out int rx0, out int ry0, out int rw, out int rh) {
            rx0 = ry0 = rw = rh = 0;
            empty = false;
            if (mask == null || mask.Length != w * h) {
                return false;
            }
            fixed (bool* m = mask) {
                return TryDeriveRefineCropRect(m, w, h, token, out empty, out rx0, out ry0, out rw, out rh);
            }
        }

        /// <summary>テスト・ハーネス用。<see cref="ExtendFringe(bool*, Color32*, int, int, System.Threading.CancellationToken, int, int)"/> の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static void ExtendFringe(bool[] mask, Color32[] pixelsBottomUp, int w, int h,
                                        System.Threading.CancellationToken token = default) {
            if (mask == null || mask.Length != w * h) {
                return;
            }
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) {
                return;
            }
            fixed (bool* m = mask)
            fixed (Color32* p = pixelsBottomUp) {
                ExtendFringe(m, p, w, h, token);
            }
        }

        /// <summary>テスト・ハーネス用。<see cref="PeelThinSpill(bool*, Color32*, int, int, System.Threading.CancellationToken, int, int)"/> の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static void PeelThinSpill(bool[] mask, Color32[] pixelsBottomUp, int w, int h,
                                         System.Threading.CancellationToken token = default) {
            if (mask == null || mask.Length != w * h) {
                return;
            }
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) {
                return;
            }
            fixed (bool* m = mask)
            fixed (Color32* p = pixelsBottomUp) {
                PeelThinSpill(m, p, w, h, token);
            }
        }

        /// <summary>テスト・ハーネス用。<see cref="DropForeignComponents(bool*, Color32*, int, int, int, int, System.Threading.CancellationToken, float, float)"/>
        /// の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static int DropForeignComponents(bool[] mask, Color32[] pixelsBottomUp, int w, int h, int clickX, int clickY,
                                                float nearColor = ForeignNearColor, float ratio = ForeignNearFracRatio) {
            if (mask == null || mask.Length != w * h) {
                return 0;
            }
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) {
                return 0;
            }
            fixed (bool* m = mask)
            fixed (Color32* p = pixelsBottomUp) {
                return DropForeignComponents(m, p, w, h, clickX, clickY, default, nearColor, ratio);
            }
        }

        /// <summary>テスト・ハーネス用。<see cref="SnapBoundary(bool*, Color32*, int, int, System.Threading.CancellationToken, int, int)"/> の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static void SnapBoundary(bool[] mask, Color32[] pixelsBottomUp, int w, int h,
                                        System.Threading.CancellationToken token = default) {
            if (mask == null || mask.Length != w * h) {
                return;
            }
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) {
                return;
            }
            fixed (bool* m = mask)
            fixed (Color32* p = pixelsBottomUp) {
                SnapBoundary(m, p, w, h, token);
            }
        }

        /// <summary>
        /// テスト・ハーネス用。<see cref="TransferCoverage(bool*, int, int, bool*, int, int)"/> の、書き先を新しい配列で返す版
        /// (src が sw*sh 個でないか寸法が正でなければ null)。
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
    }

    internal static unsafe partial class SamZoomOps {
        /// <summary>テスト・ハーネス用。<see cref="ClickComponentBBoxLong(bool*, int, int, int, int)"/> の配列版(マスクが w*h 個でなければ 0)。</summary>
        public static int ClickComponentBBoxLong(bool[] maskBottomUp, int w, int h,
                                                 int clickX, int clickY) {
            if (maskBottomUp == null || maskBottomUp.Length != w * h) {
                return 0;
            }
            fixed (bool* m = maskBottomUp) {
                return ClickComponentBBoxLong(m, w, h, clickX, clickY);
            }
        }

        /// <summary>テスト・ハーネス用。<see cref="ExtractCrop(Color32*, int, int, int, int, int)"/> の、切り出しを配列で返す版。</summary>
        public static Color32[] ExtractCrop(Color32[] pixelsBottomUp, int w, int h,
                                            int x0, int y0, int side) {
            CheckCropInside(w, h, x0, y0, side);
            var crop = new Color32[side * side];
            for (int y = 0; y < side; y++) {
                System.Array.Copy(pixelsBottomUp, (y0 + y) * w + x0, crop, y * side, side);
            }
            return crop;
        }

        /// <summary>テスト・ハーネス用。<see cref="PasteCrop(bool*, int, int, int, int, int, out int)"/> の、配列で受けて配列で返す版。</summary>
        public static bool[] PasteCrop(bool[] cropMaskBottomUp, int side, int w, int h,
                                       int x0, int y0, out int trueCount) {
            var full = new bool[w * h];
            fixed (bool* c = cropMaskBottomUp)
            fixed (bool* f = full) {
                trueCount = PasteCrop(c, side, w, h, x0, y0, f);
            }
            return full;
        }
    }

    internal static unsafe partial class SamImageOps {
        /// <summary>
        /// テスト・ハーネス用。<see cref="BuildEncoderInputInto(Color32*, int, int, float*, System.Threading.CancellationToken)"/>
        /// の、画素を配列で受けて書き先を新しい配列で返す版(画素が w*h に足りなければ例外)。
        /// </summary>
        public static float[] BuildEncoderInput(Color32[] pixelsBottomUp, int w, int h,
                                                System.Threading.CancellationToken token = default) {
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) {
                throw new System.ArgumentException("pixelsBottomUp が w*h 画素に足りません", nameof(pixelsBottomUp));
            }
            var chw = new float[3 * InputSize * InputSize];
            fixed (Color32* p = pixelsBottomUp)
            fixed (float* d = chw) {
                BuildEncoderInputInto(p, w, h, d, token);
            }
            return chw;
        }
    }
}
