// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 自動化 API(MCP)のマスク操作。プレビューの右クリックと同じ操作(ここも塗る・ここは塗らない・この部分だけ塗る・
    /// ここのマスクを消す)を、編集状態のマスクへ順に当てる。範囲は右クリックと同じく AI の提案かメッシュの UV の島で、
    /// 当てる規則は <see cref="MaskWorkspace.ApplyRegion"/>、提案の転写は <see cref="ProposalMaskRegion"/> を共有する。
    /// AI の提案は EditorApplication.update で進むので、メインスレッドの <see cref="Advance"/> で進める
    /// (<see cref="AutomationAutoTune"/> と同じ。update と MCP の問い合わせのたび)。
    /// </summary>
    internal sealed unsafe class AutomationMaskEdits {
        internal enum By { Ai, Island, OtherMeshes }

        /// <summary>確かめ終わった 1 件の操作。</summary>
        internal sealed class Edit {
            public string zoneId;     // null = 全ゾーン共通
            public string zoneName;   // 結果に出す名前(共通は空)
            public MaskRegionOp op;
            public string opName;     // 依頼に書かれた名前(結果に返す)
            public By by;
            public string byName;
            public MaskSuggestGranularity size;
            public Vector2 uv;
        }

        private readonly IrocaSessionState _state;
        private readonly Texture2D _texture;
        private readonly List<Edit> _edits;
        private readonly List<string> _warnings;
        private readonly List<IrocaAutomation.MaskEditInfo> _results;
        private readonly string _key;
        private NativePixels _pixels;
        private MaskWorkspace _masks;
        private UvChartMap _map;
        private MeshUvLocator.Result _found;
        private int _index;
        private bool _requested;
        private bool _askedAi;
        private double _deadline;

        /// <summary>続けられない失敗の文言(無ければ null)。呼び出し側は結果を保存せずに返す。</summary>
        public string Error { get; private set; }
        public bool Done => _index >= _edits.Count || Error != null;
        public int Total => _edits.Count;
        public int Finished => _index;


        /// <param name="pixels">作業解像度の原本(下原点)。参照を足して持つ。終わるまで書き換えないこと。</param>
        public AutomationMaskEdits(IrocaSessionState state, Texture2D texture, NativePixels pixels, List<Edit> edits,
            List<string> warnings, List<IrocaAutomation.MaskEditInfo> results) {
            _state = state;
            _texture = texture;
            _pixels = pixels.AddRef();
            _key = AutomationAutoTune.CacheKey(texture, pixels.Width, pixels.Height);
            _edits = edits;
            _warnings = warnings;
            _results = results;
            if (Done) {
                Release();
            }
        }

        /// <summary>進められるところまで進める(提案の待ちに入ったら返る)。メインスレッドから呼ぶ。</summary>
        public void Advance() {
            while (!Done) {
                var edit = _edits[_index];
                if (edit.by == By.Ai) {
                    // 待ちに入ったら抜ける。提案がその場で届いて失敗した(Error)ときも、下の後始末を通す
                    // (以前は return で抜けて、原本の参照を持ったままだった)。
                    if (!AdvanceAi(edit)) {
                        break;
                    }
                } else {
                    ApplyMesh(edit);
                }
                if (Error == null) {
                    Next();
                }
            }
            if (Error != null) {
                Release();
            }
        }

        public void Cancel() {
            AiSuggestBroker.Cancel(this);
            _index = _edits.Count;
            Release();
        }

        /// <summary>
        /// 操作を終えた・やめた後に呼ぶ。AI を使っていて、いろかの窓が 1 つも無ければ、推論のワーカー(GPU のメモリ)と
        /// サービスが持つ元の画素の写しを手放す(<see cref="AutomationAutoTune.ReleaseAiIfNoWindow"/> と同じ)。
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

        // 1 件進める。この件が終わった(結果か失敗が決まった)なら true、提案を待つなら false。
        private bool AdvanceAi(Edit edit) {
            if (_requested) {
                if (EditorApplication.timeSinceStartup < _deadline) {
                    return false;
                }
                AiSuggestBroker.Cancel(this);
                Error = $"timed out waiting for the AI at [{edit.uv.x}, {edit.uv.y}].";
                return true;
            }
            if (_deadline <= 0d) {
                _deadline = EditorApplication.timeSinceStartup + AutoTuneService.EvidenceWaitSeconds;
            }
            if (!_askedAi) {
                AiSuggestBroker.RetryAfterError();
            }
            _askedAi = true;
            bool finished = false;
            var r = AiSuggestBroker.RequestWhenIdle(this, _key, _pixels, edit.uv.x, edit.uv.y, proposal => {
                OnProposal(edit, proposal);
                finished = true;
                _requested = false;
                _deadline = 0d;
                if (Error == null) {
                    Next();
                }
            }, edit.size);
            switch (r) {
                case AiSuggestBroker.Request.Started:
                    // 提案がその場で届いたら、次の件は受け取り先が進めてある。
                    _requested = !finished;
                    return false;
                case AiSuggestBroker.Request.Busy:
                    if (EditorApplication.timeSinceStartup >= _deadline) {
                        Error = "timed out waiting for the AI to get ready.";
                        return true;
                    }
                    return false;
                default:
                    Error = "the AI is not available (Unity Sentis and the model are needed, or it reported an error). "
                        + "Use by:'island' to select by the mesh's UV island instead.";
                    return true;
            }
        }

        private void OnProposal(Edit edit, MaskSuggestProposal proposal) {
            if (proposal == null || proposal.mask == null) {
                // AI のエラーで捨てられたのなら、その理由を出す(AutoTuneService の証拠の受け取りと同じ)。受理した直後に
                // 同期でエラーになると(Sentis のデコーダが投げたなど)、ここへその場で null が届く。
                var svc = MaskSuggestBridge.Service;
                if (svc != null && svc.Phase == MaskSuggestPhase.Error && !string.IsNullOrEmpty(svc.ErrorMessage)) {
                    Error = $"the AI reported an error at [{edit.uv.x}, {edit.uv.y}]: {svc.ErrorMessage}";
                } else {
                    Error = $"the AI returned nothing at [{edit.uv.x}, {edit.uv.y}].";
                }
                return;
            }
            EnsureMasks();
            var region = ProposalMaskRegion.ToMaskSize(proposal.mask, _masks.Width, _masks.Height, _texture, out bool owned, out _);
            try {
                int count = region.CountTrue();
                // 空の提案で「この部分だけ」を当てると全体が除外になるので、当てずに止める(右クリックと同じ)。
                if (count == 0) {
                    Error = $"the AI returned an empty region at [{edit.uv.x}, {edit.uv.y}]; try another point or size.";
                    return;
                }
                if (proposal.floodWarning) {
                    _warnings.Add($"the AI region at [{edit.uv.x}, {edit.uv.y}] may have spread to the background; "
                        + "check the preview, or retry with size:'fine'.");
                }
                ApplyRegion(edit, region, count);
            } finally {
                if (owned) {
                    region.Release();
                }
            }
        }

        private void ApplyMesh(Edit edit) {
            EnsureMasks();
            var map = Map();
            if (map == null) {
                Error = "no readable mesh uses this texture, so by:'" + edit.byName + "' cannot be used "
                    + "(put the avatar in the scene, or enable Read/Write on the model).";
                return;
            }
            PreviewCoords.UvToPixel(edit.uv.x, edit.uv.y, map.Width, map.Height, out int x, out int y);
            int radius = map.DefaultPaddingRadius;
            int chart = map.ChartAt(x, y, radius);
            if (chart < 0) {
                Error = $"no UV island at [{edit.uv.x}, {edit.uv.y}].";
                return;
            }
            var region = edit.by == By.Island
                ? map.MaskOf(k => k == chart, radius)
                : map.ExcludeOtherSources(new[] { map.ChartSource[chart] }, radius);
            try { ApplyRegion(edit, region, region.CountTrue()); } finally { region.Release(); }
        }

        private void ApplyRegion(Edit edit, NativeBools region, int regionPixels) {
            if (!_masks.ApplyRegion(region.Ptr, edit.zoneId, edit.op, null, out var result)) {
                Error = $"mask operation '{edit.opName}' could not be applied.";
                return;
            }
            _results.Add(new IrocaAutomation.MaskEditInfo {
                zone = edit.zoneName,
                op = edit.opName,
                by = edit.byName,
                at = new[] { edit.uv.x, edit.uv.y },
                regionFraction = (float)regionPixels / (_masks.Width * (float)_masks.Height),
                changedPixels = result.changed,
            });
            string where = $"mask operation '{edit.opName}' at [{edit.uv.x}, {edit.uv.y}]";
            if (result.changed == 0) {
                _warnings.Add($"{where} changed nothing (already in that state, or no mask there to clear).");
            }
            if (result.commonOverlap > 0) {
                _warnings.Add($"{where} overlaps a mask common to all zones, which still keeps that area unpainted "
                    + "(remove it with op:'clear', common:true).");
            }
            if (edit.op == MaskRegionOp.OnlyThisPart) {
                var zone = _state.zones.Find(z => z != null && z.id == edit.zoneId);
                if (zone != null && zone.tolerance <= 0f) {
                    _warnings.Add($"zone '{zone.name}' has tolerance 0, so nothing inside the part is selected by color.");
                }
            }
        }

        private void Next() {
            _index++;
            if (_index < _edits.Count) {
                return;
            }
            // 全部当てたら保存形式へ書く(続く自動調整も、最後の保存もこれを読む)。失敗した依頼は保存しないので書かない。
            if (_masks != null) {
                _state.maskState ??= new MaskState();
                _masks.EncodeTo(_state.maskState);
            }
            Release();
        }

        // マスクの土台を用意する。窓の EnsureMasks と同じ規則: 寸法は元画像の寸法で、合わない既存のマスクは写し直す。
        private void EnsureMasks() {
            if (_masks != null) {
                return;
            }
            int fileW = 0, fileH = 0;
            if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(_texture)) is TextureImporter ti) {
                ti.GetSourceTextureWidthAndHeight(out fileW, out fileH);
            }
            MaskRegionEdit.CanvasSize(fileW, fileH, _texture.width, _texture.height, out int w, out int h);
            _masks = new MaskWorkspace();
            if (_state.maskState != null) {
                _masks.DecodeFrom(_state.maskState);
            }
            if (_masks.Width != w || _masks.Height != h) {
                if (_masks.HasLayers && _masks.Width > 0 && _masks.Height > 0) {
                    _masks.Rescale(w, h);
                } else {
                    _masks.Reset(w, h);
                }
            }
        }

        // チャート地図(ヒープの外)。右クリックと同じく、マスクの寸法で作り、にじみ代を割り当てる。
        private UvChartMap Map() {
            if (_map != null) {
                return _map;
            }
            _found ??= MeshUvLocator.FindForTexture(_texture);
            if (_found == null || _found.found.Count == 0) {
                return null;
            }
            _map = UvChartMap.Build(_masks.Width, _masks.Height, _found.Sources());
            _map.AssignPaddingBaked(_map.DefaultPaddingRadius);
            return _map;
        }

        private void Release() {
            var px = _pixels;
            _pixels = null;
            px?.Release();
            _map?.Dispose();
            _map = null;
            _masks?.Clear();
            _masks = null;
        }
    }
}
