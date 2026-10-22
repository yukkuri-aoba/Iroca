// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 任意の依存(AI の Sentis・Inference Engine、MCP for Unity)の asmdef の版の範囲を、Unity がそのとおりに
    /// 読んでいるか。上限は次の版のベータ版も外すよう "-0" で書く("[10.0.0,11.0.0-0)" なら 11.0.0-beta.1 は外れる)。
    /// 書き間違えると、確かめていない版に対して連携部分がコンパイルされ、プロジェクト全体のコンパイルが止まる
    /// (あるいは連携が黙って消える)。入っているパッケージの版から、付くはずの define を出して照らす。
    /// NDMF の範囲は NdmfSupportTests が見る。
    /// </summary>
    public class OptionalPackageRangeTests
    {
        static string InstalledVersion(string package) =>
            PackageInfo.FindForAssetPath($"Packages/{package}/package.json")?.version;

        static int[] Core(string v)
        {
            int dash = v.IndexOf('-');
            return (dash < 0 ? v : v.Substring(0, dash)).Split('.').Select(int.Parse).ToArray();
        }

        static int CompareCore(int[] a, int[] b)
        {
            for (int i = 0; i < 3; i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return 0;
        }

        // [lo, hi-0) に入るか。版の比べ方は SemVer(プレリリースは同じ番号の正式版より前。hi-0 は hi のどのベータ版よりも前)。
        static bool InRange(string version, string lo, string hi)
        {
            if (version == null) return false;
            var v = Core(version);
            int c = CompareCore(v, Core(lo));
            bool atLeastLo = c > 0 || (c == 0 && version.IndexOf('-') < 0);
            return atLeastLo && CompareCore(v, Core(hi)) < 0;
        }

        static bool Loaded(string assembly) =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == assembly);

        [Test]
        public void VersionDefines_FollowTheInstalledPackages()
        {
            string sentis = InstalledVersion("com.unity.sentis");
            string inference = InstalledVersion("com.unity.ai.inference");
            string mcp = InstalledVersion("com.coplaydev.unity-mcp");
            string env = $"Sentis {sentis ?? "なし"}・Inference Engine {inference ?? "なし"}・MCP for Unity {mcp ?? "なし"}";

            bool engine = InRange(inference, "2.2.0", "3.0.0");
            bool ai = InRange(sentis, "2.0.0", "2.2.0") || engine;
            Assert.AreEqual(ai, Loaded("Iroca.SentisIntegration"), $"AI 連携がコンパイルされるか（{env}）");
            Assert.AreEqual(ai, MaskSuggestBridge.Available, $"AI 連携がサービスを登録したか（{env}）");
            if (ai)
            {
                var asm = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                    .First(a => a.name == "Iroca.SentisIntegration");
                Assert.AreEqual(engine, asm.defines.Contains("IROCA_INFERENCE_ENGINE"), $"IROCA_INFERENCE_ENGINE（{env}）");
            }

            Assert.AreEqual(InRange(mcp, "10.0.0", "11.0.0"), Loaded("Iroca.McpIntegration"),
                $"MCP 連携がコンパイルされるか（{env}）");
        }

        [Test]
        public void RangeCheck_ExcludesTheNextMajorsBetas()
        {
            // このテスト自身の比べ方の確認(Unity の比べ方と同じ前提)。
            Assert.IsTrue(InRange("10.3.1-beta.11", "10.0.0", "11.0.0"), "今の系列のベータ版は範囲に入る");
            Assert.IsFalse(InRange("11.0.0-beta.1", "10.0.0", "11.0.0"), "次のメジャーのベータ版は外れる");
            Assert.IsFalse(InRange("11.0.0", "10.0.0", "11.0.0"));
            Assert.IsFalse(InRange("10.0.0-beta.3", "10.0.0", "11.0.0"), "下限の版のベータ版は外れる");
            Assert.IsTrue(InRange("2.1.3", "2.0.0", "2.2.0"));
            Assert.IsFalse(InRange("2.2.0", "2.0.0", "2.2.0"), "Unity 6 の中継だけの Sentis 2.2.0 は外れる");
        }
    }
}
