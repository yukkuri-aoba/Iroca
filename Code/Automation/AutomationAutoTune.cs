// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 自動化 API(MCP)の自動調整。UI の「自動調整」と同じ手順をゾーンごとに順に行う:
    /// スポイト位置のあるゾーンは AI(MobileSAM)にその位置の提案を 1 件求め、それを証拠に
    /// <see cref="ZoneAutoTuner.AnalyzeWithEvidence"/>。位置の無いゾーン(色で指定)と、提案が背景まで
    /// 広がったときは証拠なしの <see cref="ZoneAutoTuner.Analyze"/>。結果は <see cref="ZoneAutoTuner.TuneResult.ApplyTo"/>
    /// でゾーンに当てる(UI・ハーネスと同じ写像)。
    /// AI が無いときは、UI と同じく位置のあるゾーンを証拠なしの導出に落とさない(落とすとツヤを取りこぼす)。
    /// そのゾーンは新しいゾーンの初期値のまま残し、warnings で知らせる。
    ///
    /// AI の推論は EditorApplication.update で少しずつ進むので、この仕事もメインスレッドの <see cref="Advance"/>
    /// で進める(update と、MCP の問い合わせのたび)。解析そのものは背景のスレッドで走る。
    /// </summary>
    internal sealed class AutomationAutoTune
    {
        // UI と同じ待ちの上限(IrocaWindow.AutoTune の EvidenceWaitSeconds)。埋め込みの計算は
        // CPU バックエンドの大きなテクスチャで数十秒かかることがある。
        private const double EvidenceWaitSeconds = 180.0;

        private readonly IrocaSessionState _state;
        private readonly Texture2D _texture;
        private readonly Color32[] _pixels;
        private readonly int _w, _h;
        private readonly List<string> _zoneIds;
        private readonly List<string> _warnings;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private int _index;
        private Evidence _evidence;
        private Task<ZoneAutoTuner.TuneResult> _analysis;
        private bool _aiChecked;
        private bool _aiAvailable;

        /// <summary>ゾーン名 → どう調整したか("evidence" / "conventional" / "skipped: …")。</summary>
        public readonly Dictionary<string, string> How = new Dictionary<string, string>();

        public bool Done => _index >= _zoneIds.Count;
        public int Total => _zoneIds.Count;
        public int Finished => _index;

        /// <param name="pixels">作業解像度の原本(下原点)。解析が背景で読むので、終わるまで書き換えないこと。</param>
        public AutomationAutoTune(IrocaSessionState state, Texture2D texture, Color32[] pixels, int w, int h,
            List<string> zoneIds, List<string> warnings)
        {
            _state = state;
            _texture = texture;
            _pixels = pixels;
            _w = w;
            _h = h;
            _zoneIds = zoneIds ?? new List<string>();
            _warnings = warnings;
        }

        public void Cancel()
        {
            _cts.Cancel();
            _evidence?.Dispose();
            _evidence = null;
            _index = _zoneIds.Count;
        }

        /// <summary>進められるところまで進める(待ちに入ったら返る)。メインスレッドから呼ぶ。</summary>
        public void Advance()
        {
            while (!Done)
            {
                var zone = FindZone(_zoneIds[_index]);
                if (zone == null) { _index++; continue; }

                // 解析の結果待ち
                if (_analysis != null)
                {
                    if (!_analysis.IsCompleted) return;
                    var task = _analysis;
                    _analysis = null;
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        task.Result.ApplyTo(zone, _state);
                        zone.UpdateCacheIfNeeded();
                    }
                    else
                    {
                        var ex = task.Exception?.GetBaseException();
                        How[zone.name] = "skipped: analysis failed";
                        _warnings.Add($"auto-tune of '{zone.name}' failed ({ex?.GetType().Name}: {ex?.Message}); it keeps the initial settings.");
                    }
                    _index++;
                    continue;
                }

                // 位置の無いゾーン(色で指定)は、UI と同じく証拠なしで導出する
                if (!zone.HasSampleUV)
                {
                    StartAnalysis(zone, null, 0, 0, "conventional");
                    continue;
                }

                if (!AiAvailable())
                {
                    How[zone.name] = "skipped: AI not available";
                    _warnings.Add($"auto-tune of '{zone.name}' was skipped because the AI (Unity Sentis and the model) is not installed; "
                        + "it keeps the initial settings, so highlights or shadows may be missed. See the manual's AI chapter to install it.");
                    _index++;
                    continue;
                }

                // 証拠(スポイト位置の AI 提案)待ち
                if (_evidence == null)
                    _evidence = new Evidence(_texture, _pixels, _w, _h, zone.sampleUV);
                if (!_evidence.Poll(out var proposal, out string failure)) return;
                _evidence.Dispose();
                _evidence = null;

                if (failure != null)
                {
                    How[zone.name] = "skipped: " + failure;
                    _warnings.Add($"auto-tune of '{zone.name}' was skipped ({failure}); it keeps the initial settings.");
                    _index++;
                    continue;
                }
                // 背景まで広がった提案は素材の証拠にならない。UI と同じく、このときだけ証拠なしで導出する。
                if (proposal.floodWarning)
                    StartAnalysis(zone, null, 0, 0, "conventional (AI region spread to the background)");
                else
                    StartAnalysis(zone, proposal.maskBottomUp, proposal.width, proposal.height, "evidence");
            }
        }

        private void StartAnalysis(ColorZone zone, bool[] evidence, int evW, int evH, string how)
        {
            How[zone.name] = how;
            // 背景の解析には値のコピーを渡す。セッションから読むものはここ(メインスレッド)で取る(UI と同じ)。
            var zoneSnapshot = zone.Clone();
            var session = ZoneAutoTuner.SessionInput.From(_state);
            bool[] excluded = CombinedExclusion(_state.maskState, zone.id, out int mw, out int mh);
            var ct = _cts.Token;
            var pixels = _pixels;
            int w = _w, h = _h;
            _analysis = Task.Run(() => evidence != null
                ? ZoneAutoTuner.AnalyzeWithEvidence(pixels, w, h, zoneSnapshot, session, evidence, evW, evH, excluded, mw, mh, ct)
                : ZoneAutoTuner.Analyze(pixels, w, h, zoneSnapshot, session, excluded, mw, mh, ct), ct);
        }

        // UI の BuildCombinedExclusionForZone と同じ: 共通の除外 ∪ そのゾーンの除外。無ければ null。
        private static bool[] CombinedExclusion(MaskState ms, string zoneId, out int mw, out int mh)
        {
            var zones = new Dictionary<string, bool[]>();
            var includes = new Dictionary<string, bool[]>();
            if (!MaskStateCodec.Decode(ms, out mw, out mh, out bool[] common, zones, includes)) { mw = mh = 0; return null; }
            zones.TryGetValue(zoneId ?? "", out var zoneMask);
            int len = mw * mh;
            bool commonOk = common != null && common.Length >= len;
            bool zoneOk = zoneMask != null && zoneMask.Length >= len;
            if (!commonOk && !zoneOk) return null;
            var combined = new bool[len];
            for (int i = 0; i < len; i++)
                combined[i] = (commonOk && common[i]) || (zoneOk && zoneMask[i]);
            return combined;
        }

        private ColorZone FindZone(string id)
        {
            foreach (var z in _state.zones) if (z != null && z.id == id) return z;
            return null;
        }

        /// <summary>
        /// 調整を終えた・やめた後に呼ぶ。AI を使っていて、いろかの窓が 1 つも無ければ、推論のワーカー(GPU のメモリ)と
        /// サービスが持つ元の画素の写しを手放す。窓は閉じるときに手放す(IrocaWindow.OnDestroy)が、窓を開かずに AI だけで
        /// 使うと、次のスクリプトのリロードまで持ち続けていた。窓があれば、窓がまだ使うので手放さない。
        /// </summary>
        public void ReleaseAiIfNoWindow()
        {
            if (!_aiAvailable) return;
            if (EditingSessionRegistry.Any) return;
            AiSuggestBroker.ReleaseGpuIfUnused();
            _aiAvailable = false;
        }

        private bool AiAvailable()
        {
            if (_aiChecked) return _aiAvailable;
            _aiChecked = true;
            var svc = MaskSuggestBridge.Service;
            _aiAvailable = svc != null && svc.TryEnsureModels();
            return _aiAvailable;
        }

        /// <summary>
        /// スポイト位置の AI 提案を 1 件もらう(待ち行列は <see cref="AiSuggestBroker"/> が持つ。窓の右クリックの提案と
        /// 同じ列に並ぶ)。元のキーは窓のプレビューと同じ形なので、窓が同じテクスチャを表示していれば埋め込みを
        /// 使い回し、窓の AI 提案の元も切り替えない。
        /// </summary>
        private sealed class Evidence : IDisposable
        {
            private readonly Texture2D _texture;
            private readonly Color32[] _pixels;
            private readonly int _w, _h;
            private readonly Vector2 _uv;
            private readonly double _deadline;
            private NativePixels _native;
            private string _key;
            private bool _requested;
            private bool _received;
            private MaskSuggestProposal _proposal;

            public Evidence(Texture2D texture, Color32[] pixels, int w, int h, Vector2 uv)
            {
                _texture = texture;
                _pixels = pixels;
                _w = w;
                _h = h;
                _uv = uv;
                _deadline = EditorApplication.timeSinceStartup + EvidenceWaitSeconds;
            }

            /// <summary>終わったら true(proposal か failure のどちらか)。待ちなら false。</summary>
            public bool Poll(out MaskSuggestProposal proposal, out string failure)
            {
                proposal = null;
                failure = null;
                var svc = MaskSuggestBridge.Service;
                if (svc == null) { failure = "AI not available"; return true; }
                if (svc.Phase == MaskSuggestPhase.Error)
                {
                    failure = "AI error: " + svc.ErrorMessage;
                    return true;
                }
                if (_received)
                {
                    if (_proposal == null || _proposal.maskBottomUp == null || _proposal.width <= 0 || _proposal.height <= 0)
                    {
                        failure = "the AI returned no region";
                        return true;
                    }
                    proposal = _proposal;
                    return true;
                }
                if (EditorApplication.timeSinceStartup > _deadline)
                {
                    AiSuggestBroker.Cancel(this);
                    failure = "timed out waiting for the AI";
                    return true;
                }

                if (!_requested) Request();
                return false;
            }

            private void Request()
            {
                if (_native == null)
                {
                    _native = NativePixels.CopyOf(_pixels, _w, _h);
                    _key = CacheKey(_texture, _w, _h);
                }
                // Busy(準備中・ほかの元の要求が並んでいる)は次の Poll で求め直す。Unavailable はサービスの状態
                // (エラー)を次の Poll が見る。
                var r = AiSuggestBroker.RequestWhenIdle(this, _key, _native, _uv.x, _uv.y,
                    p => { _proposal = p; _received = true; });
                if (r == AiSuggestBroker.Request.Started) _requested = true;
            }

            public void Dispose()
            {
                AiSuggestBroker.Cancel(this);
                _native?.Release();
                _native = null;
            }
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
