// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Iroca {
    /// <summary>
    /// レシピ(<see cref="IrocaRecipe"/>)から、非破壊ビルドで差し替える色替え済みテクスチャを作る。
    /// <list type="number">
    /// <item>原本の画素を「適用して保存」と同じ手順で読み、プレビューと同じ作業解像度(<see cref="WorkingResolution"/>。
    ///   既定は取り込み後の寸法)にそろえる(<see cref="ExportPipeline.ReadWorkingPixelsNative"/>)。</item>
    /// <item>保存済みの編集状態をそのまま当てる(<see cref="SessionRecolor.Apply"/>)。</item>
    /// <item>取り込み済みの元テクスチャと同じ寸法・圧縮形式・ミップ・色空間・サンプリング設定にそろえる。
    ///   そろえないと VRAM が増えてパフォーマンスランクが落ちる。形式はビルド対象(PC/Android)ごとに
    ///   Unity が取り込んだものに従うので、Android へ切り替えれば ASTC 等になる。</item>
    /// </list>
    /// 再生のたびに走るので、出来上がり(圧縮済みの生データ)を Library にキャッシュする。
    /// </summary>
    internal static class RecipeTextureBuilder {
        internal enum Failure {
            None,
            /// <summary>レシピに元テクスチャが設定されていない。</summary>
            NoSourceTexture,
            /// <summary>レシピの中身が空・壊れている・この版より新しい。</summary>
            UnreadableRecipe,
            /// <summary>レシピに有効なゾーンが無い(色替えするものが無い)。</summary>
            NoEnabledZones,
            /// <summary>原本も取り込み済みテクスチャも読めない。</summary>
            SourceUnreadable,
            /// <summary>原本の PNG/JPG が壊れているか扱えない形式で、取り込み済みテクスチャも読めない(Read/Write が無い)。</summary>
            SourceUndecodable,
        }

        // キャッシュの中身の作り方を変えたら上げる(古いキャッシュを読まないため)。
        private const int CacheFormatVersion = 1;
        private const long CacheMaxBytes = 2L * 1024 * 1024 * 1024;

        internal static string CacheDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library/Iroca/RecipeTextureCache"));

        /// <summary>
        /// 差し替え用テクスチャを作る(キャッシュがあればそれを使う)。失敗したら null と理由を返す。
        /// </summary>
        public static Texture2D Build(IrocaRecipe recipe, out Failure failure) {
            failure = Failure.None;
            var like = recipe != null ? recipe.sourceTexture : null;
            if (like == null) { failure = Failure.NoSourceTexture; return null; }

            string key = CacheKey(recipe, like);
            var cached = TryLoadCache(key, like);
            if (cached != null) {
                return cached;
            }

            BeginCompute();
            try {
                if (!TryRecolorNative(recipe, out var pixels, out failure)) {
                    return null;
                }
                Texture2D tex;
                try {
                    tex = CreateMatching(pixels, like);
                } finally {
                    pixels.Release();
                }
                TryStoreCache(key, tex);
                FinishLike(tex, like);
                return tex;
            } finally {
                EndCompute();
            }
        }

        // 色替えの計算をしている数(作業配列を借りうる区間。続けて作る区間も含む)と、計算した後まだ手放していないか。
        // メインスレッドからしか触らない。
        private static int s_computing;
        private static bool s_releaseDue;

        // 計算を始める(メインスレッド)。終えたら必ず EndCompute を呼ぶ。
        private static void BeginCompute() {
            s_computing++;
            s_releaseDue = true;
            NativeMemoryLifecycle.WorkStarted();
        }

        // 計算を 1 つ終える(メインスレッド)。
        private static void EndCompute() {
            NativeMemoryLifecycle.WorkEnded();
            EndHold();
        }

        // 続けて投げた計算も全部終わったら、作業配列(ネイティブメモリ)と色の表を OS へ返す。いろかのウィンドウが
        // 開いていれば返さない(編集の次の 1 手が作業配列と色の表を作り直すことになる。ウィンドウを閉じたときに
        // まとめて返る)。ウィンドウを閉じるとシーンのプレビューが登録済みのレシピを作り直すので、ここで返さないと
        // 閉じたときの解放が打ち消され、4K で 600MB ほどがエディタを閉じるまで残っていた。
        private static void EndHold() {
            if (--s_computing > 0 || !s_releaseDue) {
                return;
            }
            if (EditingSessionRegistry.Any) {
                return;
            }
            s_releaseDue = false;
            NativeMemoryLifecycle.ReleaseWhenIdle();
        }

        /// <summary>
        /// 続けて何枚も作る間(非破壊ビルドの 1 段)は作業配列を手放さず、終わったときにまとめて手放す
        /// (1 枚ごとに手放すと、次の 1 枚が作業配列を確保し直す)。メインスレッドで使う。
        /// </summary>
        internal static IDisposable KeepWorkBuffers() {
            s_computing++;
            return new KeepScope();
        }

        private sealed class KeepScope : IDisposable {
            private bool _disposed;

            public void Dispose() {
                if (_disposed) {
                    return;
                }
                _disposed = true;
                EndHold();
            }
        }

        /// <summary>
        /// <see cref="Build"/> の非同期版(シーンのプレビュー用。初めて映すときにエディタを止めないため)。
        /// 重い色替えの計算と縮小はバックグラウンドで回す。原本の読み込み・テクスチャ作り・圧縮(Unity の API)は
        /// メインスレッドで、1 つずつ別の tick に分けて行う(4K では読み込み 0.2 秒・画素の確定 0.15 秒・
        /// BC7 圧縮 0.3 秒ほど)。出来上がりとキャッシュは <see cref="Build"/> と同じ。
        /// <paramref name="done"/> は必ずメインスレッドで 1 回呼ぶ(作れなければ null)。キャッシュがあるときや
        /// 作れないと分かっているとき(レシピが空・壊れている)は、この呼び出しの中で呼ぶ。
        /// <para>
        /// 途中で例外が出ても null で呼ぶ。シーンのプレビューは NDMF が全部のノードの完了を待ってから切り替えるので、
        /// 呼ばれないままだと、ほかのツールの分も含めてシーンの NDMF のプレビュー全体が古い表示のまま止まる
        /// (作りかけは <see cref="RecipePreviewTextures"/> が共有するので、プレビューを入れ直しても直らない)。
        /// </para>
        /// </summary>
        internal static void BuildAsync(IrocaRecipe recipe, Action<Texture2D> done)
            => BuildAsync(recipe, CancellationToken.None, done);

        /// <summary>
        /// <see cref="BuildAsync(IrocaRecipe, Action{Texture2D})"/> の取り消せる版。<paramref name="cancellationToken"/> が
        /// 取り消されたら、計算の途中でも止まるのを待たずに(次の tick で)<paramref name="done"/> を null で呼ぶ
        /// (待っていたシーンのプレビューが、元のテクスチャのまま次の表示へ進めるように)。計算は作業配列をプールへ返して
        /// 止まり、途中の結果はテクスチャにもキャッシュにもしない。計算の数の勘定(作業配列の解放の判断)は崩さない。
        /// </summary>
        internal static void BuildAsync(IrocaRecipe recipe, CancellationToken cancellationToken, Action<Texture2D> done) {
            bool called = false;
            var registration = default(CancellationTokenRegistration);
            void Once(Texture2D tex) {
                if (called) {
                    if (tex != null) {
                        UnityEngine.Object.DestroyImmediate(tex);   // 受け取り手がいない
                    }
                    return;
                }
                called = true;
                registration.Dispose();
                done(tex);
            }

            // 取り消しはどのスレッドから来てもよいので、知らせるのはメインスレッドへ戻してから。
            if (cancellationToken.CanBeCanceled) {
                registration = cancellationToken.Register(() => PreviewJobMainThread.Post(() => Once(null)));
            }
            try {
                StartBuild(recipe, cancellationToken, Once);
            } catch (Exception ex) {
                Debug.LogException(ex);
                Once(null);
            }
        }

        private static void StartBuild(IrocaRecipe recipe, CancellationToken ct, Action<Texture2D> done) {
            var like = recipe != null ? recipe.sourceTexture : null;
            if (like == null) { done(null); return; }

            string key = CacheKey(recipe, like);
            var cached = TryLoadCache(key, like);
            if (cached != null) { done(cached); return; }

            if (!TryLoadState(recipe, out var state, out _)) { done(null); return; }
            var build = new AsyncBuild { like = like, key = key, state = state, ct = ct, done = done };
            PostOrFail(done, () => {
                // 読む前に取り消された(待っていたプレビューがもう要らない)。
                if (ct.IsCancellationRequested) { done(null); return; }
                if (like == null) { done(null); return; }
                // PNG は順番が来てから計算のスレッドで読む(4K で 0.2 秒ほどメインスレッドを止めないように。待っている間、
                // 映すレシピの数だけ画素を持ち続けることもなくなる)。それ以外と扱わない PNG は LoadImage・GPU の展開が
                // 要るのでここで読む。
                string srcPath = AssetDatabase.GetAssetPath(like);
                NativePixels pixels = null;
                if (!CanReadOffMainThread(srcPath) && !TryReadPixels(like, out pixels, out _)) { done(null); return; }
                QueueCompute(build, pixels, srcPath, like.width, like.height);
            });
        }

        // 非同期で作る 1 件の、計算の前後で使うもの(メインスレッドで作り、計算のスレッドは state を読むだけ)。
        private sealed class AsyncBuild {
            public Texture2D like;
            public string key;
            public IrocaSessionState state;
            public CancellationToken ct;
            public Action<Texture2D> done;
        }

        // 色替えの計算を、前に投げた計算の後ろにつなぐ(メインスレッド)。pixels が null なら、計算のスレッドで PNG(srcPath)を
        // importW×importH の作業解像度で読む。
        // 計算は 1 本ずつ。シーンのプレビューは映すレシピの数だけ同時にここへ来るので、並べて走らせると作業配列のプールが
        // その組数ぶん膨らむ。計算そのものが全コアを使うので、順番にしても合計の時間はほぼ同じ。
        // 前の計算の成否は見ない(1 つが失敗しても後ろは作る)。
        private static void QueueCompute(AsyncBuild b, NativePixels pixels, string srcPath, int importW, int importH) {
            var ct = b.ct;
            var state = b.state;
            BeginCompute();
            var compute = s_lastCompute.ContinueWith(_ => {
                try {
                    // 順番を待つ間に取り消されたら計算しない。計算中に取り消されたら、PixelProcessor が作業配列を
                    // プールへ返して OperationCanceledException で抜ける(画素はここで手放す)。
                    ct.ThrowIfCancellationRequested();
                    if (pixels == null) {
                        pixels = TextureIO.TryDecodePngOffMainThread(srcPath, importW, importH, ct);
                        if (pixels == null) {
                            // 見出しは読めたが、画素の途中で壊れている。窓・同期のビルドと同じ画素にするため、メインスレッドで
                            // 読み直す(FinishAsync。LoadImage と取り込み済みテクスチャ)。
                            return null;
                        }
                    }
                    Recolor(pixels, state, ct);
                    return pixels;
                } catch {
                    pixels?.Release();
                    throw;
                }
            }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            // compute そのものを持つと、結果の画素(4K で 64MB)まで static で持ち続けるので、結果を持たない
            // 後続のタスクをつなぎ目にする(後続は走り終えると前のタスクへの参照を手放す)。
            s_lastCompute = compute.ContinueWith(_ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            compute.ContinueWith(t => PostOrFail(b.done, () => {
                try {
                    FinishAsync(t, b);
                } finally {
                    EndCompute();
                }
            }), TaskScheduler.Default);
        }

        // 最後に投げた計算(メインスレッドからしか触らない)。
        private static Task s_lastCompute = Task.CompletedTask;

        // 別スレッドで読める PNG か。ファイルを開けないときは false(TryReadPixels が理由を記録して失敗にする)。
        private static bool CanReadOffMainThread(string srcPath) {
            try { return PngFileDecoder.CanDecode(srcPath); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                                                                         || ex is ArgumentException || ex is NotSupportedException) {
                return false;
            }
        }

        // メインスレッドの次の tick で work を回す。例外で抜けたら記録して done(null)(done は 1 回目だけ効く)。
        private static void PostOrFail(Action<Texture2D> done, Action work) {
            PreviewJobMainThread.Post(() => {
                try {
                    work();
                } catch (Exception ex) {
                    Debug.LogException(ex);
                    done(null);
                }
            });
        }

        // 計算が終わったあとのメインスレッドの仕事(画素の確定、次の tick で圧縮とキャッシュ)。
        private static void FinishAsync(Task<NativePixels> t, AsyncBuild b) {
            var like = b.like;
            var key = b.key;
            var ct = b.ct;
            var done = b.done;
            // 計算している間に元テクスチャが消えたら作らない(取り込み直しで寸法が変わったなら合わせて縮める)。
            // 計算した画素はテクスチャへ写したらすぐ手放す(失敗した計算は自分で手放している)。
            var pixels = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            // 取り消されたら作らない(計算し終えていても、もう要らないものに圧縮とキャッシュの書き込みで
            // メインスレッドを使わない。done は取り消したときに呼ばれる)。
            if (ct.IsCancellationRequested) {
                pixels?.Release();
                done(null);
                return;
            }
            if (t.Status == TaskStatus.RanToCompletion && pixels == null) {
                // 計算のスレッドで PNG の画素を読めなかった(見出しは読めたが途中で壊れている)。同期のビルドと同じく
                // メインスレッドで読み直し(LoadImage・取り込み済みテクスチャ)、同じ計算の列へもう一度積む。読めなければ作らない。
                if (like == null || !TryReadPixels(like, out var reread, out _)) {
                    done(null);
                    return;
                }
                QueueCompute(b, reread, null, 0, 0);
                return;
            }
            Texture2D tex;
            try {
                tex = Step(() => pixels == null || like == null ? null : CreateUncompressed(pixels, like), null);
            } finally {
                pixels?.Release();
            }
            if (tex == null) {
                if (t.IsFaulted && !(t.Exception.GetBaseException() is OperationCanceledException)) {
                    Debug.LogException(t.Exception.GetBaseException());
                }
                done(null);
                return;
            }
            PostOrFail(done, () => {
                if (like == null || ct.IsCancellationRequested) {
                    UnityEngine.Object.DestroyImmediate(tex);
                    done(null);
                    return;
                }
                done(Step(() => {
                    Compress(tex, like);
                    TryStoreCache(key, tex);
                    FinishLike(tex, like);
                    return tex;
                }, tex));
            });
        }

        // 失敗したら記録して、作りかけを捨てて null を返す。
        private static Texture2D Step(Func<Texture2D> work, Texture2D partial) {
            try {
                return work();
            } catch (Exception ex) {
                Debug.LogException(ex);
                if (partial != null) {
                    UnityEngine.Object.DestroyImmediate(partial);
                }
                return null;
            }
        }

        /// <summary>
        /// 原本を作業解像度(<see cref="WorkingResolution"/>)で読み、レシピの編集状態を当てた画素を返す。
        /// プレビューと同じ読み方・同じ処理なので、同じ編集状態ならいろかの画面の確定結果とバイト単位で一致する
        /// (作業解像度が元ファイルと同じときは「適用して保存」の書き出しとも一致する)。
        /// </summary>
        internal static bool TryRecolor(IrocaRecipe recipe, out Color32[] pixels, out int width, out int height,
            out Failure failure) {
            pixels = null; width = height = 0;
            if (!TryRecolorNative(recipe, out var native, out failure)) {
                return false;
            }
            try {
                pixels = native.ToArray();
                width = native.Width;
                height = native.Height;
            } finally {
                native.Release();
            }
            return true;
        }

        // TryRecolor と同じ画素を Mono のヒープの外に作る(ビルドが使う。参照 1 で返すので呼び出し側が Release する)。
        // 非破壊ビルド・シーンのプレビューはいろかのウィンドウを閉じた後にも走るので、管理配列で持つとその分
        // (4K で 64MB)Mono のヒープが広がったまま残る。
        private static bool TryRecolorNative(IrocaRecipe recipe, out NativePixels pixels, out Failure failure) {
            pixels = null;
            if (!TryLoadState(recipe, out var state, out failure)) {
                return false;
            }
            if (!TryReadPixels(recipe.sourceTexture, out pixels, out failure)) {
                return false;
            }
            try {
                Recolor(pixels, state);
            } catch {
                pixels.Release();
                pixels = null;
                throw;
            }
            return true;
        }

        // 画素をその場で色替えする(バックグラウンドスレッドから呼んでよい)。
        private static unsafe void Recolor(NativePixels pixels, IrocaSessionState state,
            CancellationToken cancellationToken = default)
            => SessionRecolor.Apply(pixels.Ptr, pixels.Width, pixels.Height, state, cancellationToken);

        // レシピの編集状態を読み、色替えするものがあるか確かめる(軽い)。
        private static bool TryLoadState(IrocaRecipe recipe, out IrocaSessionState state, out Failure failure) {
            state = null; failure = Failure.None;
            var src = recipe != null ? recipe.sourceTexture : null;
            if (src == null) { failure = Failure.NoSourceTexture; return false; }
            state = RecipeStore.Load(recipe);
            if (state == null) { failure = Failure.UnreadableRecipe; return false; }
            if (RecolorInput.CountEnabled(state.zones) == 0) { failure = Failure.NoEnabledZones; return false; }
            return true;
        }

        // 原本の画素を作業解像度で読む(4K で 0.2 秒ほど)。Unity の API を使うのでメインスレッドで呼ぶ
        // (シーンのプレビューの PNG は計算のスレッドで読むので、ここを通らない)。
        // 読めたら参照 1 の画素を返す(呼び出し側が Release する)。
        private static bool TryReadPixels(Texture2D src, out NativePixels pixels, out Failure failure) {
            failure = Failure.None;
            string srcPath = AssetDatabase.GetAssetPath(src);
            ExportPipeline.SourceKind kind;
            try {
                kind = ExportPipeline.ReadWorkingPixelsNative(srcPath, src, out pixels);
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                                         || ex is ArgumentException || ex is NotSupportedException) {
                // 原本のファイルを開けない(ほかのアプリが掴んでいる・同期サービスのオンライン専用ファイル・
                // Unity の外で消した等)。ビルドでは「元テクスチャを読めません」として報告する。
                Debug.LogWarning($"[Iroca] 元テクスチャのファイルを読めませんでした: {srcPath}\n{ex.Message}");
                pixels = null;
                failure = Failure.SourceUnreadable;
                return false;
            }
            if (!ExportPipeline.HasPixels(kind)) {
                failure = kind == ExportPipeline.SourceKind.Undecodable ? Failure.SourceUndecodable : Failure.SourceUnreadable;
                return false;
            }
            return true;
        }

        /// <summary>
        /// 取り込み済みの <paramref name="like"/> と同じ寸法・形式・ミップ有無・色空間のテクスチャを作る
        /// (読み書き可能のまま返す。サンプリング設定などは <see cref="FinishLike"/> でそろえる)。
        /// </summary>
        internal static unsafe Texture2D CreateMatching(Color32[] pixels, int width, int height, Texture2D like) {
            if (pixels.Length < (long)width * height) {
                throw new ArgumentException($"pixels.Length({pixels.Length}) < {width}x{height}", nameof(pixels));
            }
            Texture2D tex;
            fixed (Color32* p = pixels) {
                tex = CreateUncompressed(p, width, height, like);
            }
            Compress(tex, like);
            return tex;
        }

        private static Texture2D CreateMatching(NativePixels pixels, Texture2D like) {
            var tex = CreateUncompressed(pixels, like);
            Compress(tex, like);
            return tex;
        }

        private static unsafe Texture2D CreateUncompressed(NativePixels pixels, Texture2D like)
            => CreateUncompressed(pixels.Ptr, pixels.Width, pixels.Height, like);

        // 圧縮の前まで(寸法・ミップ有無・色空間を like にそろえた RGBA32)。画素(width*height)はテクスチャの
        // 一番上のミップへ直接書く(寸法が違えば縮めながら)。管理配列を経由しない。
        private static unsafe Texture2D CreateUncompressed(Color32* pixels, int width, int height, Texture2D like) {
            int dw = like.width, dh = like.height;
            bool linear = !GraphicsFormatUtility.IsSRGBFormat(like.graphicsFormat);
            bool mips = like.mipmapCount > 1;
            var tex = NewLike(dw, dh, TextureFormat.RGBA32, mips ? -1 : 1, linear, like);
            try {
                var top = (Color32*)NativeArrayUnsafeUtility.GetUnsafePtr(tex.GetPixelData<Color32>(0));
                if (dw == width && dh == height) {
                    long bytes = (long)dw * dh * sizeof(Color32);
                    Buffer.MemoryCopy(pixels, top, bytes, bytes);
                } else {
                    WorkingResolution.ResizeArea(pixels, width, height, dw, dh, top);
                }
                tex.Apply(updateMipmaps: mips, makeNoLongerReadable: false);
            } catch {
                UnityEngine.Object.DestroyImmediate(tex);
                throw;
            }
            return tex;
        }

        /// <summary>
        /// 差し替え用のテクスチャを作る。mipmap limit の設定(「Ignore Mipmap Limit」と Mipmap Limit Group)を
        /// <paramref name="like"/> から写す(利用者の画質設定の上限が元と同じように効く)。作るときにしか渡せない
        /// (グループは後から変えられない)ので、ここで作る。Unity 6 では既定の作り方だと上限を無視する
        /// (ignoreMipmapLimit = true)テクスチャになるので、写さないと低画質の設定が効かない(2022.3 は無視しない)。
        /// 画素は変わらないので、キャッシュの鍵には入れない。
        /// </summary>
        internal static Texture2D NewLike(int width, int height, TextureFormat format, int mipCount, bool linear,
            Texture2D like) {
#if UNITY_6000_0_OR_NEWER
            return new Texture2D(width, height, format, mipCount, linear, false,
                new MipmapLimitDescriptor(!like.ignoreMipmapLimit, like.mipmapLimitGroup));
#else
            return new Texture2D(width, height, format, mipCount, linear, false, like.ignoreMipmapLimit,
                like.mipmapLimitGroup);
#endif
        }

        // 取り込み時の形式へそろえる(アルファを取り込み設定どおりにしてから、圧縮形式なら圧縮、非圧縮なら同じ形式へ詰め替え)。
        // 形式を変える Reinitialize と CompressTexture は、2022.3 で「Ignore Mipmap Limit」を既定へ戻す(グループは残る。
        // NewLike で写したもの)ので、終わったら写し直す。
        private static void Compress(Texture2D tex, Texture2D like) {
            bool ignoreMipmapLimit = tex.ignoreMipmapLimit;
            try {
                ConvertFormat(tex, like);
            } finally {
                tex.ignoreMipmapLimit = ignoreMipmapLimit;
            }
        }

        private static void ConvertFormat(Texture2D tex, Texture2D like) {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(like)) as TextureImporter;
            ApplyAlphaSource(tex, importer);
            var target = like.format;
            if (target == tex.format) {
                return;
            }
            if (!GraphicsFormatUtility.IsCompressedFormat(like.graphicsFormat)) {
                ConvertUncompressed(tex, target);
                return;
            }
            int quality = 50;
            if (importer != null) {
                quality = Mathf.Clamp(importer.compressionQuality, 0, 100);
            }
            try {
                EditorUtility.CompressTexture(tex, target, quality);
            } catch (Exception) when (CrunchFallback(target) != target) {
                // Crunch 圧縮をエディタ上で再現できない環境では、同じ VRAM 量の非 Crunch 形式にする
                // (Crunch が減らすのはダウンロードサイズで、VRAM は変わらない)。
                EditorUtility.CompressTexture(tex, CrunchFallback(target), quality);
            }
        }

        /// <summary>
        /// 原本ファイル(PNG・JPG)から読んだ画素に、取り込み設定の Alpha Source を取り込みと同じく当てる。原本ファイルは
        /// 取り込み設定を通さずに読む(<see cref="ExportPipeline.ReadWorkingPixelsNative"/>)ので、当てないと None でも PNG の
        /// アルファが戻り、アルファを使うシェーダーで元には無かった透けや穴が出る。None ならアルファを捨てて不透明に、
        /// From Gray Scale なら RGB の平均をアルファにする(Unity の説明どおり)。色替えはアルファも見て選ぶので、
        /// 色替えの後(出来上がり)に当てる。色替えした画像を同じ取り込み設定で取り込んだとき(書き出し)と同じになる。
        /// PSD・TGA などは取り込み済みテクスチャの画素を読むので、Unity が当て済み。
        /// </summary>
        internal static unsafe void ApplyAlphaSource(Texture2D tex, TextureImporter importer) {
            if (importer == null || importer.alphaSource == TextureImporterAlphaSource.FromInput) {
                return;
            }
            if (tex.format != TextureFormat.RGBA32 || !ReadsSourceFile(importer.assetPath)) {
                return;
            }
            bool gray = importer.alphaSource == TextureImporterAlphaSource.FromGrayScale;
            var top = (Color32*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility
                .GetUnsafePtr(tex.GetPixelData<Color32>(0));
            // シーンへ上げる前にメインスレッドで全画素を回すので、行の範囲に分けて並列に書く(画素ごとに独立)。
            int w = tex.width, h = tex.height;
            System.Threading.Tasks.Parallel.For(0, h,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = CoreParallelism.Max() }, y => {
                    for (int i = y * w, end = i + w; i < end; i++) {
                        top[i].a = gray ? (byte)((top[i].r + top[i].g + top[i].b + 1) / 3) : (byte)255;
                    }
                });
            // ミップのアルファも作り直す(RGB は同じ一番上から作るので変わらない)。
            tex.Apply(updateMipmaps: tex.mipmapCount > 1, makeNoLongerReadable: false);
        }

        // 原本ファイルをそのまま読む形式か(LoadImage が読める PNG・JPG。ExportPipeline.ReadSourcePixels と同じ)。
        private static bool ReadsSourceFile(string assetPath) {
            string ext = Path.GetExtension(assetPath).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg";
        }

        /// <summary>
        /// 非圧縮の取り込み形式(アルファの無い画像なら RGB24 など)へ詰め替える。RGBA32 のまま渡すと、アップロードする
        /// 大きさが元より増える(RGB24 なら 1.33 倍)。各ミップの画素はそのまま写す(作り直さない)。
        /// 詰め替えられない形式なら RGBA32 のまま残す。
        /// </summary>
        private static unsafe void ConvertUncompressed(Texture2D tex, TextureFormat target) {
            if (tex.format != TextureFormat.RGBA32) {
                return;
            }
            int mips = tex.mipmapCount;
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat);
            var original = tex.graphicsFormat;
            var levels = new Unity.Collections.NativeArray<Color32>[mips];
            try {
                for (int m = 0; m < mips; m++) {
                    levels[m] = new Unity.Collections.NativeArray<Color32>(tex.GetPixelData<Color32>(m),
                        Unity.Collections.Allocator.Persistent);
                }
                try {
                    tex.Reinitialize(tex.width, tex.height, GraphicsFormatUtility.GetGraphicsFormat(target, srgb), mips > 1);
                    for (int m = 0; m < mips; m++) {
                        if (target == TextureFormat.RGB24) {
                            // いちばん多い形(アルファの無い画像を圧縮なしで取り込んだもの)は、管理配列を作らずに詰める。
                            var dst = (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility
                                .GetUnsafePtr(tex.GetPixelData<byte>(m));
                            var src = (Color32*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility
                                .GetUnsafeReadOnlyPtr(levels[m]);
                            for (int i = 0, n = levels[m].Length; i < n; i++, dst += 3) {
                                dst[0] = src[i].r;
                                dst[1] = src[i].g;
                                dst[2] = src[i].b;
                            }
                        } else {
                            tex.SetPixels32(levels[m].ToArray(), m);   // まれな形式(RGB565 など)は Unity に変換させる
                        }
                    }
                    tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                } catch (Exception ex) {
                    // SetPixels32 が扱えない形式など。RGBA32 に戻して中身も戻す(大きさが増えるだけで、見た目は同じ)。
                    Debug.LogWarning($"[Iroca] 色替えしたテクスチャを {target} にできなかったので RGBA32 のままにします: {ex.Message}");
                    tex.Reinitialize(tex.width, tex.height, original, mips > 1);
                    for (int m = 0; m < mips; m++) {
                        tex.GetPixelData<Color32>(m).CopyFrom(levels[m]);
                    }
                    tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                }
            } finally {
                foreach (var level in levels) {
                    if (level.IsCreated) {
                        level.Dispose();
                    }
                }
            }
        }

        private static TextureFormat CrunchFallback(TextureFormat f) {
            switch (f) {
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
        internal static void FinishLike(Texture2D tex, Texture2D like) {
            tex.name = like.name + " (Iroca)";
            tex.filterMode = like.filterMode;
            tex.wrapModeU = like.wrapModeU;
            tex.wrapModeV = like.wrapModeV;
            tex.wrapModeW = like.wrapModeW;
            tex.anisoLevel = like.anisoLevel;
            tex.mipMapBias = like.mipMapBias;
            if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(like)) is TextureImporter importer
                && importer.streamingMipmaps) {
                // 実行時に作った Texture2D には mip streaming の公開 API が無いのでシリアライズ値を直接立てる。
                var so = new SerializedObject(tex);
                var streaming = so.FindProperty("m_StreamingMipmaps");
                var priority = so.FindProperty("m_StreamingMipmapsPriority");
                if (streaming != null) {
                    streaming.boolValue = true;
                }
                if (priority != null) {
                    priority.intValue = importer.streamingMipmapsPriority;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            if (!like.isReadable) {
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            }
        }

        // ───────────── キャッシュ(Library/Iroca/RecipeTextureCache) ─────────────
        // 鍵 = 入力の鍵(元テクスチャの取り込み結果(ファイル内容と取り込み設定) + レシピの中身 + 出来上がりの形式)
        //      + いろかのコードの同一性(Core と Services の MVID・Unity の版)。どれかが変われば別の鍵になる。

        /// <summary>
        /// いろかのコードの同一性(キャッシュの鍵のうち、入力でない部分)。出来上がりは色替えの計算(Core)だけでなく、原本の
        /// 読み込み・縮小・Alpha Source・形式の詰め替え(Services の RecipeTextureBuilder・ExportPipeline・TextureIO・
        /// PngFileDecoder・WorkingResolution)でも変わりうるので、両方のアセンブリの MVID を入れる(再コンパイルのたびに変わる)。
        /// 圧縮(CompressTexture)の結果は Unity の版で変わりうるのに Library/Iroca は版を上げても消えないので、Unity の版も入れる。
        /// </summary>
        internal static readonly string CodeIdentity =
            typeof(SessionRecolor).Assembly.ManifestModule.ModuleVersionId + "|"
            + typeof(RecipeTextureBuilder).Assembly.ManifestModule.ModuleVersionId + "|"
            + Application.unityVersion;

        /// <summary>キャッシュの鍵(<see cref="InputsKey"/> と今のコードの同一性 <see cref="CodeIdentity"/>)。</summary>
        internal static string CacheKey(IrocaRecipe recipe, Texture2D like) => CacheKey(recipe, like, CodeIdentity);

        /// <summary>
        /// コードの同一性を渡す版(テストが「コードが違えば鍵も変わる」を確かめる)。鍵 = 「キャッシュの形式 + コードの同一性 +
        /// 入力の鍵」の SHA256。
        /// </summary>
        internal static string CacheKey(IrocaRecipe recipe, Texture2D like, string codeIdentity) {
            var memo = InputsMemo(recipe, like);
            // 同じコードで前に求めた鍵があれば使う(シーンのプレビューは差し替え枠ごとに呼ぶ)。
            if (ReferenceEquals(memo.code, codeIdentity) && memo.key != null) {
                return memo.key;
            }
            string key = Sha256Hex(CacheFormatVersion + "|" + codeIdentity + "|" + memo.inputsKey, null);
            memo.code = codeIdentity;
            memo.key = key;
            return key;
        }

        /// <summary>
        /// 入力の鍵: 元テクスチャの取り込み結果(ファイル内容と取り込み設定のハッシュ)・寸法・形式・ミップ・レシピの formatVersion・
        /// 作業解像度の規則・レシピの中身(sessionJson)の SHA256。いろかのコードは入れない(コードが変わっても入力は同じと
        /// 分かるように。D-1 の「古くなった」の判定に使う)。
        /// </summary>
        internal static string InputsKey(IrocaRecipe recipe, Texture2D like) => InputsMemo(recipe, like).inputsKey;

        private static KeyMemo InputsMemo(IrocaRecipe recipe, Texture2D like) {
            string srcPath = AssetDatabase.GetAssetPath(like);
            var depHash = AssetDatabase.GetAssetDependencyHash(srcPath);
            string json = recipe.sessionJson;
            int formatVersion = recipe.formatVersion;
            int w = like.width, h = like.height, mips = like.mipmapCount;
            var format = like.format;
            var graphicsFormat = like.graphicsFormat;

            // シーンのプレビューは差し替え枠ごと・入力が変わるたびにここを呼ぶ。sessionJson はマスクの base64 を含み
            // 4K で数 MB あるので、同じ入力なら前に求めた鍵を返す。sessionJson は文字列の参照で見分ける(同じ参照なら
            // 中身も同じ。弱い参照の表なので、レシピが文字列を手放せば覚えた分も消える)。ほかの入力(取り込み結果の
            // ハッシュ・寸法・形式・ミップ・formatVersion)は毎回読んで比べ、1 つでも違えば計算し直す。
            // 作業解像度の上限はドメインの中で変わらない(リロードで static ごと作り直す)。
            if (json != null && s_keyMemo.TryGetValue(json, out var memo)
                && memo.depHash == depHash && memo.formatVersion == formatVersion
                && memo.width == w && memo.height == h && memo.mipmapCount == mips
                && memo.format == format && memo.graphicsFormat == graphicsFormat) {
                return memo;
            }

            var sb = new StringBuilder();
            sb.Append(depHash.ToString()).Append('|');
            sb.Append(w).Append('x').Append(h).Append('|');
            // 作業解像度の規則(WorkingResolution)。2026-10-25 に、元ファイルの辺が上限以下なら元の解像度で処理する
            // ように変えた(それまでの "src" / "work" と別の鍵にして、前の規則で作った出来上がりを使わない)。
            sb.Append("src").Append(WorkingResolution.MaxSourceSide).Append('|');
            sb.Append((int)format).Append('|').Append((int)graphicsFormat).Append('|');
            sb.Append(mips).Append('|');
            sb.Append(formatVersion).Append('|');
            // 「前置き + sessionJson」をつないだ文字列の UTF-8 の SHA256。sessionJson をつないだ写し・UTF-8 のバイト列を
            // 丸ごと作らず、小さい作業配列で少しずつハッシュにかける(Sha256Hex)。
            var fresh = new KeyMemo {
                depHash = depHash,
                formatVersion = formatVersion,
                width = w,
                height = h,
                mipmapCount = mips,
                format = format,
                graphicsFormat = graphicsFormat,
                inputsKey = Sha256Hex(sb.ToString(), json),
            };
            if (json != null) {
                s_keyMemo.Remove(json);
                s_keyMemo.Add(json, fresh);
            }
            return fresh;
        }

        // InputsMemo の覚え(sessionJson の文字列の参照 → そのとき使ったほかの入力と入力の鍵、最後に求めた鍵とそのコードの同一性)。
        // メインスレッドからしか触らないが、ConditionalWeakTable はスレッド安全なのでどこから触っても壊れない。
        private sealed class KeyMemo {
            public Hash128 depHash;
            public int formatVersion, width, height, mipmapCount;
            public TextureFormat format;
            public GraphicsFormat graphicsFormat;
            public string inputsKey;
            public string code, key;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, KeyMemo> s_keyMemo =
            new System.Runtime.CompilerServices.ConditionalWeakTable<string, KeyMemo>();

        // a と b(null は空)をつないだ文字列の UTF-8 の SHA256 を小文字の 16 進で返す
        // (= SHA256(Encoding.UTF8.GetBytes(a + b)))。4K 文字ずつ符号化してハッシュにかける。Encoder は区切りを
        // またぐサロゲートの組を持ち越し、組になっていないものは GetBytes と同じく置き換え文字にする。
        internal static string Sha256Hex(string a, string b) {
            const int CharChunk = 4096;
            var encoding = Encoding.UTF8;
            var encoder = encoding.GetEncoder();
            var bytes = new byte[encoding.GetMaxByteCount(CharChunk)];
            var chunk = new char[CharChunk];
            using (var sha = SHA256.Create()) {
                // 1 つの Encoder で続けて符号化し、最後だけ flush する(つないだ文字列を符号化するのと同じ)。
                void Feed(string s, bool flushAtEnd) {
                    if (s == null) {
                        s = string.Empty;
                    }
                    for (int i = 0; ;) {
                        int n = Math.Min(chunk.Length, s.Length - i);
                        s.CopyTo(i, chunk, 0, n);
                        i += n;
                        bool end = i >= s.Length;
                        int count = encoder.GetBytes(chunk, 0, n, bytes, 0, flushAtEnd && end);
                        if (count > 0) {
                            sha.TransformBlock(bytes, 0, count, null, 0);
                        }
                        if (end) {
                            break;
                        }
                    }
                }
                Feed(a, flushAtEnd: false);
                Feed(b, flushAtEnd: true);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hash = sha.Hash;
                var hex = new StringBuilder(hash.Length * 2);
                foreach (var x in hash) {
                    hex.Append(x.ToString("x2"));
                }
                return hex.ToString();
            }
        }

        // テストもこれでキャッシュファイルを特定する(ファイル名の規則を 1 か所に保つ)。
        internal static string CachePath(string key) => Path.Combine(CacheDir, key + ".tex");

        // 中身はテクスチャの CPU 側のデータとファイルの間で直接写す(管理配列を経由しない)。再生のたびに読むので、
        // 管理配列にすると 1 枚ごと(4K の BC7 で 21MB、非圧縮なら 85MB)の一時配列で Mono のヒープが広がる。
        private static unsafe Texture2D TryLoadCache(string key, Texture2D like) {
            WaitForPendingStore(key);
            string path = CachePath(key);
            if (!File.Exists(path)) {
                return null;
            }
            Texture2D tex = null;
            try {
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs)) {
                    if (br.ReadInt32() != CacheFormatVersion) {
                        return null;
                    }
                    int w = br.ReadInt32(), h = br.ReadInt32(), fmt = br.ReadInt32();
                    int mipCount = br.ReadInt32();
                    bool linear = br.ReadBoolean();
                    int len = br.ReadInt32();
                    if (len < 0 || fs.Length - fs.Position < len) {
                        return null;   // 書きかけ
                    }
                    // 計算の途中で元テクスチャの取り込み設定が変わると、前の設定の鍵で今の寸法の結果が入っていることがある。
                    if (w != like.width || h != like.height || mipCount != like.mipmapCount) {
                        throw new InvalidDataException($"cache size {w}x{h} (mips {mipCount}) != texture {like.width}x{like.height} (mips {like.mipmapCount})");
                    }
                    tex = NewLike(w, h, (TextureFormat)fmt, mipCount, linear, like);
                    var raw = tex.GetRawTextureData<byte>();
                    if (raw.Length != len) {
                        throw new InvalidDataException($"cache size {len} != texture data size {raw.Length}");
                    }
                    using (var dst = new UnmanagedMemoryStream((byte*)NativeArrayUnsafeUtility.GetUnsafePtr(raw), len, len, FileAccess.Write)) {
                        CopyExactly(fs, dst, len);
                    }
                }
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);   // 使ったものを残す(古い順に消す)
                FinishLike(tex, like);
                return tex;
            } catch (Exception ex) {
                if (tex != null) {
                    UnityEngine.Object.DestroyImmediate(tex);
                }
                Debug.LogWarning($"[Iroca] テクスチャのキャッシュを読めなかったので作り直します: {ex.Message}");
                try { File.Delete(path); } catch (Exception) { /* 次回また作り直すだけ */ }
                return null;
            }
        }

        // ちょうど count バイトを写す(小さい作業配列 1 つで。足りなければ投げる)。
        private static void CopyExactly(Stream from, Stream to, long count) {
            var buffer = new byte[64 * 1024];
            while (count > 0) {
                int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (n <= 0) {
                    throw new EndOfStreamException();
                }
                to.Write(buffer, 0, n);
                count -= n;
            }
        }

        // ───────────── キャッシュの書き込み(背景) ─────────────
        // 1 枚(4K の BC7 で 21MB)を書いてデバイスまで書き切る(AtomicFile の Flush(true))のは、メインスレッドで
        // 数十 ms 以上かかる(シーンのプレビューでは仕上げの tick がそのぶん止まる)。テクスチャの生データをヒープの外へ
        // 写したら(数 ms)、書き込みは背景で 1 本ずつ順に行う(同じ一時ファイル .tmp と TrimCache を取り合わない)。
        // ファイルは AtomicFile が書き切ってから置き換えるので、書きかけが見えることはない。書き終える前に同じ鍵を
        // 読みに来たら(TryLoadCache)、書き終えるのを待ってから読む(作り直さない)。リロードの前・エディタを
        // 閉じる前も書き終えるのを待つ。

        // 最後に投げた書き込みと、書いている途中の鍵(どちらもメインスレッドからしか触らない)。
        private static Task s_lastStore = Task.CompletedTask;
        private static readonly Dictionary<string, Task> s_pendingStores = new Dictionary<string, Task>();
        // リロード・終了の前に書き終えるのを待つ上限(ミリ秒)。
        private const int StoreDrainMilliseconds = 10_000;

        [InitializeOnLoadMethod]
        private static void InstallStoreDrain() {
            AssemblyReloadEvents.beforeAssemblyReload -= DrainStores;
            AssemblyReloadEvents.beforeAssemblyReload += DrainStores;
            EditorApplication.quitting -= DrainStores;
            EditorApplication.quitting += DrainStores;
        }

        // リロード・終了の前に呼ばれる。ここで投げると他ツールの後始末が呼ばれないので止める。
        private static void DrainStores() {
            try { WaitForCacheWrites(StoreDrainMilliseconds); } catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>背景のキャッシュの書き込みが全部終わるまで待つ(メインスレッド。テストも使う)。終われば true。</summary>
        internal static bool WaitForCacheWrites(int milliseconds = Timeout.Infinite) {
            bool done = s_lastStore.Wait(milliseconds);
            if (done) {
                s_pendingStores.Clear();
            }
            return done;
        }

        // その鍵を書いている途中なら、書き終えるまで待つ(メインスレッド)。
        private static void WaitForPendingStore(string key) {
            if (!s_pendingStores.TryGetValue(key, out var pending)) {
                return;
            }
            pending.Wait();   // 書き込みの例外は中で受けるので、ここへは出ない
            s_pendingStores.Remove(key);
        }

        private static unsafe void TryStoreCache(string key, Texture2D tex) {
            NativeBuffer data = null;
            try {
                // 書き終えた分を一覧から外す(ここと読むときにしか見ないので、溜めない)。
                if (s_pendingStores.Count > 0) {
                    var done = new List<string>();
                    foreach (var kv in s_pendingStores) {
                        if (kv.Value.IsCompleted) {
                            done.Add(kv.Key);
                        }
                    }
                    foreach (var k in done) {
                        s_pendingStores.Remove(k);
                    }
                }
                // FinishLike で読み書き不可にすると CPU 側のデータが消えるので、ここで写す。
                var raw = tex.GetRawTextureData<byte>();
                int len = raw.Length;
                data = new NativeBuffer(Math.Max(1, len), NativeUsage.Other);
                Buffer.MemoryCopy(NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(raw), (void*)data.Ptr, len, len);
                var header = new CacheHeader {
                    width = tex.width,
                    height = tex.height,
                    format = (int)tex.format,
                    mipCount = tex.mipmapCount,
                    linear = !GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat),
                    length = len,
                };
                string dir = CacheDir, path = CachePath(key);
                var buffer = data;
                data = null;
                var store = s_lastStore.ContinueWith(_ => WriteCacheFile(dir, path, buffer, header),
                    CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
                s_lastStore = store;
                s_pendingStores[key] = store;
            } catch (Exception ex) {
                data?.Release();
                // キャッシュは速さのためだけ。書けなくても出来上がりは正しい。
                Debug.LogWarning($"[Iroca] テクスチャのキャッシュを書けませんでした: {ex.Message}");
            }
        }

        private struct CacheHeader {
            public int width, height, format, mipCount, length;
            public bool linear;
        }

        // 背景のスレッド。中身(buffer)は書き終えたら手放す。失敗は警告に残すだけ(Debug.Log はどのスレッドからでもよい)。
        private static unsafe void WriteCacheFile(string dir, string path, NativeBuffer buffer, CacheHeader h) {
            try {
                Directory.CreateDirectory(dir);
                AtomicFile.WriteAll(path, fs => {
                    using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true)) {
                        bw.Write(CacheFormatVersion);
                        bw.Write(h.width); bw.Write(h.height); bw.Write(h.format);
                        bw.Write(h.mipCount);
                        bw.Write(h.linear);
                        bw.Write(h.length);
                    }
                    using (var src = new UnmanagedMemoryStream((byte*)buffer.Ptr, h.length)) {
                        CopyExactly(src, fs, h.length);
                    }
                });
                TrimCache(dir);
            } catch (Exception ex) {
                Debug.LogWarning($"[Iroca] テクスチャのキャッシュを書けませんでした: {ex.Message}");
            } finally {
                buffer.Release();
            }
        }

        // 合計が上限を超えたら、最後に使った時刻が古いものから消す(背景の書き込みのスレッドで呼ぶ。
        // メインスレッドが同時に読んでいるファイルは消せずに残るか、読む側が読めなかったものとして作り直す)。
        private static void TrimCache(string dir) {
            var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles("*.tex"));
            long total = 0;
            foreach (var f in files) {
                total += f.Length;
            }
            if (total <= CacheMaxBytes) {
                return;
            }
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var f in files) {
                if (total <= CacheMaxBytes) {
                    break;
                }
                try { total -= f.Length; f.Delete(); } catch (Exception) { /* 使用中などは次回 */ }
            }
        }
    }
}
