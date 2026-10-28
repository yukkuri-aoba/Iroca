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
            // 部分クロップのときの位置とフル解像度の寸法(フル画像なら 0 と W・H)。
            public int OriginX, OriginY, FullW, FullH;
            // 入力の控え(処理中は読むだけ)。
            public Color32* Original;
            public ParallelOptions Po;
            public CancellationToken Ct;
            // 色の表(無ければ null)と画素ごとの HSV。
            public ColorPalette Palette;
            public PixelHsv Hsv;
            public float* PixH, PixS, PixV;
            public int* PixIdx;
            // 共通の除外マスクと、作業座標 → マスクの画素の対応(マスクが無ければ null)。
            public ulong[] CommonMask;
            public int MaskW, MaskH;
            public int[] MaskColOf, MaskRowBase;
            // 全体設定(RecolorSettings を展開したもの)。
            public float EdgeFeather, RelaxedSatMin, RelaxedSatRamp;
            public int AntiAliasCleanup, HoleFillPasses, HoleFillMinNeighbors;
            public IDebugCapture Debug;
            public PreviewParityCache ParityCache;
            public SelectionCache SelectionCache;
            // 計測(値・分岐は変えない)。
            public long[] PhaseTicks;
            public SubPhaseClock Sub;
            public long Tp;
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

            public void ReturnAll()
            {
                if (RegMidMap != null) s_floatPool.Return(RegMidMap);
                if (RegUpMap != null) s_floatPool.Return(RegUpMap);
                if (HighlightPot != null) s_floatPool.Return(HighlightPot);
                if (Strength != null) s_floatPool.Return(Strength);
                if (MatchConf != null) s_floatPool.Return(MatchConf);
                if (IncludedPx != null) s_boolPool.Return(IncludedPx);
                RegMidMap = RegUpMap = HighlightPot = Strength = MatchConf = null;
                IncludedPx = null;
            }
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
