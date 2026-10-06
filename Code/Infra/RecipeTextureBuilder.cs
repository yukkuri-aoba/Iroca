// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Iroca
{
    /// <summary>
    /// レシピ(<see cref="IrocaRecipe"/>)から、非破壊ビルドで差し替える色替え済みテクスチャを作る。
    /// <list type="number">
    /// <item>原本の画素を「適用して保存」と同じ手順で読む(<see cref="ExportPipeline.ReadSourcePixels"/>)。</item>
    /// <item>保存済みの編集状態をそのまま当てる(<see cref="SessionRecolor.Apply"/>)。</item>
    /// <item>取り込み済みの元テクスチャと同じ寸法・圧縮形式・ミップ・色空間・サンプリング設定にそろえる。
    ///   そろえないと VRAM が増えてパフォーマンスランクが落ちる。形式はビルド対象(PC/Android)ごとに
    ///   Unity が取り込んだものに従うので、Android へ切り替えれば ASTC 等になる。</item>
    /// </list>
    /// 再生のたびに走るので、出来上がり(圧縮済みの生データ)を Library にキャッシュする。
    /// </summary>
    internal static class RecipeTextureBuilder
    {
        internal enum Failure
        {
            None,
            /// <summary>レシピに元テクスチャが設定されていない。</summary>
            NoSourceTexture,
            /// <summary>レシピの中身が空・壊れている・この版より新しい。</summary>
            UnreadableRecipe,
            /// <summary>レシピに有効なゾーンが無い(色替えするものが無い)。</summary>
            NoEnabledZones,
            /// <summary>原本も取り込み済みテクスチャも読めない。</summary>
            SourceUnreadable,
        }

        // キャッシュの中身の作り方を変えたら上げる(古いキャッシュを読まないため)。
        private const int CacheFormatVersion = 1;
        private const long CacheMaxBytes = 2L * 1024 * 1024 * 1024;

        internal static string CacheDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library/Iroca/RecipeTextureCache"));

        /// <summary>
        /// 差し替え用テクスチャを作る(キャッシュがあればそれを使う)。失敗したら null と理由を返す。
        /// </summary>
        public static Texture2D Build(IrocaRecipe recipe, out Failure failure)
        {
            failure = Failure.None;
            var like = recipe != null ? recipe.sourceTexture : null;
            if (like == null) { failure = Failure.NoSourceTexture; return null; }

            string key = CacheKey(recipe, like);
            var cached = TryLoadCache(key, like);
            if (cached != null) return cached;

            if (!TryRecolor(recipe, out var pixels, out int w, out int h, out failure)) return null;
            var tex = CreateMatching(pixels, w, h, like);
            TryStoreCache(key, tex);
            FinishLike(tex, like);
            return tex;
        }

        /// <summary>
        /// <see cref="Build"/> の非同期版(シーンのプレビュー用。初めて映すときにエディタを止めないため)。
        /// 重い色替えの計算と縮小はバックグラウンドで回す。原本の読み込み・テクスチャ作り・圧縮(Unity の API)は
        /// メインスレッドで、1 つずつ別の tick に分けて行う(4K では読み込み 0.2 秒・画素の確定 0.15 秒・
        /// BC7 圧縮 0.3 秒ほど)。出来上がりとキャッシュは <see cref="Build"/> と同じ。
        /// <paramref name="done"/> は必ずメインスレッドで 1 回呼ぶ(作れなければ null)。キャッシュがあるときや
        /// 作れないと分かっているとき(レシピが空・壊れている)は、この呼び出しの中で呼ぶ。
        /// </summary>
        internal static void BuildAsync(IrocaRecipe recipe, Action<Texture2D> done)
        {
            var like = recipe != null ? recipe.sourceTexture : null;
            if (like == null) { done(null); return; }

            string key = CacheKey(recipe, like);
            var cached = TryLoadCache(key, like);
            if (cached != null) { done(cached); return; }

            if (!TryLoadState(recipe, out var state, out _)) { done(null); return; }
            PreviewJobMainThread.Post(() =>
            {
                if (like == null || !TryReadPixels(like, out var pixels, out int w, out int h, out _)) { done(null); return; }
                int dw = like.width, dh = like.height;
                Task.Run(() =>
                {
                    SessionRecolor.Apply(pixels, w, h, state);
                    return (dw == w && dh == h) ? pixels : ResizeArea(pixels, w, h, dw, dh);
                }).ContinueWith(t => PreviewJobMainThread.Post(() => FinishAsync(t, like, dw, dh, key, done)),
                    TaskScheduler.Default);
            });
        }

        // 計算が終わったあとのメインスレッドの仕事(画素の確定、次の tick で圧縮とキャッシュ)。
        private static void FinishAsync(Task<Color32[]> t, Texture2D like, int dw, int dh, string key,
            Action<Texture2D> done)
        {
            // 計算している間に元テクスチャが消えたら作らない(取り込み直しで寸法が変わったなら合わせて縮める)。
            var tex = Step(() => t.IsFaulted || like == null ? null : CreateUncompressed(t.Result, dw, dh, like), null);
            if (tex == null)
            {
                if (t.IsFaulted) Debug.LogException(t.Exception.GetBaseException());
                done(null);
                return;
            }
            PreviewJobMainThread.Post(() =>
            {
                if (like == null)
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    done(null);
                    return;
                }
                done(Step(() =>
                {
                    Compress(tex, like);
                    TryStoreCache(key, tex);
                    FinishLike(tex, like);
                    return tex;
                }, tex));
            });
        }

        // 失敗したら記録して、作りかけを捨てて null を返す。
        private static Texture2D Step(Func<Texture2D> work, Texture2D partial)
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                if (partial != null) UnityEngine.Object.DestroyImmediate(partial);
                return null;
            }
        }

        /// <summary>
        /// 原本を読み、レシピの編集状態を当てた画素(原本の解像度)を返す。
        /// 「適用して保存」と同じ読み方・同じ処理なので、同じ編集状態なら書き出しとバイト単位で一致する。
        /// </summary>
        internal static bool TryRecolor(IrocaRecipe recipe, out Color32[] pixels, out int width, out int height,
            out Failure failure)
        {
            pixels = null; width = height = 0;
            if (!TryLoadState(recipe, out var state, out failure)) return false;
            if (!TryReadPixels(recipe.sourceTexture, out pixels, out width, out height, out failure)) return false;
            SessionRecolor.Apply(pixels, width, height, state);
            return true;
        }

        // レシピの編集状態を読み、色替えするものがあるか確かめる(軽い)。
        private static bool TryLoadState(IrocaRecipe recipe, out IrocaSessionState state, out Failure failure)
        {
            state = null; failure = Failure.None;
            var src = recipe != null ? recipe.sourceTexture : null;
            if (src == null) { failure = Failure.NoSourceTexture; return false; }
            state = RecipeStore.Load(recipe);
            if (state == null) { failure = Failure.UnreadableRecipe; return false; }
            if (SessionRecolor.EnabledZoneCopies(state.zones).Count == 0) { failure = Failure.NoEnabledZones; return false; }
            return true;
        }

        // 原本の画素を読む(4K で 0.2 秒ほど)。Unity の API を使うのでメインスレッドで呼ぶ。
        private static bool TryReadPixels(Texture2D src, out Color32[] pixels, out int width, out int height,
            out Failure failure)
        {
            failure = Failure.None;
            string srcPath = AssetDatabase.GetAssetPath(src);
            var kind = ExportPipeline.ReadSourcePixels(srcPath, src, out pixels, out width, out height);
            if (kind == ExportPipeline.SourceKind.Unavailable) { failure = Failure.SourceUnreadable; return false; }
            return true;
        }

        /// <summary>
        /// 取り込み済みの <paramref name="like"/> と同じ寸法・形式・ミップ有無・色空間のテクスチャを作る
        /// (読み書き可能のまま返す。サンプリング設定などは <see cref="FinishLike"/> でそろえる)。
        /// </summary>
        internal static Texture2D CreateMatching(Color32[] pixels, int width, int height, Texture2D like)
        {
            var tex = CreateUncompressed(pixels, width, height, like);
            Compress(tex, like);
            return tex;
        }

        // 圧縮の前まで(寸法・ミップ有無・色空間を like にそろえた RGBA32)。
        private static Texture2D CreateUncompressed(Color32[] pixels, int width, int height, Texture2D like)
        {
            int dw = like.width, dh = like.height;
            var px = (dw == width && dh == height) ? pixels : ResizeArea(pixels, width, height, dw, dh);
            bool linear = !GraphicsFormatUtility.IsSRGBFormat(like.graphicsFormat);
            bool mips = like.mipmapCount > 1;
            var tex = new Texture2D(dw, dh, TextureFormat.RGBA32, mips, linear);
            tex.SetPixels32(px);
            tex.Apply(updateMipmaps: mips, makeNoLongerReadable: false);
            return tex;
        }

        // 取り込み時の圧縮形式へそろえる。非圧縮(RGBA32 等)はそのまま。
        private static void Compress(Texture2D tex, Texture2D like)
        {
            var target = like.format;
            if (target == tex.format || !GraphicsFormatUtility.IsCompressedFormat(like.graphicsFormat)) return;
            int quality = 50;
            if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(like)) is TextureImporter importer)
                quality = Mathf.Clamp(importer.compressionQuality, 0, 100);
            try
            {
                EditorUtility.CompressTexture(tex, target, quality);
            }
            catch (Exception) when (CrunchFallback(target) != target)
            {
                // Crunch 圧縮をエディタ上で再現できない環境では、同じ VRAM 量の非 Crunch 形式にする
                // (Crunch が減らすのはダウンロードサイズで、VRAM は変わらない)。
                EditorUtility.CompressTexture(tex, CrunchFallback(target), quality);
            }
        }

        private static TextureFormat CrunchFallback(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.DXT1Crunched: return TextureFormat.DXT1;
                case TextureFormat.DXT5Crunched: return TextureFormat.DXT5;
                case TextureFormat.ETC_RGB4Crunched: return TextureFormat.ETC_RGB4;
                case TextureFormat.ETC2_RGBA8Crunched: return TextureFormat.ETC2_RGBA8;
                default: return f;
            }
        }

        /// <summary>
        /// サンプリング設定・mip streaming・読み書き可否を元テクスチャにそろえ、名前を付ける。
        /// 読み書き不可にするとそれ以降 GetPixels できないので最後に呼ぶ。
        /// </summary>
        internal static void FinishLike(Texture2D tex, Texture2D like)
        {
            tex.name = like.name + " (Iroca)";
            tex.filterMode = like.filterMode;
            tex.wrapModeU = like.wrapModeU;
            tex.wrapModeV = like.wrapModeV;
            tex.wrapModeW = like.wrapModeW;
            tex.anisoLevel = like.anisoLevel;
            tex.mipMapBias = like.mipMapBias;
            if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(like)) is TextureImporter importer
                && importer.streamingMipmaps)
            {
                // 実行時に作った Texture2D には mip streaming の公開 API が無いのでシリアライズ値を直接立てる。
                var so = new SerializedObject(tex);
                var streaming = so.FindProperty("m_StreamingMipmaps");
                var priority = so.FindProperty("m_StreamingMipmapsPriority");
                if (streaming != null) streaming.boolValue = true;
                if (priority != null) priority.intValue = importer.streamingMipmapsPriority;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            if (!like.isReadable) tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        }

        /// <summary>
        /// 面積平均で縮小する(取り込み時の最大サイズへ合わせる用途。整数倍ならボックスフィルタと同じ)。
        /// 拡大方向(取り込み側が大きい)のときは最近傍。行 0 = 画像下端の並び(Unity の画素順)のまま扱う。
        /// </summary>
        internal static Color32[] ResizeArea(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int y0 = (int)((long)y * sh / dh);
                int y1 = Math.Max(y0 + 1, (int)(((long)y + 1) * sh / dh));
                for (int x = 0; x < dw; x++)
                {
                    int x0 = (int)((long)x * sw / dw);
                    int x1 = Math.Max(x0 + 1, (int)(((long)x + 1) * sw / dw));
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * sw;
                        for (int xx = x0; xx < x1; xx++)
                        {
                            var c = src[row + xx];
                            r += c.r; g += c.g; b += c.b; a += c.a; n++;
                        }
                    }
                    int half = n / 2;
                    dst[y * dw + x] = new Color32(
                        (byte)((r + half) / n), (byte)((g + half) / n),
                        (byte)((b + half) / n), (byte)((a + half) / n));
                }
            }
            return dst;
        }

        // ───────────── キャッシュ(Library/Iroca/RecipeTextureCache) ─────────────
        // 鍵 = 元テクスチャの取り込み結果(ファイル内容と取り込み設定) + レシピの中身 + いろかのコード
        //      + 出来上がりの形式。どれかが変われば別の鍵になる。

        internal static string CacheKey(IrocaRecipe recipe, Texture2D like)
        {
            string srcPath = AssetDatabase.GetAssetPath(like);
            var sb = new StringBuilder();
            sb.Append(CacheFormatVersion).Append('|');
            sb.Append(AssetDatabase.GetAssetDependencyHash(srcPath).ToString()).Append('|');
            // いろかのコードが変われば出力も変わり得る。Editor アセンブリは再コンパイルのたびに MVID が変わる。
            sb.Append(typeof(SessionRecolor).Assembly.ManifestModule.ModuleVersionId).Append('|');
            sb.Append(like.width).Append('x').Append(like.height).Append('|');
            sb.Append((int)like.format).Append('|').Append((int)like.graphicsFormat).Append('|');
            sb.Append(like.mipmapCount).Append('|');
            sb.Append(recipe.formatVersion).Append('|').Append(recipe.sessionJson);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        // テストもこれでキャッシュファイルを特定する(ファイル名の規則を 1 か所に保つ)。
        internal static string CachePath(string key) => Path.Combine(CacheDir, key + ".tex");

        private static Texture2D TryLoadCache(string key, Texture2D like)
        {
            string path = CachePath(key);
            if (!File.Exists(path)) return null;
            try
            {
                byte[] data;
                int w, h, fmt, mipCount;
                bool linear;
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    if (br.ReadInt32() != CacheFormatVersion) return null;
                    w = br.ReadInt32(); h = br.ReadInt32(); fmt = br.ReadInt32();
                    mipCount = br.ReadInt32(); linear = br.ReadBoolean();
                    int len = br.ReadInt32();
                    data = br.ReadBytes(len);
                    if (data.Length != len) return null;
                }
                var tex = new Texture2D(w, h, (TextureFormat)fmt, mipCount, linear);
                tex.LoadRawTextureData(data);
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);   // 使ったものを残す(古い順に消す)
                FinishLike(tex, like);
                return tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Iroca] テクスチャのキャッシュを読めなかったので作り直します: {ex.Message}");
                try { File.Delete(path); } catch (Exception) { /* 次回また作り直すだけ */ }
                return null;
            }
        }

        private static void TryStoreCache(string key, Texture2D tex)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                byte[] data = tex.GetRawTextureData();
                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms))
                {
                    bw.Write(CacheFormatVersion);
                    bw.Write(tex.width); bw.Write(tex.height); bw.Write((int)tex.format);
                    bw.Write(tex.mipmapCount);
                    bw.Write(!GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat));
                    bw.Write(data.Length);
                    bw.Write(data);
                    bw.Flush();
                    AtomicFile.WriteAllBytes(CachePath(key), ms.ToArray());
                }
                TrimCache();
            }
            catch (Exception ex)
            {
                // キャッシュは速さのためだけ。書けなくても出来上がりは正しい。
                Debug.LogWarning($"[Iroca] テクスチャのキャッシュを書けませんでした: {ex.Message}");
            }
        }

        // 合計が上限を超えたら、最後に使った時刻が古いものから消す。
        private static void TrimCache()
        {
            var files = new List<FileInfo>(new DirectoryInfo(CacheDir).GetFiles("*.tex"));
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= CacheMaxBytes) return;
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var f in files)
            {
                if (total <= CacheMaxBytes) break;
                try { total -= f.Length; f.Delete(); }
                catch (Exception) { /* 使用中などは次回 */ }
            }
        }
    }
}
