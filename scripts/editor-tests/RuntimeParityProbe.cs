// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 塗り(<see cref="PixelProcessor"/>)の実行系の突き合わせ(Unity の Mono ↔ ハーネスの .NET 8)の書き出しの本体。
    /// 同じソースを Unity の EditMode(<see cref="RuntimeParityDumpTests"/>)と、dev_safe の .NET の小さな実行ファイル
    /// (<c>_spike/runtime_parity/probe</c>)の両方でコンパイルして回し、同じ入力から同じ形のファイルを書く。比べるのは
    /// dev_safe の台本(<c>scripts/runtime_parity.py</c>)。測るだけで、製品の処理は変えない(診断の口 <see cref="IDebugCapture"/> で
    /// 段ごとの強度を受け取る)。
    /// <para>
    /// 入力(台本が書く。タブ区切りの文字): 1 行 1 件 <c>case &lt;id&gt; &lt;テクスチャの raw&gt; &lt;ゾーンのファイル&gt; &lt;除外の raw か -&gt;</c>。
    /// raw はハーネスの in.raw と同じ形([int32 w][int32 h][画素]。行 0 = 下端)。ゾーンのファイルは 1 行 1 項目で、
    /// 浮動小数は float のビット列の 16 進(文字の読み書きで丸めが変わらないように)。
    /// </para>
    /// <para>
    /// 書くもの(&lt;out&gt;/&lt;id&gt;/): 段ごとの強度(<c>NN_&lt;段&gt;.f32.gz</c>)・デコンタミの縁(<c>.u8.gz</c>)・再着色の分岐の地図・
    /// 出力(<c>out.raw.gz</c>)・選択だけの最終の強度(<c>selection.f32.gz</c>)と、その一覧 <c>stages.txt</c>。
    /// &lt;out&gt;/math.bin に浮動小数の式・関数の結果(実行系の違いの切り分け)、&lt;out&gt;/info.txt に実行系の情報。
    /// </para>
    /// </summary>
    internal static unsafe class RuntimeParityProbe {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static void RunAll(string casesFile, string outDir, string runtimeInfo, Action<string> log) {
            Directory.CreateDirectory(outDir);
            var info = new StringBuilder();
            info.AppendLine("runtime\t" + runtimeInfo);
            info.AppendLine("version\t" + Environment.Version);
            info.AppendLine("processors\t" + Environment.ProcessorCount);
            info.AppendLine("parallelismDefault\t" + CoreParallelism.Default);
            info.AppendLine("parallelismOverride\t" + CoreParallelism.Override);
            info.AppendLine("is64\t" + Environment.Is64BitProcess);
            File.WriteAllText(Path.Combine(outDir, "info.txt"), info.ToString());
            WriteMath(Path.Combine(outDir, "math.bin"));
            log?.Invoke("math.bin を書いた");
            if (string.IsNullOrEmpty(casesFile)) {
                return;
            }
            foreach (string line in File.ReadAllLines(casesFile)) {
                var f = line.Split('\t');
                if (f.Length < 5 || f[0] != "case") {
                    continue;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RunCase(f[1], f[2], f[3], f[4] == "-" ? null : f[4], Path.Combine(outDir, f[1]));
                log?.Invoke($"{f[1]}: {sw.Elapsed.TotalSeconds:F1} 秒");
            }
        }

        // ───────── 1 件 ─────────
        static void RunCase(string id, string texRaw, string zoneFile, string excludeRaw, string dir) {
            Directory.CreateDirectory(dir);
            var px = ReadRgba(texRaw, out int w, out int h);
            var zone = ReadZone(zoneFile);
            var masks = new MaskWorkspace();
            masks.Reset(w, h);
            bool excludeApplied = false;
            if (excludeRaw != null) {
                var ex = ReadMask(excludeRaw, out int ew, out int eh);
                if (ew != w || eh != h) {
                    throw new InvalidDataException($"{id}: 除外の寸法が違う");
                }
                fixed (bool* p = ex) {
                    excludeApplied = masks.ApplyRegion(p, zone.id, MaskRegionOp.DontPaintHere, null, out _);
                }
            }
            var settings = RecolorSettings.Default;
            var zones = new List<ColorZone> { zone };
            var lines = new List<string>();
            try {
                // 診断の口あり(段ごとの強度)
                var outDbg = (Color32[])px.Clone();
                var cap = new StageCapture(dir, lines);
                var snap = masks.ToSnapshot();
                try {
                    PixelProcessor.ProcessPixelsArray(outDbg, w, h, snap, zones, in settings, default, cap);
                } finally {
                    snap.Release();
                }
                // 診断の口なし(製品と同じ呼び方。出力はこちらを書く)
                var outPx = (Color32[])px.Clone();
                snap = masks.ToSnapshot();
                try {
                    PixelProcessor.ProcessPixelsArray(outPx, w, h, snap, zones, in settings);
                } finally {
                    snap.Release();
                }
                WriteRgbaGz(Path.Combine(dir, "out.raw.gz"), outPx, w, h);
                int dbgDiff = 0;
                for (int i = 0; i < outPx.Length; i++) {
                    if (outPx[i].r != outDbg[i].r || outPx[i].g != outDbg[i].g || outPx[i].b != outDbg[i].b || outPx[i].a != outDbg[i].a) {
                        dbgDiff++;
                    }
                }
                lines.Add($"nodebug_vs_debug_diff\t{dbgDiff}");
                // 選択だけ(マスクの契約の後の最終の強度。再着色の段は走らない)
                var sel = new float[w * h];
                snap = masks.ToSnapshot();
                try {
                    fixed (Color32* p = px) {
                        fixed (float* s = sel) {
                            PixelProcessor.SimulateSelection(p, w, h, zone.Clone(), in settings, snap, null, s);
                        }
                    }
                } finally {
                    snap.Release();
                }
                WriteFloatsGz(Path.Combine(dir, "selection.f32.gz"), sel, w, h);
                lines.Add("selection\tselection.f32.gz");
                lines.Add($"exclude_applied\t{excludeApplied}");
                lines.Add($"zone_after_clamp\t{ZoneLine(zone)}");
            } finally {
                masks.Clear();
                File.WriteAllLines(Path.Combine(dir, "stages.txt"), lines);
            }
        }

        // 範囲へ丸めた後の値(ProcessPixelsArray の冒頭がゾーンへ書き戻す)。両方の実行系で同じかを比べる。
        static string ZoneLine(ColorZone z) =>
            string.Join(" ", Array.ConvertAll(new[] { z.tolerance, z.valueBlend, z.edgeSoftness, z.saturationStrictness,
                z.saturationGuard, z.chromaThreshold, z.shadowDesaturation, z.shadowForgivenessSatMin, z.shadowValueFloor,
                z.partSatCeiling, z.partHueBand, z.chromaCeiling, z.holeFillMargin, z.matchCutoff, z.outputSaturation }, Hex));

        /// <summary>段ごとの強度などを、呼ばれた順の番号を付けて書く。</summary>
        sealed class StageCapture : IDebugCapture {
            readonly string _dir;
            readonly List<string> _lines;
            int _seq;

            public StageCapture(string dir, List<string> lines) {
                _dir = dir;
                _lines = lines;
            }

            public void BeginCapture(int width, int height) { }

            public void RecordStage(string zoneId, string stageName, float[] strength, int width, int height) {
                lock (this) {
                    string name = $"{_seq++:00}_{stageName}.f32.gz";
                    WriteFloatsGz(Path.Combine(_dir, name), strength, width, height);
                    _lines.Add($"stage\t{stageName}\t{name}");
                }
            }

            public void RecordDecontamination(string zoneId, bool[] aaMask, int width, int height) {
                lock (this) {
                    var b = new byte[aaMask.Length];
                    for (int i = 0; i < b.Length; i++) {
                        b[i] = aaMask[i] ? (byte)1 : (byte)0;
                    }
                    string name = $"{_seq++:00}_Decontam.u8.gz";
                    WriteBytesGz(Path.Combine(_dir, name), b, width, height);
                    _lines.Add($"aamask\tDecontam\t{name}");
                }
            }

            public void RecordRecolorBranches(string zoneId, byte[] branchMap, int width, int height) {
                lock (this) {
                    string name = $"{_seq++:00}_Branches.u8.gz";
                    WriteBytesGz(Path.Combine(_dir, name), branchMap, width, height);
                    _lines.Add($"branches\tBranches\t{name}");
                }
            }
        }

        // ───────── ゾーン(1 行 1 項目。浮動小数は float のビット列の 16 進) ─────────
        static ColorZone ReadZone(string path) {
            var zone = new ColorZone { enabled = true, mode = SelectionMode.ColorPick };
            foreach (string raw in File.ReadAllLines(path)) {
                var f = raw.Split('\t');
                if (f.Length < 2) {
                    continue;
                }
                string k = f[0];
                switch (k) {
                    case "name": zone.name = f[1]; break;
                    case "sample": zone.sampleColor = Col(f); break;
                    case "extraSample": zone.extraSamples.Add(Col(f)); break;
                    case "target": zone.targetColor = Col(f); break;
                    case "seedUV": zone.seedUV = new Vector2(F(f[1]), F(f[2])); break;
                    case "sampleUV": zone.sampleUV = new Vector2(F(f[1]), F(f[2])); break;
                    case "layerIndex": zone.layerIndex = int.Parse(f[1], Inv); break;
                    case "highlightRecovery": zone.highlightRecovery = f[1] == "1"; break;
                    case "highlightBandExpand": zone.highlightBandExpand = f[1] == "1"; break;
                    case "applyHighlightWash": zone.applyHighlightWash = f[1] == "1"; break;
                    case "autoHighlightSample": zone.autoHighlightSample = f[1] == "1"; break;
                    case "autoRecolorAnchor": zone.autoRecolorAnchor = f[1] == "1"; break;
                    case "useFloodFill": zone.useFloodFill = f[1] == "1"; break;
                    default: {
                            var fi = typeof(ColorZone).GetField(k);
                            if (fi == null || fi.FieldType != typeof(float)) {
                                throw new InvalidDataException($"ゾーンの項目を読めない: {k}");
                            }
                            fi.SetValue(zone, F(f[1]));
                            break;
                        }
                }
            }
            zone.EnsureId();
            return zone;
        }

        static Color Col(string[] f) => new Color(F(f[1]), F(f[2]), F(f[3]), 1f);

        static float F(string hex) {
            uint u = uint.Parse(hex, NumberStyles.HexNumber, Inv);
            return BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
        }

        static string Hex(float v) => BitConverter.ToUInt32(BitConverter.GetBytes(v), 0).ToString("x8", Inv);

        // ───────── 浮動小数の式と関数(実行系の違いの切り分け) ─────────
        // 入力は整数の乱数から作る(int → float の変換と 2 の冪の掛け算は丸めが無いので、どちらの実行系でも同じ値)。
        const int MathN = 65536;

        static void WriteMath(string path) {
            var a = new float[MathN];
            var b = new float[MathN];
            var c = new float[MathN];
            var ba = new byte[MathN];
            var bb = new byte[MathN];
            var bc = new byte[MathN];
            uint s = 2463534242u;
            uint Next() {
                s ^= s << 13;
                s ^= s >> 17;
                s ^= s << 5;
                return s;
            }
            for (int i = 0; i < MathN; i++) {
                a[i] = (Next() >> 8) * (1f / 16777216f);
                b[i] = (Next() >> 8) * (1f / 16777216f);
                c[i] = (Next() >> 8) * (1f / 16777216f);
                ba[i] = (byte)(Next() >> 24);
                bb[i] = (byte)(Next() >> 24);
                bc[i] = (byte)(Next() >> 24);
            }
            var blocks = new List<KeyValuePair<string, float[]>>();
            void Add(string name, Func<int, float> fn) {
                var r = new float[MathN];
                for (int i = 0; i < MathN; i++) {
                    r[i] = fn(i);
                }
                blocks.Add(new KeyValuePair<string, float[]>(name, r));
            }
            // 式の中間の精度(float で丸めるか、倍精度のまま次へ渡すか)
            Add("expr_big_add_sub", i => (a[i] * 16777216f + b[i]) - a[i] * 16777216f);
            Add("expr_mul_add", i => a[i] * b[i] + c[i]);
            Add("expr_dot3", i => 0.4122214708f * a[i] + 0.5363325363f * b[i] + 0.0514459929f * c[i]);
            Add("expr_div", i => a[i] / (b[i] + 0.001f));
            Add("expr_local_store", i => {
                float t = a[i] * b[i];
                float u = t + c[i];
                return u * 0.3333333f;
            });
            Add("expr_cube", i => {
                float m = a[i] - 0.3f * b[i];
                return m * m * m;
            });
            Add("expr_lerp_mathf", i => Mathf.Lerp(a[i], b[i], c[i]));
            Add("expr_clamp01_mul", i => Mathf.Clamp01(a[i] * 1.7f - b[i]));
            // 関数
            Add("fn_cbrt", i => MathF.Cbrt(a[i]));
            Add("fn_cbrt_neg", i => MathF.Cbrt(a[i] - 0.5f));
            Add("fn_pow_2.4", i => Mathf.Pow((a[i] + 0.055f) / 1.055f, 2.4f));
            Add("fn_pow_inv2.4", i => Mathf.Pow(a[i], 1f / 2.4f));
            Add("fn_pow_ab", i => Mathf.Pow(a[i] * 4f, b[i] * 3f - 1.5f));
            Add("fn_sqrt", i => Mathf.Sqrt(a[i]));
            Add("fn_mathf_sqrt_sum", i => Mathf.Sqrt(a[i] * a[i] + b[i] * b[i]));
            Add("fn_exp", i => Mathf.Exp(-a[i] * 8f));
            Add("fn_log", i => Mathf.Log(a[i] + 1e-6f));
            Add("fn_atan2", i => Mathf.Atan2(a[i] - 0.5f, b[i] - 0.5f));
            Add("fn_sin", i => Mathf.Sin(a[i] * 6.2831853f));
            Add("fn_mathf_round", i => Mathf.Round(a[i] * 255f));
            Add("fn_roundtoint", i => Mathf.RoundToInt(a[i] * 255f));
            Add("fn_trunc_byte", i => (byte)(a[i] * 255f + 0.5f));
            // 製品の色の関数
            Add("hsv_h", i => { Color.RGBToHSV(new Color(a[i], b[i], c[i]), out float hh, out _, out _); return hh; });
            Add("hsv_s", i => { Color.RGBToHSV(new Color(a[i], b[i], c[i]), out _, out float ss, out _); return ss; });
            Add("hsv_v", i => { Color.RGBToHSV(new Color(a[i], b[i], c[i]), out _, out _, out float vv); return vv; });
            Add("hsv_bytes_h", i => {
                Color.RGBToHSV((Color)new Color32(ba[i], bb[i], bc[i], 255), out float hh, out _, out _);
                return hh;
            });
            Add("oklab_byte_L", i => { OkLab.RgbToOklab(ba[i], bb[i], bc[i], out float L, out _, out _); return L; });
            Add("oklab_byte_a", i => { OkLab.RgbToOklab(ba[i], bb[i], bc[i], out _, out float aa, out _); return aa; });
            Add("oklab_byte_b", i => { OkLab.RgbToOklab(ba[i], bb[i], bc[i], out _, out _, out float b2); return b2; });
            Add("oklab_float_L", i => { OkLab.RgbToOklab(a[i], b[i], c[i], out float L, out _, out _); return L; });
            Add("oklab_inv_r", i => {
                OkLab.OklabToRgb(a[i], (b[i] - 0.5f) * 0.4f, (c[i] - 0.5f) * 0.4f, out float r, out _, out _);
                return r;
            });
            Add("oklab_inv_gamut_g", i => {
                OkLab.OklabToRgbInGamut(a[i], (b[i] - 0.5f) * 0.6f, (c[i] - 0.5f) * 0.6f, out _, out float g, out _);
                return g;
            });
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs)) {
                bw.Write(blocks.Count);
                foreach (var kv in blocks) {
                    var nameBytes = Encoding.UTF8.GetBytes(kv.Key);
                    bw.Write(nameBytes.Length);
                    bw.Write(nameBytes);
                    bw.Write(kv.Value.Length);
                    foreach (float v in kv.Value) {
                        bw.Write(v);
                    }
                }
            }
        }

        // ───────── 深掘り: 同じ強度・同じ再着色の値から、再着色の統計・1 画素の再着色・混色帯を回す ─────────
        // 入力(タブ区切り): deep <id> <raw> <zone> <除外 or -> <強度の f32.gz(どちらかの実行系の Recolor の段)>
        //                   <再着色の値のファイル or -> <x,y;x,y;...(下原点)>
        // 段の中身は製品の private の型・関数を名前で呼ぶ(RecolorStatsStage.Run・RecolorPixel・MixtureBandStage.Run・
        // ClassifyMixture)。名前を変えたら、ここも直す。混色帯の 1 画素の判定の途中の値は、製品の AnalyzeMixtureBand の
        // 画素ごとの部分を写して出す(測るための写し。製品の判定そのものは ClassifyMixture を呼んで突き合わせる)。
        const BindingFlags All = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        static readonly Type PP = typeof(PixelProcessor);

        static object NewPrivate(string nested) => Activator.CreateInstance(PP.GetNestedType(nested, BindingFlags.NonPublic), true);
        static void SetF(object o, string f, object v) => o.GetType().GetField(f, All).SetValue(o, v);
        static object GetF(object o, string f) => o.GetType().GetField(f, All).GetValue(o);

        // ポインタの項目・引数は、Mono のリフレクションが System.Reflection.Pointer を受けないので、小さな IL を作って読み書きする
        // (IntPtr とポインタは IL では同じ native int)。
        static void SetP(object o, string f, void* p) {
            var fi = o.GetType().GetField(f, All);
            var dm = new System.Reflection.Emit.DynamicMethod("rtp_set_" + f, null, new[] { typeof(object), typeof(IntPtr) }, PP, true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, fi.DeclaringType);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Stfld, fi);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            ((Action<object, IntPtr>)dm.CreateDelegate(typeof(Action<object, IntPtr>)))(o, (IntPtr)p);
        }

        static void* GetP(object o, string f) {
            var fi = o.GetType().GetField(f, All);
            var dm = new System.Reflection.Emit.DynamicMethod("rtp_get_" + f, typeof(IntPtr), new[] { typeof(object) }, PP, true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, fi.DeclaringType);
            il.Emit(System.Reflection.Emit.OpCodes.Ldfld, fi);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (void*)((Func<object, IntPtr>)dm.CreateDelegate(typeof(Func<object, IntPtr>)))(o);
        }

        // GetOrBuildPalette(Color32*, int, int, ParallelOptions, SubPhaseClock, SelectionCache) を呼ぶ。
        static object BuildPalette(Color32* px, int w, int h, System.Threading.Tasks.ParallelOptions po, SubPhaseClock sub) {
            var m = PP.GetMethod("GetOrBuildPalette", All);
            var dm = new System.Reflection.Emit.DynamicMethod("rtp_palette", typeof(object),
                new[] { typeof(IntPtr), typeof(int), typeof(int), typeof(System.Threading.Tasks.ParallelOptions), typeof(SubPhaseClock) }, PP, true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_2);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_3);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_S, (byte)4);
            il.Emit(System.Reflection.Emit.OpCodes.Ldnull);
            il.Emit(System.Reflection.Emit.OpCodes.Call, m);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            var fn = (Func<IntPtr, int, int, System.Threading.Tasks.ParallelOptions, SubPhaseClock, object>)dm.CreateDelegate(
                typeof(Func<IntPtr, int, int, System.Threading.Tasks.ParallelOptions, SubPhaseClock, object>));
            return fn((IntPtr)px, w, h, po, sub);
        }
        static object Call(Type t, string name, params object[] args) {
            var m = t.GetMethod(name, All);
            if (m == null) {
                throw new MissingMethodException(t.Name + "." + name);
            }
            return m.Invoke(null, args);
        }

        internal static void RunDeep(string specFile, string outFile, string runtimeInfo) {
            var sb = new StringBuilder();
            sb.AppendLine("runtime\t" + runtimeInfo);
            foreach (string line in File.ReadAllLines(specFile)) {
                var f = line.Split('\t');
                if (f.Length < 8 || f[0] != "deep") {
                    continue;
                }
                try {
                    DeepCase(sb, f[1], f[2], f[3], f[4] == "-" ? null : f[4], f[5], f[6] == "-" ? null : f[6], f[7]);
                } catch (Exception e) {
                    sb.AppendLine($"error\t{f[1]}\t{e}");
                }
            }
            File.WriteAllText(outFile, sb.ToString());
        }

        static float[] ReadFloatsGz(string path, out int w, out int h) {
            using (var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            using (var ms = new MemoryStream()) {
                gz.CopyTo(ms);
                var data = ms.ToArray();
                w = BitConverter.ToInt32(data, 0);
                h = BitConverter.ToInt32(data, 4);
                var a = new float[w * h];
                Buffer.BlockCopy(data, 8, a, 0, a.Length * 4);
                return a;
            }
        }

        static void DeepCase(StringBuilder sb, string id, string texRaw, string zoneFile, string excludeRaw,
            string strengthFile, string paramsFile, string pixelList) {
            var px = ReadRgba(texRaw, out int w, out int h);
            var strength = ReadFloatsGz(strengthFile, out int sw, out int sh);
            if (sw != w || sh != h) {
                throw new InvalidDataException("強度の寸法が違う");
            }
            if (excludeRaw != null) {
                sb.AppendLine($"note\t{id}\t除外のある被写体(混色帯の写しは除外を見ない)");
            }
            var zone = ReadZone(zoneFile);
            RecolorValueRanges.ClampZone(zone);
            var settings = RecolorSettings.Default;
            int len = w * h;
            var pts = new List<int[]>();
            foreach (var p in pixelList.Split(';')) {
                var xy = p.Split(',');
                if (xy.Length == 2) {
                    pts.Add(new[] { int.Parse(xy[0], Inv), int.Parse(xy[1], Inv) });
                }
            }
            var gpx = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
            var gst = System.Runtime.InteropServices.GCHandle.Alloc(strength, System.Runtime.InteropServices.GCHandleType.Pinned);
            var decon = new Color32[len];
            var gdc = System.Runtime.InteropServices.GCHandle.Alloc(decon, System.Runtime.InteropServices.GCHandleType.Pinned);
            object palette = null, zb = null;
            float[] hArr = null, sArr = null, vArr = null;
            System.Runtime.InteropServices.GCHandle gh = default, gs = default, gv = default;
            try {
                Color32* orig = (Color32*)gpx.AddrOfPinnedObject();
                float* st = (float*)gst.AddrOfPinnedObject();
                var po = CoreParallelism.Options();
                var sub = new SubPhaseClock(64);
                palette = BuildPalette(orig, w, h, po, sub);
                PixelProcessor.PixelHsv hsv;
                float* pV;
                int* pIdx = null;
                if (palette != null) {
                    pIdx = (int*)GetP(palette, "Index");
                    pV = (float*)GetP(palette, "V");
                    hsv = new PixelProcessor.PixelHsv((float*)GetP(palette, "H"), (float*)GetP(palette, "S"), pV, pIdx);
                } else {
                    hArr = new float[len];
                    sArr = new float[len];
                    vArr = new float[len];
                    for (int i = 0; i < len; i++) {
                        Color.RGBToHSV((Color)px[i], out hArr[i], out sArr[i], out vArr[i]);
                    }
                    gh = System.Runtime.InteropServices.GCHandle.Alloc(hArr, System.Runtime.InteropServices.GCHandleType.Pinned);
                    gs = System.Runtime.InteropServices.GCHandle.Alloc(sArr, System.Runtime.InteropServices.GCHandleType.Pinned);
                    gv = System.Runtime.InteropServices.GCHandle.Alloc(vArr, System.Runtime.InteropServices.GCHandleType.Pinned);
                    pV = (float*)gv.AddrOfPinnedObject();
                    hsv = new PixelProcessor.PixelHsv((float*)gh.AddrOfPinnedObject(), (float*)gs.AddrOfPinnedObject(), pV, null);
                }
                sb.AppendLine($"case\t{id}\tpalette={(palette != null)}");

                var run = NewPrivate("RunContext");
                SetF(run, "W", w);
                SetF(run, "H", h);
                SetF(run, "Len", len);
                SetP(run, "Original", orig);
                SetP(run, "Pixels", orig);
                SetF(run, "Po", po);
                SetF(run, "Palette", palette);
                SetF(run, "Hsv", hsv);
                SetP(run, "PixH", hsv.H);
                SetP(run, "PixS", hsv.S);
                SetP(run, "PixV", hsv.V);
                SetP(run, "PixIdx", pIdx);
                SetF(run, "EdgeFeather", settings.edgeFeather);
                SetF(run, "UseDecontamination", settings.useDecontamination);
                SetF(run, "DecontaminationRadius", settings.decontaminationRadius);
                SetF(run, "Buf", NewPrivate("RunBuffers"));
                SetF(run, "PhaseTicks", new long[64]);
                SetF(run, "PerfZones", new ZonePerfEntry[4]);
                SetF(run, "Sub", sub);

                // 強度の外接矩形(選択の範囲。製品の Box は余白付きだが、外は強度 0 なので統計は同じ)
                int x0 = w, y0 = h, x1 = -1, y1 = -1;
                for (int y = 0; y < h; y++) {
                    for (int x = 0; x < w; x++) {
                        if (st[y * w + x] > 0f) {
                            if (x < x0) { x0 = x; }
                            if (x > x1) { x1 = x; }
                            if (y < y0) { y0 = y; }
                            if (y > y1) { y1 = y; }
                        }
                    }
                }
                var box = new SelectionCache.PostBox { Has = x1 >= 0, MinX = x0, MinY = y0, MaxX = x1, MaxY = y1 };
                Color.RGBToHSV(zone.sampleColor, out _, out float sampS, out _);
                bool mixMode = PixelProcessor.IsMixtureBandZone(zone, settings.useDecontamination, settings.edgeFeather);
                zb = NewPrivate("ZoneBuffers");
                var z = NewPrivate("ZoneState");
                SetF(z, "Zone", zone);
                SetF(z, "Buffers", zb);
                SetF(z, "MixMode", mixMode);
                SetF(z, "SampS", sampS);
                SetP(z, "Strength", st);
                SetF(z, "Box", box);
                SetF(z, "Scan", box);
                SetP(z, "Decontaminated", (Color32*)gdc.AddrOfPinnedObject());
                SetF(z, "AchromaWeight", Call(PP, "ComputeAchromaWeight", zone.sampleColor, zone.targetColor));
                sb.AppendLine($"zone\tmixMode={mixMode}\tachromaWeight={Hex((float)GetF(z, "AchromaWeight"))}\tbox={x0},{y0},{x1},{y1}");

                Call(PP.GetNestedType("RecolorStatsStage", BindingFlags.NonPublic), "Run", run, z);
                object prm = GetF(z, "Params");
                var tPrm = prm.GetType();
                var pl = new List<string>();
                foreach (var fi in tPrm.GetFields(All)) {
                    object v = fi.GetValue(prm);
                    pl.Add(fi.Name + "=" + (v is float fv ? Hex(fv) : v is bool bv ? (bv ? "1" : "0") : Convert.ToString(v, Inv)));
                }
                sb.AppendLine("params_own\t" + string.Join(" ", pl));
                sb.AppendLine($"regLmid_own\t{Hex((float)GetF(z, "RegLmid"))}");
                float* regMid = (float*)GetP(z, "RegMidMap");
                float* regUp = (float*)GetP(z, "RegUpMap");
                sb.AppendLine($"regMap\t{(regMid != null)}");
                if (paramsFile != null) {
                    // 相手の実行系の値で差し替える(統計の差と、画素ごとの式の差を分けるため)
                    object box2 = Activator.CreateInstance(tPrm);
                    foreach (var kv in File.ReadAllText(paramsFile).Trim().Split(' ')) {
                        var p = kv.Split('=');
                        var fi = tPrm.GetField(p[0], All);
                        if (fi.FieldType == typeof(float)) {
                            fi.SetValue(box2, F(p[1]));
                        } else if (fi.FieldType == typeof(bool)) {
                            fi.SetValue(box2, p[1] == "1");
                        }
                    }
                    SetF(z, "Params", box2);
                    sb.AppendLine("params_injected\t" + paramsFile);
                }
                object prmUse = GetF(z, "Params");
                var rp = PP.GetMethod("RecolorPixel", All);
                float regLmid = (float)GetF(z, "RegLmid");
                const float achromaFormOffset = 0.16f;
                foreach (var p in pts) {
                    int i = p[1] * w + p[0];
                    var o = px[i];
                    float oV = pV[PixelProcessor.HsvSlot(pIdx, i)];
                    bool hasMap = regMid != null && regMid[i] >= 0f;
                    float rMid = hasMap ? regMid[i] : regLmid;
                    float rUp = hasMap && regUp != null ? regUp[i] : achromaFormOffset;
                    var a1 = (Color32)rp.Invoke(null, new object[] { o.r, o.g, o.b, oV, o.a / 255f, prmUse, rMid, rUp });
                    sb.AppendLine($"recolor\t{p[0]},{p[1]}\tin={o.r},{o.g},{o.b},{o.a}\toV={Hex(oV)}\ts={Hex(st[i])}\tregMid={Hex(rMid)}\tregUp={Hex(rUp)}\tout={a1.r},{a1.g},{a1.b},{a1.a}");
                }

                if (mixMode) {
                    Call(PP.GetNestedType("MixtureBandStage", BindingFlags.NonPublic), "Run", run, z);
                    float* mixA = (float*)GetP(z, "MixAlpha");
                    Color32* mixed = (Color32*)gdc.AddrOfPinnedObject();
                    foreach (var p in pts) {
                        int i = p[1] * w + p[0];
                        float ma = mixA != null ? mixA[i] : float.NaN;
                        sb.AppendLine($"mix\t{p[0]},{p[1]}\talpha={Hex(ma)}({ma.ToString("R", Inv)})\tmixed={mixed[i].r},{mixed[i].g},{mixed[i].b}");
                        MixReplica(sb, orig, st, w, h, p[0], p[1], settings.decontaminationRadius, zone);
                    }
                }
            } finally {
                if (zb != null) {
                    zb.GetType().GetMethod("ReturnAll", All).Invoke(zb, null);
                }
                if (palette != null) {
                    palette.GetType().GetMethod("Release", All).Invoke(palette, null);
                }
                gpx.Free();
                gst.Free();
                gdc.Free();
                if (gh.IsAllocated) { gh.Free(); }
                if (gs.IsAllocated) { gs.Free(); }
                if (gv.IsAllocated) { gv.Free(); }
            }
        }

        // 製品の AnalyzeMixtureBand の 1 画素分(除外・含めるマスクなし)を写し、ClassifyMixture の入力と途中の値を出す。
        // 判定そのものは製品の ClassifyMixture を呼ぶ。途中の値は float(この実行系の書き方のまま)と double の両方で出す。
        static void MixReplica(StringBuilder sb, Color32* orig, float* st, int w, int h, int x, int y, int radius, ColorZone zone) {
            const int R = 2;
            int B = Math.Max(radius, R + 1);
            bool Sel(int xx, int yy) => xx >= 0 && xx < w && yy >= 0 && yy < h && orig[yy * w + xx].a > 0 && st[yy * w + xx] >= 0.97f;
            bool AnyS(int xx, int yy) => xx >= 0 && xx < w && yy >= 0 && yy < h && orig[yy * w + xx].a > 0 && st[yy * w + xx] > 0f;
            bool Inner(int xx, int yy) {
                for (int dy = -1; dy <= 1; dy++) {
                    for (int dx = -1; dx <= 1; dx++) {
                        if (!Sel(xx + dx, yy + dy)) {
                            return false;
                        }
                    }
                }
                return true;
            }
            int Count(Func<int, int, bool> f, int ax0, int ay0, int ax1, int ay1) {
                int n = 0;
                for (int yy = Math.Max(0, ay0); yy <= Math.Min(h - 1, ay1); yy++) {
                    for (int xx = Math.Max(0, ax0); xx <= Math.Min(w - 1, ax1); xx++) {
                        if (f(xx, yy)) {
                            n++;
                        }
                    }
                }
                return n;
            }
            string pre = $"mixrep\t{x},{y}\t";
            int i = y * w + x;
            bool isSolid = Sel(x, y);
            int nBi = Count(Sel, x - B, y - B, x + B, y + B);
            int nAnyR = Count(AnyS, x - R, y - R, x + R, y + R);
            sb.AppendLine(pre + $"isSolid={isSolid} nBi={nBi} nAnyR={nAnyR} s={Hex(st[i])}");
            var idx = new List<int>();
            if (nBi > 0) {
                for (int pass = 0; pass < 2 && idx.Count == 0; pass++) {
                    if (pass == 1 && isSolid) {
                        break;
                    }
                    for (int d = R; d <= B && idx.Count == 0; d++) {
                        for (int yy = Math.Max(0, y - d); yy <= Math.Min(h - 1, y + d); yy++) {
                            int ady = Math.Abs(yy - y);
                            for (int xx = Math.Max(0, x - d); xx <= Math.Min(w - 1, x + d); xx++) {
                                if (d > R && ady < d && Math.Abs(xx - x) < d) {
                                    continue;
                                }
                                if ((yy == y && xx == x) || !Sel(xx, yy)) {
                                    continue;
                                }
                                if (pass == 0 && !Inner(xx, yy)) {
                                    continue;
                                }
                                idx.Add(yy * w + xx);
                            }
                        }
                    }
                }
            }
            float lfR = 0f, lfG = 0f, lfB = 0f, lcR = 0f, lcG = 0f, lcB = 0f, lc2 = 0f, lfM = 0f, lNoise = 0f;
            bool local = idx.Count > 0;
            if (local) {
                foreach (int j in idx) {
                    lfR += orig[j].r; lfG += orig[j].g; lfB += orig[j].b;
                }
                float inv = 1f / idx.Count;
                lfR *= inv; lfG *= inv; lfB *= inv;
                lfM = (lfR + lfG + lfB) * (1f / 3f);
                lcR = lfR - lfM; lcG = lfG - lfM; lcB = lfB - lfM;
                lc2 = lcR * lcR + lcG * lcG + lcB * lcB;
                float v = 0f;
                foreach (int j in idx) {
                    var o = orig[j];
                    float oM = (o.r + o.g + o.b) * (1f / 3f);
                    float dr = o.r - oM - lcR, dg = o.g - oM - lcG, db = o.b - oM - lcB;
                    v += dr * dr + dg * dg + db * db;
                }
                lNoise = Mathf.Sqrt(v * inv);
            }
            sb.AppendLine(pre + $"F n={idx.Count} lf={Hex(lfR)},{Hex(lfG)},{Hex(lfB)} lfM={Hex(lfM)} lc2={Hex(lc2)}({lc2.ToString("R", Inv)}) noise={Hex(lNoise)}({lNoise.ToString("R", Inv)})");
            var op = orig[i];
            float pM = (op.r + op.g + op.b) * (1f / 3f);
            float cpR = op.r - pM, cpG = op.g - pM, cpB = op.b - pM;
            // 背景(面)の候補
            float bR = 0f, bG = 0f, bB = 0f;
            int bN = 0;
            for (int d = 1; d <= B && bN == 0; d++) {
                for (int yy = Math.Max(0, y - d); yy <= Math.Min(h - 1, y + d); yy++) {
                    int ady = Math.Abs(yy - y);
                    for (int xx = Math.Max(0, x - d); xx <= Math.Min(w - 1, x + d); xx++) {
                        if (ady < d && Math.Abs(xx - x) < d) {
                            continue;
                        }
                        int j = yy * w + xx;
                        if (st[j] > 0f) {
                            continue;
                        }
                        var o = orig[j];
                        if (o.a == 0) {
                            continue;
                        }
                        float oM = (o.r + o.g + o.b) * (1f / 3f);
                        float ocR = o.r - oM, ocG = o.g - oM, ocB = o.b - oM;
                        bool flat = true;
                        for (int ny = Math.Max(0, yy - R); ny <= Math.Min(h - 1, yy + R) && flat; ny++) {
                            for (int nx = Math.Max(0, xx - R); nx <= Math.Min(w - 1, xx + R); nx++) {
                                if (orig[ny * w + nx].a == 0) { flat = false; break; }
                            }
                        }
                        for (int ny = yy - 1; ny <= yy + 1 && flat; ny++) {
                            if (ny < 0 || ny >= h) {
                                continue;
                            }
                            for (int nx = xx - 1; nx <= xx + 1; nx++) {
                                if (nx < 0 || nx >= w) {
                                    continue;
                                }
                                int k = ny * w + nx;
                                if (k == j) {
                                    continue;
                                }
                                if (st[k] > 0f) { flat = false; break; }
                                var q = orig[k];
                                float qM = (q.r + q.g + q.b) * (1f / 3f);
                                float eR0 = q.r - qM - ocR, eG0 = q.g - qM - ocG, eB0 = q.b - qM - ocB;
                                if (eR0 * eR0 + eG0 * eG0 + eB0 * eB0 > 144f) { flat = false; break; }
                            }
                        }
                        if (flat) {
                            bR += o.r; bG += o.g; bB += o.b; bN++;
                        }
                    }
                }
            }
            float cbR = 0f, cbG = 0f, cbB = 0f, bM = 0f;
            if (bN > 0) {
                float inv = 1f / bN;
                float mR = bR * inv, mG = bG * inv, mB = bB * inv;
                bM = (mR + mG + mB) * (1f / 3f);
                cbR = mR - bM; cbG = mG - bM; cbB = mB - bM;
            }
            sb.AppendLine(pre + $"p={op.r},{op.g},{op.b} pM={Hex(pM)} B n={bN} cb={Hex(cbR)},{Hex(cbG)},{Hex(cbB)} bM={Hex(bM)}");
            var cm = PP.GetMethod("ClassifyMixture", All);
            if (local) {
                var args = new object[] { cpR, cpG, cpB, pM, lcR, lcG, lcB, lc2, lfM, bN > 0, cbR, cbG, cbB, bM, lNoise, 0f };
                int kind = (int)cm.Invoke(null, args);
                float alpha = (float)args[15];
                sb.AppendLine(pre + $"classify(local)\tkind={kind}\talpha={Hex(alpha)}({alpha.ToString("R", Inv)})");
                ClassifyBreakdown(sb, pre, cpR, cpG, cpB, pM, lcR, lcG, lcB, lc2, lfM, bN > 0, cbR, cbG, cbB, bM, lNoise);
            }
        }

        // ClassifyMixture の比べる量を、float と double で出す(製品の式と同じ並び)。
        static void ClassifyBreakdown(StringBuilder sb, string pre, float cpR, float cpG, float cpB, float pm,
            float cfR, float cfG, float cfB, float cf2, float fm, bool hasB, float cbR, float cbG, float cbB, float bm, float noise) {
            float noiseTol = 2f * noise;
            float cp2 = cpR * cpR + cpG * cpG + cpB * cpB;
            float cb2 = cbR * cbR + cbG * cbG + cbB * cbB;
            double dnoiseTol = 2.0 * noise;
            double dcp2 = (double)cpR * cpR + (double)cpG * cpG + (double)cpB * cpB;
            double dcb2 = (double)cbR * cbR + (double)cbG * cbG + (double)cbB * cbB;
            sb.AppendLine(pre + $"h1? hasB={hasB} cp2={cp2.ToString("R", Inv)} cb2={cb2.ToString("R", Inv)} (flat2=144)");
            if (hasB && (cp2 > 144f || cb2 <= 144f)) {
                float qR = cpR - cbR, qG = cpG - cbG, qB = cpB - cbB;
                float q2 = qR * qR + qG * qG + qB * qB;
                float dR = cfR - cbR, dG = cfG - cbG, dB = cfB - cbB;
                float d2 = dR * dR + dG * dG + dB * dB;
                float a = (qR * dR + qG * dG + qB * dB) / d2;
                float eR = qR - a * dR, eG = qG - a * dG, eB = qB - a * dB;
                float res2 = eR * eR + eG * eG + eB * eB;
                float frac = cb2 <= 144f ? 0.10f : 0.25f;
                float tol = Mathf.Max(8f, frac * Mathf.Sqrt(q2)) + noiseTol;
                float lightRes = Mathf.Abs(pm - (a * fm + (1f - a) * bm));
                float lightTol = Mathf.Max(24f, 0.25f * Mathf.Abs(fm - bm));
                double dq2 = (double)qR * qR + (double)qG * qG + (double)qB * qB;
                double dd2 = (double)dR * dR + (double)dG * dG + (double)dB * dB;
                double da = ((double)qR * dR + (double)qG * dG + (double)qB * dB) / dd2;
                double deR = qR - da * dR, deG = qG - da * dG, deB = qB - da * dB;
                double dres2 = deR * deR + deG * deG + deB * deB;
                double dtol = Math.Max(8.0, frac * Math.Sqrt(dq2)) + dnoiseTol;
                double dlightRes = Math.Abs(pm - (da * fm + (1.0 - da) * bm));
                sb.AppendLine(pre + $"h1near q2={q2.ToString("R", Inv)}(<=64?) |pm-bm|={Mathf.Abs(pm - bm).ToString("R", Inv)}|{Math.Abs((double)pm - bm).ToString("R", Inv)}(<=24 → 面)");
                sb.AppendLine(pre + $"h1 d2={d2.ToString("R", Inv)} (>=576?) a={a.ToString("R", Inv)}|{da.ToString("R", Inv)} " +
                              $"res2={res2.ToString("R", Inv)}|{dres2.ToString("R", Inv)} tol2={(tol * tol).ToString("R", Inv)}|{(dtol * dtol).ToString("R", Inv)} " +
                              $"lightRes={lightRes.ToString("R", Inv)}|{dlightRes.ToString("R", Inv)} lightTol={lightTol.ToString("R", Inv)}");
            }
            float a1 = (cpR * cfR + cpG * cfG + cpB * cfB) / cf2;
            float e1R = cpR - a1 * cfR, e1G = cpG - a1 * cfG, e1B = cpB - a1 * cfB;
            float r1 = e1R * e1R + e1G * e1G + e1B * e1B;
            float tol1 = Mathf.Max(8f, 0.10f * Mathf.Sqrt(cp2)) + noiseTol;
            double da1 = ((double)cpR * cfR + (double)cpG * cfG + (double)cpB * cfB) / cf2;
            double de1R = cpR - da1 * cfR, de1G = cpG - da1 * cfG, de1B = cpB - da1 * cfB;
            double dr1 = de1R * de1R + de1G * de1G + de1B * de1B;
            double dtol1 = Math.Max(8.0, 0.10 * Math.Sqrt(dcp2)) + dnoiseTol;
            float rem = pm - a1 * fm;
            float bn = Mathf.Clamp(rem / (1f - a1), 0f, 255f);
            float lightGap = Mathf.Abs(rem - (1f - a1) * bn);
            double drem = pm - da1 * fm;
            double dbn = Math.Min(255.0, Math.Max(0.0, drem / (1.0 - da1)));
            double dlightGap = Math.Abs(drem - (1.0 - da1) * dbn);
            sb.AppendLine(pre + $"h2 a1={a1.ToString("R", Inv)}|{da1.ToString("R", Inv)} r1={r1.ToString("R", Inv)}|{dr1.ToString("R", Inv)} " +
                          $"tol1^2={(tol1 * tol1).ToString("R", Inv)}|{(dtol1 * dtol1).ToString("R", Inv)} lightGap={lightGap.ToString("R", Inv)}|{dlightGap.ToString("R", Inv)} (>24 → 素材)");
        }

        // ───────── 読み書き ─────────
        static Color32[] ReadRgba(string path, out int w, out int h) {
            var data = File.ReadAllBytes(path);
            w = BitConverter.ToInt32(data, 0);
            h = BitConverter.ToInt32(data, 4);
            var px = new Color32[w * h];
            fixed (byte* s = data) {
                fixed (Color32* d = px) {
                    Buffer.MemoryCopy(s + 8, d, (long)w * h * 4, (long)w * h * 4);
                }
            }
            return px;
        }

        static bool[] ReadMask(string path, out int w, out int h) {
            var data = File.ReadAllBytes(path);
            w = BitConverter.ToInt32(data, 0);
            h = BitConverter.ToInt32(data, 4);
            var m = new bool[w * h];
            for (int i = 0; i < m.Length; i++) {
                m[i] = data[8 + i] != 0;
            }
            return m;
        }

        static Stream CreateGz(string path) => new GZipStream(File.Create(path), System.IO.Compression.CompressionLevel.Fastest);

        static void WriteHeader(Stream s, int w, int h) {
            s.Write(BitConverter.GetBytes(w), 0, 4);
            s.Write(BitConverter.GetBytes(h), 0, 4);
        }

        // 4K の強度は 64MB あるので、写しの配列を作らずに 1MB ずつ書く(Mono のヒープを膨らませない)。
        static void WriteFloatsGz(string path, float[] a, int w, int h) {
            var chunk = new byte[1 << 20];
            using (var gz = CreateGz(path)) {
                WriteHeader(gz, w, h);
                long total = (long)a.Length * 4;
                for (long off = 0; off < total; off += chunk.Length) {
                    int n = (int)Math.Min(chunk.Length, total - off);
                    Buffer.BlockCopy(a, (int)off, chunk, 0, n);
                    gz.Write(chunk, 0, n);
                }
            }
        }

        static void WriteBytesGz(string path, byte[] bytes, int w, int h) {
            using (var gz = CreateGz(path)) {
                WriteHeader(gz, w, h);
                gz.Write(bytes, 0, bytes.Length);
            }
        }

        static void WriteRgbaGz(string path, Color32[] px, int w, int h) {
            var bytes = new byte[(long)px.Length * 4];
            fixed (Color32* s = px) {
                fixed (byte* d = bytes) {
                    Buffer.MemoryCopy(s, d, bytes.Length, bytes.Length);
                }
            }
            WriteBytesGz(path, bytes, w, h);
        }
    }
}
