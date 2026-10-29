// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Linq;

namespace Iroca
{
    /// <summary>
    /// プリセット(<see cref="IrocaPresetData"/>)を編集状態へ写す規則(0.4.0 の計画の 4-3)。UI の読み込み
    /// (<see cref="EditingSession.ApplyPreset"/>)と自動化(MCP の色替え)が同じものを使う。
    /// 編集モード(かんたん/通常/上級)は UI 表示の好みであって色設定ではないので写さない。
    /// </summary>
    internal static class PresetApplier
    {
        /// <summary>
        /// プリセットのゾーン(古い優先順位を並び順へ直し、id を振る)と処理設定を <paramref name="state"/> へ写す。
        /// ゾーンのリストはプリセットのものをそのまま使う(読み込んだプリセットは使い捨て)。
        /// </summary>
        internal static void ApplyZonesAndSettings(IrocaPresetData preset, IrocaSessionState state)
        {
            // Unity の JsonUtility は JSON に含まれないフィールドを「既定値で上書き」ではなく
            // 「クラスのフィールド初期化子の値を保持」するため、ここで `> 0 ? : default` のような
            // defaulting を行うとユーザーが明示的に 0 を保存したケース（UI レンジに 0 を含む
            // antiAliasCleanup や holeFillPasses）を不当に書き換えてしまう。
            // 旧バージョンとの後方互換は IrocaPresetData の初期化子側に寄せる。
            state.zones = preset.zones ?? new List<ColorZone>();
            MigrateLegacyLayerPriority(state.zones);
            foreach (var z in state.zones) z?.EnsureId();
            RecolorSettings.From(preset).CopyTo(state);
        }

        /// <summary>
        /// プリセットのマスク（RLE + Base64）を保存形式の <see cref="MaskState"/> へ写す。どちらも同じ符号化なので
        /// 文字列はそのまま渡し、寸法の合わない項目・壊れた項目は展開(<see cref="MaskStateCodec"/>・
        /// <see cref="MaskWorkspace.DecodeFrom"/>)で読み飛ばされる。マスクが無ければ空の MaskState（寸法 0 ＝マスクなし）。
        /// </summary>
        internal static MaskState MaskStateOf(IrocaPresetData preset)
        {
            var ms = new MaskState { schemaVersion = MaskState.CurrentSchemaVersion };
            if (!HasMasks(preset)) return ms;
            ms.width = preset.maskWidth;
            ms.height = preset.maskHeight;
            ms.commonMaskBase64 = preset.commonMaskBase64 ?? "";
            CopyMaskEntries(preset.zoneMasks, ms.zones);
            // 含めるマスク(v2)。旧プリセットでは欠落フィールド=空リスト。
            CopyMaskEntries(preset.zoneIncludeMasks, ms.zoneIncludes);
            return ms;
        }

        /// <summary>プリセットがマスクを持つ(寸法が書かれている)か。</summary>
        internal static bool HasMasks(IrocaPresetData preset) => preset.maskWidth > 0 && preset.maskHeight > 0;

        private static void CopyMaskEntries(List<ZoneMaskEntry> src, List<MaskZoneEntry> dst)
        {
            if (src == null) return;
            foreach (var e in src)
                if (e != null && !string.IsNullOrEmpty(e.zoneId))
                    dst.Add(new MaskZoneEntry { zoneId = e.zoneId, maskBase64 = e.maskBase64 ?? "" });
        }

        /// <summary>
        /// 旧バージョン互換の一度きり移行。かつて優先度は layerIndex(数値・大きいほど
        /// 重なりで上に来る)で表していたが、現在は「リストの並び順＝優先度(先頭が最優先)」
        /// に変更した。layerIndex に差異を持つ旧プリセットだけを降順で安定ソートし、
        /// 旧来の勝敗(大きい layerIndex が勝つ → 先頭が勝つ)を再現する。
        /// 全ゾーンが同値(既定 0 を含む)なら並び順は既に意図どおりなので何もしない。
        /// </summary>
        internal static void MigrateLegacyLayerPriority(List<ColorZone> zones)
        {
            if (zones == null || zones.Count < 2) return;
            int first = zones[0].layerIndex;
            bool allSame = true;
            for (int i = 1; i < zones.Count; i++)
            {
                if (zones[i].layerIndex != first) { allSame = false; break; }
            }
            if (allSame) return;
            var migrated = zones.OrderByDescending(z => z.layerIndex).ToList();
            zones.Clear();
            zones.AddRange(migrated);
        }
    }
}
