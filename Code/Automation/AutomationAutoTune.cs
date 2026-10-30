// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
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
    /// </summary>
    internal sealed class AutomationAutoTune
    {
        private readonly IrocaSessionState _state;
        private readonly List<string> _zoneIds;
        private readonly List<string> _warnings;
        // 作業解像度の原本(参照を 1 つ持つ。終わったら返す)と、AI の元のキー(窓のプレビューと同じ形)。
        private NativePixels _pixels;
        private readonly string _key;
        // 除外マスク(状態のマスクを 1 回だけ展開する。終わったら返す)。
        private MaskWorkspace _masks;

        private int _index;
        private AutoTuneService.Run _run;
        private bool _askedAi;

        /// <summary>ゾーン名 → どう調整したか("evidence" / "conventional" / "skipped: …")。</summary>
        public readonly Dictionary<string, string> How = new Dictionary<string, string>();

        public bool Done => _index >= _zoneIds.Count;
        public int Total => _zoneIds.Count;
        public int Finished => _index;

        /// <param name="pixels">作業解像度の原本(下原点)。参照を足して持つ。解析が背景で読むので、終わるまで書き換えないこと。</param>
        public AutomationAutoTune(IrocaSessionState state, Texture2D texture, NativePixels pixels,
            List<string> zoneIds, List<string> warnings)
        {
            _state = state;
            _pixels = pixels.AddRef();
            _key = CacheKey(texture, pixels.Width, pixels.Height);
            _zoneIds = zoneIds ?? new List<string>();
            _warnings = warnings;
            if (Done) Release();
        }

        public void Cancel()
        {
            _run?.Cancel();
            _run = null;
            _index = _zoneIds.Count;
            Release();
        }

        /// <summary>進められるところまで進める(待ちに入ったら返る)。メインスレッドから呼ぶ。</summary>
        public void Advance()
        {
            while (!Done)
            {
                var zone = FindZone(_zoneIds[_index]);
                if (zone == null)
                {
                    _run?.Cancel();
                    _run = null;
                    Next();
                    continue;
                }

                if (_run == null)
                {
                    bool[] excluded = CombinedExclusion(zone.id, out int mw, out int mh);
                    _run = AutoTuneService.Start(zone, _state, _pixels, _key, excluded, mw, mh);
                    _askedAi |= _run.AskedAi;
                }
                _run.Advance();
                var run = _run;
                switch (run.Status)
                {
                    case AutoTuneService.Status.Done:
                        run.ApplyTo(zone, _state);
                        zone.UpdateCacheIfNeeded();
                        How[zone.name] = HowOf(run);
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

        private void Next()
        {
            _index++;
            if (Done) Release();
        }

        private static string HowOf(AutoTuneService.Run run)
        {
            if (run.HadEvidence) return "evidence";
            return run.ConventionalReason == AutoTuneService.ReasonEvidenceFlood
                ? "conventional (AI region spread to the background)"
                : "conventional";
        }

        private void Skip(ColorZone zone, AutoTuneService.Run run)
        {
            switch (run.Failure)
            {
                case AutoTuneService.Failure.AiUnavailable:
                    How[zone.name] = "skipped: AI not available";
                    _warnings.Add($"auto-tune of '{zone.name}' was skipped because the AI (Unity Sentis and the model) is not installed; "
                        + "it keeps the initial settings, so highlights or shadows may be missed. See the manual's AI chapter to install it.");
                    return;
                case AutoTuneService.Failure.AnalysisFailed:
                {
                    var ex = run.Error;
                    How[zone.name] = "skipped: analysis failed";
                    _warnings.Add($"auto-tune of '{zone.name}' failed ({ex?.GetType().Name}: {ex?.Message}); it keeps the initial settings.");
                    return;
                }
            }
            string failure = run.Failure switch
            {
                AutoTuneService.Failure.AiError => "AI error: " + run.FailureDetail,
                AutoTuneService.Failure.Timeout => "timed out waiting for the AI",
                AutoTuneService.Failure.Cancelled => "cancelled",
                _ => "the AI returned no region",
            };
            How[zone.name] = "skipped: " + failure;
            _warnings.Add($"auto-tune of '{zone.name}' was skipped ({failure}); it keeps the initial settings.");
        }

        // UI(IrocaWindow.BuildCombinedExclusionForZone)と同じ: 共通の除外 ∪ そのゾーンの除外。無ければ null。
        private bool[] CombinedExclusion(string zoneId, out int mw, out int mh)
        {
            if (_masks == null)
            {
                _masks = new MaskWorkspace();
                if (_state.maskState != null) _masks.DecodeFrom(_state.maskState);
            }
            mw = _masks.Width;
            mh = _masks.Height;
            if (mw <= 0 || mh <= 0) return null;
            return _masks.CombinedExclusion(zoneId);
        }

        private ColorZone FindZone(string id)
        {
            foreach (var z in _state.zones) if (z != null && z.id == id) return z;
            return null;
        }

        // 原本と展開したマスク(ヒープの外)を返す。
        private void Release()
        {
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
        public void ReleaseAiIfNoWindow()
        {
            if (!_askedAi) return;
            if (EditingSessionRegistry.Any) return;
            AiSuggestBroker.ReleaseGpuIfUnused();
            _askedAi = false;
        }

        /// <summary>窓のプレビューと同じ形の埋め込みキャッシュのキー(PreviewView.TrueSourceCacheKey)。</summary>
        internal static string CacheKey(Texture2D texture, int w, int h)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            long ticks = 0;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                ticks = File.GetLastWriteTimeUtc(path).Ticks;
            return $"{path}|{ticks}|{w}x{h}";
        }
    }
}
