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
            if (_detailOverlayDirectPending)
            {
                _detailOverlayDirectPending = false;
                ActiveDetailOverlayTexture()?.Apply();
            }
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
        /// スケジュールする。マスクの層は参照を足してワーカに渡し(複製しない)、ペイント側は書く前に
        /// 自分だけのものにする(<see cref="MaskWorkspace"/>)ので、ペイント中の変更とデータレースしない。
        /// 参照は計算の後(取り消されて計算しなかったときも)返す。SetPixels32 / Apply は次フレーム以降に
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

            CollectOverlayLayers(out var commonSnap, out var commonColor, out var zoneInfos);

            _overlayJob.Schedule(
                work: token => ComputeOverlayPixels(commonSnap, commonColor, zoneInfos, capW, capH, capMw, capMh, token),
                apply: result =>
                {
                    _pendingOverlayResult = result;
                    _host.RequestRepaint();
                },
                cleanup: () => ReleaseOverlayLayers(commonSnap, zoneInfos));
        }

        /// <summary>
        /// オーバーレイに描く層を集める(全体用・拡大表示用で共通)。各層は参照を足して返すので、使い終えたら
        /// <see cref="ReleaseOverlayLayers"/> で返す。バックグラウンドへ渡しても、ペイント側は書く前に写すので
        /// データレースしない。
        /// </summary>
        private void CollectOverlayLayers(out NativeBitMask commonSnap,
                                          out Color32 commonColor, out List<(Color32 color, NativeBitMask mask)> zoneInfos)
        {
            // 適用中のマスクはすべて表示する(共通の除外=赤 / ゾーン別の除外=ゾーン色 /
            // 含める=緑)。編集対象×種類の 1 枚だけを出す旧仕様は「表示されていないのに
            // 効いている」マスクを生み、対象や種類を切り替えた直後・プリセット読込
            // (編集対象が共通へ戻る)直後に、原因の見えない除外・変換漏れに見えていた
            // (2026-08-24 のユーザー報告)。どれを編集中かは明暗差で示す: 編集対象は
            // 従来アルファ、他は減光(InactiveOverlayAlphaScale)。編集対象は最後に
            // 描いて最前面にする(RenderMaskCoverage は上書き合成のため)。
            var zones = _host.Session.zones;
            bool commonIsActive = IsCommonTarget(zones);

            commonSnap = null;
            commonColor = ExcludedOverlayColor;
            var common = Workspace.Common;
            if (common != null && maskWidth > 0 && maskHeight > 0)
            {
                commonSnap = common.AddRef();
                if (!commonIsActive) commonColor = DimOverlayColor(ExcludedOverlayColor);
            }

            zoneInfos = new List<(Color32 color, NativeBitMask mask)>();
            if (zones != null && maskWidth > 0 && maskHeight > 0)
            {
                (Color32 color, NativeBitMask mask)? editingEntry = null;
                for (int i = 0; i < zones.Count; i++)
                {
                    var zone = zones[i];
                    if (zone == null || string.IsNullOrEmpty(zone.id)) continue;
                    bool zoneIsTarget = i == activeMaskTarget;

                    var ex = Workspace.Layer(false, zone.id);
                    if (ex != null)
                    {
                        bool isEditing = zoneIsTarget && !editIncludeLayer;
                        var color = OverlayColorForZone(i);
                        var entry = (isEditing ? color : DimOverlayColor(color), ex.AddRef());
                        if (isEditing) editingEntry = entry;
                        else zoneInfos.Add(entry);
                    }
                    var inc = Workspace.Layer(true, zone.id);
                    if (inc != null)
                    {
                        bool isEditing = zoneIsTarget && editIncludeLayer;
                        var entry = (isEditing ? IncludedOverlayColor
                                               : DimOverlayColor(IncludedOverlayColor),
                                     inc.AddRef());
                        if (isEditing) editingEntry = entry;
                        else zoneInfos.Add(entry);
                    }
                }
                if (editingEntry.HasValue) zoneInfos.Add(editingEntry.Value);
            }
        }

        /// <summary><see cref="CollectOverlayLayers"/> が足した参照を返す(重ね表示の計算の後。ワーカのスレッドから呼ぶ)。</summary>
        private static void ReleaseOverlayLayers(NativeBitMask common, List<(Color32 color, NativeBitMask mask)> zoneInfos)
        {
            common?.Release();
            foreach (var (_, m) in zoneInfos) m.Release();
        }

        private static OverlayResult ComputeOverlayPixels(
            NativeBitMask common, Color32 commonColor, List<(Color32 color, NativeBitMask mask)> zoneInfos,
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
        private static unsafe Color32[] RenderMaskCoverage(
            NativeBitMask mask, Color32 color, Color32[] accumulate, int w, int h, int mw, int mh)
        {
            var pixels = accumulate ?? new Color32[w * h];
            ulong* bits = mask.Bits;
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
                        {
                            int i = myBase + mx;
                            if ((bits[i >> 6] & (1UL << (i & 63))) != 0UL) count++;
                        }
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

        // ─── 拡大表示用の等倍オーバーレイ ───
        // 拡大表示(詳細プレビュー)は見えている範囲の再着色結果を元画像の解像度で描き直すが、全体用の
        // オーバーレイは表示格子の整数倍(上限 OverlayMaxSize)なので、4K では 1 テクセル ≒ 2 画素の粗い
        // ブロックに見え、画素単位のマスク(AI 提案・拡大して塗ったブラシ)の形が分からなかった。詳細プレビューと
        // 同じ範囲・同じ寸法でマスクを描き、その範囲では全体用の代わりに重ねる(PreviewView.DrawMaskOverlays)。
        // 以前あったクロップ専用オーバーレイは詳細の再生成まで更新されずペイントが見えなかったので廃止したが、
        // こちらはペイントを直接書き込む(PaintMask)ので塗ったそばから見える。表示専用で、再着色には関与しない。
        [System.NonSerialized] public Texture2D detailMaskOverlayTexture;
        [System.NonSerialized] public Texture2D detailZoneOverlayTexture;

        /// <summary>拡大表示用オーバーレイの範囲: 元画像 srcW×srcH のうち [x0, x0+cw) × [y0, y0+ch)(行 0 = 下端)を
        /// outW×outH で描いたもの。詳細プレビューのテクスチャと同じ。</summary>
        private struct DetailRange
        {
            public int srcW, srcH, x0, y0, cw, ch, outW, outH;
            public bool Same(DetailRange o) => srcW == o.srcW && srcH == o.srcH && x0 == o.x0 && y0 == o.y0
                                               && cw == o.cw && ch == o.ch && outW == o.outW && outH == o.outH;
        }

        private struct DetailOverlayResult
        {
            public OverlayResult overlay;
            public DetailRange range;
        }

        // _detailApplied = いまのテクスチャの中身の範囲(直接書き込みと描画の照合に使う)。
        [System.NonSerialized] private DetailRange _detailApplied;
        [System.NonSerialized] private bool _detailOverlayDirectPending;
        [System.NonSerialized] private readonly PreviewJob<DetailOverlayResult> _detailOverlayJob = new PreviewJob<DetailOverlayResult>();
        [System.NonSerialized] private DetailOverlayResult? _pendingDetailOverlay;

        /// <summary>
        /// 拡大表示用オーバーレイの再構築をバックグラウンドにスケジュールする。マスクの層は参照を足して渡し、
        /// 見えている範囲だけを読む(切り出しの写しは作らない)。範囲は詳細プレビューと同じ(<see cref="DetailRange"/>)。
        /// </summary>
        public void RebuildDetailOverlay(int srcW, int srcH, int x0, int y0, int cw, int ch, int outW, int outH)
        {
            if (srcW <= 0 || srcH <= 0 || cw <= 0 || ch <= 0 || outW <= 0 || outH <= 0) return;
            if (maskWidth <= 0 || maskHeight <= 0) return;
            var range = new DetailRange { srcW = srcW, srcH = srcH, x0 = x0, y0 = y0, cw = cw, ch = ch, outW = outW, outH = outH };

            // 元画像の範囲をマスクの範囲へ(始まりは切り捨て・終わりは切り上げ)。
            int mw = maskWidth, mh = maskHeight;
            int mx0 = Mathf.Clamp((int)((long)x0 * mw / srcW), 0, mw - 1);
            int mx1 = Mathf.Clamp((int)(((long)(x0 + cw) * mw + srcW - 1) / srcW), mx0 + 1, mw);
            int my0 = Mathf.Clamp((int)((long)y0 * mh / srcH), 0, mh - 1);
            int my1 = Mathf.Clamp((int)(((long)(y0 + ch) * mh + srcH - 1) / srcH), my0 + 1, mh);
            int cmw = mx1 - mx0, cmh = my1 - my0;
            CollectOverlayLayers(out var commonSnap, out var commonColor, out var zoneInfos);

            // 出力の各列・行が覆うマスクの範囲(切り出しの中の座標)。
            var cx0 = new int[outW]; var cx1 = new int[outW];
            var cy0 = new int[outH]; var cy1 = new int[outH];
            DetailAxisRanges(outW, x0, cw, srcW, mw, mx0, cmw, cx0, cx1);
            DetailAxisRanges(outH, y0, ch, srcH, mh, my0, cmh, cy0, cy1);

            _detailOverlayJob.Schedule(
                work: token =>
                {
                    var r = new OverlayResult { width = outW, height = outH };
                    if (commonSnap != null)
                    {
                        r.hasCommon = true;
                        r.commonPixels = RenderCoverageRanges(commonSnap, mx0, my0, cx0, cx1, cy0, cy1, outW, outH, commonColor, null);
                        token.ThrowIfCancellationRequested();
                    }
                    if (zoneInfos.Count > 0)
                    {
                        r.hasZone = true;
                        Color32[] pixels = null;
                        foreach (var (color, zm) in zoneInfos)
                        {
                            pixels = RenderCoverageRanges(zm, mx0, my0, cx0, cx1, cy0, cy1, outW, outH, color, pixels);
                            token.ThrowIfCancellationRequested();
                        }
                        r.zonePixels = pixels;
                    }
                    return new DetailOverlayResult { overlay = r, range = range };
                },
                apply: result =>
                {
                    _pendingDetailOverlay = result;
                    _host.RequestRepaint();
                },
                cleanup: () => ReleaseOverlayLayers(commonSnap, zoneInfos));
        }

        /// <summary>
        /// 出力の 1 軸(n 個)の各画素が覆う元画像の範囲 [from + i*count/n, from + (i+1)*count/n) を、マスクの範囲
        /// (×maskTotal/srcTotal、終わりは切り上げ)へ写し、切り出しの始まり maskFrom からの座標で r0/r1 に入れる。
        /// </summary>
        private static void DetailAxisRanges(int n, int from, int count, int srcTotal, int maskTotal,
                                             int maskFrom, int maskCount, int[] r0, int[] r1)
        {
            for (int i = 0; i < n; i++)
            {
                long s0 = from + (long)i * count / n;
                long s1 = from + (long)(i + 1) * count / n;
                if (s1 <= s0) s1 = s0 + 1;
                int a = (int)(s0 * maskTotal / srcTotal) - maskFrom;
                int b = (int)((s1 * maskTotal + srcTotal - 1) / srcTotal) - maskFrom;
                a = Mathf.Clamp(a, 0, maskCount - 1);
                b = Mathf.Clamp(b, a + 1, maskCount);
                r0[i] = a;
                r1[i] = b;
            }
        }

        /// <summary>
        /// <see cref="RenderMaskCoverage"/> と同じ被覆率アルファの描画を、前計算した列・行の範囲で行う。範囲は見えている
        /// 部分の始まり (offX, offY)(マスクの画素)からの座標。
        /// </summary>
        private static unsafe Color32[] RenderCoverageRanges(NativeBitMask mask, int offX, int offY,
                                                             int[] cx0, int[] cx1, int[] cy0, int[] cy1,
                                                             int w, int h, Color32 color, Color32[] accumulate)
        {
            var pixels = accumulate ?? new Color32[w * h];
            ulong* bits = mask.Bits;
            int stride = mask.Width;
            for (int y = 0; y < h; y++)
            {
                int my0 = cy0[y], my1 = cy1[y];
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    int mx0 = cx0[x], mx1 = cx1[x];
                    int count = 0;
                    for (int my = my0; my < my1; my++)
                    {
                        int myBase = (offY + my) * stride + offX;
                        for (int mx = mx0; mx < mx1; mx++)
                        {
                            int i = myBase + mx;
                            if ((bits[i >> 6] & (1UL << (i & 63))) != 0UL) count++;
                        }
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
        /// バックグラウンドで作った拡大表示用オーバーレイをテクスチャへ反映する(メインスレッド)。ペイント中に
        /// 直接書き込みへ移行済みなら保留する(全体用と同じ理由)。
        /// </summary>
        public void ApplyPendingDetailOverlay()
        {
            if (!_pendingDetailOverlay.HasValue) return;
            if (isPainting && _strokeHadDirectOverlayWrite) return;
            var p = _pendingDetailOverlay.Value;
            _pendingDetailOverlay = null;
            var r = p.overlay;
            // 縮小して描いた(出力が元画像の範囲より小さい)ときは Bilinear、等倍以上は Point(詳細プレビューと同じ)。
            var filter = r.width < p.range.cw ? FilterMode.Bilinear : FilterMode.Point;
            if (r.hasCommon)
            {
                TextureSlot.Resize(ref detailMaskOverlayTexture, r.width, r.height, filter);
                detailMaskOverlayTexture.filterMode = filter;
                detailMaskOverlayTexture.SetPixels32(r.commonPixels);
                detailMaskOverlayTexture.Apply();
            }
            else TextureSlot.Release(ref detailMaskOverlayTexture);
            if (r.hasZone)
            {
                TextureSlot.Resize(ref detailZoneOverlayTexture, r.width, r.height, filter);
                detailZoneOverlayTexture.filterMode = filter;
                detailZoneOverlayTexture.SetPixels32(r.zonePixels);
                detailZoneOverlayTexture.Apply();
            }
            else TextureSlot.Release(ref detailZoneOverlayTexture);
            _detailApplied = p.range;
        }

        /// <summary>拡大表示用オーバーレイが、この範囲(詳細プレビューのいまの範囲)で描かれているか。</summary>
        public bool DetailOverlayMatches(int srcW, int srcH, int x0, int y0, int cw, int ch, int outW, int outH)
            => _detailApplied.Same(new DetailRange { srcW = srcW, srcH = srcH, x0 = x0, y0 = y0, cw = cw, ch = ch, outW = outW, outH = outH });

        /// <summary>編集対象の層を描いている拡大表示用オーバーレイ(共通=detailMaskOverlayTexture、ゾーン=detailZone…)。</summary>
        private Texture2D ActiveDetailOverlayTexture()
        {
            if (_detailApplied.cw <= 0) return null;
            return IsCommonTarget(_host.Session.zones) ? detailMaskOverlayTexture : detailZoneOverlayTexture;
        }

        /// <summary>
        /// ブラシの円を拡大表示用オーバーレイへ直接書く(中心 cx, cy・半径 r はマスクの画素単位)。
        /// 範囲の外の円や、テクスチャがまだ無いときは何もしない。
        /// </summary>
        private void StampDetailOverlay(float cx, float cy, float r, Color32 color)
        {
            var tex = ActiveDetailOverlayTexture();
            var a = _detailApplied;
            if (tex == null || a.cw <= 0 || a.ch <= 0) return;
            // マスクの画素 → 元画像の画素 → 出力テクセル。
            float toSrcX = a.srcW / (float)maskWidth, toSrcY = a.srcH / (float)maskHeight;
            float toOutX = tex.width / (float)a.cw, toOutY = tex.height / (float)a.ch;
            float ox = (cx * toSrcX - a.x0) * toOutX;
            float oy = (cy * toSrcY - a.y0) * toOutY;
            float orad = r * toSrcX * toOutX;
            if (ox + orad < 0f || oy + orad < 0f || ox - orad > tex.width || oy - orad > tex.height) return;
            if (StampOverlayDisk(tex, ox, oy, orad, color)) _detailOverlayDirectPending = true;
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
