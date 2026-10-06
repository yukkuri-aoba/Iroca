// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Iroca
{
    // MaskPaintView: マスクオーバーレイ(表示専用)の色・解像度・非同期再構築と、ペイント中の直接書き込みの GPU 反映。
    // 再着色出力には関与しない。
    internal partial class MaskPaintView
    {
        // 共通(除外)マスクのオーバーレイ色。非同期再構築と直接書き込みで共用。
        private static readonly Color32 ExcludedOverlayColor = new Color32(255, 60, 60, 80);
        // 含めるマスクのオーバーレイ色。ゾーンに依らず固定の緑(「緑 = 含める」を一意にする。
        // 除外側のゾーン色は黄金比生成で緑近傍も出るため、彩度と明度で差を付けている)。
        private static readonly Color32 IncludedOverlayColor = new Color32(50, 230, 110, 100);

        // マスクオーバーレイテクスチャの一辺の上限(px)。オーバーレイは「マスクに入っているか /
        // いないか」の二値表示なので、拡大時に GPU 補間でぼかすのは表示として不正確
        // (2026-08-24 のユーザー指摘)。Point 補間でくっきり出すには、オーバーレイ自体が拡大
        // 表示に耐える解像度を持っている必要がある(プレビュー寸法固定のままだと 1 テクセル =
        // マスク 10px 級の粗いブロックになり、判定は正しいのにマスクがはみ出して見える。
        // 2570c32 がぼかしで隠していた症状)。表示倍率に応じてプレビュー寸法の整数倍へ
        // 引き上げ、ここで頭打ちにする(<see cref="OverlayScale"/>)。
        // 2048 は 4K マスクで 1 テクセル ≒ 2px、2K マスクではほぼ画素一致になり、RGBA32 で
        // 15MB/枚(共通・ゾーンの最大 2 枚)と、ゾーン別選択キャッシュ(4K で 67MB/ゾーン)に
        // 対して十分小さい。表示専用なので再着色出力には関与しない。
        private const int OverlayMaxSize = 2048;

        // 編集対象でないマスクのオーバーレイに掛けるアルファ係数。適用中のマスクは
        // すべて表示した上で(「見えないのに効いているマスク」を作らない)、どれを
        // 編集中かは明暗差で示す。0.4 は存在が見えて、かつ編集対象と取り違えない値。
        private const float InactiveOverlayAlphaScale = 0.4f;

        private static Color32 DimOverlayColor(Color32 c)
        {
            c.a = (byte)Mathf.Max(1, Mathf.RoundToInt(c.a * InactiveOverlayAlphaScale));
            return c;
        }

        /// <summary>
        /// オーバーレイ解像度の倍率 k(オーバーレイ寸法 = プレビュー寸法 × k)。
        ///
        /// オーバーレイは二値マスクの表示なので、拡大時は Point 補間でくっきり出す。そのため
        /// には、表示倍率ぶんの解像度をオーバーレイ自身が持っていなければ「1 テクセル = マスク
        /// 10px 級の粗いブロック」になり、判定が正しくてもマスクがはみ出して見える。
        ///
        /// k は次の 3 つで頭打ちにする:
        ///   - 表示倍率(floor)。zoom 未満に切り捨てるので画面上は常に等倍以上の拡大になり、
        ///     Point 補間で間引き(縮小エイリアス)が起きない。
        ///   - マスク解像度。マスクより細かくしても情報が増えない。
        ///   - OverlayMaxSize。メモリと SetPixels32/Apply のコストの上限。
        /// k=1 は従来と同一寸法で、ブロック整列ペイントの直接書き込みもそのまま効く。
        /// </summary>
        public int OverlayScale(int prevW, int prevH, float previewZoom)
        {
            if (prevW <= 0 || prevH <= 0 || maskWidth <= 0 || maskHeight <= 0) return 1;
            int byMask = Mathf.Max(1, Mathf.Min(maskWidth / prevW, maskHeight / prevH));
            int byCap = Mathf.Max(1, OverlayMaxSize / Mathf.Max(prevW, prevH));
            int byZoom = Mathf.Max(1, Mathf.FloorToInt(previewZoom));
            return Mathf.Min(byZoom, Mathf.Min(byMask, byCap));
        }

        /// <summary>
        /// オーバーレイの補間を表示倍率に合わせる。等倍以上(拡大)では Point:
        /// マスクは「入っている / いない」の二値なので、中間アルファを作る補間は表示として
        /// 不正確になる(2026-08-24 のユーザー指摘)。縮小表示では Point だとテクセルの間引きで
        /// 細いマスクがちらつく/消えるため Bilinear に戻す(縮小なのでぼけは見えない)。
        /// 倍率は再構築を伴わずに変わり得るので、描画側から毎フレーム呼んで追従させる。
        /// </summary>
        public void SyncOverlayFilter(float previewZoom)
        {
            var want = previewZoom >= 1f ? FilterMode.Point : FilterMode.Bilinear;
            if (maskOverlayTexture != null && maskOverlayTexture.filterMode != want)
                maskOverlayTexture.filterMode = want;
            if (zoneMaskOverlayTexture != null && zoneMaskOverlayTexture.filterMode != want)
                zoneMaskOverlayTexture.filterMode = want;
        }

        /// <summary>
        /// 現在の編集対象マスクに対応するオーバーレイテクスチャと塗り色を返す。
        /// (共通=maskOverlayTexture/赤、ゾーン=zoneMaskOverlayTexture/ゾーン色)
        /// </summary>
        private Texture2D ActiveOverlayTexture(out Color32 paintColor)
        {
            var zones = _host.Session.zones;
            if (IsCommonTarget(zones))
            {
                paintColor = ExcludedOverlayColor;
                return maskOverlayTexture;
            }
            // 含めるレイヤーも表示スロットはゾーン用テクスチャを共用する(同時表示しないため)。
            paintColor = editIncludeLayer ? IncludedOverlayColor : OverlayColorForZone(activeMaskTarget);
            return zoneMaskOverlayTexture;
        }

        /// <summary>
        /// PaintMask のオーバーレイ直接書き込みを GPU へ反映する。スタンプごとではなく
        /// ドラッグイベント 1 回分につき 1 回だけ Apply するため、呼び出し側
        /// (PaintAtScreenPos)の末尾で呼ぶ。
        /// </summary>
        public void FlushOverlayDirect()
        {
            if (!_overlayDirectPendingApply) return;
            _overlayDirectPendingApply = false;
            var tex = ActiveOverlayTexture(out _);
            if (tex != null) tex.Apply();
        }

        // バックグラウンドで生成したオーバーレイ Color32[] をメインスレッドで Texture2D に
        // 書き戻すまでの中継。SetPixels32 / Apply は Unity API なので必ずメインスレッド。
        private struct OverlayResult
        {
            public bool hasCommon;
            public Color32[] commonPixels;
            public bool hasZone;
            public Color32[] zonePixels;
            public int width;
            public int height;
        }

        [System.NonSerialized] private readonly PreviewJob<OverlayResult> _overlayJob = new PreviewJob<OverlayResult>();
        [System.NonSerialized] private OverlayResult? _pendingOverlayResult;

        /// <summary>
        /// 共通マスクとゾーン別マスクのオーバーレイテクスチャの再構築をバックグラウンドに
        /// スケジュールする。bool[] バッファを Clone してワーカに渡すため、ペイント中の
        /// 変更とデータレースしない。SetPixels32 / Apply は次フレーム以降に
        /// <see cref="ApplyPendingOverlay"/> で適用される。
        /// </summary>
        public void RebuildMaskOverlay(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            // 「構築済み寸法」は適用時ではなくスケジュール時に更新する。適用まで不一致の
            // ままにすると、寸法変更で再構築を促す側(PreviewView)が毎フレーム再スケジュール
            // してジョブを Cancel し続け、オーバーレイが永久に完成しない。
            overlayBuiltW = width;
            overlayBuiltH = height;

            int capW = width;
            int capH = height;
            int capMw = maskWidth;
            int capMh = maskHeight;

            // 適用中のマスクはすべて表示する(共通の除外=赤 / ゾーン別の除外=ゾーン色 /
            // 含める=緑)。編集対象×種類の 1 枚だけを出す旧仕様は「表示されていないのに
            // 効いている」マスクを生み、対象や種類を切り替えた直後・プリセット読込
            // (編集対象が共通へ戻る)直後に、原因の見えない除外・変換漏れに見えていた
            // (2026-08-24 のユーザー報告)。どれを編集中かは明暗差で示す: 編集対象は
            // 従来アルファ、他は減光(InactiveOverlayAlphaScale)。編集対象は最後に
            // 描いて最前面にする(RenderMaskCoverage は上書き合成のため)。
            var zones = _host.Session.zones;
            bool commonIsActive = IsCommonTarget(zones);

            bool[] commonSnap = null;
            Color32 commonColor = ExcludedOverlayColor;
            if (exclusionMask != null && capMw > 0 && capMh > 0)
            {
                commonSnap = (bool[])exclusionMask.Clone();
                if (!commonIsActive) commonColor = DimOverlayColor(ExcludedOverlayColor);
            }

            var zoneInfos = new List<(Color32 color, bool[] mask)>();
            if (zones != null && capMw > 0 && capMh > 0)
            {
                (Color32 color, bool[] mask)? editingEntry = null;
                for (int i = 0; i < zones.Count; i++)
                {
                    var zone = zones[i];
                    if (zone == null || string.IsNullOrEmpty(zone.id)) continue;
                    bool zoneIsTarget = i == activeMaskTarget;

                    if (zoneMasks.TryGetValue(zone.id, out var ex) && ex != null)
                    {
                        bool isEditing = zoneIsTarget && !editIncludeLayer;
                        var color = OverlayColorForZone(i);
                        var entry = (isEditing ? color : DimOverlayColor(color), (bool[])ex.Clone());
                        if (isEditing) editingEntry = entry;
                        else zoneInfos.Add(entry);
                    }
                    if (zoneIncludeMasks.TryGetValue(zone.id, out var inc) && inc != null)
                    {
                        bool isEditing = zoneIsTarget && editIncludeLayer;
                        var entry = (isEditing ? IncludedOverlayColor
                                               : DimOverlayColor(IncludedOverlayColor),
                                     (bool[])inc.Clone());
                        if (isEditing) editingEntry = entry;
                        else zoneInfos.Add(entry);
                    }
                }
                if (editingEntry.HasValue) zoneInfos.Add(editingEntry.Value);
            }

            _overlayJob.Schedule(
                work: token => ComputeOverlayPixels(commonSnap, commonColor, zoneInfos, capW, capH, capMw, capMh, token),
                apply: result =>
                {
                    _pendingOverlayResult = result;
                    _host.RequestRepaint();
                });
        }

        private static OverlayResult ComputeOverlayPixels(
            bool[] common, Color32 commonColor, List<(Color32 color, bool[] mask)> zoneInfos,
            int w, int h, int mw, int mh, CancellationToken token)
        {
            var result = new OverlayResult { width = w, height = h };

            if (common != null && mw > 0 && mh > 0)
            {
                result.hasCommon = true;
                result.commonPixels = RenderMaskCoverage(common, commonColor, null, w, h, mw, mh);
                token.ThrowIfCancellationRequested();
            }

            if (zoneInfos != null && zoneInfos.Count > 0 && mw > 0 && mh > 0)
            {
                result.hasZone = true;
                Color32[] pixels = null;
                foreach (var (color, zm) in zoneInfos)
                {
                    pixels = RenderMaskCoverage(zm, color, pixels, w, h, mw, mh);
                    token.ThrowIfCancellationRequested();
                }
                result.zonePixels = pixels;
            }

            return result;
        }

        /// <summary>
        /// マスクをオーバーレイ解像度へ「被覆率比例アルファ」で描画する。
        /// マスクと表示が同解像度なら被覆率は 0/1 で従来の最近傍と同一出力。
        /// マスクの方が高解像度(例: 4096 マスク→2048 表示)のときは境界セルのアルファが
        /// 被覆率で階調化され、実体どおりの滑らかな縁に見える(二値ブロックの偽ギザギザを防ぐ)。
        /// accumulate 非 null 時はその配列に上書き合成して返す(ゾーン重ね描き用)。
        /// </summary>
        private static Color32[] RenderMaskCoverage(
            bool[] mask, Color32 color, Color32[] accumulate, int w, int h, int mw, int mh)
        {
            var pixels = accumulate ?? new Color32[w * h];
            // 列のマスク範囲は行に依らないので前計算する(オーバーレイは表示倍率に応じて
            // OverlayMaxSize² まで大きくなるため、画素あたりの整数除算が効いてくる)。
            var cx0 = new int[w];
            var cx1 = new int[w];
            for (int x = 0; x < w; x++)
            {
                cx0[x] = Mathf.Clamp((int)((long)x * mw / w), 0, mw - 1);
                cx1[x] = Mathf.Clamp((int)((long)(x + 1) * mw / w), cx0[x] + 1, mw);
            }
            for (int y = 0; y < h; y++)
            {
                int my0 = Mathf.Clamp((int)((long)y * mh / h), 0, mh - 1);
                int my1 = Mathf.Clamp((int)((long)(y + 1) * mh / h), my0 + 1, mh);
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    int mx0 = cx0[x];
                    int mx1 = cx1[x];
                    int count = 0;
                    for (int my = my0; my < my1; my++)
                    {
                        int myBase = my * mw;
                        for (int mx = mx0; mx < mx1; mx++)
                            if (mask[myBase + mx]) count++;
                    }
                    if (count == 0) continue;
                    int total = (my1 - my0) * (mx1 - mx0);
                    if (count == total)
                    {
                        pixels[rowBase + x] = color;
                    }
                    else
                    {
                        var c = color;
                        c.a = (byte)Mathf.Clamp(Mathf.RoundToInt(color.a * count / (float)total), 1, color.a);
                        pixels[rowBase + x] = c;
                    }
                }
            }
            return pixels;
        }

        /// <summary>
        /// バックグラウンドで生成された Color32[] をオーバーレイテクスチャに適用する。
        /// PreviewView.Draw の冒頭から呼ばれる。
        /// </summary>
        public void ApplyPendingOverlay()
        {
            if (!_pendingOverlayResult.HasValue) return;
            // ペイント中に直接書き込みへ移行済みなら適用を保留する。スナップショット clone
            // 時点より新しいスタンプが古い結果に一瞬上書きされて見えるのを防ぐ。結果は保持し、
            // ストローク終了後(EndStroke が maskDirty を立てて再構築)に最新内容へ収束する。
            if (isPainting && _strokeHadDirectOverlayWrite) return;
            var r = _pendingOverlayResult.Value;
            _pendingOverlayResult = null;

            if (r.hasCommon)
            {
                // Point: マスクは「入っている / いない」の二値なので、拡大時に中間アルファを
                // 作る補間は表示として不正確(2026-08-24 のユーザー指摘)。粗いブロックに
                // 見えないよう、オーバーレイ側の解像度を表示倍率に合わせて上げてある
                // (OverlayScale)。縮小表示のときだけ SyncOverlayFilter が Bilinear へ戻す。
                TextureSlot.Resize(ref maskOverlayTexture, r.width, r.height, FilterMode.Point);
                maskOverlayTexture.SetPixels32(r.commonPixels);
                maskOverlayTexture.Apply();
            }
            else
            {
                TextureSlot.Release(ref maskOverlayTexture);
            }

            if (r.hasZone)
            {
                TextureSlot.Resize(ref zoneMaskOverlayTexture, r.width, r.height, FilterMode.Point);
                zoneMaskOverlayTexture.SetPixels32(r.zonePixels);
                zoneMaskOverlayTexture.Apply();
            }
            else
            {
                TextureSlot.Release(ref zoneMaskOverlayTexture);
            }
        }

        /// <summary>
        /// ゾーンインデックスから黄金比ベースのオーバーレイ色を決定する。
        /// </summary>
        public static Color32 OverlayColorForZone(int zoneIndex)
        {
            const float golden = 0.61803398875f;
            float h = (zoneIndex * golden) % 1f;
            if (h < 0) h += 1f;
            Color rgb = Color.HSVToRGB(h, 0.8f, 1f);
            return new Color32(
                (byte)Mathf.RoundToInt(rgb.r * 255f),
                (byte)Mathf.RoundToInt(rgb.g * 255f),
                (byte)Mathf.RoundToInt(rgb.b * 255f),
                100);
        }
    }
}
