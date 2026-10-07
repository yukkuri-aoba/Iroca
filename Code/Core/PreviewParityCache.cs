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
    /// 部分クロップ(ProcessPixelsArray に originX/Y と fullW/H を渡す経路)をフル画像の処理と一致させる
    /// ための転写キャッシュ。クロップは可視範囲のピクセルしか持たないため、「マッチ領域全体の統計から
    /// しか正しく決まらない値」をクロップ内統計で再計算すると値がズレる。これを防ぐため、フル画像で
    /// 1 度だけ解いた結果をゾーン別に保持し、クロップ処理へフル座標で転写する。保持するのは次の 3 種:
    ///  ・keep  : 連結成分アンカリング(flood fill)で「残す」と判定された画素ビット(=選択結果)。
    ///            1画素=1bit(true=残す)で fullW*fullH を表現する。クロップへは AND で転写する。
    ///  ・forced: 閉領域ハイライト復帰(RecoverEnclosedHighlight)がフル画像で戻した画素ビット。
    ///            クロップは色だけでは芯を選べない(復帰の存在理由)ので、keep(AND)とは別に OR で転写する。
    ///  ・stats : 再着色アンカー(autoRecolorAnchor)・wash 実効サンプル・無彩再着色の領域 L 統計。
    ///            いずれもマッチ領域全体の統計から導出されるので、クロップ領域だけでは別の色になる。
    /// 3 つの Dictionary は _lock で保護する。
    /// 製品 UI は 2026-10-03(254af56)以降これを使わない(詳細プレビューはフル段の出力を切り出す)。
    /// 現在の利用者は検証経路だけで、Harness の --ffcheck・dev_safe の parity 回帰・locality probe。
    /// </summary>
    internal sealed class PreviewParityCache
    {
        // この内容を解いた元テクスチャの識別子(0=未設定)。現在の書き手は検証用プローブのみで、
        // Core は読まない。
        public int sourceId;
        public int fullW;
        public int fullH;
        // 同型の SelectionCache は lock 保護済み。こちらは「未公開インスタンスにだけ書く」という
        // 遠隔の呼び出し規律だけが安全性を担保していて、規律を破る変更をコンパイラも実行時も
        // 検出できなかった（レビュー §4 中）。Dictionary は並行アクセスで無限ループや破損を起こす。
        private readonly object _lock = new object();
        private readonly Dictionary<string, ulong[]> _keep = new Dictionary<string, ulong[]>();
        private readonly Dictionary<string, ulong[]> _forced = new Dictionary<string, ulong[]>();
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

        /// <summary>閉領域ハイライト復帰の画素ビット(フル画像で確定)。null=該当なし(旧エントリを消す)。</summary>
        public void SetForced(string zoneId, ulong[] forced)
        {
            if (string.IsNullOrEmpty(zoneId)) return;
            lock (_lock) _forced[zoneId] = forced;
        }

        public ulong[] GetForced(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return null;
            lock (_lock) return _forced.TryGetValue(zoneId, out var f) ? f : null;
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
        public float regLmid;
        public float[] regMidMapFull;    // 無彩再着色の成分別中央値 L マップ(フル画像 per-pixel)。null=不要
    }
}
