// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Iroca
{
    /// <summary>
    /// Unity との境目で、画素をヒープの外(<see cref="NativePixels"/>)のまま読み書きする口(0.4.0 の計画の 2-4)。
    /// Unity 2022.3 の <c>GetPixels32</c>・<c>EncodeToPNG</c>・<c>ImageConversion.EncodeArrayToPNG</c> は管理配列しか
    /// 返さない・受け取らないので、8K の書き出しでは原本・出力・PNG だけで 500MB を超える管理配列が Mono のヒープに載り、
    /// 広がったヒープはエディタを閉じるまで縮まなかった。ここは <c>GetPixelData</c>(テクスチャの中身をそのまま見る)と
    /// <c>ImageConversion.EncodeNativeArrayToPNG</c>(ヒープの外の画素を受け取り、ヒープの外に PNG を返す)を使い、
    /// ファイルへは小さな置き場を通して流す。
    /// 残る管理配列は、<c>LoadImage</c> に渡す圧縮されたファイルの中身(2022.3 では管理配列しか受け取らない。
    /// 計画の 3.4.3 で T3 の例外)だけ。元ファイルをデコードできないもの(PSD など)は、取り込み済みテクスチャを
    /// 呼び出し側が読む(圧縮された中身を GPU の形式で持っているので <c>GetPixels32</c> で戻す。ここでは扱わない)。
    /// </summary>
    internal static unsafe class TextureIO
    {
        /// <summary>
        /// 原本の画素をヒープの外へ読む(メインスレッド専用。参照 1 で返すので、呼び出し側が Release する)。
        /// 書き出しの原本の読み込み(UI の ExportPipeline.ReadSourcePixels)と同じ画素で、<paramref name="workingSize"/> なら
        /// 編集の作業解像度(<see cref="WorkingResolution"/>)、そうでなければ元の解像度(書き出し)。
        /// 元ファイルを直接デコードできなければ(PSD・TGA・EXR など Unity が取り込みで変換するもの)null。そのときは
        /// 呼び出し側が取り込み済みテクスチャから読む。
        /// </summary>
        public static NativePixels ReadSourceFile(string srcPath, Texture2D imported, bool workingSize)
        {
            Texture2D loadTex = null;
            try
            {
                byte[] srcBytes = File.ReadAllBytes(srcPath);
                loadTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                // 取り込み済みテクスチャを渡さなければ作業解像度は元ファイルのまま(元の解像度)。
                if (loadTex.LoadImage(srcBytes))
                    return WorkingResolution.NativePixelsOf(loadTex, workingSize ? imported : null);
            }
            finally
            {
                if (loadTex != null) UnityEngine.Object.DestroyImmediate(loadTex);
            }
            return null;
        }

        /// <summary>
        /// 画素を PNG にする(UI の ExportPipeline.EncodePng と同じバイト列)。返す PNG はヒープの外にあり、
        /// 呼び出し側が Dispose する。画素は返るまで呼び出し側が持つこと。どのスレッドから呼んでもよいか
        /// (<c>EncodeNativeArrayToPNG</c> がスレッドセーフか)は EditMode テストで確かめる(TextureIOTests)。
        /// </summary>
        public static NativeArray<byte> EncodePng(NativePixels pixels)
        {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            using (var view = NativeView<byte>.Over(pixels.Ptr, pixels.Length * sizeof(Color32)))
            {
                // Color32 は sRGB のバイト値なので R8G8B8A8_SRGB(ExportPipeline.EncodePng と同じ指定)。
                var png = ImageConversion.EncodeNativeArrayToPNG(view.Array, GraphicsFormat.R8G8B8A8_SRGB,
                    (uint)pixels.Width, (uint)pixels.Height);
                if (!png.IsCreated || png.Length == 0)
                {
                    if (png.IsCreated) png.Dispose();
                    // 文言は呼び出し側(UI)が出す(Infra からは Localization を見ない)。
                    throw new InvalidOperationException("EncodeNativeArrayToPNG returned no data");
                }
                return png;
            }
        }

        // ファイルへ流すときの置き場の大きさ(管理配列。これ以上は Mono のヒープに載せない)。
        private const int StreamChunkBytes = 1 << 20;

        /// <summary>
        /// ヒープの外のバイト列(PNG)を、小さな置き場を通してファイルへアトミックに書く
        /// (<see cref="AtomicFile.WriteAllBytes"/> と同じ置き換え方。途中で落ちても既存のファイルを壊さない)。
        /// 出力先のフォルダが無ければ作る。
        /// </summary>
        public static void WriteAllBytes(string path, NativeArray<byte> bytes)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAll(path, fs =>
            {
                var chunk = new byte[StreamChunkBytes];
                for (int off = 0; off < bytes.Length; off += chunk.Length)
                {
                    int n = Math.Min(chunk.Length, bytes.Length - off);
                    NativeArray<byte>.Copy(bytes, off, chunk, 0, n);
                    fs.Write(chunk, 0, n);
                }
            });
        }

        /// <summary>
        /// ヒープの外の領域を <see cref="NativeArray{T}"/> に見せる(写さない)。エディタでは安全ハンドルを作り、
        /// Dispose で手放す(領域そのものは手放さない。持ち主は呼び出し側)。見せている間、領域を手放さないこと。
        /// </summary>
        internal struct NativeView<T> : IDisposable where T : struct
        {
            public NativeArray<T> Array;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            private AtomicSafetyHandle _safety;
#endif

            public static NativeView<T> Over(void* ptr, int length)
            {
                if (ptr == null) throw new ArgumentNullException(nameof(ptr));
                var v = new NativeView<T>
                {
                    Array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(ptr, length, Allocator.None),
                };
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                v._safety = AtomicSafetyHandle.Create();
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref v.Array, v._safety);
#endif
                return v;
            }

            public void Dispose()
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.Release(_safety);
#endif
                Array = default;
            }
        }
    }
}
