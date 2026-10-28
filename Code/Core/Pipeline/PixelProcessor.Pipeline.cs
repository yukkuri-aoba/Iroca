// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 1 つのゾーンの処理。ここがパイプラインの仕様(0.4.0 の計画の 3-7b で ProcessPixelsArrayCore のゾーンの
        /// ループの中身を書き換えずに切り出した)。ゾーンの入力(マスク・混色帯モード・サンプルの HSV・含めるマスク)を
        /// 揃え、旧の選択 → マスクの契約 → 再着色の段(無彩の固め → デコンタミ → 再着色の統計 → 混色帯 → 合成 →
        /// 無彩のフチ消し)の順に呼ぶ。段どうしは <see cref="ZoneState"/> で値を受け渡す。
        /// 段の順と条件は出力のビット一致に効くので、変えるときは abcheck で確かめる。
        /// </summary>
        private static void RunZone(RunContext run, MaskSnapshot masks, ColorZone zone)
        {
            // ProcessPixelsArrayCore と同じ名前のローカルへ展開する(切り出した本体をそのまま読めるように)。
            int w = run.W, h = run.H, len = run.Len;
            int originX = run.OriginX, originY = run.OriginY, fullW = run.FullW, fullH = run.FullH;
            var po = run.Po;
            var cancellationToken = run.Ct;
            ulong[] commonMask = run.CommonMask;
            int[] maskColOf = run.MaskColOf, maskRowBase = run.MaskRowBase;
            bool useDecontamination = run.UseDecontamination;
            float edgeFeather = run.EdgeFeather;
            var _sub = run.Sub;

            cancellationToken.ThrowIfCancellationRequested();

            ulong[] zoneMask = null;
            if (masks != null && masks.zones != null && !string.IsNullOrEmpty(zone.id))
                masks.zones.TryGetValue(zone.id, out zoneMask);
            // 含めるマスク(ゾーン別のみ)。除外と重なった画素は除外が勝つ。
            ulong[] zoneInclude = null;
            if (masks != null && masks.zoneIncludes != null && !string.IsNullOrEmpty(zone.id))
                masks.zoneIncludes.TryGetValue(zone.id, out zoneInclude);

            // Parallel.For に入る前にキャッシュを確定させてホットループ内の条件分岐を排除
            zone.UpdateCacheIfNeeded();
            // 混色帯モード(境界クリーンアップ ON かつ有彩サンプル): 選択は主マッチの結果だけにして(緩和マッチの
            // 穴埋め・境界回復は行わない)、境界の混色は再着色の直前に被覆率で塗る(PixelProcessor.MixtureBand.cs の
            // AnalyzeMixtureBand)。緩和マッチは選択を縁から一定の幅だけ広げるので、素材でない地まで拾うと
            // パーツの周りに輪が出る。
            bool zMixMode = IsMixtureBandZone(zone, useDecontamination, edgeFeather);
            long _tZone = Stopwatch.GetTimestamp();
            run.Tp = _tZone;
            _sub.Restart();

            // 作業配列の借用は per-zone の try/finally で必ず返却する。
            // Parallel.For は po.CancellationToken でキャンセル時に OperationCanceledException を投げる。
            // 後段ヘルパー(Decontaminate/CleanAchromaFringe/GaussianBlur/FillSmallHoles/RecoverBoundaryEdges
            // /Solidify/GrowHighlightBand)にも cancellationToken を渡しており、内部 Parallel.For も
            // 同様にキャンセル例外を伝播するため、例外経路でもプールが汚染されないよう finally でガードする。
            var zb = new ZoneBuffers();
            try
            {
                // サンプル色の HSV(ゾーン内で不変。彩度天井ゲート・別パーツの色相幅・緩和マッチ・
                // 中性リジェクト・再着色パラメータが同じ値を使う)。
                Color.RGBToHSV(zone.sampleColor, out float sampH, out float sampS, out float sampV);

                // フル画像経路(メインプレビュー/Apply/Export)か部分クロップ(詳細プレビュー)か。
                // 大域統計(連結成分/再着色アンカー/wash/領域L)はフル画像でしか正しく解けないので、
                // フル画像では解いてキャッシュへ書き、クロップではキャッシュを転写する。
                // 部分クロップ経路は現在は検証用(製品 UI の詳細プレビューはフル段出力の切り出し)。
                bool isFullImagePath = originX == 0 && originY == 0 && fullW == w && fullH == h;

                // 含めるマスクをテクスチャ解像度の bool[] へ展開する(除外優先を焼き込む)。
                // 用途は 3 つ: (a) 選択への強制適用(マスク再適用の直後・キャッシュ保存の前)
                // (b) 大域統計(再着色アンカー/領域Lレンジ)からの除外 — 手動追加した画素が
                //     地色統計を汚し、ゾーン全体の再着色が遠隔で変わるのを防ぐ
                // (c) 中性リジェクト等 target 依存ヒューリスティック後の再主張
                // キャッシュヒット時も (b)(c) で必要になるため selCached と無関係に作る。
                // 含めるマスクが無ければ null のまま = 以降の全分岐が no-op(出力ビット不変)。
                if (zoneInclude != null)
                {
                    zb.IncludedPx = s_boolPool.Rent(len);
                    var incLocal = zb.IncludedPx;
                    Parallel.For(0, h, po, y =>
                    {
                        int rowOff = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            incLocal[rowOff + x] =
                                IsIncludedAt(maskColOf, maskRowBase, x, y, zoneInclude)
                                && !IsExcludedAt(maskColOf, maskRowBase, x, y, commonMask, zoneMask);
                        }
                    });
                }
                bool* includedPx = zb.IncludedPx;

                // 選択(旧の選択)。マスクを掛ける前の強度と後段の範囲を返す。強度の配列はゾーンの作業配列(zb)の
                // 持ち物で、ゾーンの終わりに返す。
                var sel = RunLegacySelection(run, zone, zb, zoneMask, zMixMode, isFullImagePath, sampH, sampS, sampV);
                float* strength = sel.Strength;

                // マスクの契約(除外の掛け直し・含める)。
                ApplyMaskContract(run, zone, strength, zoneMask, includedPx);

                // 再着色の段。段どうしは z で値を受け渡す(0.4.0 の計画の 3-7b)。
                var z = new ZoneState
                {
                    Zone = zone, Buffers = zb, ZoneMask = zoneMask, MixMode = zMixMode, IsFullImagePath = isFullImagePath,
                    SampH = sampH, SampS = sampS, SampV = sampV, IncludedPx = includedPx,
                    Strength = strength, Box = sel.Box,
                };
                AchromaSolidifyStage.Run(run, z);
                DecontamStage.Run(run, z);
                RecolorStatsStage.Run(run, z);
                MixtureBandStage.Run(run, z);
                ComposeStage.Run(run, z);
                AchromaFringeStage.Run(run, z);

                if (run.Debug != null) RecolorBranchRecorder.Record(run, z);
                run.PerfZones[run.PerfCount++] = new ZonePerfEntry(zone.id, TicksToMs(Stopwatch.GetTimestamp() - _tZone));
            }
            finally
            {
                zb.ReturnAll();
            }
        }
    }
}
