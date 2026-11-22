// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// AI 提案の後処理の段(<see cref="SamProposalSteps"/>)の結果。第 1 段では提案の範囲とズームの計画、
    /// ズームの後処理では提案の範囲だけ(hasCrop=false)。
    /// </summary>
    internal sealed class SamPostOutcome {
        // 提案の範囲(ヒープの外。この結果が参照を 1 つ持ち、配信で提案へ渡す)。
        public NativeBools mask;
        // 採用チャンネル(1..3。診断用。SamMaskPostprocess.Result.channel)。
        public int channel;
        public float score;
        public float areaFrac;
        public bool floodWarning;
        // ズーム計画(hasCrop=true のときのみ有効。下原点矩形)。
        // hasCrop=true の mask は粗マスクのまま(精密化はズーム不発時のみ行う)。
        // ズーム失敗で第 1 段へ退避するときは、配信前に精密化する(SamProposalSteps.RefineStage1)。
        public bool hasCrop;
        public int cropX0, cropY0, cropSide;
        // 切り出した画素(ヒープの外。この結果が参照を 1 つ持つ。読む段は自分の参照を足す)。
        public NativePixels cropPixels;
        // 計測のログ用(呼んだ側が測って書く。段は時計を見ない)。
        public double postMs;
        public double refineMs; // 精密化 4 段(走ったときのみ非 0)
        // 部分をマスクに当てる用途(MaskSuggestUse.Region)なら、精密化の後に色の違う離れた塊を外す。
        // クリックは全体の画素座標(下原点)。ズームの第 2 段・第 1 段への退避もこれを見る。
        public bool dropForeign;
        // 「ここも塗る」(MaskSuggestUse.RegionPaintHere)で粒度が自動なら、最後に右クリックした所の色に近い画素だけに絞る
        // (SamMaskRefine.KeepNearClickColor)。ズームの第 2 段・第 1 段への退避もこれを見る。
        public bool keepNear;
        // keepNear で外した画素数(診断用。ハーネスの SCRIPT の行と、窓の「足す所が無い」の知らせが読む)。
        public int colorDropped;
        // 部分をマスクに当てる用途で粒度が自動なら、候補を色で選ぶ(SamMaskPostprocess.SelectAndUpscaleByColor)。
        // ズームの第 2 段もこれを見る。
        public bool byColor;
        public int clickX, clickY;

        /// <summary>持っている画素(範囲・切り出し)を返す。配信で範囲を渡した後は切り出しだけ。</summary>
        public void Release() {
            mask?.Release();
            mask = null;
            cropPixels?.Release();
            cropPixels = null;
        }
    }

    /// <summary>
    /// AI 提案の後処理の段(デコーダの出力 → 提案の範囲)。推論のサービス(Sentis)は背景のジョブからこれを呼ぶだけにし、
    /// 同じ段をハーネスからも呼べるように Ops に置く。どの段も時計・スレッド・推論を知らない(背景のスレッドで呼んでよい)。
    /// 段の順と条件は出力のビット一致に効くので、変えるときは呼ぶ側と合わせて測る。
    ///   第 1 段(<see cref="Stage1"/>): 粗マスク → ズームの判定 →(ズームしなければ)精密化。
    ///   ズームの後処理(<see cref="ZoomPost"/>): 切り出しの提案 → 全体へ貼り戻す。
    ///   第 1 段へ戻す(<see cref="RefineStage1"/>): ズームが失敗したとき、第 1 段の粗マスクを精密化する。
    /// 画素は <see cref="NativePixels"/>(ヒープの外)で受ける。ポインタを扱うのはここに閉じ込める
    /// (推論のサービスの asmdef は unsafe を許していない)。
    /// </summary>
    internal static unsafe class SamProposalSteps {
        /// <summary>
        /// 候補を色で選ぶか。右クリックの自動は、隣の部品を巻き込んだ大きい候補を避けて色で選ぶ。自動調整の証拠と、
        /// 利用者が大きさを選んだ細かい・大きいは既定の選び方。
        /// </summary>
        public static bool ChoosesByColor(MaskSuggestUse use, MaskSuggestGranularity granularity)
            => IsRegion(use) && granularity == MaskSuggestGranularity.Auto;

        /// <summary>
        /// 右クリック・自動化の範囲の操作に使う提案の用途。「ここも塗る」(paintsHere)を、もうそのゾーンで塗れている所で押した
        /// (clickPainted。決め方は MaskRegionEdit.ZonePaintsAt)ときだけ、範囲を右クリックした所の色に近い画素へ絞る用途
        /// (<see cref="MaskSuggestUse.RegionPaintHere"/>)、ほかは <see cref="MaskSuggestUse.Region"/>(範囲を色に関係なく当てる)。
        /// 塗れている所を押したなら、利用者は塗れていない部品を指していない。範囲に入る色の違う所はゾーンが色で分けて塗らなかった
        /// 所なので足さない(2026-11-22 ユーザー判断: 黒い文字の上で押しても下地は塗らない)。塗れていない所を押したなら、色の照合が
        /// 取りこぼしたその部品を指しているので、色が違っても丸ごと足す(星柄の明るい雲・宝石の明暗・瞳)。色の距離では「別の物」と
        /// 「同じ物の明暗」を分けられない(dev_safe の 2026-11-22 の診断)ので、押した所で分ける。
        /// 窓・自動化・ハーネスが同じ規則で求める(操作の型 MaskRegionOp は Core にあり、ここからは見えないので、呼ぶ側が渡す)。
        /// </summary>
        public static MaskSuggestUse RegionUse(bool paintsHere, bool clickPainted)
            => paintsHere && clickPainted ? MaskSuggestUse.RegionPaintHere : MaskSuggestUse.Region;

        /// <summary>部分をマスクに当てる用途か(右クリック・自動化のマスク操作。「ここも塗る」を含む)。</summary>
        public static bool IsRegion(MaskSuggestUse use)
            => use == MaskSuggestUse.Region || use == MaskSuggestUse.RegionPaintHere;

        /// <summary>
        /// 最後に、右クリックした所の色に近い画素だけに絞るか。「ここも塗る」で粒度が自動のときだけ。利用者が大きさを選んだ
        /// 「小さめ・大きめ」は、選んだ大きさの範囲をそのまま足す(色の違う所まで足したいときの道。影で暗い歯と白い歯のように、
        /// 同じ部品の明暗が色で分かれる細部を小さめで足す使い方も残る)。ChoosesByColor と同じ分け方。
        /// </summary>
        public static bool KeepsNearClickColor(MaskSuggestUse use, MaskSuggestGranularity granularity)
            => use == MaskSuggestUse.RegionPaintHere && granularity == MaskSuggestGranularity.Auto;

        /// <summary>
        /// 精密化の後に、クリックした塊と色の違う離れた塊を外すか。細かいは細部を狙って選ぶ粒度なので外さない
        /// (SamMaskRefine.DropForeignComponents の説明)。
        /// </summary>
        public static bool DropsForeign(MaskSuggestUse use, MaskSuggestGranularity granularity)
            => IsRegion(use) && granularity != MaskSuggestGranularity.Fine;

        /// <summary>切り出しの埋め込みの鍵(元の鍵と切り出しの矩形)。</summary>
        public static string CropEmbeddingKey(string sourceKey, int x0, int y0, int side)
            => $"{sourceKey}|{x0},{y0},{side}";

        /// <summary>
        /// 第 1 段(全体)の後処理: 粗マスク → ズームの判定 →(ズームしなければ)精密化。結果は受け取った側が返す
        /// (投げたときは、作りかけの範囲・切り出しをここで返す)。
        /// px は元の画素(w*h。下原点)。u, v はクリックの UV(下原点)。
        /// coarseDone は粗マスクとズームの判定(切り出しを含む)を終えたところで呼ぶ(計測の観察者。null なら呼ばない)。
        /// </summary>
        public static SamPostOutcome Stage1(float[] logits, float[] scores, int w, int h, NativePixels px,
                                            float u, float v, MaskSuggestGranularity granularity, MaskSuggestUse use,
                                            CancellationToken ct, System.Action coarseDone = null) {
            // まず精密化なしの粗マスクだけ作り、ズームイン再推論の要否を判定する。
            // ズーム発火時は第 1 段マスクが最終出力に使われない(ズームの後処理が
            // 空配列へ PasteCrop した結果で置き換える)ため、発火時に精密化すると
            // その分(4K 実測 0.8-1.4s)が丸ごと捨てられる。不発時のみ従来と同一
            // 順序・同一入力で精密化する(= SelectAndUpscale(px) と厳密同値)。
            // 右クリックの自動は、隣の部品を巻き込んだ大きい候補を避けて色で選ぶ(画素は色を比べるのに読むだけで、
            // 精密化はここでもしない)。自動調整の証拠と、利用者が大きさを選んだ細かい・大きいは既定の選び方。
            bool byColor = ChoosesByColor(use, granularity);
            var s1 = byColor
                ? SelectAndUpscaleByColor(logits, scores, w, h, px, ct)
                : SelectAndUpscale(logits, scores, w, h, null, granularity, ct);
            var o = new SamPostOutcome {
                mask = s1.mask,
                channel = s1.channel,
                score = s1.score,
                areaFrac = s1.areaFrac,
                floodWarning = s1.floodWarning,
                byColor = byColor,
            };
            try {
                // ズームイン再推論の判定: クリック成分が小さい(=256²ロジットで形状表現
                // できない)場合のみ、クリック周辺クロップの再推論計画を積む。粗マスクの
                // bbox は精密化後と数 px しか違わず、クロップ矩形は 2 冪スナップで吸収される
                // (実 SAM fixture で crop 決定の一致を確認済み)。
                int cx = Mathf.Clamp((int)(u * w), 0, w - 1);
                int cy = Mathf.Clamp((int)(v * h), 0, h - 1); // v は下原点 → 下原点行と一致
                o.dropForeign = DropsForeign(use, granularity);
                o.keepNear = KeepsNearClickColor(use, granularity);
                o.clickX = cx;
                o.clickY = cy;
                int bb = ClickComponentBBoxLong(o.mask, cx, cy);
                bool hasCrop = SamZoomOps.TryDeriveCropRect(bb, cx, cy, w, h,
                                                            out int x0, out int y0, out int side);
                if (hasCrop) {
                    o.hasCrop = true;
                    o.cropX0 = x0;
                    o.cropY0 = y0;
                    o.cropSide = side;
                    o.cropPixels = ExtractCrop(px, x0, y0, side);
                }
                coarseDone?.Invoke();
                if (!hasCrop) {
                    o.colorDropped = RefineStage1(o.mask, px, o.dropForeign, o.keepNear, cx, cy, ct);
                }
                return o;
            } catch {
                o.Release();
                throw;
            }
        }

        /// <summary>
        /// 第 1 段の範囲の精密化 4 段 →(dropForeign なら)クリックした塊と色の違う離れた塊を外す →(keepNear なら)
        /// クリックの色に近い画素だけに絞る。ズームしなかったときの第 1 段と、ズームが失敗して第 1 段へ戻すときの両方がこれを通る。
        /// mask はその場で書き換える(持ち主は変わらない)。クリックは px の画素座標(下原点)。px が無ければ何もしない。
        /// 色で絞って外した画素数を返す。
        /// </summary>
        public static int RefineStage1(NativeBools mask, NativePixels px, bool dropForeign, bool keepNear,
                                       int clickX, int clickY, CancellationToken ct) {
            RefineInPlace(mask, px, ct);
            if (dropForeign) {
                DropForeignComponents(mask, px, clickX, clickY, ct);
            }
            return keepNear ? KeepNearClickColor(mask, px, clickX, clickY, ct) : 0;
        }

        /// <summary>
        /// ズームの第 2 段の後処理: 切り出しの提案 → 全体(w*h)へ貼り戻す。plan は第 1 段の結果(切り出しの矩形・
        /// byColor・dropForeign・クリックを読むだけで、持ち主は変わらない)。cropPx は切り出した画素(読むだけ)。
        /// 結果(範囲だけ。hasCrop=false)は受け取った側が返す。
        /// </summary>
        public static SamPostOutcome ZoomPost(SamPostOutcome plan, MaskSuggestGranularity granularity,
                                              float[] logits, float[] scores, int w, int h, NativePixels cropPx,
                                              CancellationToken ct) {
            int x0 = plan.cropX0, y0 = plan.cropY0, side = plan.cropSide;
            // 色で選ぶときは、選んだ後に精密化する(SelectAndUpscale に画素を渡したときと同じ順)。
            var res = plan.byColor
                ? SelectAndUpscaleByColor(logits, scores, side, side, cropPx, ct)
                : SelectAndUpscale(logits, scores, side, side, cropPx, granularity, ct);
            NativeBools full;
            int trueCount;
            int colorDropped = 0;
            try {
                if (plan.byColor) {
                    RefineInPlace(res.mask, cropPx, ct);
                }
                // 切り出しの中で外しても、貼り戻した全体で外したのと同じ(提案は切り出しの外に無い)。
                if (plan.dropForeign) {
                    DropForeignComponents(res.mask, cropPx, plan.clickX - x0, plan.clickY - y0, ct);
                }
                if (plan.keepNear) {
                    colorDropped = KeepNearClickColor(res.mask, cropPx, plan.clickX - x0, plan.clickY - y0, ct);
                }
                full = PasteCrop(res.mask, w, h, x0, y0, out trueCount);
            } finally { res.mask.Release(); }
            return new SamPostOutcome {
                mask = full,
                channel = res.channel,
                score = res.score,
                areaFrac = trueCount / (float)(w * h),
                floodWarning = res.floodWarning,
                colorDropped = colorDropped,
            };
        }

        // ── 画素(NativePixels)で呼ぶ部品。 ──

        /// <summary>精密化 4 段(<see cref="SamMaskPostprocess.RefineInPlace(bool*, Color32*, int, int, CancellationToken)"/>)。画素か範囲が無ければ何もしない(配列版の RefineInPlace と同じ約束)。</summary>
        public static void RefineInPlace(NativeBools mask, NativePixels px, CancellationToken token = default) {
            if (px == null || mask == null) {
                return;
            }
            SamMaskPostprocess.RefineInPlace(mask.Ptr, px.Ptr, px.Width, px.Height, token);
        }

        /// <summary>
        /// クリックした塊と色の違う離れた塊を外す(<see cref="SamMaskRefine.DropForeignComponents(bool*, Color32*, int, int, int, int, CancellationToken, float, float)"/>)。
        /// クリックは px の画素座標(下原点)。画素が無ければ何もしない。外した画素数を返す。
        /// </summary>
        public static int DropForeignComponents(NativeBools mask, NativePixels px, int clickX, int clickY,
            CancellationToken token = default) {
            if (px == null || mask == null) {
                return 0;
            }
            return SamMaskRefine.DropForeignComponents(mask.Ptr, px.Ptr, px.Width, px.Height, clickX, clickY, token);
        }

        /// <summary>
        /// クリックの色に近い画素だけを残す(<see cref="SamMaskRefine.KeepNearClickColor(bool*, Color32*, int, int, int, int, CancellationToken, float, float)"/>)。
        /// クリックは px の画素座標(下原点)。画素か範囲が無ければ何もしない。外した画素数を返す。
        /// </summary>
        public static int KeepNearClickColor(NativeBools mask, NativePixels px, int clickX, int clickY,
            CancellationToken token = default) {
            if (px == null || mask == null) {
                return 0;
            }
            return SamMaskRefine.KeepNearClickColor(mask.Ptr, px.Ptr, px.Width, px.Height, clickX, clickY, token);
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

        /// <summary>
        /// 右クリックの提案(用途 Region・粒度 自動)の候補選びと拡大(<see cref="SamMaskPostprocess.SelectAndUpscaleByColor(float[], float[], int, int, float, Color32*, CancellationToken, float, float)"/>。
        /// 精密化はしない)。px は候補の色を比べるのに読む(texW*texH 画素。null なら既定の自動と同じ)。結果のマスクは受け取った側が手放す。
        /// </summary>
        public static SamMaskPostprocess.Result SelectAndUpscaleByColor(float[] logits, float[] scores, int texW, int texH,
            NativePixels px, CancellationToken token = default)
            => SamMaskPostprocess.SelectAndUpscaleByColor(logits, scores, texW, texH,
                SamMaskPostprocess.DefaultFloodRejectFrac, px == null ? null : px.Ptr, token);

        /// <summary>クリック画素を含む連結成分の外接矩形の長辺(<see cref="SamZoomOps.ClickComponentBBoxLong(bool*, int, int, int, int)"/>)。</summary>
        public static int ClickComponentBBoxLong(NativeBools mask, int clickX, int clickY)
            => SamZoomOps.ClickComponentBBoxLong(mask.Ptr, mask.Width, mask.Height, clickX, clickY);

        /// <summary>切り出しのマスク(side 四方)を w*h へ貼り戻す(参照 1。受け取った側が手放す)。</summary>
        public static NativeBools PasteCrop(NativeBools crop, int w, int h, int x0, int y0, out int trueCount)
            => SamZoomOps.PasteCrop(crop.Ptr, crop.Width, w, h, x0, y0, out trueCount);
    }
}
