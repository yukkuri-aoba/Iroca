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
    /// 選択結果(マスク再適用直後の strength=「生の選択」と flood fill keep)をゾーン別にキャッシュする。
    /// 「ターゲット色/再着色パラメータだけ変えた」プレビュー再生成では、選択フェーズ
    /// (Match/Highlight/FloodFill/穴埋め/境界/ブラー/マスク再適用)の結果は不変なので、再計算せず
    /// キャッシュした strength を復元し、再着色フェーズ以降だけを走らせる(=「1回の変更ごと」の高速化)。
    ///
    /// キーは **選択に影響する入力だけ** から作る(<see cref="PixelProcessor.BuildSelectionKey"/>)。
    /// ターゲット色・valueBlend・出力彩度・シャドウ脱彩・wash・再着色アンカーは含めない(=これらの
    /// 変更ではヒットして選択を再利用する)。tolerance・サンプル色・edgeSoftness・各しきい・マスク内容
    /// などが変われば別キー=ミス=再計算。曖昧なものは安全側で「選択影響」に含める(ミスが増えるだけ)。
    ///
    /// strength は ArrayPool 由来で後段が破壊的に書き換えるため、Store では必ずコピーを取る。
    /// 復元結果が「同一入力でフル計算した strength」と完全一致するので出力はビット不変。
    /// フル画像経路でのみ使う(詳細プレビューのクロップでは使わない)。テクスチャ/寸法変更時は
    /// 呼び出し側(PreviewView)が Clear する。
    /// </summary>
    internal sealed class SelectionCache
    {
        private sealed class Entry
        {
            public string Key;
            public float[] Strength;   // マスク再適用直後の strength のコピー(len=w*h)
            public ulong[] Keep;       // flood fill keep(毎回 new されるので参照保持で安全)。FF OFF は null
            public int W, H;
        }

        private readonly Dictionary<string, Entry> _byZone = new Dictionary<string, Entry>();
        // メインプレビューの PreviewJob はキャンセル猶予中に旧タスクと新タスクが一時的に並走しうる
        // (どちらもバックグラウンドスレッド)。Dictionary はスレッド安全でないので lock で保護する。
        private readonly object _gate = new object();

        public bool TryGet(string zoneId, string key, int w, int h, out float[] strength, out ulong[] keep)
        {
            strength = null; keep = null;
            if (string.IsNullOrEmpty(zoneId)) return false;
            lock (_gate)
            {
                if (_byZone.TryGetValue(zoneId, out var e) && e.Key == key && e.W == w && e.H == h)
                {
                    strength = e.Strength; keep = e.Keep; return true;
                }
            }
            return false;
        }

        public void Store(string zoneId, string key, float[] strength, ulong[] keep, int w, int h)
        {
            if (string.IsNullOrEmpty(zoneId)) return;
            int len = w * h;
            var copy = new float[len];
            Array.Copy(strength, copy, len);
            lock (_gate) { _byZone[zoneId] = new Entry { Key = key, Strength = copy, Keep = keep, W = w, H = h }; }
        }

        public void Clear() { lock (_gate) { _byZone.Clear(); } }

        /// <summary>指定した zoneId のキャッシュを破棄する(ゾーン削除時など)。</summary>
        public void Remove(string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId)) return;
            lock (_gate) { _byZone.Remove(zoneId); }
        }

        /// <summary>liveZoneIds に含まれない zoneId のエントリを一括破棄する。
        /// ゾーン削除後もエントリ(4K で float[w*h]≈67MB)が恒久残留するのを防ぐ。
        /// プレビュー生成のたびにセッションの全ゾーン id を渡して呼ぶ。</summary>
        public void RetainOnly(ICollection<string> liveZoneIds)
        {
            if (liveZoneIds == null) return;
            lock (_gate)
            {
                if (_byZone.Count == 0) return;
                List<string> stale = null;
                foreach (var id in _byZone.Keys)
                    if (!liveZoneIds.Contains(id)) (stale ??= new List<string>()).Add(id);
                if (stale != null)
                    foreach (var id in stale) _byZone.Remove(id);
            }
        }
    }
}
