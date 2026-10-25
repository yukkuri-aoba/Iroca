// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Text.RegularExpressions;

namespace Iroca
{
    /// <summary>
    /// Unity の .meta(テキスト)の書き換え。書き出しで元テクスチャのインポート設定を引き継ぐとき
    /// (<see cref="ExportPipeline"/>)に、元の .meta の GUID を書き出し側の GUID へ差し替える。
    /// UnityEditor に依存しないので scripts/unit-run で確かめる。
    /// </summary>
    internal static class MetaFile
    {
        // 行頭の「guid: <16 進>」(アセット自身の GUID)。入れ子の参照(  - {fileID: …, guid: …})は行頭に来ない。
        // 行末は CRLF でもよい(Windows の Git が改行を CRLF にして取り出した .meta)。以前は $ の直前に \r が
        // あると一致せず、元の GUID のまま写していた(上書きした書き出し画像に Unity が新しい GUID を振り、
        // マテリアルから外れる)。
        private static readonly Regex s_guidLine = new Regex(@"^guid: [0-9a-f]+(?=\r?$)", RegexOptions.Multiline);

        /// <summary>
        /// <paramref name="metaText"/> のアセット自身の GUID を <paramref name="guid"/> に差し替える。
        /// 改行など、ほかの部分はそのまま。GUID の行が見つからなければ false(<paramref name="replaced"/> は null)。
        /// </summary>
        internal static bool TryReplaceGuid(string metaText, string guid, out string replaced)
        {
            replaced = null;
            if (string.IsNullOrEmpty(metaText) || string.IsNullOrEmpty(guid)) return false;
            if (!s_guidLine.IsMatch(metaText)) return false;
            replaced = s_guidLine.Replace(metaText, "guid: " + guid, 1);
            return true;
        }
    }
}
