// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;

namespace Iroca
{
    /// <summary>
    /// 保存形式のマスク(<see cref="MaskState"/>: RLE + Base64)と作業用 bool[] の相互変換、
    /// および保存形式から処理用スナップショットを作る手順。
    /// 編集画面(MaskPaintView)の保存・復元と、UI を持たない経路(非破壊ビルド・ハーネス)が
    /// 同じ規則を通るよう、ここにだけ書く。
    /// </summary>
    internal static class MaskStateCodec
    {
        /// <summary>
        /// 作業用マスクを保存形式へ書き込む。全 false のマスクは書かない
        /// (共通は空文字、ゾーン別は項目ごと省く。ゾーン id の無い項目も省く)。
        /// </summary>
        public static void Encode(MaskState ms, int width, int height, bool[] common,
            IEnumerable<KeyValuePair<string, bool[]>> zones,
            IEnumerable<KeyValuePair<string, bool[]>> includes)
        {
            ms.width = width;
            ms.height = height;
            ms.commonMaskBase64 = MaskSnapshot.AnyTrue(common)
                ? MaskRle.Encode(common, width, height)
                : "";

            if (ms.zones == null) ms.zones = new List<MaskZoneEntry>();
            ms.zones.Clear();
            AppendEntries(ms.zones, zones, width, height);

            if (ms.zoneIncludes == null) ms.zoneIncludes = new List<MaskZoneEntry>();
            ms.zoneIncludes.Clear();
            AppendEntries(ms.zoneIncludes, includes, width, height);
        }

        /// <summary>
        /// 保存形式を作業用マスクへ展開する。寸法が無効なら false(マスクなし)を返し、何も足さない。
        /// 壊れた項目・寸法の合わない項目・ゾーン id の無い項目は読み飛ばす。
        /// 辞書へは追加だけ行う(空にするのは呼び出し側)。
        /// 旧 JSON(v1)は zoneIncludes が欠落 = 空なので、含めるマスクが何も入らないだけになる。
        /// </summary>
        public static bool Decode(MaskState ms, out int width, out int height, out bool[] common,
            IDictionary<string, bool[]> zones, IDictionary<string, bool[]> includes)
        {
            width = height = 0;
            common = null;
            if (ms == null || ms.width <= 0 || ms.height <= 0) return false;
            width = ms.width;
            height = ms.height;

            if (!string.IsNullOrEmpty(ms.commonMaskBase64))
                common = DecodeSized(ms.commonMaskBase64, width, height);
            ReadEntries(ms.zones, zones, width, height);
            ReadEntries(ms.zoneIncludes, includes, width, height);
            return true;
        }

        /// <summary>
        /// 保存形式から処理用スナップショットを作る。マスクが無ければ null
        /// (<see cref="PixelProcessor.ProcessPixelsArray"/> は null を「マスクなし」として扱う)。
        /// </summary>
        public static MaskSnapshot ToSnapshot(MaskState ms)
        {
            var zones = new Dictionary<string, bool[]>();
            var includes = new Dictionary<string, bool[]>();
            if (!Decode(ms, out int w, out int h, out bool[] common, zones, includes)) return null;
            return MaskSnapshot.FromBuffers(w, h, common, zones, includes);
        }

        private static void AppendEntries(List<MaskZoneEntry> dst,
            IEnumerable<KeyValuePair<string, bool[]>> src, int width, int height)
        {
            if (src == null) return;
            foreach (var kv in src)
            {
                if (string.IsNullOrEmpty(kv.Key) || !MaskSnapshot.AnyTrue(kv.Value)) continue;
                dst.Add(new MaskZoneEntry
                {
                    zoneId = kv.Key,
                    maskBase64 = MaskRle.Encode(kv.Value, width, height),
                });
            }
        }

        private static void ReadEntries(List<MaskZoneEntry> src,
            IDictionary<string, bool[]> dst, int width, int height)
        {
            if (src == null || dst == null) return;
            foreach (var entry in src)
            {
                if (entry == null || string.IsNullOrEmpty(entry.zoneId)) continue;
                var arr = DecodeSized(entry.maskBase64, width, height);
                if (arr != null) dst[entry.zoneId] = arr;
            }
        }

        // 寸法が保存形式の width/height と一致したときだけ採る(全レイヤー同一キャンバスが前提)。
        private static bool[] DecodeSized(string encoded, int width, int height)
        {
            var arr = MaskRle.Decode(encoded, out int w, out int h);
            return (arr != null && w == width && h == height) ? arr : null;
        }
    }
}
