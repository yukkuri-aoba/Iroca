// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    // ZoneAutoTuner: tolerance 導出(無彩/有彩/マルチサンプル)と自動トーン抽出。
    internal static partial class ZoneAutoTuner
    {
        // ─────────────────── クラスタ距離の共通定数 / マスク判定 ───────────────────

        private const int DistBins = 600;          // 距離 [0,1.5] を 600 分割（分解能 0.0025）
        private const float DistMax = 1.5f;

        private static bool HasUsableMask(bool[] excluded, int maskW, int maskH)
        {
            if (excluded == null || maskW <= 0 || maskH <= 0) return false;
            if (excluded.Length < maskW * maskH) return false;
            // 何も除外していない / 全部除外 のマスクは「パーツ分離」として使えない。
            int total = maskW * maskH;
            int ex = 0;
            for (int i = 0; i < total; i++) if (excluded[i]) ex++;
            return ex > 0 && ex < total;
        }

        // ピクセル (x,y) がマスクで除外されているか。excluded==null なら常に false(マスク無し)。
        private static bool IsMaskExcluded(bool[] excluded, int maskW, int maskH, int x, int y, int w, int h)
        {
            if (excluded == null) return false;
            int mx = Mathf.Clamp(x * maskW / w, 0, maskW - 1);
            int my = Mathf.Clamp(y * maskH / h, 0, maskH - 1);
            return excluded[my * maskW + mx];
        }

        // ─────────────────── 無彩色(低彩度サンプル)の tolerance ───────────────────
        // 無彩色サンプル(白/黒/グレー)はマッチングがグレーモード=純 RGB 距離になる。
        // 旧来の「near-box の V 広がり(vSpread)+0.10、上限 0.50」は、(1)サンプルからの距離
        // ではなくパート全体の V 幅(両側)を使うため約 2 倍に過大評価し、(2)上限 0.50 が
        // 黒や(RGB が近ければ)有彩色まで巻き込む。代わりにマスク認識型と同じく「サンプルからの
        // 実 RGB 距離分布の高パーセンタイル」で導出する。クラスタは『無彩寄り(低彩度)かつ
        // サンプルと明度が近い』画素に限定し、別パート(黒/白)や有彩を距離分布から排除する。
        private const float AchromaSampleSatMax = 0.15f; // このサンプル彩度未満で無彩 tolerance を使う
        private const float AchromaClusterSatMax = 0.20f; // クラスタに入れる画素の彩度上限(有彩を除外)
        private const float AchromaVWindow = 0.30f;       // サンプル明度からの V 窓(想定シェーディング幅)
        private const float AchromaPercentile = 0.95f;
        private const float AchromaMargin = 0.04f;
        private const float AchromaTolMin = 0.12f;
        private const float AchromaTolMax = 0.40f;

        private static bool TryDeriveAchromaticTolerance(Color32[] pixels, int w, int h, ColorZone zone,
            bool[] excluded, int maskW, int maskH, HsvGrid hsv, out float tolerance)
        {
            tolerance = 0f;
            Color.RGBToHSV(zone.sampleColor, out _, out _, out float sV);
            float sr = zone.sampleColor.r, sg = zone.sampleColor.g, sb = zone.sampleColor.b;

            int stride = hsv.stride;
            var bins = new int[DistBins];
            int count = 0;
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                int grow = gy * hsv.gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue; // マスク除外領域は対象外
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    float pS = hsv.s[grow + gx], pV = hsv.v[grow + gx];
                    if (pS > AchromaClusterSatMax) continue;        // 有彩は別素材として距離分布に入れない
                    if (Mathf.Abs(pV - sV) > AchromaVWindow) continue; // 明度が遠い(黒/白の別パート)は除外
                    float dr = r - sr, dg = g - sg, db = b - sb;
                    float d = Mathf.Sqrt(dr * dr + dg * dg + db * db) * ColorZone.InvSqrt3; // グレーモードの距離式と一致
                    int bi = Mathf.Clamp((int)(d / DistMax * DistBins), 0, DistBins - 1);
                    bins[bi]++;
                    count++;
                }
            }
            if (count < MinNearSampleCount) return false;

            int target = Mathf.CeilToInt(count * AchromaPercentile);
            int cum = 0;
            float pctDist = DistMax;
            for (int i = 0; i < DistBins; i++)
            {
                cum += bins[i];
                if (cum >= target) { pctDist = (i + 1) / (float)DistBins * DistMax; break; }
            }
            tolerance = Mathf.Clamp(pctDist + AchromaMargin, AchromaTolMin, AchromaTolMax);
            return true;
        }

        // ─────────────────── 有彩(中〜高彩度サンプル)の tolerance ───────────────────
        // 有彩サンプルの no-mask tolerance は従来 MergeAnalyzed で hSpread(色相広がり P90)+0.10 と
        // 導出していたが、(1) 実マッチ距離(彩度・明度項を含む)と切り離され、(2) 平坦な +0.10 マージンが
        // 暗い高彩度パーツ(明度が低いまま彩度がほぼ飽和した布地。濃紺・濃青の生地など)で過大選択を
        // 招いていた。この種のパーツは実距離分布が本来タイトなので、平坦マージンはその 1.5 倍規模の
        // tolerance を与え、隣接素材まで巻き込む(実測例: 導出 0.13 に対し最適 ~0.08。tol を最適側へ
        // 下げると precision 0.85→0.99)。無彩経路(TryDeriveAchromaticTolerance)と同じく
        // 「サンプル近傍クラスタの実距離分布の高パーセンタイル」から導出する。
        // 距離式は本番(ColorZone)と同一:
        //   d = hd + |dS|*satDistWeight + |dV|*valueWeight*(1 - clamp(pS/sS))
        // クラスタは near-sample(色相<0.10, |dS|<0.20, |dV|<0.30 = サンプルに似た同パーツ相当の画素)に
        // 限定し、別パーツを距離分布から除外する。床は無彩/マスク認識(0.12)より低い 0.08 とし、暗い高彩度
        // パーツのタイトな分布に追従できるようにする(素直なパーツでは P95+margin がこの床に収まり、内部変動
        // が大きいパーツでは P95 が上がるので自然に広がる ⇒ 取りこぼしと過選択のバランスが取れる)。
        // クラスタが過少(<MinNearSampleCount)なら false を返し、MergeAnalyzed の hSpread tolerance を温存。
        //
        // 【無彩画素の混入対策】Unity の Color.RGBToHSV は無彩(R=G=B)画素の hue を 0 に丸める。
        // そのため暖色(hue≈0)かつ低彩度(sS<NearSatDist=0.20)のサンプルでは、背景や陰影の
        // グレー画素(pS≈0, hue=0)が near-sample クラスタに紛れ込み、その大きな value 項距離で
        // P95 が跳ね上がって tolerance が上限に張り付く(実測: 暖色 sS≈0.17 で 0.35〜0.40)。
        // クラスタは「色のついた同パーツ画素」を表すべきなので、サンプル彩度の一定割合に満たない
        // 無彩寄り画素を距離分布から除外する(無彩経路 TryDeriveAchromaticTolerance が彩度上限で
        // 有彩を除外するのと対称)。パーツ自身の中程度の陰影(pS ≳ sS*frac)は残り、彩度が大きく
        // 落ちる深い陰影は本番のシャドウ免除が拾うので tolerance で覆う必要はない。
        // 上限は HSV 距離で色相 0.22 ぶん(≈79°)までに抑える(0.40 は色相 144° 相当で広すぎた)。
        private const float ChromaPercentile = 0.95f;
        private const float ChromaMargin = 0.03f;
        private const float ChromaTolMin = 0.08f;
        private const float ChromaTolMax = 0.22f;
        private const float ChromaClusterSatFrac = 0.35f; // pS < sS*frac の無彩寄り画素はクラスタから除外

        // ── foreign-hue 隣接パーツ検出(adjacent-part bleed の打ち切り) ──
        // near 窓(hd<NearHueDist=0.10)内に「彩度/明度は近いが hue が自パーツの自然な広がりを超えて
        // 外れる」画素が多数あるとき、それは色相が近い別パーツ(例: 青の隣に水色)。tolerance がその
        // 距離を越えると巻き込む。自パーツ core の hue 広がり(P90)から許容 hue ゲートを導出し、それを
        // 超える foreign 画素の量(core 比)が閾値を超えたら、foreign の最小距離(P10)の直下で tolerance を
        // 打ち切る。通常の床(ChromaTolMin=0.08)より低い ForeignLowFloor まで下げてよい(隣接別パーツの
        // 存在という積極的根拠があるため)。foreign が無いテクスチャでは一切発火せず現行挙動と同一。
        // 特定の色相・キャラ・ピクセルに依存せず、テクスチャ統計(core hue spread / foreign 比)のみから決まる。
        private const int   ForeignHueBins     = 200;    // [0,NearHueDist] の hue ヒストグラム分解能
        private const float CoreHueWindow      = 0.03f;  // core 抽出: サンプルからの hue 窓
        private const float CoreSatWindow      = 0.15f;  // core 抽出: 彩度窓
        private const float CoreValWindow      = 0.20f;  // core 抽出: 明度窓
        private const float ForeignGateK       = 2.5f;   // 許容 hue ゲート = K*coreSpread + floor
        private const float ForeignGateFloor   = 0.015f;
        private const float ForeignGateMin     = 0.03f;  // ゲート下限(締まった core でもこの幅は同パーツ扱い)
        private const float ForeignRatioThresh = 0.25f;  // foreign/core 比がこれ超で隣接別パーツと判定
        private const int   ForeignMinCount    = 30;     // foreign 画素数の下限(ノイズ無視)
        private const float ForeignLowFloor    = 0.04f;  // 打ち切り時に許す tolerance 下限(通常床 0.08 より低い)
        private const float ForeignCapEps      = 0.005f; // foreign 最小距離(P10)からのマージン

        private static bool TryDeriveChromaticTolerance(Color32[] pixels, int w, int h, ColorZone zone,
            bool[] excluded, int maskW, int maskH, HsvGrid hsv, out float tolerance, out bool foreignCapped)
        {
            tolerance = 0f;
            foreignCapped = false;
            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);
            float satDistW = zone.satDistWeight;
            float valueW = zone.valueWeight;

            int stride = hsv.stride;

            // ── 事前パス: 自パーツ core の hue 広がり(P90)から foreign 判定の hue ゲートを導出 ──
            // core = サンプルにごく近い(hue/彩度/明度の窓内)有彩画素。その hue 広がりの数倍までを
            // 「同パーツの色相」とみなし、それを超える画素を foreign(別パーツ)候補にする。
            var coreHueBins = new int[ForeignHueBins];
            int coreHueCount = 0;
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                int grow = gy * hsv.gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    float pH = hsv.h[grow + gx], pS = hsv.s[grow + gx], pV = hsv.v[grow + gx];
                    if (pS < sS * ChromaClusterSatFrac) continue;
                    float hdc = Mathf.Abs(pH - sH); if (hdc > 0.5f) hdc = 1f - hdc;
                    if (hdc >= CoreHueWindow) continue;
                    if (Mathf.Abs(pS - sS) >= CoreSatWindow) continue;
                    if (Mathf.Abs(pV - sV) >= CoreValWindow) continue;
                    int cb = Mathf.Clamp((int)(hdc / NearHueDist * ForeignHueBins), 0, ForeignHueBins - 1);
                    coreHueBins[cb]++;
                    coreHueCount++;
                }
            }
            float coreSpread = 0.02f;
            if (coreHueCount >= MinNearSampleCount)
            {
                int ctgt = Mathf.CeilToInt(coreHueCount * 0.90f), ccum = 0;
                for (int i = 0; i < ForeignHueBins; i++)
                {
                    ccum += coreHueBins[i];
                    if (ccum >= ctgt) { coreSpread = (i + 1) / (float)ForeignHueBins * NearHueDist; break; }
                }
            }
            float effHueGate = Mathf.Clamp(ForeignGateK * coreSpread + ForeignGateFloor,
                                           ForeignGateMin, NearHueDist);

            // ── 主パス: 既存の near-sample クラスタ距離 P95 + foreign 距離分布/個数を同時に集計 ──
            var bins = new int[DistBins];
            int count = 0;
            var fgnBins = new int[DistBins];      // foreign 画素(別 hue)の距離分布
            int fgnCount = 0, coreCount = 0;       // 同パーツ core 個数(foreign 比の分母)
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                int grow = gy * hsv.gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue; // マスク除外領域は対象外
                    float pH = hsv.h[grow + gx], pS = hsv.s[grow + gx], pV = hsv.v[grow + gx];
                    float hd = Mathf.Abs(pH - sH);
                    if (hd > 0.5f) hd = 1f - hd;
                    // near-sample クラスタ(サンプルに似た画素=同パーツ相当)に限定
                    if (hd >= NearHueDist) continue;
                    if (Mathf.Abs(pS - sS) >= NearSatDist) continue;
                    if (Mathf.Abs(pV - sV) >= NearValDist) continue;
                    // 無彩寄り画素(背景/陰影のグレー, hue=0 で暖色サンプルに誤マッチ)を除外
                    if (pS < sS * ChromaClusterSatFrac) continue;
                    float sd = Mathf.Abs(pS - sS);
                    float vd = Mathf.Abs(pV - sV);
                    float sRatio = (sS > 0.01f) ? Mathf.Clamp01(pS / sS) : 1f;
                    float d = hd + sd * satDistW + vd * valueW * (1f - sRatio);
                    int bi = Mathf.Clamp((int)(d / DistMax * DistBins), 0, DistBins - 1);
                    bins[bi]++;
                    count++;
                    // core / foreign の振り分け(hue ゲートで分割)
                    if (hd < effHueGate) coreCount++;
                    else { fgnBins[bi]++; fgnCount++; }
                }
            }
            if (count < MinNearSampleCount) return false;

            int target = Mathf.CeilToInt(count * ChromaPercentile);
            int cum = 0;
            float pctDist = DistMax;
            for (int i = 0; i < DistBins; i++)
            {
                cum += bins[i];
                if (cum >= target) { pctDist = (i + 1) / (float)DistBins * DistMax; break; }
            }
            tolerance = Mathf.Clamp(pctDist + ChromaMargin, ChromaTolMin, ChromaTolMax);

            // ── foreign 打ち切り: 色相の近い隣接別パーツを検出したら、その手前で tolerance を止める ──
            // foreign 画素が core に対して十分多い(=隣に別パーツがある)ときのみ発火。foreign の最小側
            // 距離(P10)の直下まで下げ、別パーツの巻き込みを防ぐ。暗部の取りこぼしは本番のシャドウ免除が拾う。
            if (coreCount > 0 && fgnCount >= ForeignMinCount
                && fgnCount > coreCount * ForeignRatioThresh)
            {
                int ftgt = Mathf.CeilToInt(fgnCount * 0.10f), fcum = 0;
                float fgnP10 = DistMax;
                for (int i = 0; i < DistBins; i++)
                {
                    fcum += fgnBins[i];
                    if (fcum >= ftgt) { fgnP10 = (i + 1) / (float)DistBins * DistMax; break; }
                }
                float capped = Mathf.Clamp(Mathf.Min(tolerance, fgnP10 - ForeignCapEps),
                                           ForeignLowFloor, ChromaTolMax);
                foreignCapped = capped < tolerance; // 実際に打ち切りが効いたときのみ報告
                tolerance = capped;
            }
            return true;
        }

        // ─────────────────── マルチサンプル tolerance 導出 ───────────────────
        // 複数スポイト（主サンプル + extraSamples）から、各画素の「最も近いサンプルまでの距離」
        // の高パーセンタイルで tolerance を決める。和集合マッチング（ColorZone 側で全サンプルの
        // max 強度）と整合する。単一サンプルのとき（extraSamples 空）は呼ばれず、既存の単一経路が
        // そのまま走るので後方互換は保たれる。

        private struct SampleHSV
        {
            public float r, g, b;
            public float h, s, v;
            public float effHueGate; // foreign 判定の hue ゲート（このサンプルの core hue 広がりから導出）
        }

        private static SampleHSV[] BuildSampleHSVs(Color primary, List<Color> extras)
        {
            int extraN = extras?.Count ?? 0;
            var arr = new SampleHSV[1 + extraN];
            arr[0] = MakeSampleHSV(primary);
            for (int i = 0; i < extraN; i++) arr[i + 1] = MakeSampleHSV(extras[i]);
            return arr;
        }

        // ─────────────────── 自動トーン抽出（内部マルチサンプル） ───────────────────
        // スポイト1点(zone.sampleColor)から、同色相のパーツ全体のトーン分布を走査し、暗部・中間・
        // 明部の代表色を内部サンプルとして自動生成する。ユーザーの追加スポイト操作は一切不要。
        // 目的: スポイト位置（明るい所/暗い所/中間）に依存せず、トーン全域を和集合で覆い、
        // 「明部クリックで暗部を取りこぼす（または逆）」位置依存を解消する。
        //
        // 仕組み: まず主サンプル近傍クラスタ(near-window)の彩度分布から「パーツの彩度バンド」を
        // 推定し、その下限(satFloor)を求める。次に、同色相かつ satFloor 以上の画素を明度(V)で
        // ビン分けし、低/中/高パーセンタイルの代表色(そのVバンドの平均RGB)を取る。サンプル色や
        // 既存代表に近すぎる代表は重複として除く。無彩サンプルでは呼ばない(背景の白/黒と分離不能)。
        //
        // ★彩度バンドで限定する理由★: 同色相でも「彩度が著しく低い別マテリアル」(例: 暗い紺色の
        // パーツ S≈0.4 / パーツ本体 S≈0.85+)が暗部に大量にあると、単純な明度パーセンタイルの
        // 暗部代表がその別パーツの色になって巻き込む。パーツの彩度バンド内に絞れば、percentile 前に
        // 別マテリアルを除外でき、暗部代表がパーツ本来の暗い影になる。色だけでパーツ分離できない領域での
        // 過検出を防ぐ安全弁(汎化のため特定色・座標に依存せず、テクスチャ統計のみから決める)。
        private const float AutoToneHueBand   = 0.06f;  // 同パーツとみなす hue 近傍
        private const float AutoToneSatFrac   = 0.35f;  // satFloor の下限 = sS*frac
        private const float AutoTonePartSatRelax = 0.85f; // satFloor = max(sS*frac, nearClusterSatP10*relax)
        private const float AutoToneDarkPct   = 0.12f;  // 暗部代表のパーセンタイル
        private const float AutoToneMidPct    = 0.50f;  // 中間代表のパーセンタイル
        private const float AutoToneLightPct  = 0.85f;  // 明部代表のパーセンタイル
        private const float AutoToneMinSep    = 0.10f;  // サンプル/既存代表とこの距離未満は重複として除外
        private const int   AutoToneMinPixels = 200;    // 同色相画素がこれ未満なら抽出しない
        private const int   AutoToneValueBins = 64;
        private const int   AutoToneSatBins   = 64;
        private const float AutoToneGapFloorFrac = 0.0005f; // V ヒストグラムの「空の谷」判定床(総数比)

        // vConnLoBin/vConnHiBin: V 連結領域ゲートで確定した「サンプルのトーン連結域」の V bin 範囲
        // (AutoToneValueBins 分割)。明部ツヤ救済(VerifyBrightSheenRecall)が「連結域のすぐ上まで」を
        // ツヤとみなす上限に使う。ヒストグラムが作れず範囲を確定できなかったときは -1(無効)。
        private static List<Color> DeriveAutoTonalSamples(Color32[] pixels, int w, int h,
            ColorZone zone, bool[] excluded, int maskW, int maskH,
            HsvGrid hsv, out int vConnLoBin, out int vConnHiBin)
        {
            vConnLoBin = -1;
            vConnHiBin = -1;
            var samples = new List<Color>();
            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);
            if (sS < AchromaSampleSatMax) return samples; // 無彩は対象外

            int stride = hsv.stride;

            // ── パス1: near-window(主サンプルに似た=パーツ本体相当の画素)の彩度 P10 を求める ──
            // |dS|<NearSatDist かつ |dV|<NearValDist の窓に入る同色相画素の彩度分布。低彩度の別
            // マテリアルはこの窓(サンプル彩度の近傍)に入らないので、P10 はパーツ本体の彩度下限を表す。
            // 同じループで core(サンプルにごく近い画素)の hue 広がりも集計し、代表色の hue 純度
            // ゲート(下記 TryAdd)に使う。窓・式は foreign 打ち切りの core 抽出と同一。
            var satBins = new int[AutoToneSatBins];
            int nearCount = 0;
            var coreHueBins = new int[ForeignHueBins];
            int coreHueCount = 0;
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                int grow = gy * hsv.gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    float pH = hsv.h[grow + gx], pS = hsv.s[grow + gx], pV = hsv.v[grow + gx];
                    float hd0 = Mathf.Abs(pH - sH); if (hd0 > 0.5f) hd0 = 1f - hd0;
                    if (hd0 >= NearHueDist) continue;
                    if (Mathf.Abs(pS - sS) >= NearSatDist) continue;
                    if (Mathf.Abs(pV - sV) >= NearValDist) continue;
                    int sb = Mathf.Clamp((int)(pS * AutoToneSatBins), 0, AutoToneSatBins - 1);
                    satBins[sb]++; nearCount++;
                    if (pS >= sS * ChromaClusterSatFrac
                        && hd0 < CoreHueWindow
                        && Mathf.Abs(pS - sS) < CoreSatWindow
                        && Mathf.Abs(pV - sV) < CoreValWindow)
                    {
                        int cb = Mathf.Clamp((int)(hd0 / NearHueDist * ForeignHueBins), 0, ForeignHueBins - 1);
                        coreHueBins[cb]++;
                        coreHueCount++;
                    }
                }
            }
            float nearSatP10 = 0f;
            if (nearCount >= MinNearSampleCount)
            {
                int tgt = Mathf.CeilToInt(nearCount * 0.10f), cum0 = 0;
                for (int i = 0; i < AutoToneSatBins; i++)
                {
                    cum0 += satBins[i];
                    if (cum0 >= tgt) { nearSatP10 = i / (float)AutoToneSatBins; break; }
                }
            }
            // core hue 広がり(P90)→ 代表色に許す hue ずれの上限。foreign 打ち切りの effHueGate と
            // 同じ導出式(K*coreSpread+floor)。色相が一定のパーツではタイトに、陰影で色相が回る
            // パーツでは core 自体の広がりが大きくなるため自動的に緩む。
            float repCoreSpread = 0.02f;
            if (coreHueCount >= MinNearSampleCount)
            {
                int ctgt = Mathf.CeilToInt(coreHueCount * 0.90f), ccum = 0;
                for (int i = 0; i < ForeignHueBins; i++)
                {
                    ccum += coreHueBins[i];
                    if (ccum >= ctgt) { repCoreSpread = (i + 1) / (float)ForeignHueBins * NearHueDist; break; }
                }
            }
            float repHueGate = Mathf.Clamp(ForeignGateK * repCoreSpread + ForeignGateFloor,
                                           ForeignGateMin, NearHueDist);
            // パーツの彩度バンド下限: sS*frac と「near-cluster の彩度 P10*relax」の大きい方。
            // 高彩度均一パーツでは P10≈0.85 が効いて低彩度の別マテリアルを弾く。脱彩する素材では
            // P10 が低く出るので下限も下がり、自パーツの中程度の影は残る。
            float satFloor = Mathf.Max(sS * AutoToneSatFrac, nearSatP10 * AutoTonePartSatRelax);

            // ── パス2: 同色相 かつ 彩度バンド内 の画素を V でビン分け ──
            int VB = AutoToneValueBins;
            var cnt = new int[VB];
            var sumR = new float[VB];
            var sumG = new float[VB];
            var sumB = new float[VB];
            int total = 0;
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                int grow = gy * hsv.gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
                    float pH = hsv.h[grow + gx], pS = hsv.s[grow + gx], pV = hsv.v[grow + gx];
                    if (pS < satFloor) continue;                     // パーツの彩度バンド外(別マテリアル/脱彩)を除外
                    float hd = Mathf.Abs(pH - sH); if (hd > 0.5f) hd = 1f - hd;
                    if (hd >= AutoToneHueBand) continue;              // 別色相パーツを除外
                    int vb = Mathf.Clamp((int)(pV * VB), 0, VB - 1);
                    cnt[vb]++; sumR[vb] += r; sumG[vb] += g; sumB[vb] += b; total++;
                }
            }
            if (total < AutoToneMinPixels) return samples;

            // ── V 連結領域ゲート: 代表色はサンプル自身のトーン連結域からのみ選ぶ ──
            // シェーディングは V 上で連続に分布するため、同一パーツの V ヒストグラムは
            // サンプル V を含むひと続きの山になる。(ほぼ)空の谷を挟んで現れる別クラスタは、
            // 同色相・彩度バンド内でもベース明度の異なる別パーツ(暗い革地の靴に対する明るい
            // 生地部分など、同じ色味で明度だけが段違いの隣接素材)であり、そこから明部/暗部代表を
            // 取ると和集合マッチがそのパーツ全体を巻き込む(実測例: 選択面積が正解の 5 倍まで膨張)。
            // サンプル V の bin から両方向へ、gapFloor 以上の画素を持つ bin が続く範囲(1 bin だけの
            // 欠けは疎なシェーディングとして橋渡し)を代表色の母集団にする。gapFloor は総数比で
            // スケール不変。連続シェーディングのパーツでは全域が連結のままなので挙動不変
            // (検証した被写体では代表 bin が変わらないことを確認済み)。
            int sBin = Mathf.Clamp((int)(sV * VB), 0, VB - 1);
            int gapFloor = Mathf.Max(2, Mathf.CeilToInt(total * AutoToneGapFloorFrac));
            if (cnt[sBin] < gapFloor)
            {
                // サンプル bin 自体が疎(クリック画素が satFloor 直下等)なら最寄りの実在 bin へ寄せる
                int nearest = -1;
                for (int off = 1; off < VB; off++)
                {
                    if (sBin - off >= 0 && cnt[sBin - off] >= gapFloor) { nearest = sBin - off; break; }
                    if (sBin + off < VB && cnt[sBin + off] >= gapFloor) { nearest = sBin + off; break; }
                }
                if (nearest < 0) return samples;
                sBin = nearest;
            }
            int loBin = sBin, hiBin = sBin;
            while (loBin > 0)
            {
                if (cnt[loBin - 1] >= gapFloor) { loBin--; continue; }
                if (loBin > 1 && cnt[loBin - 2] >= gapFloor) { loBin -= 2; continue; }
                break;
            }
            while (hiBin < VB - 1)
            {
                if (cnt[hiBin + 1] >= gapFloor) { hiBin++; continue; }
                if (hiBin < VB - 2 && cnt[hiBin + 2] >= gapFloor) { hiBin += 2; continue; }
                break;
            }
            if (loBin > 0 || hiBin < VB - 1)
            {
                int regionTotal = 0;
                for (int i = loBin; i <= hiBin; i++) regionTotal += cnt[i];
                if (regionTotal < AutoToneMinPixels) return samples;
                for (int i = 0; i < loBin; i++) cnt[i] = 0;
                for (int i = hiBin + 1; i < VB; i++) cnt[i] = 0;
                total = regionTotal;
            }
            vConnLoBin = loBin;
            vConnHiBin = hiBin;

            // 指定パーセンタイルの V バンドの平均色を代表色として取る。
            Color RepAtPct(float pct)
            {
                int targetCount = Mathf.Clamp(Mathf.RoundToInt(pct * total), 1, total);
                int cum = 0, bin = VB - 1;
                for (int i = 0; i < VB; i++) { cum += cnt[i]; if (cum >= targetCount) { bin = i; break; } }
                // RepAtPct は累積が閾値を越えた bin を返すので cnt[bin] > 0 が保証される。
                float inv = 1f / cnt[bin];
                return new Color(sumR[bin] * inv, sumG[bin] * inv, sumB[bin] * inv, 1f);
            }
            void TryAdd(float pct)
            {
                Color rep = RepAtPct(pct);
                if (ColorDist(rep, zone.sampleColor) < AutoToneMinSep) return; // クリック色と重複
                foreach (var s in samples) if (ColorDist(rep, s) < AutoToneMinSep) return; // 既存代表と重複
                // hue 純度ゲート: 代表色(bin 平均)の hue がサンプルの core hue 広がり由来のゲートを
                // 超えて外れる場合、その bin は hue 帯(AutoToneHueBand)内に同居する近色相の
                // 「別パーツ」に支配されている(例: 同 V 帯を占める hue差0.05 の隣接パーツが
                // V 連結ゲートの再正規化後に暗部/明部パーセンタイルを乗っ取る)。同一パーツの
                // 暗部/明部代表は hue≈サンプルなので影響しない。棄却=安全側(単一サンプル挙動へ)。
                Color.RGBToHSV(rep, out float repH, out _, out _);
                float repHd = Mathf.Abs(repH - sH); if (repHd > 0.5f) repHd = 1f - repHd;
                if (repHd >= repHueGate) return;
                samples.Add(rep);
            }
            TryAdd(AutoToneDarkPct);
            TryAdd(AutoToneMidPct);
            TryAdd(AutoToneLightPct);
            return samples;
        }

        private static float ColorDist(Color a, Color b)
        {
            float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db) * ColorZone.InvSqrt3;
        }

        private static SampleHSV MakeSampleHSV(Color c)
        {
            SampleHSV s;
            s.r = c.r; s.g = c.g; s.b = c.b;
            Color.RGBToHSV(c, out s.h, out s.s, out s.v);
            s.effHueGate = ForeignGateMin;
            return s;
        }

        // 有彩マルチサンプル: 各画素を「いずれかのサンプルの near 窓に入る画素」に限定し、
        // その画素から全サンプルへの最小マッチ距離を距離分布に積む。P95+margin を tolerance とする。
        // foreign 打ち切り: どのサンプルの core hue ゲートにも入らない near 画素（=隣接別パーツ）が
        // core に対し多いとき、その最小距離手前で tolerance を止める（単一経路と同じ思想を和集合化）。
        private static bool TryDeriveChromaticToleranceMulti(Color32[] pixels, int w, int h,
            ColorZone zone, SampleHSV[] samples, bool[] excluded, int maskW, int maskH,
            HsvGrid hsv, out float tolerance, out bool foreignCapped)
        {
            tolerance = 0f;
            foreignCapped = false;
            float satDistW = zone.satDistWeight;
            float valueW = zone.valueWeight;
            int stride = hsv.stride;

            // ── 事前パス: 各サンプルの core hue 広がり(P90)→ effHueGate を導出 ──
            for (int si = 0; si < samples.Length; si++)
            {
                var sm = samples[si];
                if (sm.s < AchromaSampleSatMax) { samples[si].effHueGate = ForeignGateMin; continue; }
                var coreHueBins = new int[ForeignHueBins];
                int coreHueCount = 0;
                for (int y = 0, gy = 0; y < h; y += stride, gy++)
                {
                    int rowStart = y * w;
                    int grow = gy * hsv.gw;
                    for (int x = 0, gx = 0; x < w; x += stride, gx++)
                    {
                        Color32 c = pixels[rowStart + x];
                        if (c.a < 128) continue;
                        if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                        float pH = hsv.h[grow + gx], pS = hsv.s[grow + gx], pV = hsv.v[grow + gx];
                        if (pS < sm.s * ChromaClusterSatFrac) continue;
                        float hdc = Mathf.Abs(pH - sm.h); if (hdc > 0.5f) hdc = 1f - hdc;
                        if (hdc >= CoreHueWindow) continue;
                        if (Mathf.Abs(pS - sm.s) >= CoreSatWindow) continue;
                        if (Mathf.Abs(pV - sm.v) >= CoreValWindow) continue;
                        int cb = Mathf.Clamp((int)(hdc / NearHueDist * ForeignHueBins), 0, ForeignHueBins - 1);
                        coreHueBins[cb]++;
                        coreHueCount++;
                    }
                }
                float coreSpread = 0.02f;
                if (coreHueCount >= MinNearSampleCount)
                {
                    int ctgt = Mathf.CeilToInt(coreHueCount * 0.90f), ccum = 0;
                    for (int i = 0; i < ForeignHueBins; i++)
                    {
                        ccum += coreHueBins[i];
                        if (ccum >= ctgt) { coreSpread = (i + 1) / (float)ForeignHueBins * NearHueDist; break; }
                    }
                }
                samples[si].effHueGate = Mathf.Clamp(ForeignGateK * coreSpread + ForeignGateFloor,
                                                     ForeignGateMin, NearHueDist);
            }

            // ── 主パス: 最小距離分布 + foreign 集計 ──
            var bins = new int[DistBins];
            int count = 0;
            var fgnBins = new int[DistBins];
            int fgnCount = 0, coreCount = 0;
            for (int y = 0, gy = 0; y < h; y += stride, gy++)
            {
                int rowStart = y * w;
                int grow = gy * hsv.gw;
                for (int x = 0, gx = 0; x < w; x += stride, gx++)
                {
                    Color32 c = pixels[rowStart + x];
                    if (c.a < 128) continue;
                    if (IsMaskExcluded(excluded, maskW, maskH, x, y, w, h)) continue;
                    float pH = hsv.h[grow + gx], pS = hsv.s[grow + gx], pV = hsv.v[grow + gx];

                    float minDist = float.MaxValue;
                    bool inAnyNear = false, isCore = false;
                    for (int si = 0; si < samples.Length; si++)
                    {
                        var sm = samples[si];
                        float hd = Mathf.Abs(pH - sm.h); if (hd > 0.5f) hd = 1f - hd;
                        if (hd >= NearHueDist) continue;
                        if (Mathf.Abs(pS - sm.s) >= NearSatDist) continue;
                        if (Mathf.Abs(pV - sm.v) >= NearValDist) continue;
                        if (pS < sm.s * ChromaClusterSatFrac) continue; // 無彩寄り画素を除外
                        inAnyNear = true;
                        float sd = Mathf.Abs(pS - sm.s);
                        float vd = Mathf.Abs(pV - sm.v);
                        float sRatio = (sm.s > 0.01f) ? Mathf.Clamp01(pS / sm.s) : 1f;
                        float d = hd + sd * satDistW + vd * valueW * (1f - sRatio);
                        if (d < minDist) minDist = d;
                        if (hd < sm.effHueGate) isCore = true; // どれかのサンプル core hue 内なら core
                    }
                    if (!inAnyNear) continue;
                    int bi = Mathf.Clamp((int)(minDist / DistMax * DistBins), 0, DistBins - 1);
                    bins[bi]++;
                    count++;
                    if (isCore) coreCount++;
                    else { fgnBins[bi]++; fgnCount++; }
                }
            }
            if (count < MinNearSampleCount) return false;

            int target = Mathf.CeilToInt(count * ChromaPercentile);
            int cum = 0;
            float pctDist = DistMax;
            for (int i = 0; i < DistBins; i++)
            {
                cum += bins[i];
                if (cum >= target) { pctDist = (i + 1) / (float)DistBins * DistMax; break; }
            }
            tolerance = Mathf.Clamp(pctDist + ChromaMargin, ChromaTolMin, ChromaTolMax);

            // foreign 打ち切り（単一経路と同形）
            if (coreCount > 0 && fgnCount >= ForeignMinCount
                && fgnCount > coreCount * ForeignRatioThresh)
            {
                int ftgt = Mathf.CeilToInt(fgnCount * 0.10f), fcum = 0;
                float fgnP10 = DistMax;
                for (int i = 0; i < DistBins; i++)
                {
                    fcum += fgnBins[i];
                    if (fcum >= ftgt) { fgnP10 = (i + 1) / (float)DistBins * DistMax; break; }
                }
                float capped = Mathf.Clamp(Mathf.Min(tolerance, fgnP10 - ForeignCapEps),
                                           ForeignLowFloor, ChromaTolMax);
                foreignCapped = capped < tolerance; // 実際に打ち切りが効いたときのみ報告
                tolerance = capped;
            }
            return true;
        }
    }
}
