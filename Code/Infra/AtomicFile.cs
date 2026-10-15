// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using System.Threading;

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

        // 置換先をほかのプロセス(ウイルス対策・検索インデクサ・Unity の取り込みなど)が一瞬つかんでいると、
        // File.Replace は「置換されるファイルを削除できません」(IOException)で失敗する。2026-10 に実機で
        // セッション保存とテストがこれで失敗した。一時的なものなので、少し待って繰り返す(合計 1 秒弱)。
        private static readonly int[] RetryDelaysMs = { 15, 30, 60, 120, 250, 500 };

        private static void Write(string path, Action<string> writeTemp)
        {
            // 一時ファイルは必ず同一フォルダに置く（File.Replace/Move が同一ボリューム内で完結し
            // rename が原子的になる）。
            string tmp = path + ".tmp";
            bool existed = File.Exists(path);
            try
            {
                writeTemp(tmp);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        // 試すたびに置換先の有無を確かめ直す。Replace が途中で失敗すると置換先が消えて
                        // いることがある(ERROR_UNABLE_TO_MOVE_REPLACEMENT)ので、そのときは Move で書き切る。
                        if (File.Exists(path))
                            File.Replace(tmp, path, null);
                        else
                            File.Move(tmp, path);
                        return;
                    }
                    catch (Exception e) when (attempt < RetryDelaysMs.Length && IsTransient(e))
                    {
                        Thread.Sleep(RetryDelaysMs[attempt]);
                    }
                }
            }
            catch
            {
                // 置換の途中で元のファイルが消えたときは、新しい中身を持つ一時ファイルを残す(消すと両方失う)。
                bool lostOriginal = existed && !File.Exists(path);
                try { if (!lostOriginal && File.Exists(tmp)) File.Delete(tmp); }
                catch { /* 一時ファイルの掃除失敗は本エラーを隠さない */ }
                throw;
            }
        }

        // 共有違反・アクセス拒否は一時的なことがある。フォルダが無い・パスが長すぎるなどは待っても直らない。
        private static bool IsTransient(Exception e)
            => e is UnauthorizedAccessException
               || (e is IOException
                   && !(e is FileNotFoundException || e is DirectoryNotFoundException
                        || e is PathTooLongException || e is DriveNotFoundException));
    }
}
