// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
// AI 提案の推論(ISamInference)の onnxruntime(CPU)の実装。ハーネスだけが持つ(Core は onnxruntime を知らない)。
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Iroca {
    /// <summary>
    /// 製品と同じ MobileSAM の ONNX(エンコーダ・デコーダ)を onnxruntime の CPU で走らせる <see cref="ISamInference"/>。
    /// 製品(Sentis)と違うのはモデルを走らせる所だけで、前処理(<see cref="SamImageOps.BuildEncoderInputInto"/>)・座標・
    /// 後処理・段取りは製品の C# をそのまま通す。入出力の名前と形は製品の呼び方(エンコーダ: 入力 1 つ → 埋め込み
    /// [1,256,64,64]。デコーダ: 埋め込み・点 [1,2,2]・ラベル [1,2] → low_res_logits [1,4,256,256]・iou_predictions [1,4])
    /// と dev_safe の Python の写しに合わせた。セッションの設定も Python に合わせる(メモリのアリーナを切る。出力は同じで、
    /// 推論の後に作業領域を持ち続けない)。
    ///
    /// 埋め込みのファイルの控え(cacheDir があるとき): 鍵 = エンコーダの入力の中身・エンコーダの ONNX・onnxruntime の版の
    /// ダイジェスト。中身は float32 の並びだけ(大きさが違うファイルは読まずに作り直す)。書くときは一時ファイルから置き換える
    /// (並列に書いても壊れない)。同じプロセスの中では、最後に使った全体の埋め込みを 2 つまで覚えておく(常駐の依頼で同じ
    /// テクスチャを続けて測るとき、エンコードも読み込みもしない)。
    /// </summary>
    internal sealed unsafe class OrtSamInference : ISamInference {
        const int LogitsLength = 4 * SamMaskPostprocess.LowRes * SamMaskPostprocess.LowRes;
        const int ScoresLength = 4;

        /// <summary>読み込んだモデル(セッション)。置き場とスレッド数ごとに 1 つ作り、プロセスの間は使い回す。</summary>
        internal sealed class Models {
            public readonly string Directory;
            public readonly int IntraOpThreads;
            public readonly InferenceSession Encoder, Decoder;
            public readonly string EncoderDigest, DecoderDigest;
            public readonly string EncoderInputName;

            Models(string dir, int threads) {
                Directory = dir;
                IntraOpThreads = threads;
                string enc = Path.Combine(dir, SamModelFiles.EncoderFileName);
                string dec = Path.Combine(dir, SamModelFiles.DecoderFileName);
                if (!File.Exists(enc) || !File.Exists(dec)) {
                    throw new FileNotFoundException($"AI モデルがありません: {dir}({SamModelFiles.EncoderFileName}・{SamModelFiles.DecoderFileName})");
                }
                EncoderDigest = FileDigest(enc);
                DecoderDigest = FileDigest(dec);
                Encoder = new InferenceSession(enc, Options(threads));
                Decoder = new InferenceSession(dec, Options(threads));
                using (var e = Encoder.InputMetadata.Keys.GetEnumerator()) {
                    e.MoveNext();
                    EncoderInputName = e.Current;
                }
            }

            static SessionOptions Options(int threads) {
                var so = new SessionOptions { EnableCpuMemArena = false };
                if (threads > 0) {
                    so.IntraOpNumThreads = threads;
                }
                return so;
            }

            static readonly Dictionary<string, Models> s_loaded = new Dictionary<string, Models>();

            /// <summary>置き場 dir のモデル(threads = onnxruntime の 1 演算の中のスレッド数。0 は onnxruntime の既定)。</summary>
            public static Models Load(string dir, int threads) {
                string key = Path.GetFullPath(dir) + "|" + threads;
                lock (s_loaded) {
                    if (!s_loaded.TryGetValue(key, out var m)) {
                        m = new Models(dir, threads);
                        s_loaded[key] = m;
                    }
                    return m;
                }
            }
        }

        /// <summary>製品と同じ既定のモデルの置き場(%LOCALAPPDATA%\Iroca\Models。製品の規則のうち、共有フォルダの場所だけ)。</summary>
        public static string DefaultModelsDirectory
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iroca", "Models");

        /// <summary>onnxruntime の版(ネイティブのライブラリが返すもの)。</summary>
        public static string OrtVersion => OrtEnv.Instance().GetVersionString();

        // 同じプロセスの中で最後に使った全体の埋め込み(鍵 → 並び)。
        static readonly List<KeyValuePair<string, float[]>> s_recentFull = new List<KeyValuePair<string, float[]>>();
        const int RecentFullCapacity = 2;

        readonly Models _m;
        readonly NativePixels _source;
        readonly string _cacheDir;
        float[] _full;

        /// <summary>全体の埋め込みをどこから得たか(given = 渡された・memory・file = 控え・encoded = エンコードした)。</summary>
        public string FullEmbeddingOrigin { get; private set; }
        /// <summary>切り出しの埋め込みをどこから得たか(file・encoded。切り出しが無ければ null)。</summary>
        public string CropEmbeddingOrigin { get; private set; }
        /// <summary>全体の埋め込み(得た後。--emit-dir が書く)。</summary>
        public float[] FullEmbedding => _full;
        /// <summary>最後の切り出しの埋め込み(--emit-dir が書く)。</summary>
        public float[] LastCropEmbedding { get; private set; }

        /// <summary>
        /// source は元の画素(下原点。読むだけ。呼んだ側が持つ)。cacheDir は埋め込みの控えの置き場(null なら控えない)。
        /// givenFullEmbedding を渡すと、全体の埋め込みはエンコードせずにそれを使う(切り分け用。長さは埋め込みの float の数)。
        /// </summary>
        public OrtSamInference(Models models, NativePixels source, string cacheDir, float[] givenFullEmbedding = null) {
            _m = models;
            _source = source;
            _cacheDir = string.IsNullOrEmpty(cacheDir) ? null : cacheDir;
            if (givenFullEmbedding != null) {
                if (givenFullEmbedding.Length != SamProposalFlow.EmbeddingFloatCount) {
                    throw new ArgumentException($"埋め込みの長さが違う: {givenFullEmbedding.Length}");
                }
                _full = givenFullEmbedding;
                FullEmbeddingOrigin = "given";
            }
        }

        public void DecodeFull(float x1024, float y1024, out float[] logits, out float[] scores) {
            if (_full == null) {
                _full = EncodeWithCache(_source, recent: true, out string origin);
                FullEmbeddingOrigin = origin;
            }
            Decode(_full, x1024, y1024, out logits, out scores);
        }

        public NativeBuffer EncodeCrop(NativePixels crop, string key) {
            float[] emb = EncodeWithCache(crop, recent: false, out string origin);
            CropEmbeddingOrigin = origin;
            LastCropEmbedding = emb;
            var buf = new NativeBuffer((long)emb.Length * sizeof(float), NativeUsage.Other);
            fixed (float* p = emb) {
                Buffer.MemoryCopy(p, (void*)buf.Ptr, buf.Bytes, buf.Bytes);
            }
            return buf;
        }

        public void DecodeWith(NativeBuffer cropEmbedding, float x1024, float y1024, out float[] logits, out float[] scores) {
            int n = (int)(cropEmbedding.Bytes / sizeof(float));
            var emb = new float[n];
            fixed (float* p = emb) {
                Buffer.MemoryCopy((void*)cropEmbedding.Ptr, p, cropEmbedding.Bytes, cropEmbedding.Bytes);
            }
            Decode(emb, x1024, y1024, out logits, out scores);
        }

        void Decode(float[] embedding, float x, float y, out float[] logits, out float[] scores) {
            var inputs = new List<NamedOnnxValue> {
                NamedOnnxValue.CreateFromTensor("image_embeddings", new DenseTensor<float>(embedding, new[] { 1, 256, 64, 64 })),
                NamedOnnxValue.CreateFromTensor("point_coords", new DenseTensor<float>(new[] { x, y, 0f, 0f }, new[] { 1, 2, 2 })),
                NamedOnnxValue.CreateFromTensor("point_labels", new DenseTensor<float>(new[] { 1f, -1f }, new[] { 1, 2 })),
            };
            logits = null;
            scores = null;
            using (var results = _m.Decoder.Run(inputs)) {
                foreach (var r in results) {
                    if (r.Name == "low_res_logits") {
                        logits = ToArray(r.AsTensor<float>(), LogitsLength);
                    } else if (r.Name == "iou_predictions") {
                        scores = ToArray(r.AsTensor<float>(), ScoresLength);
                    }
                }
            }
            if (logits == null || scores == null) {
                throw new InvalidOperationException("デコーダの出力(low_res_logits・iou_predictions)がありません");
            }
        }

        static float[] ToArray(Tensor<float> t, int expected) {
            if (t.Length != expected) {
                throw new InvalidOperationException($"推論の出力の大きさが違う: {t.Length}(期待 {expected})");
            }
            var a = new float[expected];
            if (t is DenseTensor<float> d) {
                d.Buffer.Span.CopyTo(a);
            } else {
                int i = 0;
                foreach (float f in t) {
                    a[i++] = f;
                }
            }
            return a;
        }

        // 画素(下原点)からエンコーダの入力を作り(製品と同じ前処理)、控えにあれば読み、無ければエンコードして控える。
        float[] EncodeWithCache(NativePixels px, bool recent, out string origin) {
            var input = new float[SamImageOps.EncoderInputLength];
            fixed (float* p = input) {
                SamImageOps.BuildEncoderInputInto(px.Ptr, px.Width, px.Height, p, default);
            }
            string key = EmbeddingKey(input);
            if (recent) {
                lock (s_recentFull) {
                    foreach (var kv in s_recentFull) {
                        if (kv.Key == key) {
                            origin = "memory";
                            return kv.Value;
                        }
                    }
                }
            }
            float[] emb = TryReadCache(key);
            origin = "file";
            if (emb == null) {
                emb = RunEncoder(input);
                origin = "encoded";
                TryWriteCache(key, emb);
            }
            if (recent) {
                lock (s_recentFull) {
                    s_recentFull.Add(new KeyValuePair<string, float[]>(key, emb));
                    while (s_recentFull.Count > RecentFullCapacity) {
                        s_recentFull.RemoveAt(0);
                    }
                }
            }
            return emb;
        }

        float[] RunEncoder(float[] input) {
            var inputs = new List<NamedOnnxValue> {
                NamedOnnxValue.CreateFromTensor(_m.EncoderInputName,
                    new DenseTensor<float>(input, new[] { 1, 3, SamImageOps.InputSize, SamImageOps.InputSize })),
            };
            using (var results = _m.Encoder.Run(inputs)) {
                foreach (var r in results) {
                    return ToArray(r.AsTensor<float>(), SamProposalFlow.EmbeddingFloatCount);
                }
            }
            throw new InvalidOperationException("エンコーダの出力がありません");
        }

        string EmbeddingKey(float[] input) {
            using var sha = SHA256.Create();
            byte[] inputDigest;
            fixed (float* p = input) {
                inputDigest = sha.ComputeHash(new UnmanagedMemoryStream((byte*)p, (long)input.Length * sizeof(float)));
            }
            string s = "samemb-v1|" + Convert.ToHexString(inputDigest) + "|" + _m.EncoderDigest + "|" + OrtVersion;
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        }

        string CachePath(string key) => Path.Combine(_cacheDir, "samemb", key + ".f32");

        float[] TryReadCache(string key) {
            if (_cacheDir == null) {
                return null;
            }
            string path = CachePath(key);
            try {
                var fi = new FileInfo(path);
                long bytes = (long)SamProposalFlow.EmbeddingFloatCount * sizeof(float);
                if (!fi.Exists || fi.Length != bytes) {
                    return null;   // 無い・大きさが違う(壊れた)ものは読まずに作り直す
                }
                byte[] data = File.ReadAllBytes(path);
                if (data.Length != bytes) {
                    return null;
                }
                var emb = new float[SamProposalFlow.EmbeddingFloatCount];
                Buffer.BlockCopy(data, 0, emb, 0, data.Length);
                return emb;
            } catch (IOException) {
                return null;
            } catch (UnauthorizedAccessException) {
                return null;
            }
        }

        void TryWriteCache(string key, float[] emb) {
            if (_cacheDir == null) {
                return;
            }
            string path = CachePath(key);
            string tmp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var data = new byte[(long)emb.Length * sizeof(float)];
                Buffer.BlockCopy(emb, 0, data, 0, data.Length);
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, path, overwrite: true);
            } catch (IOException) {
                TryDelete(tmp);   // 控えは無くても測れる(次に作り直す)
            } catch (UnauthorizedAccessException) {
                TryDelete(tmp);
            }
        }

        static void TryDelete(string path) {
            try {
                File.Delete(path);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        static readonly Dictionary<string, string> s_fileDigests = new Dictionary<string, string>();

        static string FileDigest(string path) {
            var fi = new FileInfo(path);
            string key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            lock (s_fileDigests) {
                if (s_fileDigests.TryGetValue(key, out var d)) {
                    return d;
                }
            }
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            string digest = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
            lock (s_fileDigests) {
                s_fileDigests[key] = digest;
            }
            return digest;
        }
    }
}
