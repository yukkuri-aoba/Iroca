// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 保存済みの編集状態(<see cref="IrocaSessionState"/>: ゾーン・処理設定・マスク)を
    /// そのまま画素へ適用する。UI を持たない経路(非破壊ビルド)の入口で、「適用して保存」と
    /// 同じ並び(有効なゾーンだけ・並び順のまま・複製)と同じ設定で
    /// <see cref="PixelProcessor.ProcessPixelsArray"/> を呼ぶ。マスクは保存形式から
    /// <see cref="MaskStateCodec.ToSnapshot"/> で作り直す(編集画面と同じ組み立て規則)。
    /// Texture2D も UnityEngine.Object も触らないので、バックグラウンドスレッドから呼んでよい。
    /// </summary>
    internal static class SessionRecolor
    {
        // 適用対象の選び方(EnabledZoneCopies と CountEnabled で共有する)。
        private static bool IsApplied(ColorZone z) => z != null && z.enabled;

        /// <summary>
        /// 有効なゾーンを並び順のまま複製して返す。先頭ほど先に処理され、重なった領域を占有する。
        /// 複製するのは、処理中に元のゾーンが編集(自動調整・Undo)されても新旧が混ざらないようにするため
        /// (Clone は値等価コピーで出力は変わらない)。
        /// </summary>
        public static List<ColorZone> EnabledZoneCopies(IEnumerable<ColorZone> zones)
        {
            var list = new List<ColorZone>();
            if (zones == null) return list;
            foreach (var z in zones)
                if (IsApplied(z)) list.Add(z.Clone());
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

        /// <summary>
        /// 画素をその場で再着色する。有効なゾーンが無ければ何もせず false を返す。
        /// </summary>
        public static bool Apply(Color32[] pixels, int width, int height,
            IrocaSessionState state, CancellationToken cancellationToken = default)
        {
            if (state == null) return false;
            var zones = EnabledZoneCopies(state.zones);
            if (zones.Count == 0) return false;
            var masks = MaskStateCodec.ToSnapshot(state.maskState);
            PixelProcessor.ProcessPixelsArray(pixels, width, height, masks, zones,
                RecolorSettings.From(state), cancellationToken);
            return true;
        }
    }
}
