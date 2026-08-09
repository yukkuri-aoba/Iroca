// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 連結成分アンカリングの「残った画素(=full画像の keep)」をゾーン別にビットパックで保持する
    /// キャッシュ。連結性は大域演算でフル画像経路でしか正しく解けないため、メインプレビュー(フル
    /// 画像)で解いた結果をここへ書き、詳細プレビュー(部分クロップ)へフル座標で転写して両者を一致
    /// させる。1画素=1bit(true=残す)。fullW*fullH を表現。スレッド安全性は「未公開の新規インスタンス
    /// にだけ書き込み、公開後は不変として読むだけ」という運用で担保する。
    /// </summary>
    // 詳細プレビュー(クロップ)をメインプレビュー(フル画像)と完全一致させるための転写キャッシュ。
    // クロップは可視範囲のピクセルしか持たないため、「マッチ領域全体の統計からしか正しく決まらない値」を
    // クロップ内統計で再計算すると値がズレ、ズーム/スクロールで選択や出力色が変わってしまう。これを防ぐ
    // ため、フル画像で 1 度だけ解いた結果をゾーン別に保持してクロップ処理へ転写する。
    //  ・keep  : 連結成分アンカリング(flood fill)で「残す」と判定された画素ビット(=選択結果)。
    //  ・stats : 再着色アンカー(autoRecolorAnchor)・wash 実効サンプル・無彩再着色の領域 L 統計。
    //            いずれもマッチ領域全体の統計から導出されるので、クロップ領域だけでは別の色になる。
    internal sealed class PreviewParityCache
    {
        // この内容を解いた元テクスチャの識別子(UI 側が Texture2D.GetInstanceID() を入れる。0=未設定)。
        // 詳細側は寸法一致に加えてこれの一致も条件にする。寸法だけで採否を決めると、2048² など
        // アバターで揃いがちな寸法の「別テクスチャ」へ切り替えたときに旧テクスチャの keep/統計を
        // そのまま転写してしまう。テクスチャ同一性はプレビュー再生成中に揺れないので、世代スタンプ
        // のように「過渡的に外れて悪化する」ことがない(DetailPreviewView の採否コメント参照)。
        public int sourceId;
        public int fullW;
        public int fullH;
        // 同型の SelectionCache は lock 保護済み。こちらは「未公開インスタンスにだけ書く」という
        // 遠隔の呼び出し規律だけが安全性を担保していて、規律を破る変更をコンパイラも実行時も
        // 検出できなかった（レビュー §4 中）。Dictionary は並行アクセスで無限ループや破損を起こす。
        private readonly object _lock = new object();
        private readonly Dictionary<string, ulong[]> _keep = new Dictionary<string, ulong[]>();
        private readonly Dictionary<string, ZoneRecolorStats> _stats = new Dictionary<string, ZoneRecolorStats>();

        // フル画像処理が確定した入力寸法。keep / stats のどちらを書く場合も最初に設定する
        // (flood fill OFF でも stats 転写を効かせるため keep 書き込みとは独立に呼ぶ)。
        public void SetFullSize(int w, int h) { fullW = w; fullH = h; }

        public void SetKeep(string zoneId, ulong[] keep)
        {
            if (string.IsNullOrEmpty(zoneId)) return;
            lock (_lock) _keep[zoneId] = keep;
        }

        public ulong[] GetKeep(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return null;
            lock (_lock) return _keep.TryGetValue(zoneId, out var k) ? k : null;
        }

        public void SetStats(string zoneId, in ZoneRecolorStats s)
        {
            if (string.IsNullOrEmpty(zoneId)) return;
            lock (_lock) _stats[zoneId] = s;
        }

        public bool TryGetStats(string zoneId, out ZoneRecolorStats s)
        {
            if (string.IsNullOrEmpty(zoneId)) { s = default; return false; }
            lock (_lock) return _stats.TryGetValue(zoneId, out s);
        }
    }

    // フル画像のマッチ領域統計から導出され、クロップへそのまま転写すべき再着色パラメータ。
    // クロップ内のピクセル統計から再計算すると値が変わり、出力色がズーム位置で揺れる原因になる。
    internal struct ZoneRecolorStats
    {
        public bool anchorApplied;       // autoRecolorAnchor が実際に適用されたか(false=スポイト色アンカーのまま)
        public float anchorL, anchorC;   // OkLab アンカー(マッチ領域の明部地色)
        public float effShadowDesat;     // アンカー補正後の実効シャドウ脱彩
        public float washR, washG, washB, washV; // wash(ハイライト白射影)の実効サンプル
        public bool hasRegL;             // 無彩再着色の領域 L レンジが有効か
        public float regLlo, regLhi, regLmid;
        public float[] regMidMapFull;    // 無彩再着色の成分別中央値 L マップ(フル画像 per-pixel)。null=不要
    }
}
