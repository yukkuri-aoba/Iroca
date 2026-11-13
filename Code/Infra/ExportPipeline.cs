// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Iroca {
    /// <summary>
    /// 「適用して保存」と自動化 API が共有する、書き出しの決まりごと: 原本を読む口(<see cref="ReadSource"/>。窓のプレビュー・
    /// 書き出し・非破壊ビルド・自動化が、これ 1 つで読む)と読めたかの種類(<see cref="SourceKind"/>)・出力先のパス・
    /// 取り込み設定の引き継ぎ・同じファイルかの判定、比較画像などの小さい PNG 化(管理配列の版)。書き出しそのものの手順
    /// (元の解像度で読む → 色替え → PNG 化 → 書き込みと取り込み。画素も PNG もヒープの外)は、Application の ExportService が持つ。
    /// 複数のテクスチャへの一括適用の画面は外した(当面出さないまま残っていた。2026-11-13)。出力先の重なりを見つける計画
    /// (<see cref="PlanBatch{T}"/>)は、次に一括の入口を作るときのために残してある(いま呼ぶのはテストだけ)。
    /// UI（ダイアログ・進捗・通知）は持たないので、EditMode テスト（scripts/editor-tests）から直接呼べる。
    /// </summary>
    internal static class ExportPipeline {
        internal enum SourceKind {
            /// <summary>原本ファイル（PNG/JPG）を直接デコードした。</summary>
            File,
            /// <summary>原本をデコードできず、取り込み済みテクスチャ（縮小・圧縮の影響あり）を使った。</summary>
            ImportedTexture,
            /// <summary>原本も取り込み側も読めない（Read/Write を有効にすれば後者が使える）。</summary>
            Unavailable,
            /// <summary>
            /// 原本は PNG/JPG だが画素を読めない（壊れている・扱えない形式）。取り込み側も読めない（Unity が取り込めていれば、
            /// Read/Write を有効にすると後者が使える）。
            /// </summary>
            Undecodable,
            /// <summary>
            /// 原本ファイルを開けない(ほかのアプリがつかんでいる・同期サービスのオンライン専用のファイル・権限・Unity の外で消した)。
            /// 画素は読んでいない。取り込み済みテクスチャ(縮小・圧縮済み)で代えるかは、呼ぶ側が決める: 書き出し・ビルド・自動化は
            /// 代えずに止め(粗い画素を黙って出さない)、窓は代えて案内を出し、開けるようになったら読み直す(<see cref="ReadImported"/>)。
            /// </summary>
            Locked,
        }

        /// <summary>画素を読めたか(<see cref="SourceKind.File"/> か <see cref="SourceKind.ImportedTexture"/>)。</summary>
        public static bool HasPixels(SourceKind kind) => kind == SourceKind.File || kind == SourceKind.ImportedTexture;

        /// <summary>
        /// 原本ファイルを開けなかった例外か(共有の違反・権限・無い)。原本を読む口はこれを <see cref="SourceKind.Locked"/> にして
        /// 返す。口の外でファイルを開く所(PNG かどうかの確かめ・別スレッドの読み込み)も、同じ見分けを使う。
        /// </summary>
        internal static bool IsOpenFailure(Exception ex) => ex is IOException || ex is UnauthorizedAccessException;

        // 原本も取り込み側も読めなかったときの区別。原本が PNG/JPG なのにデコードできなければ Undecodable、それ以外(PSD・TGA など
        // Unity が取り込みで変換する形式、ファイルの無いテクスチャ、いま開けないファイル)は Unavailable。
        private static SourceKind Unreadable(string srcPath) {
            if (string.IsNullOrEmpty(srcPath)) {
                return SourceKind.Unavailable;
            }
            try {
                return TextureIO.IsPngOrJpeg(srcPath) ? SourceKind.Undecodable : SourceKind.Unavailable;
            } catch (Exception ex) when (IsOpenFailure(ex)) {
                return SourceKind.Unavailable;
            }
        }

        /// <summary>元のファイルへ上書きして書き出せるか。書くのは Assets の下だけ(パッケージの中は取り込まれず、更新で消える)。</summary>
        public static bool CanOverwriteInPlace(string srcPath) =>
            PathUtils.ToAssetsRelativeOrNull(srcPath) != null;

        /// <summary>
        /// 単体書き出しの出力先。新規ファイルなら名前を <see cref="PathUtils.SanitizeFileName"/> で直して
        /// 元と同じフォルダへ（元が Assets の外なら <see cref="PathUtils.ExportFolderFor"/>）、上書きなら元の拡張子を
        /// .png に替えたパス（元が PNG なら、拡張子の大小も含めて元そのもの）。
        /// </summary>
        public static string SingleOutputPath(string srcPath, bool saveAsNewFile, string newFileName) {
            if (!saveAsNewFile) {
                // 大文字の .PNG を .png に替えると、Windows は置き換えたファイルを渡した名前の大小で付け直す(大小を区別する
                // ファイルシステムでは隣に別のファイルができる)。元が PNG なら元のパスのまま書く。
                if (string.Equals(Path.GetExtension(srcPath), ".png", System.StringComparison.OrdinalIgnoreCase)) {
                    return srcPath;
                }
                return Path.ChangeExtension(srcPath, ".png");
            }
            // 区切り文字も置換するのでパストラバーサルはできない。予約名("CON" 等)や
            // 末尾の '.' / ' ' もプリセット保存と同じ規則で直す。
            string safeName = PathUtils.SanitizeFileName(newFileName, "recolored");
            return InExportFolder(srcPath, safeName + ".png");
        }

        /// <summary>
        /// 一括書き出し・自動化 API の既定の出力先（元と同じフォルダの &lt;元の名前&gt;_recolored.png。
        /// 元が Assets の外なら <see cref="PathUtils.ExportFolderFor"/> の下）。
        /// </summary>
        public static string BatchOutputPath(string srcPath)
            => InExportFolder(srcPath, Path.GetFileNameWithoutExtension(srcPath) + PathUtils.RecoloredSuffix + ".png");

        /// <summary>一括書き出しの計画(<see cref="PlanBatch{T}"/>)。</summary>
        internal sealed class BatchPlan<T> where T : class {
            /// <summary>書き出す項目(同じ項目の 2 つ目以降と、元のパスが無いものを落とした、渡した並びのまま)。</summary>
            public readonly List<T> Items = new List<T>();
            /// <summary>出力先が重なる組。出力先ごとに、そこへ書くことになる元のパス(2 つ以上。並びのまま)。</summary>
            public readonly List<(string output, List<string> sources)> SharedOutputs = new List<(string, List<string>)>();
            /// <summary>
            /// 出力先が、ほかの項目の元そのもの(X.png と X_recolored.png を並べたとき)。先に書くと、その項目は色替えした後の
            /// 中身を読んで二重に色替えする。(出力先を書く元, その出力先 = ほかの項目の元)。
            /// </summary>
            public readonly List<(string source, string output)> OverwritesASource = new List<(string, string)>();

            public bool HasConflicts => SharedOutputs.Count > 0 || OverwritesASource.Count > 0;
        }

        /// <summary>
        /// 一括書き出しの計画を立てる(ファイルにもアセットにも触らない)。同じ項目の 2 つ目以降は落とし、出力先
        /// (<see cref="BatchOutputPath"/>)が重なる組と、出力先がほかの項目の元と同じものを返す。どちらかがあれば
        /// 書き出さずに止める(元の拡張子を捨てるので Body.psd と Body.png はどちらも Body_recolored.png に書き、2 つ目が
        /// 黙って上書きしていた)。パスは大小を区別せずに比べる(<see cref="IsSameFile"/> と同じ)。
        /// </summary>
        internal static BatchPlan<T> PlanBatch<T>(IEnumerable<T> items, Func<T, string> sourcePathOf) where T : class {
            var plan = new BatchPlan<T>();
            var sources = new List<string>();
            foreach (var item in items) {
                if (item == null || plan.Items.Contains(item)) {
                    continue;
                }
                string src = sourcePathOf(item);
                if (string.IsNullOrEmpty(src)) {
                    continue;
                }
                plan.Items.Add(item);
                sources.Add(src);
            }

            var sourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in sources) {
                sourceKeys.Add(PathKey(src));
            }
            var byOutput = new Dictionary<string, (string output, List<string> sources)>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var src in sources) {
                string output = BatchOutputPath(src);
                string key = PathKey(output);
                if (!byOutput.TryGetValue(key, out var entry)) {
                    entry = (output, new List<string>());
                    byOutput.Add(key, entry);
                    order.Add(key);
                }
                entry.sources.Add(src);
                if (sourceKeys.Contains(key)) {
                    plan.OverwritesASource.Add((src, output));
                }
            }
            foreach (var key in order) {
                var entry = byOutput[key];
                if (entry.sources.Count > 1) {
                    plan.SharedOutputs.Add(entry);
                }
            }
            return plan;
        }

        /// <summary>計画が見つけた書き出し先の重なりを、1 行ずつの文にする(止めた理由として見せる)。</summary>
        internal static string DescribeConflicts<T>(BatchPlan<T> plan) where T : class {
            var lines = new List<string>();
            foreach (var (output, sources) in plan.SharedOutputs) {
                lines.Add(Localization.BatchSharedOutput(string.Join(", ", sources), output));
            }
            foreach (var (source, output) in plan.OverwritesASource) {
                lines.Add(Localization.BatchOutputIsSource(source, output));
            }
            return string.Join("\n", lines);
        }

        // 同じファイルかを比べる鍵(フルパス。'/' と '\' の違いを消す)。フルパスにできないパスは区切りだけそろえる。
        private static string PathKey(string path) {
            try {
                return Path.GetFullPath(path);
            } catch (Exception) {
                return path.Replace('\\', '/');
            }
        }

        private static string InExportFolder(string srcPath, string fileName) {
            string folder = PathUtils.ExportFolderFor(srcPath);
            return string.IsNullOrEmpty(folder) ? fileName : folder + "/" + fileName;
        }

        /// <summary>
        /// 原本の画素を読む、ただ 1 つの口(メインスレッド専用。Mono のヒープの外へ読み、参照 1 で返すので、呼ぶ側が Release する)。
        /// 窓のプレビュー・「適用して保存」・非破壊ビルド・自動化が、同じ順・同じ区別で読む(2026-11-13 まで同じ手順が 4 か所にあり、
        /// ファイルを開けなかったときの扱いが食い違っていた: 窓は取り込み済みの画素に黙って切り替わり、ビルドと書き出しは止まった)。
        /// <para>
        /// 順: PNG/JPG はファイルを直接デコードする(取り込み設定の Max Size・圧縮・Read/Write の影響を受けない)。PSD/TGA/EXR など
        /// Unity が取り込みで変換する形式と、壊れた PNG/JPG は、取り込み済みテクスチャの画素を使う(縮小・圧縮を受けている。
        /// Read/Write が要る)。ファイルを開けなければ <see cref="SourceKind.Locked"/> で、画素は読まない
        /// (<paramref name="openError"/> に OS の理由)。パスが空(ファイルの無いテクスチャ)なら、テクスチャそのものの画素を読む。
        /// </para>
        /// <paramref name="workingSize"/> なら編集の作業解像度(<see cref="WorkingResolution"/>。既定は取り込み後の寸法)、
        /// そうでなければ元の解像度(書き出し)。<paramref name="imported"/> が null なら、読むのはファイルだけ。
        /// </summary>
        internal static SourceKind ReadSource(string srcPath, Texture2D imported, bool workingSize,
            out NativePixels pixels, out string openError) {
            pixels = null;
            openError = null;
            if (!string.IsNullOrEmpty(srcPath)) {
                try {
                    pixels = TextureIO.DecodeSourceFile(srcPath, workingSize ? imported : null);
                } catch (Exception ex) when (IsOpenFailure(ex)) {
                    openError = ex.Message;
                    return SourceKind.Locked;
                }
                if (pixels != null) {
                    return SourceKind.File;
                }
            }
            return ReadImported(srcPath, imported, out pixels);
        }

        /// <summary>
        /// 取り込み済みテクスチャの画素を読む(原本ファイルから読めなかったときの代わり。寸法は取り込み後のまま。メインスレッド専用)。
        /// Read/Write が無ければ読めず、<see cref="SourceKind.Unavailable"/> か、原本が PNG/JPG なのに読めなかったなら
        /// <see cref="SourceKind.Undecodable"/>。ふつうは <see cref="ReadSource"/> が呼ぶ。じかに呼ぶのは、開けなかった原本の
        /// 代わりを取る窓だけ。
        /// </summary>
        internal static SourceKind ReadImported(string srcPath, Texture2D imported, out NativePixels pixels) {
            pixels = null;
            if (!TextureIO.IsReadable(imported)) {
                return Unreadable(srcPath);
            }
            // 取り込み済みテクスチャの寸法のまま(GetPixels32 と同じ値。非圧縮の形式は管理配列を経由しない)。
            pixels = WorkingResolution.NativePixelsOf(imported, null);
            return SourceKind.ImportedTexture;
        }

        /// <summary>
        /// 画素を PNG にする。Texture2D を介さない ImageConversion.EncodeArrayToPNG は
        /// スレッドセーフなので、バックグラウンドから呼んでよい。
        /// Unity 2022.3.22f1 と 6000.0.67f1 の実機で、バックグラウンド実行がメインスレッドとバイト一致することを
        /// EditMode テスト（EncodePng_RunsOnBackgroundThreadAndMatchesMainThread）で確認している。
        /// Color32[] は sRGB バイト値なので R8G8B8A8_SRGB を指定する（Texture2D(RGBA32).EncodeToPNG と同じ画素）。
        /// </summary>
        public static byte[] EncodePng(Color32[] pixels, int width, int height) {
            // Color32 は r, g, b, a の 4 バイトが並ぶ構造体なので、配列のバイト列がそのまま RGBA。写し直さずに渡す
            // (旧: 同じ並びの byte[] へ写していた。8K で 256MB。EncodePng_MatchesRgbaByteArrayPath で同じバイト列を確認)。
            byte[] png = ImageConversion.EncodeArrayToPNG(
                pixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height);
            if (png == null || png.Length == 0) {
                throw new System.Exception(Localization.ExportPngEncodeEmpty);
            }
            return png;
        }

        // .meta ファイルをインポート前に書き込んでおくことで、
        // ImportAsset の 1 回の圧縮パスで正しい設定が適用される（SaveAndReimport 不要）。
        // srcPath は元テクスチャのアセットのパス（Assets/… か Packages/…）。
        internal static void PreApplyImportSettings(string srcPath, string dstRelPath) {
            string root = Path.GetDirectoryName(Application.dataPath);
            string sep = Path.DirectorySeparatorChar.ToString();
            string srcRel = PathUtils.ToAssetsRelativeOrNull(srcPath);
            // パッケージ（Packages/…）の実ファイルは Packages/ か Library/PackageCache/ にある。Path.GetFullPath が
            // 実際の置き場所へ解決する（Unity のマニュアルにある方法）。
            string srcMeta = (srcRel != null ? Path.Combine(root, srcRel.Replace("/", sep)) : Path.GetFullPath(srcPath)) + ".meta";
            string dstMeta = Path.Combine(root, dstRelPath.Replace("/", sep)) + ".meta";

            // 元そのものへ上書きするときは元の .meta をそのまま使う（書き直すと、中身が同じでも
            // 文字コードや改行の違いで差分が出る）。
            if (IsSameFile(srcMeta, dstMeta)) {
                return;
            }
            if (!File.Exists(srcMeta)) {
                return;
            }

            // BOM があれば読み飛ばす。改行は元のまま保つ。
            string content = File.ReadAllText(srcMeta, System.Text.Encoding.UTF8);

            // 上書きなら既存 GUID を維持、新規ファイルなら新 GUID を生成
            string guid = AssetDatabase.AssetPathToGUID(dstRelPath);
            if (string.IsNullOrEmpty(guid)) {
                guid = System.Guid.NewGuid().ToString("N");
            }

            // GUID を差し替えられないまま写すと、元と同じ GUID の .meta になる。Unity はそれを衝突として
            // 書き出し側に新しい GUID を振るので、上書きした画像がマテリアルから外れる。差し替えられなければ
            // 写さず、通常の取り込み（既定のインポート設定）に任せる。
            if (!MetaFile.TryReplaceGuid(content, guid, out string replaced)) {
                Debug.LogWarning($"[Iroca] Import settings were not inherited: no GUID line in {srcMeta}.");
                return;
            }
            // BOM なしの UTF-8（Unity が書く .meta と同じ）。小さいがアトミックに書く。
            AtomicFile.WriteAllText(dstMeta, replaced);
        }

        /// <summary>
        /// 2 つのパスが同一ファイルを指すか。書き出し先がプレビューのソース自身かの判定に使う。
        /// AssetDatabase のパスは '/' 区切り、Path.Combine は '\' を混ぜるため、素の文字列比較では
        /// 取りこぼす。フルパスへ正規化してから比較する。
        /// </summary>
        public static bool IsSameFile(string a, string b) {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) {
                return false;
            }
            try {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                    System.StringComparison.OrdinalIgnoreCase);
            } catch { return false; }
        }
    }
}
