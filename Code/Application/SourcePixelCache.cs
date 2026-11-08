// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>原本の画素の今の状態(<see cref="SourcePixelCache.LoadInBackground"/>)。</summary>
    internal enum SourceState {
        /// <summary>画素を持っている。</summary>
        Ready,
        /// <summary>別スレッドで読んでいる途中。</summary>
        Loading,
        /// <summary>画素を取れない(元ファイルも取り込み済みテクスチャも読めない)。</summary>
        Unavailable,
    }

    /// <summary>
    /// 色替えに使う原本の画素(作業解像度。<see cref="WorkingResolution"/>)を、テクスチャ 1 枚分だけ持つ
    /// (0.4.0 の計画の 4-2 の 1。PreviewView が持っていた原本を切り出した)。
    /// 取り込み済みテクスチャの圧縮で劣化した画素ではなく、元ファイルをデコードした画素(辺が 8192 を超えるときだけ
    /// 取り込み後の寸法へ縮めたもの)を持つ。書き出し・非破壊ビルドも同じ解像度で処理するので、色替えの中身が一致する。
    /// 元ファイルを読めないもの(PSD など)は、取り込み済みテクスチャ(Read/Write が有効なとき)の画素を持つ。
    /// 画素は Mono のヒープの外に置き(4K で 64MB、8K で 256MB)、ここが参照を 1 つ持つ。別スレッドで読む
    /// ジョブ・AI 提案・自動調整はそれぞれ参照を足して持つので、ここで手放しても読んでいる途中で消えない。
    /// 読み直すかどうか(別のテクスチャ・取り込み後の寸法が変わった)と、原本から導いた状態を捨てるのは呼び出し側が決める。
    /// メインスレッド専用(読む途中の別スレッドの仕事は <see cref="RunLoad"/> だけ)。
    /// <para>
    /// PNG は別スレッドで読める(<see cref="LoadInBackground"/>。4K で 150〜320ms、8K で 560ms メインスレッドを止めていた)。
    /// 読む途中で要らなくなったら(テクスチャの切り替え・閉じる・リロード)行ごとの取り消しで止まり、読みかけの画素はその場で
    /// 手放す。読む順は 1 本ずつなので、読みかけが何枚分も重ならない。
    /// </para>
    /// </summary>
    internal sealed class SourcePixelCache {
        private Texture2D _for;
        private NativePixels _pixels;
        private int _w, _h;
        // 作業解像度を決めた前提(取り込み後の寸法)。変わったら呼び出し側が読み直す。
        private int _basisW, _basisH;
        // 元ファイルをデコードした(false = 取り込み済みテクスチャの画素。PSD など)。
        private bool _fromFile;
        // 直近に画素を取れなかったテクスチャ(毎フレームの可否判定で読み直さないための負のキャッシュ)。
        private Texture2D _failedFor;
        // 元ファイルを読み始める前に見たファイルの印(大きさと更新時刻)。書き出し・自動化が、同じ中身のファイルを
        // 読み直さずにこの画素を使えるかの判断に使う(ShareIfCurrent)。取り込み済みテクスチャの画素なら default。
        private FileStamp _stamp;

        // 別スレッドで読んでいる途中のもの。別スレッドとは lock(load) の中の値だけで受け渡す。
        private sealed class PendingLoad {
            public Texture2D Texture;
            public string Path;
            public int BasisW, BasisH;
            public FileStamp Stamp;
            public Action Finished;
            // CancellationTokenSource は待ちハンドルを作らない使い方なので Dispose しない(読み手が取り消しを見るのと競合する)。
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            // ここから下は lock(load) の中で読み書きする。
            public NativePixels Result;
            public bool Done;
            public bool Abandoned;
        }

        private PendingLoad _pending;
        // 最後に投げた読み込み。次の読み込みはこれの後ろにつなぐ(読みかけの画素が重ならない)。
        private Task _lastLoad = Task.CompletedTask;

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
        /// tex の画素を止まらずに読む。初めて呼ぶと別スレッドで PNG を読み始めて <see cref="SourceState.Loading"/> を返す。
        /// 読み終えていれば受け取って <see cref="SourceState.Ready"/>。PNG でなければ(LoadImage と GPU の展開はメインスレッド
        /// 専用)その次に呼ばれたときにメインスレッドで読む。<paramref name="finished"/> は読み終えたらメインスレッドの次の
        /// tick で呼ぶ(途中で要らなくなったときは呼ばない)。呼び先は <see cref="TryAdoptFinished"/> で受け取る。
        /// </summary>
        internal SourceState LoadInBackground(Texture2D tex, Action finished) {
            if (_pending != null && _pending.Texture != tex) {
                CancelPending();
            }
            if (_pending == null && TryBeginLoad(tex, finished)) {
                return SourceState.Loading;
            }
            if (_pending != null) {
                lock (_pending) {
                    if (!_pending.Done) {
                        return SourceState.Loading;
                    }
                }
                if (AdoptFinished()) {
                    return SourceState.Ready;
                }
            }
            return LoadOnMainThread(tex) ? SourceState.Ready : SourceState.Unavailable;
        }

        /// <summary>
        /// 別スレッドの読み込みが tex の画素を読み終えていれば受け取る。受け取ったら true。読めなかった(PNG でないなど)ときは
        /// false のまま覚えておき、次の <see cref="LoadInBackground"/> がメインスレッドで読む。別のテクスチャのものは取り消す。
        /// </summary>
        internal bool TryAdoptFinished(Texture2D tex) {
            if (_pending == null) {
                return false;
            }
            // 消えたテクスチャと null は Unity の比較では同じに見えるので、null を先に見る。
            if (tex == null || _pending.Texture != tex) {
                CancelPending();
                return false;
            }
            lock (_pending) {
                if (!_pending.Done || _pending.Result == null) {
                    return false;
                }
            }
            return AdoptFinished();
        }

        private bool TryBeginLoad(Texture2D tex, Action finished) {
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) {
                return false;
            }
            var load = new PendingLoad {
                Texture = tex,
                Path = path,
                BasisW = tex.width,
                BasisH = tex.height,
                // 読む前に見る(読んでいる間に書き換わったら、後で見る印と食い違うので使い回さない)。
                Stamp = FileStamp.Of(path),
                Finished = finished,
            };
            _pending = load;
            _lastLoad = _lastLoad.ContinueWith(_ => RunLoad(load), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            return true;
        }

        // 別スレッド側。Unity の API も、このクラスの load の外の状態も触らない(_pending を見るのはメインスレッドへ戻してから)。
        private void RunLoad(PendingLoad load) {
            NativePixels pixels = null;
            try {
                load.Cancel.Token.ThrowIfCancellationRequested();
                pixels = TextureIO.TryDecodePngOffMainThread(load.Path, load.BasisW, load.BasisH, load.Cancel.Token);
            } catch (OperationCanceledException) { /* 読みかけの画素はデコーダが手放した */ }
            // ファイルを開けないなど。メインスレッドの経路で読み直すので、そちらが同じ失敗を警告する。
            catch (Exception) { }

            bool abandoned;
            lock (load) {
                abandoned = load.Abandoned;
                if (!abandoned) {
                    load.Result = pixels;
                    load.Done = true;
                }
            }
            if (abandoned) {
                pixels?.Release();
                return;
            }
            // 知らせが届くまでに取り消された・待って受け取られたものは知らせない(閉じたウィンドウを呼ばない)。
            PreviewJobMainThread.Post(() => {
                if (ReferenceEquals(_pending, load)) {
                    load.Finished();
                }
            });
        }

        // 読み終えた読み込みを受け取る。画素を受け取れたら true。false ならメインスレッドの経路で読む。
        private bool AdoptFinished() {
            var load = _pending;
            _pending = null;
            NativePixels pixels;
            lock (load) {
                pixels = load.Result;
                load.Result = null;
            }
            if (pixels == null) {
                return false;
            }
            // 読んでいる間に取り込み後の寸法が変わった(作業解像度の前提が違う)。
            if (load.Texture.width != load.BasisW || load.Texture.height != load.BasisH) {
                pixels.Release();
                return false;
            }
            Set(load.Texture, pixels, fromFile: true, load.Stamp);
            return true;
        }

        // 読んでいる途中のものを捨てる。読み終えた画素はここで、読みかけの画素は読み手が次の行で手放す。
        private void CancelPending() {
            var load = _pending;
            if (load == null) {
                return;
            }
            _pending = null;
            NativePixels pixels;
            lock (load) {
                load.Abandoned = true;
                pixels = load.Result;
                load.Result = null;
            }
            load.Cancel.Cancel();
            pixels?.Release();
        }

        /// <summary>
        /// tex の画素を読む(返るまで待つ)。別スレッドで読んでいる途中なら、それが終わるのを待って受け取る。
        /// 読めたら前の画素を手放して差し替え、true を返す。どちらでも取れなければ失敗を覚えて false(前の画素はそのまま)。
        /// </summary>
        internal bool Load(Texture2D tex) {
            if (_pending != null) {
                if (_pending.Texture == tex) {
                    _lastLoad.Wait();
                    if (AdoptFinished()) {
                        return true;
                    }
                } else {
                    CancelPending();
                }
            }
            return LoadOnMainThread(tex);
        }

        // 可能ならディスク上の元ファイルをデコードして作業解像度にそろえ、PNG/JPG 以外や生成テクスチャ等で失敗した場合は
        // インポート済みテクスチャの画素にフォールバックする。
        private bool LoadOnMainThread(Texture2D tex) {
            string path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) {
                try {
                    var stamp = FileStamp.Of(path);
                    var decoded = TextureIO.DecodeSourceFile(path, tex);
                    if (decoded != null) {
                        Set(tex, decoded, fromFile: true, stamp);
                        return true;
                    }
                } catch (System.Exception ex) {
                    Debug.LogWarning($"[Iroca] Source file load failed, falling back to imported texture: {ex.Message}");
                }
            }

            // フォールバック: インポート済みテクスチャ（要 Read/Write）。
            if (!TextureIO.IsReadable(tex)) {
                _failedFor = tex;
                return false;
            }
            Set(tex, WorkingResolution.NativePixelsOf(tex, null), fromFile: false, default);
            return true;
        }

        // 画素を差し替える(前の画素の参照を返し、新しい画素の参照を持つ)。取り込み後の寸法も覚える。
        private void Set(Texture2D tex, NativePixels pixels, bool fromFile, FileStamp stamp) {
            Release();
            _pixels = pixels;
            _w = pixels.Width;
            _h = pixels.Height;
            _for = tex;
            _failedFor = null;
            _basisW = tex.width;
            _basisH = tex.height;
            _fromFile = fromFile;
            _stamp = fromFile ? stamp : default;
        }

        /// <summary>
        /// 持っている画素が、いま <paramref name="path"/>(<paramref name="tex"/> の元ファイル)を作業解像度で読み直したものと
        /// 同じと確かめられるなら、参照を 1 つ足して返す(呼んだ側が Release する)。確かめられなければ null(読み直す)。
        /// 確かめること: 同じテクスチャの元ファイルをデコードした画素で、取り込み後の寸法(作業解像度の前提)が今と同じ、
        /// ファイルの大きさと更新時刻が読む前に見たものと同じ。返す画素はプレビューと共有なので書き換えないこと
        /// (色替えするなら写してから)。メインスレッド専用。
        /// </summary>
        internal NativePixels ShareIfCurrent(Texture2D tex, string path) {
            if (tex == null || _for != tex || _pixels == null || !_fromFile || !_stamp.IsValid) {
                return null;
            }
            if (_basisW != tex.width || _basisH != tex.height) {
                return null;
            }
            if (string.IsNullOrEmpty(path) || !_stamp.Equals(FileStamp.Of(path))) {
                return null;
            }
            return _pixels.AddRef();
        }

        /// <summary>ファイルの大きさと更新時刻(読めなければ無効)。</summary>
        internal readonly struct FileStamp : IEquatable<FileStamp> {
            public readonly long Length;
            public readonly long WriteTicks;

            private FileStamp(long length, long writeTicks) {
                Length = length;
                WriteTicks = writeTicks;
            }

            public bool IsValid => WriteTicks != 0;

            public static FileStamp Of(string path) {
                try {
                    var info = new System.IO.FileInfo(path);
                    return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : default;
                } catch (Exception) { return default; }
            }

            public bool Equals(FileStamp other) => IsValid && Length == other.Length && WriteTicks == other.WriteTicks;
            public override bool Equals(object obj) => obj is FileStamp o && Equals(o);
            public override int GetHashCode() => (Length.GetHashCode() * 397) ^ WriteTicks.GetHashCode();
        }

        // 画素の参照を返す。読んでいる途中のジョブは自分の参照を持っているので、ここで返しても消えない。
        private void Release() {
            var old = _pixels;
            _pixels = null;
            _w = _h = 0;
            old?.Release();
        }

        /// <summary>
        /// 画素を手放し、どのテクスチャを読んだか・読めなかったかも忘れる(テクスチャの差し替え・閉じるとき)。
        /// 別スレッドで読んでいる途中のものも取り消す。
        /// </summary>
        internal void Clear() {
            CancelPending();
            _for = null;
            Release();
            _failedFor = null;
        }

        /// <summary>
        /// 取り消した読み込みが抜けるのを待つ(閉じる・リロードの前。抜ければ読みかけの画素は OS へ戻っている)。
        /// 行ごとに取り消しを見るので普通は数 ms。待てる長さで打ち切っても、抜けたときに読み手が手放す。
        /// </summary>
        internal void WaitForCancelledLoads(int milliseconds) => _lastLoad.Wait(milliseconds);
    }
}
