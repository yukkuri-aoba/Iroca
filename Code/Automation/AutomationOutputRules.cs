// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 自動化(MCP の iroca_recolor・iroca_inspect、静的 API・batchmode の CLI)の安全の決まり。どの経路もここを呼ぶ
    /// (以前は MCP と静的 API で 2 系統あり、決まりが食い違っていた)。
    ///   - 読み書きするのはプロジェクトの下だけ。書き出すのは Assets/ の下の .png だけで、元のテクスチャそのものへは
    ///     置き換えの許しがあっても書かない。
    ///   - 既にあるファイルは、置き換えの許し(overwrite)があるときだけ置き換える。
    ///   - 比較画像は UserSettings/Iroca/mcp/preview に置く(Assets に増やさない。取り込みもしない)。
    ///   - 依頼の [u, v] は 0..1 の中だけ(NaN も断る)。
    /// ゾーンと全体設定の値の範囲は Core の <see cref="RecolorValueRanges"/> が受け持つ。
    /// </summary>
    internal static class AutomationOutputRules {
        /// <summary>
        /// 自動化が書く作業ファイルの置き場(比較画像・調べた縮小画像・CLI の結果)。git の追跡しない UserSettings の下に置き、
        /// Assets の取り込みの揺れを避ける。
        /// </summary>
        internal static string McpDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "UserSettings/Iroca/mcp"));

        /// <summary>
        /// PNG の出力先を決めて確かめる。エラーなら文言、よければ null と出力先の絶対パス。
        /// 既にファイルがあれば、<paramref name="overwrite"/> が無い限り断る。既定の出力先は「適用して保存」の既定と同じ名前なので、
        /// 前に書き出してマテリアルに差している画像を黙って置き換えると、それを使うアバターの見た目まで変わる
        /// (UI の書き出しは同じ場面で確認を出す)。
        /// </summary>
        /// <param name="output">頼まれた出力先(空なら <paramref name="defaultOutput"/>)。</param>
        /// <param name="defaultOutput">既定の出力先。null なら出力先は必須。</param>
        /// <param name="sourceAbs">元のテクスチャの絶対パス(これと同じ所へは書かない)。null なら確かめない。</param>
        /// <param name="overwriteHint">既にあるときの案内に入れる、置き換えの許しの渡し方(MCP は "overwrite:true")。</param>
        internal static string ResolvePngOutput(string output, string defaultOutput, bool overwrite, string sourceAbs,
            string overwriteHint, out string outAbs) {
            outAbs = null;
            bool given = !string.IsNullOrWhiteSpace(output);
            if (!given && defaultOutput == null) {
                return "output path is empty.";
            }
            string path = given ? output.Trim() : defaultOutput;
            outAbs = ResolveOutputPath(path);
            string rel = outAbs != null ? PathUtils.ToAssetsRelativeOrNull(outAbs) : null;
            if (rel == null) {
                outAbs = null;
                return given
                    ? $"'output' must be a .png path under Assets/: {output}"
                    : $"the default output ({path}) is outside Assets/; pass 'output' (a .png path under Assets/).";
            }
            // 出力先が元のテクスチャそのものだと、色替えの結果で原本を上書きして戻せなくなる。UI の「上書き保存」は
            // 明示の操作+確認+Undo があるが、自動化には何も無い。比べ方は大小無視(取りこぼして原本を壊すより断る)。
            if (sourceAbs != null && string.Equals(outAbs, sourceAbs, StringComparison.OrdinalIgnoreCase)) {
                outAbs = null;
                return "output must differ from the source texture "
                     + $"(recoloring in place would destroy the original irreversibly): {rel}";
            }
            if (!overwrite && File.Exists(outAbs)) {
                outAbs = null;
                return $"{rel} already exists (materials or avatars may use it). Ask the user before replacing it, "
                     + $"then call again with {overwriteHint}; or pass another 'output'.";
            }
            return null;
        }

        /// <summary>
        /// 比較画像(左=前 / 中=後 / 右=変わった画素)の置き場。テクスチャごとに 1 枚(呼ぶたびに増えない)で、Assets の外。
        /// </summary>
        internal static string PreviewPanelPath(string textureAssetPath) {
            string stem = Path.GetFileNameWithoutExtension(textureAssetPath);
            string guid = string.IsNullOrEmpty(textureAssetPath) ? "" : AssetDatabase.AssetPathToGUID(textureAssetPath);
            return Path.Combine(McpDir, "preview", $"{stem}_{(string.IsNullOrEmpty(guid) ? "x" : guid.Substring(0, 8))}.png");
        }

        /// <summary>
        /// 依頼の [u, v](左下原点、0..1)を読む。無い・短い・範囲の外・NaN なら false。マスク操作・changes の at・
        /// iroca_inspect の points で共有する。JsonUtility は JSON の素の NaN を NaN のまま読むので、比べ方は「範囲の中か」
        /// にして NaN も落とす(以前の「&lt; 0 か &gt; 1」は NaN を通し、(int)NaN が端へ丸められて別の場所の色を拾った)。
        /// </summary>
        internal static bool TryReadUv(float[] at, out Vector2 uv) {
            uv = new Vector2(-1f, -1f);
            if (at == null || at.Length < 2 || !IsUnitInterval(at[0]) || !IsUnitInterval(at[1])) {
                return false;
            }
            uv = new Vector2(at[0], at[1]);
            return true;
        }

        /// <summary>0..1 の中か(NaN は false)。</summary>
        internal static bool IsUnitInterval(float x) => x >= 0f && x <= 1f;

        /// <summary>
        /// 出力パスを絶対パスへ解決する(無くてよい)。プロジェクトの下の .png だけ(自動化からの範囲外への書き込みと、
        /// 画像でないファイルを作るのを防ぐ)。満たさなければ null。
        /// </summary>
        internal static string ResolveOutputPath(string path) {
            if (string.IsNullOrEmpty(path)) {
                return null;
            }
            string abs = ToProjectAbsolute(path);
            if (abs == null || !IsWithinProjectRoot(abs)) {
                return null;
            }
            if (!abs.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) {
                return null;
            }
            return abs;
        }

        /// <summary>絶対パスはそのまま、相対パスはプロジェクトのルートから絶対パスにする。できなければ null。</summary>
        internal static string ToProjectAbsolute(string path) {
            try {
                string projRoot = Path.GetDirectoryName(Application.dataPath);
                return Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(projRoot, path));
            } catch { return null; }
        }

        /// <summary>絶対パスがプロジェクトのルートの下か(パスのたどり直しや範囲外の読み書きを防ぐ)。</summary>
        internal static bool IsWithinProjectRoot(string absPath) {
            if (string.IsNullOrEmpty(absPath)) {
                return false;
            }
            string projRoot;
            try { projRoot = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath)); } catch { return false; }
            // 末尾に区切りを付けて、"Proj" と "ProjectX" の前方一致の取り違えを防ぐ。
            string rootWithSep = projRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return absPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        }
    }
}
