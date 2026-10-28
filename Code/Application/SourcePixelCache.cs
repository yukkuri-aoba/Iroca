// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 色替えに使う原本の画素(作業解像度。<see cref="WorkingResolution"/>)を、テクスチャ 1 枚分だけ持つ
    /// (0.4.0 の計画の 4-2 の 1。PreviewView が持っていた原本を切り出した)。
    /// 取り込み済みテクスチャの圧縮で劣化した画素ではなく、元ファイルをデコードした画素(辺が 8192 を超えるときだけ
    /// 取り込み後の寸法へ縮めたもの)を持つ。書き出し・非破壊ビルドも同じ解像度で処理するので、色替えの中身が一致する。
    /// 元ファイルを読めないもの(PSD など)は、取り込み済みテクスチャ(Read/Write が有効なとき)の画素を持つ。
    /// 画素は Mono のヒープの外に置き(4K で 64MB、8K で 256MB)、ここが参照を 1 つ持つ。別スレッドで読む
    /// ジョブ・AI 提案・自動調整はそれぞれ参照を足して持つので、ここで手放しても読んでいる途中で消えない。
    /// 読み直すかどうか(別のテクスチャ・取り込み後の寸法が変わった)と、原本から導いた状態を捨てるのは呼び出し側が決める。
    /// メインスレッド専用。
    /// </summary>
    internal sealed class SourcePixelCache
    {
        private Texture2D _for;
        private NativePixels _pixels;
        private int _w, _h;
        // 作業解像度を決めた前提(取り込み後の寸法)。変わったら呼び出し側が読み直す。
        private int _basisW, _basisH;
        // 元ファイルをデコードした(false = 取り込み済みテクスチャの画素。PSD など)。
        private bool _fromFile;
        // 直近に画素を取れなかったテクスチャ(毎フレームの可否判定で読み直さないための負のキャッシュ)。
        private Texture2D _failedFor;

        /// <summary>画素を読んだテクスチャ(画素を手放した後も、次に読むか <see cref="Clear"/> するまで覚えている)。</summary>
        internal Texture2D Texture => _for;
        /// <summary>原本の画素(行 0 = 画像下端。GetPixels32 と同じ並び)。読んでいなければ null。参照は足さない。</summary>
        internal NativePixels Pixels => _pixels;
        internal int Width => _w;
        internal int Height => _h;
        /// <summary>読んだときの取り込み後の寸法。</summary>
        internal int BasisWidth => _basisW;
        internal int BasisHeight => _basisH;
        /// <summary>直近に読んだ画素が元ファイルをデコードしたものか(取り込み済みテクスチャではなく)。</summary>
        internal bool FromFile => _fromFile;

        /// <summary>
        /// 前に画素を取れなかったテクスチャで、今も Read/Write が無効か。UI の可否判定(スポイト・自動調整・手順表示)が
        /// OnGUI ごとに読みに来るので、覚えないと「読めないテクスチャ」で毎フレームファイル読み込みとデコードを試すことになる。
        /// あとで Read/Write を有効にすれば IsReadable が true へ変わるため、そのときは覚えた失敗を無視して取り直す。
        /// </summary>
        internal bool IsKnownUnreadable(Texture2D tex) => _failedFor == tex && !TextureIO.IsReadable(tex);

        /// <summary>
        /// tex の画素を読む。可能ならディスク上の元ファイルをデコードして作業解像度にそろえ、PNG/JPG 以外や生成テクスチャ等で
        /// 失敗した場合はインポート済みテクスチャの GetPixels32 にフォールバックする。読めたら前の画素を手放して差し替え、
        /// true を返す。どちらでも取れなければ失敗を覚えて false(前の画素はそのまま)。
        /// </summary>
        internal bool Load(Texture2D tex)
        {
            string path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                Texture2D tmp = null;
                try
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(path);
                    tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (tmp.LoadImage(bytes))
                    {
                        Set(tex, WorkingResolution.NativePixelsOf(tmp, tex), fromFile: true);
                        return true;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Iroca] Source file load failed, falling back to imported texture: {ex.Message}");
                }
                finally
                {
                    if (tmp != null) Object.DestroyImmediate(tmp);
                }
            }

            // フォールバック: インポート済みテクスチャ（要 Read/Write）。
            if (!TextureIO.IsReadable(tex))
            {
                _failedFor = tex;
                return false;
            }
            Set(tex, NativePixels.CopyOf(tex.GetPixels32(), tex.width, tex.height), fromFile: false);
            return true;
        }

        // 画素を差し替える(前の画素の参照を返し、新しい画素の参照を持つ)。取り込み後の寸法も覚える。
        private void Set(Texture2D tex, NativePixels pixels, bool fromFile)
        {
            Release();
            _pixels = pixels;
            _w = pixels.Width;
            _h = pixels.Height;
            _for = tex;
            _failedFor = null;
            _basisW = tex.width;
            _basisH = tex.height;
            _fromFile = fromFile;
        }

        // 画素の参照を返す。読んでいる途中のジョブは自分の参照を持っているので、ここで返しても消えない。
        private void Release()
        {
            var old = _pixels;
            _pixels = null;
            _w = _h = 0;
            old?.Release();
        }

        /// <summary>画素を手放し、どのテクスチャを読んだか・読めなかったかも忘れる(テクスチャの差し替え・閉じるとき)。</summary>
        internal void Clear()
        {
            _for = null;
            Release();
            _failedFor = null;
        }
    }
}
