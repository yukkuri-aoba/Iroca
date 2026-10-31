// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 1 回の処理(ProcessPixelsArrayCore)の、ゾーンをまたいで変わらない入力と置き場。パイプラインを
        /// 「選択 → マスクの契約 → 再着色の段」の呼び出しに分けたので、段へはこれを渡す(0.4.0 の計画の 3-7a)。
        /// 指す先(画素・作業配列・色の表)は ProcessPixelsArrayCore が持っている間だけ生きている。
        /// 計測の時刻(<see cref="Tp"/>)は段の中で進めるので、呼ぶ側は呼ぶ前に入れ、戻ってから読み直す。
        /// </summary>
        private sealed class RunContext
        {
            public int W, H, Len;
            // 入力の控え(処理中は読むだけ)。
            public Color32* Original;
            // 出力(呼び出し側の画素。入力と同じ中身で渡され、段がその場で書き換える)。
            public Color32* Pixels;
            public ParallelOptions Po;
            public CancellationToken Ct;
            // 色の表(無ければ null)と画素ごとの HSV。
            public ColorPalette Palette;
            public PixelHsv Hsv;
            public float* PixH, PixS, PixV;
            public int* PixIdx;
            // 共通の除外マスク(持ち主は MaskSnapshot)と、作業座標 → マスクの画素の対応(マスクが無ければ null)。
            public NativeBitMask CommonMask;
            public ulong* CommonBits;
            public int MaskW, MaskH;
            public int[] MaskColOf, MaskRowBase;
            // 全体設定(RecolorSettings を展開したもの)。
            public float EdgeFeather, RelaxedSatMin, RelaxedSatRamp;
            public int AntiAliasCleanup, HoleFillPasses, HoleFillMinNeighbors;
            public bool UseDecontamination;
            public int DecontaminationRadius;
            // ゾーンをまたいで使い回す作業配列。
            public RunBuffers Buf;
            public IDebugCapture Debug;
            public SelectionCache SelectionCache;
            // 新しい選択の領域と素材の地図(使うときに作り、処理の終わりに解放する)。
            public MaterialScene Materials;
            // 計測(値・分岐は変えない)。
            public long[] PhaseTicks;
            public ZonePerfEntry[] PerfZones;   // ゾーンごとの時間(PerfCount 個まで書いた)
            public int PerfCount;
            public SubPhaseClock Sub;
            public long Tp;
        }

        /// <summary>
        /// 1 回の処理で、ゾーンをまたいで使い回す作業配列の持ち主(0.4.0 の計画の 3-7b)。占有率は最初に借り、
        /// デコンタミの 2 つは設定が有効なときに借り、除外フラグと混色帯の被覆率は初めて要るゾーンで借りる。
        /// 処理の終わり(例外・取り消しのときも)に <see cref="ReturnAll"/> が返す。どれもゼロ初期化されないので、
        /// 書いた位置しか読まない(占有率だけは借りた直後に 0 で埋める)。
        /// </summary>
        private sealed class RunBuffers
        {
            // 占有率: 優先度の高い(リスト上位の)ゾーンが書き込んだカバレッジをピクセル単位で累積する。
            public float* Claimed;
            public bool* DecontamAaMask;       // デコンタミで AA 縁とした画素
            public Color32* DecontamPixels;    // デコンタミ・混色帯で塗る色
            // 除外マスクの画素の位置(デコンタミ・混色帯・無彩のフチ消しが、それぞれ自分の読む範囲だけを埋める)。
            public bool* MaskExcluded;
            public float* MixAlpha;            // 混色帯の被覆率(AnalyzeMixtureBand の出力)

            public bool* MaskExcludedOrRent(int len) =>
                MaskExcluded != null ? MaskExcluded : (MaskExcluded = s_boolPool.Rent(len));

            public float* MixAlphaOrRent(int len) =>
                MixAlpha != null ? MixAlpha : (MixAlpha = s_floatPool.Rent(len));

            public void ReturnAll()
            {
                if (MaskExcluded != null) s_boolPool.Return(MaskExcluded);
                if (DecontamPixels != null) s_color32Pool.Return(DecontamPixels);
                if (DecontamAaMask != null) s_boolPool.Return(DecontamAaMask);
                if (MixAlpha != null) s_floatPool.Return(MixAlpha);
                if (Claimed != null) s_floatPool.Return(Claimed);
                MaskExcluded = DecontamAaMask = null;
                DecontamPixels = null;
                MixAlpha = Claimed = null;
            }
        }

        /// <summary>
        /// 1 つのゾーンの処理で借りる作業配列の持ち主。段が借りたら入れ、途中で返したら null に戻す。ゾーンの終わり
        /// (例外・取り消しのときも)に <see cref="ReturnAll"/> が残っているものを返す。
        /// </summary>
        private sealed class ZoneBuffers
        {
            public float* Strength;        // 選択の強度(選択が借り、ぼかしで差し替わることがある)
            public float* HighlightPot;    // ハイライトの伝播の候補(選択の中で返す)
            public float* MatchConf;       // 連結成分のコアの判定(選択の中で返す)
            public bool* IncludedPx;       // 含めるマスクをテクスチャ解像度へ展開したもの(除外優先を焼き込み済み)
            public float* RegMidMap;       // 成分中央値の地図(再着色の統計がプールから借りたとき)
            public float* RegUpMap;        // 同、白側の余白の地図
            public Color32* PalRecolored;  // 合成の段の色ごとの再着色の表(色の数の長さ。使うゾーンだけ)

            public void ReturnAll()
            {
                if (RegMidMap != null) s_floatPool.Return(RegMidMap);
                if (RegUpMap != null) s_floatPool.Return(RegUpMap);
                if (HighlightPot != null) s_floatPool.Return(HighlightPot);
                if (Strength != null) s_floatPool.Return(Strength);
                if (MatchConf != null) s_floatPool.Return(MatchConf);
                if (IncludedPx != null) s_boolPool.Return(IncludedPx);
                if (PalRecolored != null) s_color32Pool.Return(PalRecolored);
                RegMidMap = RegUpMap = HighlightPot = Strength = MatchConf = null;
                IncludedPx = null;
                PalRecolored = null;
            }
        }

        /// <summary>
        /// 1 つのゾーンの処理で、再着色の段から段へ渡す値(0.4.0 の計画の 3-7b)。ゾーンの入力と選択の出力を入れてから
        /// 段を順に呼び、各段は自分の出力をここへ書く。指す先(強度・含めるマスク)の持ち主は <see cref="ZoneBuffers"/>。
        /// </summary>
        private sealed class ZoneState
        {
            public ColorZone Zone;
            public ZoneBuffers Buffers;          // このゾーンで借りた作業配列の持ち主
            public ulong* ZoneMask;             // ゾーン別の除外マスクの語(無ければ null。持ち主は MaskSnapshot)
            public bool MixMode;                // 混色帯モード(IsMixtureBandZone)
            public float SampH, SampS, SampV;   // サンプル色の HSV
            public bool* IncludedPx;            // 含めるマスクをテクスチャ解像度へ展開したもの(無ければ null)
            // 選択の出力(マスクの契約を掛けた後の強度と、後段が走査する範囲)。
            public float* Strength;
            public SelectionCache.PostBox Box;
            // 無彩の固め: 無彩サンプル/極端無彩ターゲットの重み(ComputeAchromaWeight)。
            public float AchromaWeight;
            // デコンタミ: AA 縁とした画素と、そこへ塗る色(設定が無効なら null。指す先は RunBuffers)。
            public bool* AaMask;
            public Color32* Decontaminated;
            // 再着色の統計: ゾーン不変の再着色パラメータと、無彩パスの領域 L(全体の中央値と、成分ごとの地図。
            // 地図は無ければ null で、指す先は ZoneBuffers)。
            public RecolorParams Params;
            public float RegLmid;
            public float* RegMidMap, RegUpMap;
            // 混色帯: 再着色する範囲(画素が無ければ RcMaxX < 0)と、帯の被覆率(帯が無ければ null。指す先は RunBuffers)。
            public int RcMinX, RcMinY, RcMaxX, RcMaxY;
            public float* MixAlpha;
        }

        /// <summary>
        /// 選択と再着色の境目(0.4.0 の計画の 3-7a)。再着色の段が選択から読むのはこれだけ。旧の選択
        /// (<see cref="RunLegacySelection"/>)も、T5 の新しい選択も、この形で出す。
        /// </summary>
        private struct SelectionResult
        {
            /// <summary>
            /// マスクを掛ける前の強度(長さ Len。内部 1・縁は被覆率)。ゾーンの作業配列(<see cref="ZoneBuffers.Strength"/>)と
            /// 同じものを指し、持ち主はゾーンの作業配列。
            /// </summary>
            public float* Strength;

            /// <summary>
            /// 後段(穴埋め・境界回復・ぼかし・デコンタミ・再着色の統計)が走査する範囲。選択の画素に余白を足したもので、
            /// この外の強度は 0。Has = false なら選択の画素が無い。
            /// </summary>
            public SelectionCache.PostBox Box;
        }
    }
}
