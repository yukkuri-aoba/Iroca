// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
// --sam-propose: AI 提案 1 クリック分を、製品と同じ段取り(SamProposalFlow)と後処理で作る。推論は onnxruntime(OrtSamInference)。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Iroca {
    /// <summary>
    /// --sam-propose &lt;in.raw(下原点 RGBA)&gt; --u &lt;u&gt; --v &lt;v&gt; --gran auto|fine|coarse --use evidence|region --out &lt;mask.raw&gt;
    ///   [--sam-models &lt;dir&gt;] [--sam-cache &lt;dir&gt;] [--sam-threads &lt;n&gt;] [--sam-embedding &lt;full.f32&gt;] [--emit-dir &lt;dir&gt;]
    /// u, v はクリックの UV(下原点。製品の RequestProposal と同じ)。--out は提案の範囲(マスクの raw。行 0 = 下端)。
    /// 標準出力は 1 行 `SAMPROPOSE {json}`(zoomPlanned・zoomed・fallbackRefined・cropError・crop・flood・channel・score・areaFrac
    /// と、推論の来歴)。
    ///   --sam-models: モデルの置き場(既定は製品と同じ %LOCALAPPDATA%\Iroca\Models)。
    ///   --sam-cache: 埋め込みの控えの置き場(既定は控えない)。
    ///   --sam-threads: onnxruntime の 1 演算の中のスレッド数(既定 0 = onnxruntime の既定)。
    ///   --sam-embedding: 全体の埋め込み(float32 の並び 1*256*64*64 個)を渡す(エンコードしない。推論の実行系の切り分け用)。
    ///   --emit-dir: 段ごとの中間を書く(全体の logits・scores・埋め込み、第 1 段の範囲、切り出しの画素・logits・埋め込み、
    ///               ズームの後・第 1 段へ戻した後の範囲)。
    /// </summary>
    internal static unsafe class SamProposeMode {
        public static int Run(string[] args) {
            var inv = CultureInfo.InvariantCulture;
            if (args.Length < 2) {
                Console.Error.WriteLine("usage: --sam-propose <in.raw> --u <u> --v <v> --gran auto|fine|coarse --use evidence|region --out <mask.raw> [...]");
                return 2;
            }
            string input = args[1];
            float? u = null, v = null;
            string gran = "auto", use = "evidence", outPath = null, models = OrtSamInference.DefaultModelsDirectory;
            string cache = null, emit = null, embeddingPath = null;
            int threads = 0;
            for (int i = 2; i < args.Length; i++) {
                string a = args[i];
                string Next() {
                    if (i + 1 >= args.Length) {
                        throw new ArgumentException($"{a} の値がありません");
                    }
                    return args[++i];
                }
                switch (a) {
                    case "--u": u = float.Parse(Next(), inv); break;
                    case "--v": v = float.Parse(Next(), inv); break;
                    case "--gran": gran = Next(); break;
                    case "--use": use = Next(); break;
                    case "--out": outPath = Next(); break;
                    case "--sam-models": models = Next(); break;
                    case "--sam-cache": cache = Next(); break;
                    case "--sam-threads": threads = int.Parse(Next(), inv); break;
                    case "--sam-embedding": embeddingPath = Next(); break;
                    case "--emit-dir": emit = Next(); break;
                    default:
                        Console.Error.WriteLine($"--sam-propose: 知らない引数 {a}");
                        return 2;
                }
            }
            if (u == null || v == null || outPath == null) {
                Console.Error.WriteLine("--sam-propose: --u・--v・--out が要る");
                return 2;
            }
            MaskSuggestGranularity g;
            switch (gran) {
                case "auto": g = MaskSuggestGranularity.Auto; break;
                case "fine": g = MaskSuggestGranularity.Fine; break;
                case "coarse": g = MaskSuggestGranularity.Coarse; break;
                default:
                    Console.Error.WriteLine($"--gran は auto|fine|coarse: {gran}");
                    return 2;
            }
            MaskSuggestUse mu;
            switch (use) {
                case "evidence": mu = MaskSuggestUse.Evidence; break;
                case "region": mu = MaskSuggestUse.Region; break;
                default:
                    Console.Error.WriteLine($"--use は evidence|region: {use}");
                    return 2;
            }
            if (emit != null) {
                Directory.CreateDirectory(emit);
            }

            OrtSamInference.Models m;
            try {
                m = OrtSamInference.Models.Load(models, threads);
            } catch (FileNotFoundException e) {
                Console.Error.WriteLine(e.Message);
                return 3;
            }
            float[] given = embeddingPath == null ? null : ReadFloats(embeddingPath, SamProposalFlow.EmbeddingFloatCount);

            var source = ReadRgbaRaw(input);
            MaskSuggestProposal proposal = null;
            try {
                var ort = new OrtSamInference(m, source, cache, given);
                var ai = new Recording(ort, emit);
                proposal = SamProposalFlow.RunSynchronously(ai, source, "harness", u.Value, v.Value, g, mu,
                    System.Threading.CancellationToken.None, out var diag,
                    emit == null ? null : (kind, o) => {
                        string name = kind == SamWorkKind.Stage1 ? "stage1" : kind == SamWorkKind.Zoom ? "zoom" : "fallback";
                        WriteMask(Path.Combine(emit, name + ".raw"), o.mask);
                        if (kind == SamWorkKind.Stage1 && o.cropPixels != null) {
                            WriteRgba(Path.Combine(emit, "crop_px.raw"), o.cropPixels);
                        }
                    });
                WriteMask(outPath, proposal.mask);
                if (emit != null) {
                    if (ort.FullEmbedding != null) {
                        WriteFloats(Path.Combine(emit, "full_embedding.f32"), ort.FullEmbedding);
                    }
                    if (ort.LastCropEmbedding != null) {
                        WriteFloats(Path.Combine(emit, "crop_embedding.f32"), ort.LastCropEmbedding);
                    }
                }
                var doc = new Dictionary<string, object> {
                    ["w"] = source.Width,
                    ["h"] = source.Height,
                    ["zoomPlanned"] = diag.zoomPlanned,
                    ["zoomed"] = diag.zoomed,
                    ["fallbackRefined"] = diag.fallbackRefined,
                    ["cropError"] = diag.cropError,
                    ["crop"] = diag.zoomPlanned ? new[] { diag.cropX0, diag.cropY0, diag.cropSide } : null,
                    ["flood"] = diag.floodWarning,
                    ["channel"] = diag.channel,
                    ["score"] = diag.score,
                    ["areaFrac"] = diag.areaFrac,
                    ["count"] = proposal.mask.CountTrue(),
                    ["fullEmbedding"] = ort.FullEmbeddingOrigin,
                    ["cropEmbedding"] = ort.CropEmbeddingOrigin,
                    ["ort"] = OrtSamInference.OrtVersion,
                    ["threads"] = threads,
                };
                string json = JsonSerializer.Serialize(doc);
                if (emit != null) {
                    File.WriteAllText(Path.Combine(emit, "result.json"), json);
                }
                Console.WriteLine("SAMPROPOSE " + json);
                return 0;
            } finally {
                proposal?.Release();
                source.Release();
            }
        }

        // 推論の入出力を書き留める(--emit-dir。書かないときは素通し)。
        sealed class Recording : ISamInference {
            readonly ISamInference _inner;
            readonly string _dir;

            public Recording(ISamInference inner, string dir) {
                _inner = inner;
                _dir = dir;
            }

            public void DecodeFull(float x1024, float y1024, out float[] logits, out float[] scores) {
                _inner.DecodeFull(x1024, y1024, out logits, out scores);
                if (_dir != null) {
                    WriteFloats(Path.Combine(_dir, "full_logits.f32"), logits);
                    WriteFloats(Path.Combine(_dir, "full_scores.f32"), scores);
                    File.WriteAllText(Path.Combine(_dir, "full_point.txt"),
                        string.Format(CultureInfo.InvariantCulture, "{0:R} {1:R}", x1024, y1024));
                }
            }

            public NativeBuffer EncodeCrop(NativePixels crop, string key) => _inner.EncodeCrop(crop, key);

            public void DecodeWith(NativeBuffer cropEmbedding, float x1024, float y1024, out float[] logits, out float[] scores) {
                _inner.DecodeWith(cropEmbedding, x1024, y1024, out logits, out scores);
                if (_dir != null) {
                    WriteFloats(Path.Combine(_dir, "crop_logits.f32"), logits);
                    WriteFloats(Path.Combine(_dir, "crop_scores.f32"), scores);
                    File.WriteAllText(Path.Combine(_dir, "crop_point.txt"),
                        string.Format(CultureInfo.InvariantCulture, "{0:R} {1:R}", x1024, y1024));
                }
            }
        }

        // raw(行 0 = 下端の RGBA)をヒープの外の画素へ直接読む。
        static NativePixels ReadRgbaRaw(string path) {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);
            int w = br.ReadInt32();
            int h = br.ReadInt32();
            var px = NativePixels.Allocate(w, h);
            try {
                long bytes = (long)w * h * 4;
                var span = new Span<byte>((void*)px.Ptr, checked((int)bytes));
                int read = 0;
                while (read < span.Length) {
                    int n = fs.Read(span.Slice(read));
                    if (n <= 0) {
                        throw new EndOfStreamException($"raw の画素が足りない: {path}");
                    }
                    read += n;
                }
                return px;
            } catch {
                px.Release();
                throw;
            }
        }

        static void WriteMask(string path, NativeBools mask) {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write(mask.Width);
            bw.Write(mask.Height);
            bw.Flush();
            // bool は 1 バイト(0 か 1)なので、そのまま書く(マスクの raw の決まり: 非 0 = true)。
            fs.Write(new ReadOnlySpan<byte>(mask.Ptr, mask.Length));
        }

        static void WriteRgba(string path, NativePixels px) {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write(px.Width);
            bw.Write(px.Height);
            bw.Flush();
            fs.Write(new ReadOnlySpan<byte>(px.Ptr, checked(px.Length * 4)));
        }

        static void WriteFloats(string path, float[] data) {
            var bytes = new byte[(long)data.Length * sizeof(float)];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(path, bytes);
        }

        static float[] ReadFloats(string path, int count) {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length != (long)count * sizeof(float)) {
                throw new InvalidDataException($"float の数が違う: {path}({bytes.Length} バイト。期待 {count} 個)");
            }
            var a = new float[count];
            Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
            return a;
        }
    }
}
