// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 自動化 API(MCP)の自動調整。UI の「自動調整」と同じ手順(<see cref="AutoTuneService"/>)をゾーンごとに順に行う:
    /// スポイト位置のあるゾーンは AI(MobileSAM)にその位置の提案を 1 件求めて証拠にし、位置の無いゾーン(色で指定)と、
    /// 提案が背景まで広がったときは証拠なしで導出する。結果は <see cref="ZoneAutoTuner.TuneResult.ApplyTo"/>
    /// でゾーンに当てる(UI・ハーネスと同じ写像)。
    /// AI が無いときは、UI と同じく位置のあるゾーンを証拠なしの導出に落とさない(落とすとツヤを取りこぼす)。
    /// そのゾーンは新しいゾーンの初期値のまま残し、warnings で知らせる。
    ///
    /// AI の推論は EditorApplication.update で少しずつ進むので、この仕事もメインスレッドの <see cref="Advance"/>
    /// で進める(update と、MCP の問い合わせのたび)。解析そのものは背景のスレッドで走る。
    /// 漏れの確かめ(<see cref="AutoTuneService.Run.LeakExclusion"/>)で外す範囲があれば、ウィンドウと同じく、ゾーンの値と一緒に
    /// そのゾーンの「塗らない」へ書く(マスク操作 <see cref="AutomationMaskEdits"/> と同じ入口 <see cref="MaskWorkspace.ApplyRegion"/>
    /// で書き、保存形式へ戻す)。
    /// </summary>
    internal sealed unsafe class AutomationAutoTune {
        private readonly IrocaSessionState _state;
        private readonly Texture2D _texture;
        private readonly List<string> _zoneIds;
        private readonly List<string> _warnings;
        // 作業解像度の原本(参照を 1 つ持つ。終わったら返す)と、AI の元のキー(窓のプレビューと同じ形)。
        private NativePixels _pixels;
        private readonly string _key;
        // 除外マスク(状態のマスクを 1 回だけ展開する。終わったら返す)。漏れの確かめで外す範囲もここへ書いて、状態へ戻す。
        private MaskWorkspace _masks;

        private int _index;
        private AutoTuneService.Run _run;
        private bool _askedAi;
        // AI の証拠待ちが 1 度時間切れになった(残りのゾーンでは AI に聞かない。聞くとゾーンの数だけ時間切れを待つ)。
        private bool _aiTimedOut;

        /// <summary>
        /// ゾーンの id → どう調整したか(<see cref="AutoTuneService.Run.Method"/> か "skipped: …")。名前を鍵にすると、同じ名前の
        /// 調整していないゾーンにも同じ報告が付く。
        /// </summary>
        public readonly Dictionary<string, string> How = new Dictionary<string, string>();

        /// <summary>
        /// ゾーンの id → 漏れの確かめで「塗らない」にした塊の数と、マスクで新しく塗らない側にした画素数(外していないゾーンは無い)。
        /// </summary>
        public readonly Dictionary<string, (int parts, int pixels)> LeakExcluded = new Dictionary<string, (int, int)>();

        public bool Done => _index >= _zoneIds.Count;
        public int Total => _zoneIds.Count;
        public int Finished => _index;

        /// <param name="pixels">作業解像度の原本(下原点)。参照を足して持つ。解析が背景で読むので、終わるまで書き換えないこと。</param>
        public AutomationAutoTune(IrocaSessionState state, Texture2D texture, NativePixels pixels,
            List<string> zoneIds, List<string> warnings) {
            _state = state;
            _texture = texture;
            _pixels = pixels.AddRef();
            _key = AiSuggestBroker.SourceKey(texture, pixels.Width, pixels.Height);
            _zoneIds = zoneIds ?? new List<string>();
            _warnings = warnings;
            if (Done) {
                Release();
            }
        }

        public void Cancel() {
            _run?.Cancel();
            _run = null;
            _index = _zoneIds.Count;
            Release();
        }

        /// <summary>進められるところまで進める(待ちに入ったら返る)。メインスレッドから呼ぶ。</summary>
        public void Advance() {
            while (!Done) {
                var zone = FindZone(_zoneIds[_index]);
                if (zone == null) {
                    _run?.Cancel();
                    _run = null;
                    Next();
                    continue;
                }

                if (_run == null && _aiTimedOut && zone.HasSampleUV) {
                    How[zone.id ?? ""] = "skipped: the AI timed out on an earlier zone";
                    _warnings.Add($"auto-tune of '{zone.name}' was skipped because the AI timed out on an earlier zone; "
                        + "it keeps its current settings. Call iroca_recolor again with the same name to tune it.");
                    Next();
                    continue;
                }
                if (_run == null) {
                    NativeBools excluded = CombinedExclusion(zone.id, out int mw, out int mh);
                    // 漏れの確かめで選択を回すときのマスク(この依頼の設定・マスク。持ち主は自動調整になる)。
                    MaskSnapshot snapshot = _masks.Width > 0 && _masks.Height > 0 ? _masks.ToSnapshot() : null;
                    _run = AutoTuneService.Start(zone, _pixels, _key, excluded, mw, mh,
                        RecolorSettings.From(_state), snapshot, retryAiError: !_askedAi);
                    _askedAi |= _run.AskedAi;
                }
                _run.Advance();
                var run = _run;
                switch (run.Status) {
                    case AutoTuneService.Status.Done:
                        try {
                            run.ApplyTo(zone);
                            ApplyLeakExclusion(zone, run);
                        } finally {
                            run.ReleaseLeakExclusion();
                        }
                        How[zone.id ?? ""] = run.Method;
                        break;
                    case AutoTuneService.Status.Failed:
                        Skip(zone, run);
                        break;
                    default:
                        return;   // 証拠・解析の待ち
                }
                _run = null;
                Next();
            }
        }

        private void Next() {
            _index++;
            if (Done) {
                Release();
            }
        }

        /// <summary>
        /// 調整中のゾーンと残りのゾーンを飛ばして終える(依頼全体の期限。<see cref="IrocaAutomation.RequestBudgetSeconds"/>)。
        /// 飛ばしたゾーンは今の設定のまま(新しいゾーンなら初期値)。
        /// </summary>
        public void SkipRemaining(double budgetSeconds) {
            _run?.Cancel();
            _run = null;
            var skipped = new List<string>();
            for (; _index < _zoneIds.Count; _index++) {
                var zone = FindZone(_zoneIds[_index]);
                if (zone == null) {
                    continue;
                }
                How[zone.id ?? ""] = "skipped: time budget";
                skipped.Add("'" + zone.name + "'");
            }
            if (skipped.Count > 0) {
                _warnings.Add($"auto-tune of {string.Join(", ", skipped)} was skipped because this call ran out of its time budget "
                    + $"({budgetSeconds:0} s; the AI may be slow on this computer). They keep their current settings; "
                    + "call iroca_recolor again with the same names to tune them.");
            }
            Release();
        }

        private void Skip(ColorZone zone, AutoTuneService.Run run) {
            switch (run.Failure) {
                case AutoTuneService.Failure.AiUnavailable:
                    How[zone.id ?? ""] = "skipped: AI not available";
                    _warnings.Add($"auto-tune of '{zone.name}' was skipped because the AI (Unity Sentis and the model) is not installed; "
                        + "it keeps its current settings, so highlights or shadows may be missed. See the manual's AI chapter to install it.");
                    return;
                case AutoTuneService.Failure.AnalysisFailed: {
                        var ex = run.Error;
                        How[zone.id ?? ""] = "skipped: analysis failed";
                        _warnings.Add($"auto-tune of '{zone.name}' failed ({ex?.GetType().Name}: {ex?.Message}); it keeps its current settings.");
                        return;
                    }
            }
            string failure = run.Failure switch {
                AutoTuneService.Failure.AiError => "AI error: " + run.FailureDetail,
                AutoTuneService.Failure.Timeout => "timed out waiting for the AI",
                AutoTuneService.Failure.Cancelled => "cancelled",
                _ => "the AI returned no region",
            };
            if (run.Failure == AutoTuneService.Failure.Timeout) {
                _aiTimedOut = true;
            }
            How[zone.id ?? ""] = "skipped: " + failure;
            _warnings.Add($"auto-tune of '{zone.name}' was skipped ({failure}); it keeps its current settings.");
        }

        // 漏れの確かめで外す範囲を、そのゾーンの「塗らない」へ書き、保存形式へ戻す(続くゾーンの自動調整と、最後の保存・比較が読む)。
        // 書き方はマスク操作の「ここは塗らない」と同じ(塗った範囲の記録が残り、「消す」で戻せる)。
        private void ApplyLeakExclusion(ColorZone zone, AutoTuneService.Run run) {
            if (!run.HasLeakExclusion || _texture == null) {
                return;
            }
            EnsureMaskCanvas();
            var region = run.LeakRegionFor(_masks, _texture);
            if (region == null) {
                return;
            }
            try {
                if (!_masks.ApplyRegion(region.Ptr, zone.id, MaskRegionOp.DontPaintHere, null, out var result)
                    || result.changed == 0) {
                    return;
                }
                _state.maskState ??= new MaskState();
                _masks.EncodeTo(_state.maskState);
                LeakExcluded[zone.id ?? ""] = (run.LeakExcludedParts, result.changed);
            } finally {
                region.Release();
            }
        }

        // マスクの土台が無ければ、マスク操作と同じ寸法で作る(今あるマスクの寸法は変えない。除外の展開と同じ土台に書く)。
        private void EnsureMaskCanvas() {
            EnsureMasksDecoded();
            if (_masks.Width > 0 && _masks.Height > 0) {
                return;
            }
            AutomationMaskEdits.CanvasSize(_texture, out int w, out int h);
            _masks.Reset(w, h);
        }

        // 状態のマスクを展開する(1 回だけ)。
        private void EnsureMasksDecoded() {
            if (_masks != null) {
                return;
            }
            _masks = new MaskWorkspace();
            if (_state.maskState != null) {
                _masks.DecodeFrom(_state.maskState);
            }
        }

        // UI(IrocaWindow.BuildCombinedExclusionForZone)と同じ: 共通の除外 ∪ そのゾーンの除外。無ければ null。
        private NativeBools CombinedExclusion(string zoneId, out int mw, out int mh) {
            EnsureMasksDecoded();
            mw = _masks.Width;
            mh = _masks.Height;
            if (mw <= 0 || mh <= 0) {
                return null;
            }
            return _masks.CombinedExclusion(zoneId);
        }

        private ColorZone FindZone(string id) {
            foreach (var z in _state.zones) {
                if (z != null && z.id == id) {
                    return z;
                }
            }
            return null;
        }

        // 原本と展開したマスク(ヒープの外)を返す。
        private void Release() {
            var px = _pixels;
            _pixels = null;
            px?.Release();
            _masks?.Clear();
            _masks = null;
        }

        /// <summary>
        /// 調整を終えた・やめた後に呼ぶ。AI を使っていて、いろかの窓が 1 つも無ければ、推論のワーカー(GPU のメモリ)と
        /// サービスが持つ元の画素の写しを手放す。窓は閉じるときに手放す(IrocaWindow.OnDestroy)が、窓を開かずに AI だけで
        /// 使うと、次のスクリプトのリロードまで持ち続けていた。窓があれば、窓がまだ使うので手放さない。
        /// </summary>
        public void ReleaseAiIfNoWindow() {
            if (!_askedAi) {
                return;
            }
            if (EditingSessionRegistry.Any) {
                return;
            }
            AiSuggestBroker.ReleaseGpuIfUnused();
            _askedAi = false;
        }
    }
}
