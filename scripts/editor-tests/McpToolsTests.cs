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
            CollectionAssert.AreEqual(new[] { "iroca_inspect", "iroca_open", "iroca_recolor" }, names);
        }

        [Test]
        public void Tools_HaveDescriptionsForTheAi()
        {
            foreach (var t in _mcp.GetTypes())
            {
                var attr = t.GetCustomAttributes(false)
                    .FirstOrDefault(a => a.GetType().FullName == "MCPForUnity.Editor.Tools.McpForUnityToolAttribute");
                if (attr == null) continue;
                var description = (string)attr.GetType().GetProperty("Description").GetValue(attr);
                Assert.IsFalse(string.IsNullOrWhiteSpace(description), t.Name + " に説明が無い(AI には 'Tool: 名前' としか見えない)");
            }
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
        public void Recolor_PassesArgumentsAsTheRequest()
        {
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string json = Call("IrocaRecolorTool",
                "{\"texture\":\"" + src + "\",\"apply\":\"preview\",\"changes\":[{\"name\":\"red\",\"at\":[0.5,0.5],\"to\":\"#1A2B5C\"}]}");
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("red", Token(json, "data.zones[0].name"), json);
            Assert.AreEqual("1", Token(json, "data.changedFraction"), json);
        }

        [Test]
        public void Recolor_AcceptsChangesSentAsString()
        {
            // 配列を JSON の文字列で送ってくるクライアントもある。
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string json = Call("IrocaRecolorTool", Args(("texture", src), ("apply", "preview"),
                ("changes", "[{\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}]")));
            Assert.AreEqual("True", Token(json, "success"), json);
        }

        [Test]
        public void Recolor_FailureInIroca_BecomesError()
        {
            string json = Call("IrocaRecolorTool", Args(("texture", _assets.Folder + "/missing.png"), ("apply", "preview")));
            Assert.AreEqual("False", Token(json, "success"), json);
            StringAssert.StartsWith("not a texture", Token(json, "error"), json);
        }

        [Test]
        public void Open_UnknownTarget_BecomesError()
        {
            string json = Call("IrocaOpenTool", Args(("target", "NoSuchObject_IrocaTest")));
            Assert.AreEqual("False", Token(json, "success"), json);
            StringAssert.StartsWith("not found:", Token(json, "error"), json);
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
