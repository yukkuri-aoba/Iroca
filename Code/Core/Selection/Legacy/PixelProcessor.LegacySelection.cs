// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 旧の選択(照合 → 彩度の天井・閉領域の中性 → ハイライト → 連結成分 → 穴埋め → 境界回復 → ぼかし →
        /// 選択キャッシュへの保存)。ProcessPixelsArrayCore から中身を書き換えずに切り出したもの(0.4.0 の計画の 3-7a)。
        /// T5 で新しい選択に置き換える(P3 で消す)。マスクを掛ける前の強度と後段の範囲を返す(<see cref="SelectionResult"/>)。
        /// 借りた作業配列は <paramref name="zb"/> に入れる(途中で抜けてもゾーンの終わりに返る)。
        /// 計測の時刻は run.Tp で受け渡す。
        /// </summary>
        private static SelectionResult RunLegacySelection(RunContext run, ColorZone zone, ZoneBuffers zb,
            ulong[] zoneMask, bool zMixMode, bool isFullImagePath, float sampH, float sampS, float sampV)
        {
            // ProcessPixelsArrayCore と同じ名前のローカルへ展開する(切り出した本体をそのまま読めるように)。
            int w = run.W, h = run.H, len = run.Len;
            int originX = run.OriginX, originY = run.OriginY, fullW = run.FullW, fullH = run.FullH;
            Color32* originalPixels = run.Original;
            var po = run.Po;
            var cancellationToken = run.Ct;
            var palette = run.Palette;
            var hsv = run.Hsv;
            float* pixH = run.PixH, pixS = run.PixS, pixV = run.PixV;
            int* pixIdx = run.PixIdx;
            ulong[] commonMask = run.CommonMask;
            int maskW = run.MaskW, maskH = run.MaskH;
            int[] maskColOf = run.MaskColOf, maskRowBase = run.MaskRowBase;
            float edgeFeather = run.EdgeFeather, relaxedSatMin = run.RelaxedSatMin, relaxedSatRamp = run.RelaxedSatRamp;
            int antiAliasCleanup = run.AntiAliasCleanup, holeFillPasses = run.HoleFillPasses;
            int holeFillMinNeighbors = run.HoleFillMinNeighbors;
            var debug = run.Debug;
            var parityCache = run.ParityCache;
            var selectionCache = run.SelectionCache;
            var _phaseTicks = run.PhaseTicks;
            var _sub = run.Sub;
            long _tp = run.Tp;
            float* strength = null;
            float* highlightPot = null;
            float* matchConf = null;

            // 選択キャッシュ: ターゲット色など「再着色のみ」の変更では、マスクを掛ける直前の
            // strength(=生の選択)を復元して選択フェーズ(Match/Highlight/FloodFill/穴埋め/
            // 境界/ブラー)を丸ごと省く。フル画像経路でのみ使う。
            string selKey = null;
            ulong[] cachedKeep = null;
            ulong[] cachedForced = null;
            var cachedBox = default(SelectionCache.PostBox);
            bool selCached = false;
            if (selectionCache != null && isFullImagePath)
            {
                selKey = BuildSelectionKey(zone, edgeFeather, antiAliasCleanup, holeFillPasses,
                    holeFillMinNeighbors, relaxedSatMin, relaxedSatRamp, maskW, maskH, zMixMode);
            }

            // 1. 元のピクセルカラーを使用した強度マップを構築(キャッシュヒット時は復元のみ)
            strength = zb.Strength = s_floatPool.Rent(len);
            // matchConf は連結成分アンカリング(flood fill)のコア判定専用。FF が有効でフル画像
            // 経路かつキャッシュミス時のみ確保・充填する(ヒット時は keep を転写するため不要)。
            bool ffNeedsMatchConf = zone.mode == SelectionMode.ColorPick && zone.useFloodFill
                && isFullImagePath;
            // キャッシュにあれば strength へ写す(キャッシュの中身はネイティブメモリにあり、貸し出し中は
            // 手放されない)。除外マスクが保存時と違えば、変わった画素でこのゾーンの照合がどれも 0 のときだけ
            // 使う(そのとき照合の直後の配列が計算し直しと同じになり、以降の段はマスクを読まないので選択も同じ)。
            if (selKey != null)
            {
                var zoneForCheck = zone;
                selCached = selectionCache.TryCopy(zone.id, selKey, w, h, strength, commonMask, zoneMask,
                    (oldCommon, oldZone) => ExclusionChangeKeepsMatch(zoneForCheck, originalPixels, hsv,
                        w, h, originX, originY, fullW, fullH, maskColOf, maskRowBase, maskW, maskH,
                        oldCommon, oldZone, commonMask, zoneMask, ffNeedsMatchConf, po),
                    out cachedKeep, out cachedForced, out cachedBox);
            }
            bool needMatchConf = ffNeedsMatchConf && !selCached;
            // ミス時に FF が確定した keep を、選択キャッシュにも保存して次回のヒット復元に使う。
            ulong[] keepBitsForCache = null;

            if (!selCached)
            {
                // strength / highlightPot / matchConf は下のマッチのループが全画素に書く(除外マスクの
                // 画素には 0)ので、先にゼロで埋めない(旧: 3 本とも単一スレッドの Array.Clear)。
                if (zone.highlightRecovery) highlightPot = zb.HighlightPot = s_floatPool.Rent(len);
                if (needMatchConf) matchConf = zb.MatchConf = s_floatPool.Rent(len);

                var strengthLocal = strength;
                var highlightPotLocal = highlightPot;
                var matchConfLocal = matchConf;
                _sub.Mark(SpAlloc);
                // ColorPick のマッチは画素の色(と色から求めた HSV)だけで決まる(位置を見るのは
                // Rect モードだけ)ので、色の表があれば色ごとに 1 回だけ求めて配る。
                float[] palStrength = null, palPot = null, palConf = null;
                if (palette != null && zone.mode == SelectionMode.ColorPick)
                {
                    int pc = palette.Count;
                    palStrength = new float[pc];
                    palPot = new float[pc];
                    palConf = new float[pc];
                    Color32* pCol = palette.Colors;
                    float* pH = palette.H, pS = palette.S, pV = palette.V;
                    float[] outS = palStrength, outPot = palPot, outConf = palConf;
                    ForEachPaletteChunk(pc, po, (k0, k1) =>
                    {
                        for (int k = k0; k < k1; k++)
                            zone.GetMatchScoresPrecomputedHSV(pH[k], pS[k], pV[k], (Color)pCol[k],
                                0, 0, fullW, fullH, out outS[k], out outPot[k], out outConf[k]);
                    });
                }
                int* palIdxMatch = palette != null ? palette.Index : null;
                Parallel.For(0, h, po, y =>
                {
                    int yf = y + originY;
                    int rowOff = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int xf = x + originX;
                        int i = rowOff + x;
                        if (IsExcludedAt(maskColOf, maskRowBase, x, y, commonMask, zoneMask))
                        {
                            strengthLocal[i] = 0f;
                            if (highlightPotLocal != null) highlightPotLocal[i] = 0f;
                            if (matchConfLocal != null) matchConfLocal[i] = 0f;
                            continue;
                        }

                        float s, hPot, mc;
                        if (zone.mode == SelectionMode.ColorPick
                            && TryQuantSmoothedColor(originalPixels, x, y, w, h, zone.tolerance, out Color qc))
                        {
                            Color.RGBToHSV(qc, out float qh, out float qs, out float qv);
                            zone.GetMatchScoresPrecomputedHSV(qh, qs, qv, qc, xf, yf, fullW, fullH,
                                out s, out hPot, out mc);
                        }
                        else if (palStrength != null)
                        {
                            int k = palIdxMatch[i];
                            s = palStrength[k]; hPot = palPot[k]; mc = palConf[k];
                        }
                        else
                        {
                            zone.GetMatchScoresPrecomputedHSV(pixH[HsvSlot(pixIdx, i)], pixS[HsvSlot(pixIdx, i)], pixV[HsvSlot(pixIdx, i)], (Color)originalPixels[i], xf, yf, fullW, fullH, out s, out hPot, out mc);
                        }
                        strengthLocal[i] = s;
                        if (highlightPotLocal != null) highlightPotLocal[i] = hPot;
                        if (matchConfLocal != null) matchConfLocal[i] = mc;
                    }
                });
                _sub.Mark(SpMatchLoop);
                // 彩度天井ゲート(グレーモード以外では内部で no-op): 無彩素材の彩度包絡を
                // 超える独立した高彩度の別素材(クリーム布等)を選択から除去する。素材自身の
                // 高彩度装飾は低彩度コア近接で保護される。ハイライト伝播・FF・穴埋めより前に
                // 適用し、後段パスが別素材を再伝播/復元しないようにする。
                // どちらも「サンプル色が無彩か」を起点にしたグレーモード専用の絞り込みなので、
                // 色サンプルを持つ ColorPick モードでのみ適用する。Rect モードは sampleColor が
                // 既定の白のままで、mode を見ないと必ずグレーモード判定が真になり、有彩画素が
                // 削除されて矩形選択が壊れる（レビュー 2026-08-06 §4 中）。現行 UI から Rect は
                // 設定できないが、enum は public でシリアライズ対象＝旧プリセット JSON から到達し得る。
                if (zone.mode == SelectionMode.ColorPick)
                {
                    ApplyChromaCeilingGate(strength, matchConf, hsv, sampS, sampV,
                        zone.chromaThreshold, zone.chromaCeiling, w, h, cancellationToken);
                        _sub.Mark(SpChromaCeiling);
                    // 中性ツヤ復帰(グレーモード以外では内部で no-op): 彩度整合ゲートが純白
                    // パディングと一緒に落とした「素材自身の純白ツヤ」を、選択領域に囲まれた
                    // 閉領域という空間条件だけで戻す。連結性は大域演算なのでフル画像経路限定
                    // (部分クロップではクロップ境界に接した閉領域を開領域と誤判定するため)。
                    if (isFullImagePath)
                        RecoverEnclosedNeutral(strength, matchConf, hsv, originalPixels,
                            zone.sampleColor, zone.tolerance, sampS, sampV,
                            zone.chromaThreshold, w, h, cancellationToken);
                }
                _sub.Mark(SpEnclosedNeutral);
                debug?.RecordStage(zone.id, DebugStages.Match, ToManaged(strength, len), w, h);
            }
            _phaseTicks[PhMatch] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

            // 1.a 空間伝播によるハイライト領域の回収 (モルフォロジー拡張)
            if (highlightPot != null)
            {
                PropagateHighlights(strength, highlightPot, w, h, cancellationToken);
                _sub.Mark(SpHlPropagate);
                s_floatPool.Return(highlightPot);
                highlightPot = zb.HighlightPot = null;
                debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, ToManaged(strength, len), w, h);
            }

            // 1.a.1 ハイライト帯成長: matched core から「sample→白 軸上の同色相の明部」へ
            //       strength を空間連結で伸ばし、薄いハイライトのベタ塗り化・取りこぼしを防ぐ。
            if (!selCached && zone.highlightBandExpand && zone.highlightRecovery)
            {
                GrowHighlightBand(strength, originalPixels, hsv, zone, w, h, cancellationToken,
                    palette);
                _sub.Mark(SpHlBand);
                debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, ToManaged(strength, len), w, h);
            }

            // 1.a.3 閉領域ハイライト復帰: 選択に囲まれ、グロー(本体より明るい周囲)に縁取られた
            //       「色相の回った有彩の芯」を空間条件で戻す(PixelProcessor.Highlight.cs の
            //       RecoverEnclosedHighlight 参照)。連結性は大域演算なのでフル画像で解き、復帰画素の
            //       ビット集合を parityCache(詳細プレビューのクロップ転写)と選択キャッシュ(ヒット時の
            //       再公開)に持たせる。クロップは色だけでは芯を選べない(それがこの復帰の存在理由)ので、
            //       転写が無いとズーム位置で芯の色が変わる。ハイライト補助(highlightRecovery)の一部。
            ulong[] forcedBits = null;
            if (zone.mode == SelectionMode.ColorPick && zone.highlightRecovery)
            {
                if (selCached)
                {
                    forcedBits = cachedForced;
                }
                else if (isFullImagePath)
                {
                    var bits = (parityCache != null || selectionCache != null)
                        ? new ulong[(len + 63) >> 6] : null;
                    int nForced = RecoverEnclosedHighlight(strength, originalPixels,
                        hsv, zone, w, h, bits, cancellationToken);
                    _sub.Mark(SpHlEnclosed);
                    if (nForced > 0)
                    {
                        forcedBits = bits;
                        debug?.RecordStage(zone.id, DebugStages.HighlightPropagate, ToManaged(strength, len), w, h);
                    }
                }
                else if (parityCache != null
                         && parityCache.fullW == fullW && parityCache.fullH == fullH)
                {
                    var fb = parityCache.GetForced(zone.id);
                    if (fb != null)
                        ApplyCachedForcedMask(strength, w, h, originX, originY, fullW, fb);
                }
                if (isFullImagePath && parityCache != null)
                {
                    parityCache.SetFullSize(w, h);
                    parityCache.SetForced(zone.id, forcedBits);   // null=該当なし(旧エントリを消す)
                }
            }

            _phaseTicks[PhHighlight] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

            // 1.a.2 連結成分アンカリング: 確信度コアを含む連結成分のみに strength を絞り込む。
            // 連結性は大域演算のため、フル画像経路(メインプレビュー/Apply/Export)でのみ実行する。
            // 部分クロップは、parityCache にフル画像で解いた keep があれば転写して一致させ(下の
            // else if)、無ければ絞り込まず色のみ=最終の上位集合(安全側)になる。
            if (zone.mode == SelectionMode.ColorPick
                && zone.useFloodFill)
            {
                if (selCached)
                {
                    // ヒット: フル画像で確定済みの keep を parityCache へ再公開する(詳細プレビュー
                    // 転写用)。FF 自体は省略済み(復元 strength に反映済み)。
                    if (parityCache != null && cachedKeep != null)
                    {
                        parityCache.SetFullSize(w, h);
                        parityCache.SetKeep(zone.id, cachedKeep);
                    }
                }
                else if (isFullImagePath)
                {
                    int seedX = -1, seedY = -1;
                    if (zone.seedUV.x >= 0f)
                    {
                        seedX = Mathf.Clamp(Mathf.RoundToInt(zone.seedUV.x * (w - 1)), 0, w - 1);
                        seedY = Mathf.Clamp(Mathf.RoundToInt(zone.seedUV.y * (h - 1)), 0, h - 1);
                    }
                    // 別パーツの色相幅は有彩サンプルでだけ意味を持つ(グレーモードの色相は不定)。
                    float ffHueBand =
                        sampS > ColorZone.GrayModeEffectiveChromaThreshold(sampV, zone.chromaThreshold)
                            ? zone.partHueBand : 0f;
                    // スポイト位置を含む成分は、色の包絡ゲートで落とさない(クリックした場所を残す)。
                    int anchorX = -1, anchorY = -1;
                    if (zone.HasSampleUV)
                        PreviewCoords.UvToPixel(zone.sampleUV.x, zone.sampleUV.y, w, h,
                            out anchorX, out anchorY);
                    ApplyConnectedComponentMask(strength, matchConf, originalPixels, w, h, seedX, seedY,
                        hsv, zone.shadowValueFloor, zone.partSatCeiling,
                        ffHueBand, sampH, sampS * ColorZone.HueReliableSatFrac,
                        anchorX, anchorY, cancellationToken);
                        _sub.Mark(SpFfComponents);
                    // フル画像で解いた keep(=残った画素 strength>0)を作り、詳細プレビュー(クロップ)へ
                    // 転写(parityCache)・次回の選択キャッシュ復元(keepBitsForCache)の両方に使う。
                    if (parityCache != null || selectionCache != null)
                    {
                        var keepBits = PackPositiveBits(strength, len, po);
                        _sub.Mark(SpFfKeepBits);
                        keepBitsForCache = keepBits;
                        if (parityCache != null)
                        {
                            parityCache.SetFullSize(w, h);
                            parityCache.SetKeep(zone.id, keepBits);
                        }
                    }
                    debug?.RecordStage(zone.id, DebugStages.FloodFill, ToManaged(strength, len), w, h);
                }
                else if (parityCache != null
                         && parityCache.fullW == fullW && parityCache.fullH == fullH)
                {
                    // 部分クロップ(詳細プレビュー): フル画像で解いた keep をフル座標で転写。
                    // keep が無い/寸法不一致なら絞り込まず色のみ=上位集合(安全側)。
                    var keepBits = parityCache.GetKeep(zone.id);
                    if (keepBits != null)
                    {
                        ApplyCachedKeepMask(strength, w, h, originX, originY, fullW, keepBits);
                        debug?.RecordStage(zone.id, DebugStages.FloodFill, ToManaged(strength, len), w, h);
                    }
                }
            }
            // matchConf を読むのは連結成分アンカリングまで。後段(穴埋め・ブラー・デコンタミ等)が
            // 借りる配列に回せるよう、ゾーンの終わりを待たずにプールへ返す(4K で 64MB)。
            if (matchConf != null)
            {
                s_floatPool.Return(matchConf);
                matchConf = zb.MatchConf = null;
            }

            _phaseTicks[PhFloodFill] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

            // 後段パス(穴埋め/境界回復/ブラー)を実マッチ範囲＋余白に限定する bbox (P2-7 拡張)。
            // strength>0 を新たに変えうるのは「現に matched な画素の近傍」だけで、各パスが領域を
            // 外側へ伸ばす最大幅は 穴埋め=±holeFillPasses / 境界回復=±antiAliasCleanup /
            // ブラー=±radius。その総和ぶん余白を取った bbox の外は、処理の前後で常に 0 のまま=
            // 出力ビット不変。bbox 内だけを走査することで、小マッチ(ロゴ等)で全 16.8M 画素の
            // 近傍走査を避ける。Array.Copy/Clear は全画素のまま(memcpy で安価)残し、重い近傍
            // 走査・relaxed 判定だけを bbox に絞る。マッチ皆無(hasPostBox=false)なら後段は全て no-op。
            // ヒット時は保存した bbox を使う(後段のデコンタミ・統計もこの範囲を読むので、計算し直したときと
            // 同じ範囲にする。復元した選択から取り直すと、含めるマスクの画素の分だけ範囲が変わりうる)。
            int ppMinX, ppMinY, ppMaxX, ppMaxY;
            bool hasPostBox;
            if (selCached)
            {
                hasPostBox = cachedBox.Has;
                ppMinX = cachedBox.MinX; ppMinY = cachedBox.MinY;
                ppMaxX = cachedBox.MaxX; ppMaxY = cachedBox.MaxY;
            }
            else
            {
                int ppBlurRadius = edgeFeather > EdgeFeatherBlurMin ? Mathf.CeilToInt(edgeFeather * GaussianRadiusPerSigma) : 0;
                int ppMargin = holeFillPasses + Mathf.Max(0, antiAliasCleanup) + ppBlurRadius + 2;
                hasPostBox = TryComputeStrengthBBox(strength, w, h, 0f,
                    out ppMinX, out ppMinY, out ppMaxX, out ppMaxY, cancellationToken);
                if (hasPostBox)
                {
                    ppMinX = Mathf.Max(0, ppMinX - ppMargin);
                    ppMinY = Mathf.Max(0, ppMinY - ppMargin);
                    ppMaxX = Mathf.Min(w - 1, ppMaxX + ppMargin);
                    ppMaxY = Mathf.Min(h - 1, ppMaxY + ppMargin);
                }
                else { ppMinX = 0; ppMinY = 0; ppMaxX = -1; ppMaxY = -1; } // 空 bbox(後段スキップ)
            }
            _sub.Mark(SpPostBBox);

            // 1b. 孤立した穴を埋める：アンチエイリアス処理された端のピクセルは低彩度を持つことが多く
            //     satConfidenceで見落とされて、元のカラーの孤立したドットを残す
            //     ゼロ強度ピクセルが主にマッチしたピクセルに囲まれている場合は埋める。
            //     穴埋め relaxed ゲート (2026-06-07 再移植): 画素自身が relaxed マッチ
            //     (dist<tolerance) を通る色だけを穴埋め候補に許可する。薄いロゴ等で
            //     「マッチ領域に囲まれただけの背景グレー/白」を full strength に塗ってしまう
            //     フリンジ(白/灰ノイズ)を構造的に防ぐ。境界回復(RecoverBoundaryEdges)と同一基準。
            // relaxed ゲート(穴埋め/境界回復)にプライマリと同じ RGB 距離ブレンドを与えるための
            // chromaConfidence と sample RGB。低彩度サンプル(白/灰)で同色相の高彩度色を弾き、
            // 境界回復が無関係な色を周囲へスピルさせる(対象の縁に別色のハローが出る)のを防ぐ。
            // 有彩は cc≈1 で従来式。
            // 式は ColorZone.BuildSampleCache の chromaConfidence と同じ(定数も共有する)。
            float relaxedChromaConf = Mathf.Min(
                Mathf.Clamp01((sampS - zone.chromaThreshold) / ColorZone.ChromaConfidenceSpan),
                Mathf.Clamp01((sampV - ColorZone.HueValueConfLo) / ColorZone.HueValueConfSpan));
            float rgSampR = zone.sampleColor.r, rgSampG = zone.sampleColor.g, rgSampB = zone.sampleColor.b;
            // 緩和マッチ(穴埋めの許可判定と境界回復が同じ引数で呼ぶ)は色だけで決まるので、
            // 色の表があれば色ごとに 1 回だけ求めて両方で使う(ビット単位で同じ)。
            float[] RelaxedPalette(float tol)
            {
                var outRel = new float[palette.Count];
                Color32* rCol = palette.Colors;
                float* rH = palette.H, rS = palette.S, rV = palette.V;
                ForEachPaletteChunk(palette.Count, po, (k0, k1) =>
                {
                    for (int k = k0; k < k1; k++)
                    {
                        Color32 hop = rCol[k];
                        outRel[k] = GetRelaxedMatchStrength(
                            rH[k], rS[k], rV[k], sampH, sampS, sampV,
                            tol, zone.edgeSoftness, zone.valueWeight,
                            zone.satDistWeight, relaxedSatMin,
                            hop.r / 255f, hop.g / 255f, hop.b / 255f,
                            rgSampR, rgSampG, rgSampB, relaxedChromaConf, zone.chromaThreshold,
                            zone.chromaCeiling);
                    }
                });
                return outRel;
            }
            // 使うのは下の穴埋めと境界回復だけで、どちらも混色帯モードでは走らない。
            float[] palRelaxed = null;
            if (palette != null && !selCached && hasPostBox && !zMixMode
                && (long)palette.Count * 2 <= (long)(ppMaxX - ppMinX + 1) * (ppMaxY - ppMinY + 1))
                palRelaxed = RelaxedPalette(zone.tolerance);
            int* palIdxRelaxed = palette != null ? palette.Index : null;
            if (!selCached && hasPostBox && !zMixMode)
            {
                // 穴埋めの許可だけ許容範囲に余白をつける(zone.holeFillMargin、証拠つき自動調整が入れる)。
                // 素材の色が境目をまたぐ所でノイズで落ちた穴を埋めるため。境界回復は余白なしのまま
                // (囲まれていない画素は広げない)。余白 0 なら許可は境界回復と同じ表を使う。
                float fillTol = zone.holeFillMargin > 0f
                    ? zone.tolerance * (1f + zone.holeFillMargin) : zone.tolerance;
                float[] palFill = palRelaxed != null && fillTol != zone.tolerance
                    ? RelaxedPalette(fillTol) : palRelaxed;
                bool* fillAllowed = s_boolPool.Rent(len);
                try
                {
                    // fillAllowed は FillSmallHoles が中心画素 idx でのみ参照する(近傍は strength を読む)
                    // ため、bbox 内だけ計算すれば足りる。bbox 外は読まれない=出力ビット不変。
                    var fillAllowedLocal = fillAllowed;
                    Parallel.For(ppMinY, ppMaxY + 1, po, y =>
                    {
                        int rowOff = y * w;
                        if (palFill != null)
                        {
                            for (int x = ppMinX; x <= ppMaxX; x++)
                            {
                                int i = rowOff + x;
                                fillAllowedLocal[i] = palFill[palIdxRelaxed[i]] > 0f;
                            }
                            return;
                        }
                        for (int x = ppMinX; x <= ppMaxX; x++)
                        {
                            int i = rowOff + x;
                            Color32 hop = originalPixels[i];
                            fillAllowedLocal[i] = GetRelaxedMatchStrength(
                                pixH[HsvSlot(pixIdx, i)], pixS[HsvSlot(pixIdx, i)], pixV[HsvSlot(pixIdx, i)], sampH, sampS, sampV,
                                fillTol, zone.edgeSoftness, zone.valueWeight,
                                zone.satDistWeight, relaxedSatMin,
                                hop.r / 255f, hop.g / 255f, hop.b / 255f,
                                rgSampR, rgSampG, rgSampB, relaxedChromaConf, zone.chromaThreshold,
                                zone.chromaCeiling) > 0f;
                        }
                    });
                    _sub.Mark(SpHoleFillGate);
                    FillSmallHoles(strength, w, h, holeFillPasses, holeFillMinNeighbors, fillAllowed,
                        ppMinX, ppMinY, ppMaxX, ppMaxY, cancellationToken);
                }
                finally
                {
                    s_boolPool.Return(fillAllowed);
                }
            }
            _sub.Mark(SpHoleFill);
            debug?.RecordStage(zone.id, DebugStages.HoleFill, ToManaged(strength, len), w, h);
            _phaseTicks[PhHoleFill] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

            // 1c. 境界復元：マッチしたピクセルに隣接するマッチしないピクセルを再評価
            //     緩和マッチ(relaxedSatMin)で、正しい段階的な強度を与える
            if (!selCached && antiAliasCleanup > 0 && hasPostBox && !zMixMode)
            {
                RecoverBoundaryEdges(strength, w, h, hsv,
                    zone.sampleColor, zone.tolerance, zone.edgeSoftness, zone.valueWeight,
                    zone.satDistWeight, relaxedSatMin, antiAliasCleanup,
                    ppMinX, ppMinY, ppMaxX, ppMaxY,
                    originalPixels, relaxedChromaConf, zone.chromaThreshold, zone.chromaCeiling,
                    cancellationToken, palRelaxed, palIdxRelaxed);
                _sub.Mark(SpBoundary);
                debug?.RecordStage(zone.id, DebugStages.BoundaryRecover, ToManaged(strength, len), w, h);
            }

            _phaseTicks[PhBoundary] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

            if (!selCached && edgeFeather > EdgeFeatherBlurMin && hasPostBox)
            {
                // ガウシアンブラー用の一時バッファ。GaussianBlur 内部の Parallel.For
                // でキャンセルが入っても preBlur/blurOut が漏れないよう try/finally で囲む。
                float* preBlur = null;
                float* blurOut = null;
                try
                {
                    preBlur = s_floatPool.Rent(len);
                    Buffer.MemoryCopy(strength, preBlur, (long)len * sizeof(float), (long)len * sizeof(float));
                    blurOut = s_floatPool.Rent(len);
                    if (GaussianBlur(strength, blurOut, w, h, edgeFeather,
                        ppMinX, ppMinY, ppMaxX, ppMaxY, cancellationToken))
                    {
                        // strength の所有権を blurOut に移し、もとの strength は返却
                        s_floatPool.Return(strength);
                        strength = zb.Strength = blurOut;
                        blurOut = null; // 二重返却防止
                        ConstrainBlur(strength, preBlur, w, h, Mathf.CeilToInt(edgeFeather * GaussianRadiusPerSigma), cancellationToken);
                    }
                }
                finally
                {
                    if (blurOut != null) s_floatPool.Return(blurOut);
                    if (preBlur != null) s_floatPool.Return(preBlur);
                }
                debug?.RecordStage(zone.id, DebugStages.Blur, ToManaged(strength, len), w, h);
            }

            _phaseTicks[PhBlur] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

            // 選択フェーズ(Match〜ブラー)完了。ここが「生の選択」の境界で、この後はマスクを掛け、
            // SolidifyAchromaInterior/decontam が target 依存で strength を破壊的に
            // 書き換える。ミス時のみ、この時点の strength(コピー)+ FF keep + bbox を選択キャッシュへ保存し、
            // 次回「再着色のみ変更」した再生成で復元して選択フェーズを丸ごと省く(出力ビット不変)。
            // マスクを掛ける前を保存するので、ヒット時も下でマスクを掛ける。
            _sub.Mark(SpBlurReapply);
            if (!selCached && selectionCache != null && isFullImagePath)
            {
                var box = new SelectionCache.PostBox
                {
                    Has = hasPostBox, MinX = ppMinX, MinY = ppMinY, MaxX = ppMaxX, MaxY = ppMaxY,
                };
                selectionCache.StoreFrom(zone.id, selKey, strength, keepBitsForCache, forcedBits, box,
                    commonMask, zoneMask, maskW, maskH, w, h, cancellationToken);
            }
            _sub.Mark(SpSelCacheStore);

            run.Tp = _tp;
            return new SelectionResult
            {
                Strength = strength,
                Box = new SelectionCache.PostBox
                {
                    Has = hasPostBox, MinX = ppMinX, MinY = ppMinY, MaxX = ppMaxX, MaxY = ppMaxY,
                },
            };
        }

    }
}
