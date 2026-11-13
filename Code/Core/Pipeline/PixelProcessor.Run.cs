// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    internal static unsafe partial class PixelProcessor {
        /// <summary>
        /// 1 回の処理(ProcessPixelsArrayCore)の、ゾーンをまたいで変わらない入力と置き場。パイプラインを
        /// 「選択 → マスクの契約 → 再着色の段」の呼び出しに分けたので、段へはこれを渡す(0.4.0 の計画の 3-7a)。
        /// 指す先(画素・作業配列・色の表)は ProcessPixelsArrayCore が持っている間だけ生きている。
        /// 計測の時刻(<see cref="Tp"/>)は段の中で進めるので、呼ぶ側は呼ぶ前に入れ、戻ってから読み直す。
        /// </summary>
        private sealed class RunContext {
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
            public float EdgeFeather, RelaxedSatMin;
            public int AntiAliasCleanup, HoleFillPasses, HoleFillMinNeighbors;
            public bool UseDecontamination;
            public int DecontaminationRadius;
            // ゾーンをまたいで使い回す作業配列。
            public RunBuffers Buf;
            public IDebugCapture Debug;
            public SelectionCache SelectionCache;
            // 計測(値・分岐は変えない)。
            public long[] PhaseTicks;
            public ZonePerfEntry[] PerfZones;   // ゾーンごとの時間(PerfCount 個まで書いた)
            public int PerfCount;
            public SubPhaseClock Sub;
            public long Tp;
        }

        /// <summary>
        /// 1 回の処理で、ゾーンをまたいで持ち続ける作業配列の持ち主(0.4.0 の計画の 3-7b)。占有率(か、その印)だけで、
        /// 最初に借りて 0 で埋める。上のゾーンが書いた値を下のゾーンが読むので、ゾーンをまたぐ。
        /// 処理の終わり(例外・取り消しのときも)に <see cref="ReturnAll"/> が返す。
        /// ほかの作業配列(除外フラグ・混色帯の被覆率・デコンタミの 2 つ)はゾーンごと(<see cref="ZoneBuffers"/>)。
        /// </summary>
        private sealed class RunBuffers {
            // 占有率: 優先度の高い(リスト上位の)ゾーンが書き込んだカバレッジをピクセル単位で累積する。
            // ゾーンが 1 つのときは借りない(null)。上位のゾーンが無いので読む値はいつも 0 で、同じゾーンの後の段
            // (無彩のフチ消し)が見るのは「占有が 0.001 を超えたか」だけなので、それを ClaimedFlag に持つ
            // (4096² で 64MB → 16MB。2026-11-08)。
            public float* Claimed;
            public bool* ClaimedFlag;

            public void ReturnAll() {
                if (Claimed != null) {
                    s_floatPool.Return(Claimed);
                }
                if (ClaimedFlag != null) {
                    s_boolPool.Return(ClaimedFlag);
                }
                ClaimedFlag = null;
                Claimed = null;
            }
        }

        /// <summary>
        /// 1 つのゾーンの処理で借りる作業配列の持ち主。段が借りたら入れ、途中で返したら null に戻す。ゾーンの終わり
        /// (例外・取り消しのときも)に <see cref="ReturnAll"/> が残っているものを返す。
        /// </summary>
        private sealed class ZoneBuffers {
            public float* Strength;        // 選択の強度(選択が借り、ぼかしで差し替わることがある)
            public float* HighlightPot;    // ハイライトの伝播の候補(選択の中で返す)
            public bool* MatchConf;        // 連結成分のコアの判定 = 照合の確信度 > 0(選択の中で返す。値は「> 0 か」しか読まないので 1 画素 1 バイト)
            // 色ごとの緩和マッチの値(色の数の長さ。色の表があるゾーンの穴埋め・境界回復が読み、選択の中で返す)。
            // PalFill は穴埋めの許可用で、許容範囲に余白があるときだけ別に借りる(無ければ PalRelaxed を読む)。
            public float* PalRelaxed;
            public float* PalFill;
            public bool* IncludedPx;       // 含めるマスクをテクスチャ解像度へ展開したもの(除外優先を焼き込み済み)
            public float* RegMidMap;       // 成分中央値の地図(再着色の統計がプールから借りたとき)
            public float* RegUpMap;        // 同、白側の余白の地図
            public Color32* PalRecolored;  // 合成の段の色ごとの再着色の表(色の数の長さ。使うゾーンだけ)
            // デコンタミで AA 縁とした画素と、デコンタミ・混色帯で塗る色(設定が有効なとき。デコンタミの段が借りる)。
            // どちらもゾーンの中で書いてから読むので、ゾーンの終わりに返す(2026-11-08 まで 1 回の処理の初めに借りて
            // 最後まで持っていたので、次のゾーンの選択の間も 4096² で 80MB 寝ていた)。
            public bool* DecontamAaMask;
            public Color32* DecontamPixels;
            // 除外マスクの画素の位置。デコンタミ・混色帯・無彩のフチ消しのうち、最初に要る段が借り、どの段も自分の読む範囲を
            // 埋めてから読む(ゼロ初期化されない。前の段が埋めた所が残っていても壊れない作り)。除外マスクが無ければ借りない。
            public bool* MaskExcluded;
            // 混色帯の被覆率(AnalyzeMixtureBand の出力。混色帯モードのゾーンだけ)。解析が書いた範囲だけを合成の段が読む。
            // この 2 つも、ゾーンの中で書いてから読み、ゾーンの後に読む所は無い。2026-11-13 まで 1 回の処理の終わりまで
            // 持っていたので、続くゾーンの選択の間も寝ていた(4K で最大 80MB・8K で最大 320MB)。
            public float* MixAlpha;

            public bool* MaskExcludedOrRent(int len) =>
                MaskExcluded != null ? MaskExcluded : (MaskExcluded = s_boolPool.Rent(len));

            public float* MixAlphaOrRent(int len) =>
                MixAlpha != null ? MixAlpha : (MixAlpha = s_floatPool.Rent(len));

            public void ReturnAll() {
                if (MaskExcluded != null) {
                    s_boolPool.Return(MaskExcluded);
                }
                if (MixAlpha != null) {
                    s_floatPool.Return(MixAlpha);
                }
                if (RegMidMap != null) {
                    s_floatPool.Return(RegMidMap);
                }
                if (RegUpMap != null) {
                    s_floatPool.Return(RegUpMap);
                }
                if (HighlightPot != null) {
                    s_floatPool.Return(HighlightPot);
                }
                if (PalFill != null) {
                    s_floatPool.Return(PalFill);
                }
                if (PalRelaxed != null) {
                    s_floatPool.Return(PalRelaxed);
                }
                if (Strength != null) {
                    s_floatPool.Return(Strength);
                }
                if (MatchConf != null) {
                    s_boolPool.Return(MatchConf);
                }
                if (IncludedPx != null) {
                    s_boolPool.Return(IncludedPx);
                }
                if (PalRecolored != null) {
                    s_color32Pool.Return(PalRecolored);
                }
                if (DecontamPixels != null) {
                    s_color32Pool.Return(DecontamPixels);
                }
                if (DecontamAaMask != null) {
                    s_boolPool.Return(DecontamAaMask);
                }
                RegMidMap = RegUpMap = HighlightPot = Strength = PalFill = PalRelaxed = null;
                IncludedPx = DecontamAaMask = MatchConf = MaskExcluded = null;
                MixAlpha = null;
                PalRecolored = DecontamPixels = null;
            }
        }

        /// <summary>
        /// 1 つのゾーンの処理で、再着色の段から段へ渡す値(0.4.0 の計画の 3-7b)。ゾーンの入力と選択の出力を入れてから
        /// 段を順に呼び、各段は自分の出力をここへ書く。指す先(強度・含めるマスク)の持ち主は <see cref="ZoneBuffers"/>。
        /// </summary>
        private sealed class ZoneState {
            public ColorZone Zone;
            public ZoneBuffers Buffers;          // このゾーンで借りた作業配列の持ち主
            public ulong* ZoneMask;             // ゾーン別の除外マスクの語(無ければ null。持ち主は MaskSnapshot)
            public bool MixMode;                // 混色帯モード(IsMixtureBandZone)
            public float SampS;                 // サンプル色の彩度(再着色の統計と分岐の記録が読む)
            public bool* IncludedPx;            // 含めるマスクをテクスチャ解像度へ展開したもの(無ければ null)
            // 選択の出力(マスクの契約を掛けた後の強度と、後段が走査する範囲)。
            public float* Strength;
            public SelectionCache.PostBox Box;
            // マスクの契約の後に、強度が 0 でない画素がありうる範囲 = Box ∪ 含めるマスクの画素の外接矩形。
            // 選択が返した時点では Box の外の強度は 0 で、契約はその外に、含めるマスクの画素(強度 1)だけを足す。後の段は
            // 強度を「すでに 0 でない画素」でしか上げないので、ゾーンの終わりまで成り立つ。画像全体から強度を拾い直す走査
            // (無彩の固め・成分の地図・再着色の範囲)を、この中に限るのに使う。Box だけに限ると、離れた所の含める画素を落とす。
            public SelectionCache.PostBox Scan;
            // 無彩の固め: 無彩サンプル/極端無彩ターゲットの重み(ComputeAchromaWeight)。
            public float AchromaWeight;
            // デコンタミ: AA 縁とした画素と、そこへ塗る色(設定が無効なら null。指す先は ZoneBuffers)。
            public bool* AaMask;
            public Color32* Decontaminated;
            // 再着色の統計: ゾーン不変の再着色パラメータと、無彩パスの領域 L(全体の中央値と、成分ごとの地図。
            // 地図は無ければ null で、指す先は ZoneBuffers)。
            public RecolorParams Params;
            public float RegLmid;
            public float* RegMidMap, RegUpMap;
            // 混色帯: 再着色する範囲(画素が無ければ RcMaxX < 0)と、帯の被覆率(帯が無ければ null。指す先は ZoneBuffers)。
            public int RcMinX, RcMinY, RcMaxX, RcMaxY;
            public float* MixAlpha;
        }

        /// <summary>
        /// 選択と再着色の境目(0.4.0 の計画の 3-7a)。再着色の段が選択から読むのはこれだけ。選択
        /// (<see cref="RunSelection"/>)の作りを替えるときも、この形で出す。
        /// </summary>
        private struct SelectionResult {
            /// <summary>
            /// マスクを掛ける前の強度(長さ Len。内部 1・縁は被覆率)。ゾーンの作業配列(<see cref="ZoneBuffers.Strength"/>)と
            /// 同じものを指し、持ち主はゾーンの作業配列。
            /// </summary>
            public float* Strength;

            /// <summary>
            /// 後段(穴埋め・境界回復・ぼかし・デコンタミ・再着色の統計)が走査する範囲。選択の画素に余白を足したもので、
            /// 選択が返した時点では、この外の強度は 0。Has = false なら選択の画素が無い。マスクの契約の後は、
            /// 含めるマスクの画素がこの外にも強度 1 で立ちうる(ForceIncluded)ので、「外は 0」を前提に読み飛ばさないこと。
            /// </summary>
            public SelectionCache.PostBox Box;
        }
    }
}
