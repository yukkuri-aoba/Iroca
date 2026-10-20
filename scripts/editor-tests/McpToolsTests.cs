// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Iroca.EditorTests
{
    /// <summary>
    /// MCP 連携（Code/McpIntegration）のツールを、MCP for Unity と同じ入口（HandleCommand(JObject)）で呼び、
    /// MCP for Unity が JSON にした応答（success・message・error・data）を確かめる。
    /// MCP for Unity が入っていないホストでは連携のアセンブリ自体が無いので飛ばす。
    /// 連携のアセンブリと Newtonsoft はコンパイル時に参照しない（無いホストでもこのテストがコンパイルできるように）。
    /// </summary>
    public class McpToolsTests
    {
        private const string McpAssemblyName = "Iroca.McpIntegration";

        private Assembly _mcp;
        private Type _jObject;
        private Type _jsonConvert;
        private TestAssets _assets;

        [SetUp]
        public void SetUp()
        {
            _mcp = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == McpAssemblyName);
            if (_mcp == null) Assert.Ignore("MCP for Unity が入っていないホスト（Iroca.McpIntegration が無い）");
            _jObject = Type.GetType("Newtonsoft.Json.Linq.JObject, Newtonsoft.Json", throwOnError: true);
            _jsonConvert = Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json", throwOnError: true);
            _assets = TestAssets.Create();
        }

        [TearDown]
        public void TearDown() => _assets?.Dispose();

        [Test]
        public void Tools_AreRegisteredUnderTheirNames()
        {
            var names = _mcp.GetTypes()
                .Select(t => t.GetCustomAttributes(false)
                    .FirstOrDefault(a => a.GetType().FullName == "MCPForUnity.Editor.Tools.McpForUnityToolAttribute"))
                .Where(a => a != null)
                .Select(a => (string)a.GetType().GetProperty("Name").GetValue(a))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(new[] { "iroca_describe_schema", "iroca_inspect", "iroca_list_presets", "iroca_recolor" }, names);
        }

        [Test]
        public void DescribeSchema_ReturnsDocumentAsData()
        {
            string json = Call("IrocaDescribeSchemaTool", "{}");
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("com.yukkuri-aoba.iroca", Token(json, "data.name"), json);
        }

        [Test]
        public void Inspect_ReturnsResultAsData()
        {
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string json = Call("IrocaInspectTool", Args(("target", src)));
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("texture", Token(json, "data.kind"), json);
            Assert.AreEqual("#C81E1E", Token(json, "data.textures[0].colors[0].hex"), json);
        }

        [Test]
        public void ListPresets_ReturnsListAsData()
        {
            string json = Call("IrocaListPresetsTool", "{}");
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.IsNotNull(Token(json, "data.presets"), json);
        }

        [Test]
        public void Recolor_WithZones_WritesOutputAndReturnsResult()
        {
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string outPath = _assets.Folder + "/out.png";
            string zones = "{\"zones\":[{\"name\":\"red\",\"sample\":[0.784,0.118,0.118],\"target\":[0.1,0.2,0.8]}]}";

            string json = Call("IrocaRecolorTool", Args(("source", src), ("output", outPath), ("zones", zones)));

            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("True", Token(json, "data.ok"), json);
            Assert.AreEqual("1", Token(json, "data.zonesApplied"), json);
            Assert.IsTrue(File.Exists(TestAssets.Abs(outPath)), "出力 PNG を書くこと");
        }

        [Test]
        public void Recolor_FailureInIroca_BecomesError()
        {
            string json = Call("IrocaRecolorTool", Args(
                ("source", _assets.Folder + "/missing.png"), ("output", _assets.Folder + "/out.png"),
                ("zones", "{\"zones\":[{\"name\":\"z\"}]}")));
            Assert.AreEqual("False", Token(json, "success"), json);
            StringAssert.StartsWith("source not found:", Token(json, "error"), json);
        }

        [TestCase(null, "out.png", "{}", null, "source is required")]
        [TestCase("src.png", null, "{}", null, "output is required")]
        [TestCase("src.png", "out.png", null, null, "exactly one of 'preset' or 'zones' must be provided")]
        [TestCase("src.png", "out.png", "{}", "MyPreset", "exactly one of 'preset' or 'zones' must be provided")]
        public void Recolor_InvalidArguments_BecomeError(string source, string output, string zones, string preset, string error)
        {
            string json = Call("IrocaRecolorTool", Args(("source", source), ("output", output), ("zones", zones), ("preset", preset)));
            Assert.AreEqual("False", Token(json, "success"), json);
            Assert.AreEqual(error, Token(json, "error"), json);
        }

        // ─── 呼び出しの道具 ───

        /// <summary>ツールの HandleCommand を呼び、MCP for Unity と同じく Newtonsoft で JSON にして返す。</summary>
        private string Call(string toolClass, string argsJson)
        {
            var tool = _mcp.GetType("Iroca.McpIntegration." + toolClass, throwOnError: true);
            var handle = tool.GetMethod("HandleCommand", BindingFlags.Public | BindingFlags.Static, null, new[] { _jObject }, null);
            Assert.IsNotNull(handle, toolClass + ".HandleCommand(JObject) が無い");
            object result = handle.Invoke(null, new[] { Parse(argsJson) });
            var serialize = _jsonConvert.GetMethod("SerializeObject", new[] { typeof(object) });
            return (string)serialize.Invoke(null, new[] { result });
        }

        private object Parse(string json)
            => _jObject.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { json });

        /// <summary>JSON の path（"data.ok" など）の値を文字列で返す。無ければ null。</summary>
        private string Token(string json, string path)
        {
            object token = _jObject.GetMethod("SelectToken", new[] { typeof(string) }).Invoke(Parse(json), new object[] { path });
            return token?.ToString();
        }

        /// <summary>null の値は入れない引数オブジェクト（MCP の呼び出しで省いた引数と同じ）。</summary>
        private string Args(params (string key, string value)[] pairs)
        {
            object o = Parse("{}");
            var add = _jObject.GetMethod("Add", new[] { typeof(string), Type.GetType("Newtonsoft.Json.Linq.JToken, Newtonsoft.Json") });
            var fromString = Type.GetType("Newtonsoft.Json.Linq.JValue, Newtonsoft.Json").GetConstructor(new[] { typeof(string) });
            foreach (var (key, value) in pairs)
                if (value != null) add.Invoke(o, new[] { key, fromString.Invoke(new object[] { value }) });
            return o.ToString();
        }

        private static Color32[] Fill(int w, int h, Color32 c) => Enumerable.Repeat(c, w * h).ToArray();
    }
}
