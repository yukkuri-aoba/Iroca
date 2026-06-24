// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Camereo
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Camereo.DebugTools
{
    /// <summary>
    /// 1 回の ProcessPixelsArray 呼び出しで採取されたスナップショット群。
    /// zone × stage の数だけ shrink-wrapped な byte[] を保持する。
    /// 量子化(0..255) と直前ステージとの差分(128 中央) を同時に保存する。
    /// </summary>
    internal sealed class StageSnapshot
    {
        public string zoneId;
        public string stageName;
        public int width;
        public int height;
        // strength を 0..255 に量子化したもの (len = width*height)
        public byte[] strengthQuantized;
        // 直前ステージとの差分 (128 中央、±127)。最初のステージは null
        public byte[] deltaQuantized;
    }

    /// <summary>
    /// パイプライン透明化用のキャプチャ実装。
    ///
    /// <see cref="PixelProcessor.ProcessPixelsArray"/> から各段階完了時に呼ばれて
    /// strength マップやサブブランチ情報を集める。Parallel.For の中ではなく
    /// 必ずステップ間の同期点で呼ばれるためスレッド危険性なし。
    ///
    /// メモリ抑制のため strength は byte 量子化、差分も byte で持つ。
    /// 4K テクスチャ × 5 zone × 9 stage × 2 byte/pixel ≒ 360 MB が上限。
    /// </summary>
    internal sealed class DebugCaptureContext : IDebugCapture
    {
        public readonly List<StageSnapshot> Snapshots = new List<StageSnapshot>();
        // zone.id → (stageName → 直前 strength)。差分を取るための前ステージキャッシュ。
        private readonly Dictionary<string, byte[]> _previousStrengthPerZone = new Dictionary<string, byte[]>();
        // zone.id → aaMask（decontamination）。
        public readonly Dictionary<string, bool[]> AaMasks = new Dictionary<string, bool[]>();
        // zone.id → branchMap（Recolor サブブランチ）。
        public readonly Dictionary<string, byte[]> BranchMaps = new Dictionary<string, byte[]>();

        public int Width { get; private set; }
        public int Height { get; private set; }

        public void BeginCapture(int width, int height)
        {
            // 同じインスタンスを使い回すケースに備え、毎回リセットする。
            Snapshots.Clear();
            _previousStrengthPerZone.Clear();
            AaMasks.Clear();
            BranchMaps.Clear();
            Width = width;
            Height = height;
        }

        public void RecordStage(string zoneId, string stageName, float[] strength, int width, int height)
        {
            int len = width * height;
            byte[] quantized = new byte[len];
            for (int i = 0; i < len; i++)
            {
                float s = strength[i];
                if (s <= 0f) quantized[i] = 0;
                else if (s >= 1f) quantized[i] = 255;
                else quantized[i] = (byte)(s * 255f + 0.5f);
            }

            byte[] delta = null;
            if (_previousStrengthPerZone.TryGetValue(zoneId, out var prev) && prev.Length == len)
            {
                delta = new byte[len];
                for (int i = 0; i < len; i++)
                {
                    // 差分: 現在 - 直前 を 128 中央にオフセット
                    int d = quantized[i] - prev[i] + 128;
                    if (d < 0) d = 0;
                    else if (d > 255) d = 255;
                    delta[i] = (byte)d;
                }
            }

            Snapshots.Add(new StageSnapshot
            {
                zoneId = zoneId,
                stageName = stageName,
                width = width,
                height = height,
                strengthQuantized = quantized,
                deltaQuantized = delta,
            });
            _previousStrengthPerZone[zoneId] = quantized;
        }

        public void RecordDecontamination(string zoneId, bool[] aaMask, int width, int height)
        {
            // aaMask は decontamination 完了直後に渡されるので呼び出し側がそのまま再利用しない。
            // 念のため clone はせず参照を保持する（呼び出し側が以後上書きしない契約）。
            AaMasks[zoneId] = (bool[])aaMask.Clone();
        }

        public void RecordRecolorBranches(string zoneId, byte[] branchMap, int width, int height)
        {
            BranchMaps[zoneId] = (byte[])branchMap.Clone();
        }

        /// <summary>キャプチャに登場した zone id の一覧（重複排除）。</summary>
        public List<string> CollectZoneIds()
        {
            var seen = new HashSet<string>();
            var result = new List<string>();
            foreach (var snap in Snapshots)
            {
                if (seen.Add(snap.zoneId)) result.Add(snap.zoneId);
            }
            return result;
        }

        /// <summary>指定 zone × stage のスナップショットを探す。なければ null。</summary>
        public StageSnapshot FindSnapshot(string zoneId, string stageName)
        {
            foreach (var snap in Snapshots)
            {
                if (snap.zoneId == zoneId && snap.stageName == stageName) return snap;
            }
            return null;
        }
    }
}
