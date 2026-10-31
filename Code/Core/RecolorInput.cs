// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;

namespace Iroca
{
    /// <summary>
    /// 色替えの入力(適用するゾーン・マスク・全体設定)を組み立てる唯一の場所(0.4.0 の計画の 4-2 の 2)。
    /// プレビュー・書き出し・一括書き出しは編集中の状態から(<see cref="FromEditing"/>)、非破壊ビルドと自動化は
    /// 保存済みの状態から(<see cref="FromSaved"/>。<see cref="SessionRecolor"/> が使う)組み立てる。
    /// どちらも「有効なゾーンだけを並び順のまま複製し、セッションの全体設定で処理する」という同じ選び方を通す
    /// (以前は経路ごとに自前で組み立てていて、一括書き出しだけ複製していなかった)。
    /// 計画では Application の層だったが、非破壊ビルドの土台(Infra の RecipeTextureBuilder)とその下の
    /// <see cref="SessionRecolor"/>(Core)からも使うので Core に置いた。Core の型だけで組み立てる。
    /// 原本の画素は入れない(読み方が経路ごとに違う: プレビューは SourcePixelCache、書き出しは ExportPipeline)。
    /// </summary>
    internal sealed class RecolorInput
    {
        /// <summary>適用するゾーン(有効なものを並び順のまま複製したもの。先頭ほど先に処理し、重なりを占有する)。</summary>
        public readonly List<ColorZone> Zones;
        /// <summary>除外・含めるマスク(無ければ null)。</summary>
        public readonly MaskSnapshot Masks;
        public readonly RecolorSettings Settings;

        private RecolorInput(List<ColorZone> zones, MaskSnapshot masks, in RecolorSettings settings)
        {
            Zones = zones;
            Masks = masks;
            Settings = settings;
        }

        /// <summary>マスクの参照を返す(この入力の持ち主が、使い終わったら 1 回呼ぶ)。</summary>
        public void ReleaseMasks() => Masks?.Release();

        /// <summary>適用するゾーンがあるか(無ければ色替えしない)。</summary>
        public bool HasZones => Zones.Count > 0;

        // 適用対象の選び方(EnabledZoneCopies と CountEnabled で共有する)。
        private static bool IsApplied(ColorZone z) => z != null && z.enabled;

        /// <summary>
        /// 編集中の状態から組み立てる。masks は編集中のマスク(ウィンドウのマスクの写し)。only を渡すと、そのゾーンが
        /// 適用対象ならそれだけにする(ソロ表示。プレビュー専用で、書き出しは常に全部を適用する)。
        /// </summary>
        public static RecolorInput FromEditing(IrocaSessionState session, MaskSnapshot masks, ColorZone only = null)
        {
            var zones = EnabledZoneCopies(session.zones, only);
            return new RecolorInput(zones, masks, RecolorSettings.From(session));
        }

        /// <summary>
        /// 保存済みの状態から組み立てる。マスクは保存形式から <see cref="MaskStateCodec.ToSnapshot"/> で作り直す
        /// (編集画面と同じ組み立て規則)。適用するゾーンが無ければ null(マスクを作り直さない)。
        /// </summary>
        public static RecolorInput FromSaved(IrocaSessionState state)
        {
            if (state == null) return null;
            var zones = EnabledZoneCopies(state.zones);
            if (zones.Count == 0) return null;
            return new RecolorInput(zones, MaskStateCodec.ToSnapshot(state.maskState), RecolorSettings.From(state));
        }

        /// <summary>
        /// 有効なゾーンを並び順のまま複製して返す(only を渡すと、それと同じものだけ)。
        /// 複製するのは、処理中に元のゾーンが編集(自動調整・Undo)されても新旧が混ざらないようにするため
        /// (Clone は値等価コピーで出力は変わらない)。
        /// </summary>
        public static List<ColorZone> EnabledZoneCopies(IEnumerable<ColorZone> zones, ColorZone only = null)
        {
            var list = new List<ColorZone>();
            if (zones == null) return list;
            foreach (var z in zones)
                if (IsApplied(z) && (only == null || ReferenceEquals(z, only))) list.Add(z.Clone());
            return list;
        }

        /// <summary>適用対象(<see cref="EnabledZoneCopies"/> が返すのと同じゾーン)の数。複製しない。</summary>
        public static int CountEnabled(IEnumerable<ColorZone> zones)
        {
            if (zones == null) return 0;
            int n = 0;
            foreach (var z in zones)
                if (IsApplied(z)) n++;
            return n;
        }
    }
}
