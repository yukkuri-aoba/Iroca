// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// 塗りの実行系の突き合わせ(Unity の Mono ↔ ハーネスの .NET 8)の、Unity 側の書き出し。カテゴリ RuntimeParityDump。
    /// 環境変数 IROCA_RT_PARITY_OUT(書き出す置き場)が無ければ Ignore(普段の EditMode では回らない)。
    /// dev_safe の台本 <c>scripts/runtime_parity.py</c> が、入力(IROCA_RT_PARITY_CASES。被写体の素材は公開リポに持たない)を作り、
    /// <c>tools/editor_tests.py -TestFilter RuntimeParityDump</c> で回して、.NET 側の同じ書き出しと比べる。中身は
    /// <see cref="RuntimeParityProbe"/>(.NET 側と同じソース)。
    /// </summary>
    [Category("RuntimeParityDump")]
    public class RuntimeParityDumpTests {
        [Test, Timeout(14400000)]
        public void Dump() {
            string outRoot = Environment.GetEnvironmentVariable("IROCA_RT_PARITY_OUT");
            if (string.IsNullOrEmpty(outRoot)) {
                Assert.Ignore("IROCA_RT_PARITY_OUT が未設定です(突き合わせは dev_safe の scripts/runtime_parity.py で回す)");
            }
            string cases = Environment.GetEnvironmentVariable("IROCA_RT_PARITY_CASES");
            string tag = Environment.GetEnvironmentVariable("IROCA_RT_PARITY_TAG");
            if (string.IsNullOrEmpty(tag)) {
                tag = "mono";
            }
            string threads = Environment.GetEnvironmentVariable("IROCA_RT_PARITY_THREADS");
            int saved = CoreParallelism.Override;
            try {
                if (!string.IsNullOrEmpty(threads)) {
                    CoreParallelism.Override = int.Parse(threads);
                }
                string info = $"unity-mono {Application.unityVersion} codeOptimization={CompilationPipeline.codeOptimization}";
                // 深掘り(IROCA_RT_PARITY_DEEP = 指定のファイル)があれば、そちらだけを回す
                string deep = Environment.GetEnvironmentVariable("IROCA_RT_PARITY_DEEP");
                if (!string.IsNullOrEmpty(deep)) {
                    Directory.CreateDirectory(outRoot);
                    RuntimeParityProbe.RunDeep(deep, Path.Combine(outRoot, "deep_" + tag + ".txt"), info);
                    return;
                }
                RuntimeParityProbe.RunAll(cases, Path.Combine(outRoot, tag), info, msg => Debug.Log("[rt-parity] " + msg));
            } finally {
                CoreParallelism.Override = saved;
            }
        }
    }
}
