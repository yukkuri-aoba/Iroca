// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.IO;

namespace Iroca
{
    /// <summary>
    /// クラッシュ・ディスクフルで既存ファイルを壊さないテキスト書き込み。
    /// 一時ファイルへ書き切ってから rename / Replace で本置換する
    /// （直接 <see cref="File.WriteAllText(string,string)"/> すると書き込み途中で
    /// 落ちたとき既存の正常データごと破損する）。
    /// </summary>
    internal static class AtomicFile
    {
        /// <summary>
        /// <paramref name="path"/> へテキストをアトミックに書き込む。
        /// 失敗時は例外を伝播する（既存ファイルは無傷、書きかけの一時ファイルは削除）。
        /// </summary>
        public static void WriteAllText(string path, string contents)
            => Write(path, tmp => File.WriteAllText(tmp, contents));

        /// <summary>
        /// <paramref name="path"/> へバイト列をアトミックに書き込む。
        /// 失敗時は例外を伝播する（既存ファイルは無傷、書きかけの一時ファイルは削除）。
        /// エクスポート PNG のように「書き潰す相手が元テクスチャそのもの」であり得る用途では、
        /// 途中で落ちた書き込みが原本を壊すため直接 <see cref="File.WriteAllBytes"/> を使わない。
        /// </summary>
        public static void WriteAllBytes(string path, byte[] contents)
            => Write(path, tmp => File.WriteAllBytes(tmp, contents));

        private static void Write(string path, System.Action<string> writeTemp)
        {
            // 一時ファイルは必ず同一フォルダに置く（File.Replace/Move が同一ボリューム内で完結し
            // rename が原子的になる）。
            string tmp = path + ".tmp";
            try
            {
                writeTemp(tmp);
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { /* 一時ファイルの掃除失敗は本エラーを隠さない */ }
                throw;
            }
        }
    }
}
