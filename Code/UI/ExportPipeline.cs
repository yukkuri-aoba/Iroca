// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Iroca
{
    /// <summary>
    /// 「適用して保存」（単体）と「一括適用」が共有する書き出しの手順。
    /// 原本の読み込み → （呼び出し側で ProcessPixelsArray）→ PNG 化 → 書き込み → 取り込み、の
    /// 各段をここに集める。以前は単体と一括が別々に書いており、一括だけ書き込みがアトミックで
    /// なかった（途中で落ちると既存の _recolored.png を壊す）。
    /// UI（ダイアログ・進捗・通知）は持たないので、EditMode テスト（scripts/editor-tests）から直接呼べる。
    /// </summary>
    internal static class ExportPipeline
    {
        internal enum SourceKind
        {
            /// <summary>原本ファイル（PNG/JPG）を直接デコードした。</summary>
            File,
            /// <summary>原本をデコードできず、取り込み済みテクスチャ（縮小・圧縮の影響あり）を使った。</summary>
            ImportedTexture,
            /// <summary>原本も取り込み側も読めない（Read/Write を有効にすれば後者が使える）。</summary>
            Unavailable,
        }

        /// <summary>
        /// 単体書き出しの出力先。新規ファイルなら名前を <see cref="PathUtils.SanitizeFileName"/> で直して
        /// 元と同じフォルダへ、上書きなら元の拡張子を .png に替えたパス（元が PNG なら元そのもの）。
        /// </summary>
        public static string SingleOutputPath(string srcPath, bool saveAsNewFile, string newFileName)
        {
            if (!saveAsNewFile) return Path.ChangeExtension(srcPath, ".png");
            // 区切り文字も置換するのでパストラバーサルはできない。予約名("CON" 等)や
            // 末尾の '.' / ' ' もプリセット保存と同じ規則で直す。
            string safeName = PathUtils.SanitizeFileName(newFileName, "recolored");
            return Path.Combine(Path.GetDirectoryName(srcPath), safeName + ".png");
        }

        /// <summary>一括書き出しの出力先（元と同じフォルダの &lt;元の名前&gt;_recolored.png）。</summary>
        public static string BatchOutputPath(string srcPath)
            => Path.Combine(Path.GetDirectoryName(srcPath),
                            Path.GetFileNameWithoutExtension(srcPath) + "_recolored.png");

        /// <summary>
        /// 原本の画素を読む（メインスレッド専用）。PNG/JPG はファイルを直接デコードするので
        /// import 設定（maxTextureSize・圧縮・Read/Write）の影響を受けない。PSD/TGA/EXR 等は Unity が
        /// 取り込み時に変換しているので LoadImage で読めず、取り込み済みテクスチャの画素を使う。
        /// </summary>
        public static SourceKind ReadSourcePixels(string srcPath, Texture2D imported,
                                                  out Color32[] pixels, out int width, out int height)
        {
            pixels = null; width = height = 0;
            Texture2D loadTex = null;
            try
            {
                byte[] srcBytes = File.ReadAllBytes(srcPath);
                loadTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (loadTex.LoadImage(srcBytes))
                {
                    pixels = loadTex.GetPixels32();
                    width = loadTex.width;
                    height = loadTex.height;
                    return SourceKind.File;
                }
            }
            finally
            {
                if (loadTex != null) Object.DestroyImmediate(loadTex);
            }
            if (!IrocaWindow.IsReadable(imported)) return SourceKind.Unavailable;
            pixels = imported.GetPixels32();
            width = imported.width;
            height = imported.height;
            return SourceKind.ImportedTexture;
        }

        /// <summary>
        /// 画素を PNG にする。Texture2D を介さない ImageConversion.EncodeArrayToPNG は
        /// Unity 2022.3 ではスレッドセーフなので、バックグラウンドから呼んでよい。
        /// ※ Unity 6+ ではメインスレッド必須に変わったため、移行時は呼び出し側をメインスレッドへ戻すこと。
        /// Color32[] は sRGB バイト値なので R8G8B8A8_SRGB を指定する（Texture2D(RGBA32).EncodeToPNG と同じ画素）。
        /// </summary>
        public static byte[] EncodePng(Color32[] pixels, int width, int height)
        {
            byte[] rgba = new byte[pixels.Length * 4];
            for (int i = 0; i < pixels.Length; i++)
            {
                int o = i * 4;
                rgba[o]     = pixels[i].r;
                rgba[o + 1] = pixels[i].g;
                rgba[o + 2] = pixels[i].b;
                rgba[o + 3] = pixels[i].a;
            }
            byte[] png = ImageConversion.EncodeArrayToPNG(
                rgba, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height);
            if (png == null || png.Length == 0)
                throw new System.Exception("EncodeArrayToPNG が空のデータを返しました");
            return png;
        }

        /// <summary>
        /// PNG を書き込み、Assets 配下なら取り込む（メインスレッド専用）。書き込みはアトミック
        /// （書き潰す相手が元テクスチャそのものであり得るので、途中で落ちても原本を失わない）。
        /// <paramref name="inheritImportSettings"/> なら取り込み前に元の .meta を写す。
        /// 戻り値は出力の Assets 相対パス（Assets の外へ書いたときは null）。
        /// </summary>
        public static string WriteAndImport(string outputPath, byte[] png, string srcPath, bool inheritImportSettings)
        {
            AtomicFile.WriteAllBytes(outputPath, png);
            string rel = PathUtils.ToAssetsRelativeOrNull(outputPath);
            if (rel == null) return null;
            if (inheritImportSettings)
            {
                string srcRel = PathUtils.ToAssetsRelativeOrNull(srcPath);
                if (srcRel != null) PreApplyImportSettings(srcRel, rel);
            }
            AssetDatabase.ImportAsset(rel);
            return rel;
        }

        // .meta ファイルをインポート前に書き込んでおくことで、
        // ImportAsset の 1 回の圧縮パスで正しい設定が適用される（SaveAndReimport 不要）。
        internal static void PreApplyImportSettings(string srcRelPath, string dstRelPath)
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            string sep  = Path.DirectorySeparatorChar.ToString();
            string srcMeta = Path.Combine(root, srcRelPath.Replace("/", sep)) + ".meta";
            string dstMeta = Path.Combine(root, dstRelPath.Replace("/", sep)) + ".meta";

            if (!File.Exists(srcMeta)) return;

            string content = File.ReadAllText(srcMeta, System.Text.Encoding.UTF8);

            // 上書きなら既存 GUID を維持、新規ファイルなら新 GUID を生成
            string guid = AssetDatabase.AssetPathToGUID(dstRelPath);
            if (string.IsNullOrEmpty(guid))
                guid = System.Guid.NewGuid().ToString("N");

            content = Regex.Replace(content, @"(?m)^guid: [0-9a-f]+$", $"guid: {guid}");
            File.WriteAllText(dstMeta, content, System.Text.Encoding.UTF8);
        }

        /// <summary>
        /// 2 つのパスが同一ファイルを指すか。書き出し先がプレビューのソース自身かの判定に使う。
        /// AssetDatabase のパスは '/' 区切り、Path.Combine は '\' を混ぜるため、素の文字列比較では
        /// 取りこぼす。フルパスへ正規化してから比較する。
        /// </summary>
        public static bool IsSameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                    System.StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
