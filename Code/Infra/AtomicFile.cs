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
            => Write(path, tmp => WriteAndFlush(tmp, Utf8NoBom.GetBytes(contents)));

        /// <summary>
        /// <paramref name="path"/> へバイト列をアトミックに書き込む。
        /// 失敗時は例外を伝播する（既存ファイルは無傷、書きかけの一時ファイルは削除）。
        /// エクスポート PNG のように「書き潰す相手が元テクスチャそのもの」であり得る用途では、
        /// 途中で落ちた書き込みが原本を壊すため直接 <see cref="File.WriteAllBytes"/> を使わない。
        /// </summary>
        public static void WriteAllBytes(string path, byte[] contents)
            => Write(path, tmp => WriteAndFlush(tmp, contents));

        // File.WriteAllText の既定と同じ「BOM なし UTF-8」を保つ。
        private static readonly System.Text.UTF8Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

        /// <summary>一時ファイルへ書き、デバイスまで書き切ってから閉じる。</summary>
        private static void WriteAndFlush(string path, byte[] bytes)
        {
            // rename の原子性が保証するのは「置換が中途半端に見えないこと」だけで、
            // 中身が永続化済みであることまでは保証しない。一時ファイルの内容が OS のバッファに
            // 残ったまま rename だけ先に永続化されると、電源断で新旧どちらも失う
            // （「壊さないための仕組み」が壊す側に回る）。Flush(true) でデバイスまで送る。
            //
            // 限界: ディレクトリエントリ自体の fsync は .NET から移植性のある形で呼べない。
            // ファイル内容の永続化までを保証する、という水準。
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
        }

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
