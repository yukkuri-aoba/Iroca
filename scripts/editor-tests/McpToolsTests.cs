// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// MCP 連携（Code/McpIntegration）のツールを、MCP for Unity と同じ入口（HandleCommand(JObject)）で呼び、
    /// MCP for Unity が JSON にした応答（success・message・error・data）を確かめる。
    /// MCP for Unity が入っていないホストでは連携のアセンブリ自体が無いので飛ばす。
    /// 連携のアセンブリと Newtonsoft はコンパイル時に参照しない（無いホストでもこのテストがコンパイルできるように）。
    /// </summary>
    public class McpToolsTests {
        private const string McpAssemblyName = "Iroca.McpIntegration";

        private Assembly _mcp;
        private Type _jObject;
        private Type _jsonConvert;
        private TestAssets _assets;

        [SetUp]
        public void SetUp() {
            _mcp = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == McpAssemblyName);
            if (_mcp == null) {
                Assert.Ignore("MCP for Unity が入っていないホスト（Iroca.McpIntegration が無い）");
            }
            _jObject = Type.GetType("Newtonsoft.Json.Linq.JObject, Newtonsoft.Json", throwOnError: true);
            _jsonConvert = Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json", throwOnError: true);
            _assets = TestAssets.Create();
        }

        [TearDown]
        public void TearDown() {
            IrocaAutomation.CancelRecolorJob();   // 待ちのまま落ちたテストの依頼を、次のテストへ持ち越さない
            _assets?.Dispose();
        }

        [Test]
        public void Tools_AreRegisteredUnderTheirNames() {
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
        public void Tools_HaveDescriptionsForTheAi() {
            foreach (var t in _mcp.GetTypes()) {
                var attr = t.GetCustomAttributes(false)
                    .FirstOrDefault(a => a.GetType().FullName == "MCPForUnity.Editor.Tools.McpForUnityToolAttribute");
                if (attr == null) {
                    continue;
                }
                var description = (string)attr.GetType().GetProperty("Description").GetValue(attr);
                Assert.IsFalse(string.IsNullOrWhiteSpace(description), t.Name + " に説明が無い(AI には 'Tool: 名前' としか見えない)");
            }
        }

        [Test]
        public void Inspect_ReturnsResultAsData() {
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string json = Call("IrocaInspectTool", Args(("target", src)));
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("texture", Token(json, "data.kind"), json);
            Assert.AreEqual("#C81E1E", Token(json, "data.textures[0].colors[0].hex"), json);
        }

        [Test]
        public void Recolor_PassesArgumentsAsTheRequest() {
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string json = Call("IrocaRecolorTool",
                "{\"texture\":\"" + src + "\",\"apply\":\"preview\",\"autoTune\":false,\"changes\":[{\"name\":\"red\",\"at\":[0.5,0.5],\"to\":\"#1A2B5C\"}]}");
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("red", Token(json, "data.zones[0].name"), json);
            Assert.AreEqual("1", Token(json, "data.changedFraction"), json);
        }

        [Test]
        public void Recolor_AcceptsChangesSentAsString() {
            // 配列を JSON の文字列で送ってくるクライアントもある。
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string json = Call("IrocaRecolorTool", Args(("texture", src), ("apply", "preview"),
                ("changes", "[{\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}]")));
            Assert.AreEqual("True", Token(json, "success"), json);
        }

        [Test]
        public void Inspect_PassesPoints() {
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string json = Call("IrocaInspectTool", "{\"target\":\"" + src + "\",\"points\":[[0.5,0.5]]}");
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("#C81E1E", Token(json, "data.textures[0].points[0].hex"), json);
            json = Call("IrocaInspectTool", Args(("target", src), ("points", "[[0.5,0.5]]")));
            Assert.AreEqual("#C81E1E", Token(json, "data.textures[0].points[0].hex"), json);
        }

        [Test]
        public void Inspect_BadPoints_BecomeError() {
            string json = Call("IrocaInspectTool", "{\"points\":[0.5,0.5]}");
            Assert.AreEqual("False", Token(json, "success"), json);
            StringAssert.Contains("'points'", Token(json, "error"), json);
        }

        [Test]
        public void Recolor_PassesMasks_AlsoWhenSentAsString() {
            // メッシュの無いテクスチャなので、依頼が届けばメッシュが無いと断る。
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string changes = "[{\"name\":\"red\",\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}]";
            string masks = "[{\"op\":\"dont_paint\",\"at\":[0.5,0.5],\"by\":\"island\"}]";
            string json = Call("IrocaRecolorTool", "{\"texture\":\"" + src + "\",\"apply\":\"preview\",\"autoTune\":false,\"changes\":" + changes + ",\"masks\":" + masks + "}");
            StringAssert.Contains("no readable mesh", Token(json, "error"), json);
            json = Call("IrocaRecolorTool", Args(("texture", src), ("apply", "preview"), ("changes", changes), ("masks", masks)));
            StringAssert.Contains("no readable mesh", Token(json, "error"), json);
        }

        [Test]
        public void Recolor_SingleObjectInsteadOfArray_IsWrapped() {
            // JsonUtility は配列の欄に 1 個のオブジェクトが来ると黙って空にする(2022.3・6000.0)。以前は masks を 1 個で渡すと
            // マスク無しで成功していた。MCP の層で配列に包む。
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string change = "{\"name\":\"red\",\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}";
            string mask = "{\"op\":\"dont_paint\",\"at\":[0.5,0.5],\"by\":\"island\"}";
            string json = Call("IrocaRecolorTool", "{\"texture\":\"" + src + "\",\"apply\":\"preview\",\"autoTune\":false,\"changes\":" + change + "}");
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("red", Token(json, "data.zones[0].name"), json);
            json = Call("IrocaRecolorTool", "{\"texture\":\"" + src + "\",\"apply\":\"preview\",\"autoTune\":false,\"changes\":[" + change + "],\"masks\":" + mask + "}");
            StringAssert.Contains("no readable mesh", Token(json, "error"), "マスク操作が届いていない: " + json);
            json = Call("IrocaRecolorTool", Args(("texture", src), ("apply", "preview"), ("changes", "[" + change + "]"), ("masks", mask)));
            StringAssert.Contains("no readable mesh", Token(json, "error"), "文字列で送ったマスク操作が届いていない: " + json);
        }

        [TestCase("{\"changes\":5}", "'changes' must be a JSON array")]
        [TestCase("{\"changes\":[{\"at\":[NaN,0.5],\"to\":\"#1A2B5C\"}]}", "changes[0].at")]
        [TestCase("{\"changes\":[{\"at\":[\"NaN\",0.5],\"to\":\"#1A2B5C\"}]}", "changes[0].at")]
        [TestCase("{\"changes\":[{\"at\":[null,0.5],\"to\":\"#1A2B5C\"}]}", "changes[0].at")]
        [TestCase("{\"changes\":[{\"at\":0.5,\"to\":\"#1A2B5C\"}]}", "changes[0].at")]
        [TestCase("{\"changes\":[{\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}],\"masks\":[{\"op\":\"only\",\"at\":[0.5,Infinity]}]}", "masks[0].at")]
        public void Recolor_AtThatIsNotTwoNumbers_IsRefused(string body, string expected) {
            // Newtonsoft は素の NaN を読むが、書き戻すと "NaN" の文字列になり、JsonUtility はそれを 0 と読む(2022.3・6000.0)。
            // 文字列・null も黙って数になる。その場所を色替えしないよう、MCP の層で断る。
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string json = Call("IrocaRecolorTool",
                "{\"texture\":\"" + src + "\",\"apply\":\"preview\",\"autoTune\":false," + body.Substring(1));
            Assert.AreEqual("False", Token(json, "success"), json);
            StringAssert.Contains(expected, Token(json, "error"), json);
        }

        [UnityTest]
        public IEnumerator Recolor_AutoTune_RepliesPendingThenResultOnStatus() {
            // 自動調整を待つ間は MCP の pending を返し、サーバーの問い合わせ直し(action=status)で最後の結果を返す。
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string args = "{\"texture\":\"" + src + "\",\"apply\":\"preview\",\"changes\":[{\"name\":\"red\",\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}]}";
            string json = Call("IrocaRecolorTool", args);
            bool sawPending = false;
            double deadline = UnityEditor.EditorApplication.timeSinceStartup + 120;
            while (Token(json, "_mcp_status") == "pending") {
                sawPending = true;
                Assert.AreEqual("True", Token(json, "success"), json);
                Assert.Less(UnityEditor.EditorApplication.timeSinceStartup, deadline, json);
                yield return null;
                json = Call("IrocaRecolorTool", AddAction(args));
            }
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("conventional", Token(json, "data.zones[0].autoTune"), json);
            if (!sawPending) {
                Assert.Pass("解析が 1 回目の呼び出しの中で終わった(pending を経ずに結果)");
            }
        }

        [UnityTest]
        public IEnumerator Recolor_Png_RepliesPendingThenWritesOnStatus() {
            // png の仕上げ(色替え・PNG 化)は背景で進めるので、自動調整なしでも pending を返し、問い合わせ直しで結果を返す。
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string args = "{\"texture\":\"" + src + "\",\"apply\":\"png\",\"autoTune\":false,\"changes\":[{\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}]}";
            string json = Call("IrocaRecolorTool", args);
            Assert.AreEqual("pending", Token(json, "_mcp_status"), json);
            double deadline = UnityEditor.EditorApplication.timeSinceStartup + 60;
            while (Token(json, "_mcp_status") == "pending") {
                Assert.Less(UnityEditor.EditorApplication.timeSinceStartup, deadline, json);
                yield return null;
                json = Call("IrocaRecolorTool", AddAction(args));
            }
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.IsTrue(File.Exists(Token(json, "data.output")), json);
        }

        [UnityTest]
        public IEnumerator Recolor_StatusWithOtherArguments_DoesNotTakeAnotherCallsResult() {
            // サーバーは、同じ引数に action=status を足して問い合わせ直す。別の引数の問い合わせ(読み直しで消えた前の依頼の
            // 問い合わせなど)は、今の依頼の結果を取らない。
            string src = _assets.WritePng("src", Fill(16, 8, new Color32(200, 30, 30, 255)), 16, 8);
            string args = "{\"texture\":\"" + src + "\",\"apply\":\"preview\",\"changes\":[{\"name\":\"red\",\"from\":\"#C81E1E\",\"to\":\"#1A2B5C\"}]}";
            string other = "{\"texture\":\"" + src + "\",\"apply\":\"preview\",\"changes\":[{\"name\":\"blue\",\"from\":\"#C81E1E\",\"to\":\"#FFAA00\"}]}";
            string json = Call("IrocaRecolorTool", args);
            if (Token(json, "_mcp_status") != "pending") {
                Assert.Inconclusive("解析が 1 回目の呼び出しの中で終わった(待ちが無い)");
            }
            string refused = Call("IrocaRecolorTool", AddAction(other));
            Assert.AreEqual("False", Token(refused, "success"), refused);
            StringAssert.Contains("not in progress", Token(refused, "error"), refused);

            double deadline = UnityEditor.EditorApplication.timeSinceStartup + 120;
            while (Token(json, "_mcp_status") == "pending") {
                Assert.Less(UnityEditor.EditorApplication.timeSinceStartup, deadline, json);
                yield return null;
                json = Call("IrocaRecolorTool", AddAction(args));
            }
            Assert.AreEqual("True", Token(json, "success"), json);
            Assert.AreEqual("red", Token(json, "data.zones[0].name"), json);
        }

        [Test]
        public void Recolor_TimeBudget_EndsBeforeTheServerStopsPolling() {
            // 依頼全体の期限は、サーバーが問い合わせをやめる時間(MaxPollSeconds)より短い(期限で終えた結果を受け取れる)。
            var tool = _mcp.GetType("Iroca.McpIntegration.IrocaRecolorTool", throwOnError: true);
            var attr = tool.GetCustomAttributes(false)
                .First(a => a.GetType().FullName == "MCPForUnity.Editor.Tools.McpForUnityToolAttribute");
            var maxPoll = Convert.ToDouble(attr.GetType().GetProperty("MaxPollSeconds").GetValue(attr));
            Assert.Greater(maxPoll, IrocaAutomation.RequestBudgetSeconds + 30, "期限の後の仕上げ(書き出し)の分の余裕");
        }

        private string AddAction(string argsJson)
            => argsJson.Substring(0, argsJson.Length - 1) + ",\"action\":\"status\"}";

        [Test]
        public void Recolor_FailureInIroca_BecomesError() {
            string json = Call("IrocaRecolorTool", Args(("texture", _assets.Folder + "/missing.png"), ("apply", "preview")));
            Assert.AreEqual("False", Token(json, "success"), json);
            StringAssert.StartsWith("not a texture", Token(json, "error"), json);
        }

        [Test]
        public void Open_UnknownTarget_BecomesError() {
            string json = Call("IrocaOpenTool", Args(("target", "NoSuchObject_IrocaTest")));
            Assert.AreEqual("False", Token(json, "success"), json);
            StringAssert.StartsWith("not found:", Token(json, "error"), json);
        }

        // ─── 呼び出しの道具 ───

        /// <summary>ツールの HandleCommand を呼び、MCP for Unity と同じく Newtonsoft で JSON にして返す。</summary>
        private string Call(string toolClass, string argsJson) {
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
        private string Token(string json, string path) {
            object token = _jObject.GetMethod("SelectToken", new[] { typeof(string) }).Invoke(Parse(json), new object[] { path });
            return token?.ToString();
        }

        /// <summary>null の値は入れない引数オブジェクト（MCP の呼び出しで省いた引数と同じ）。</summary>
        private string Args(params (string key, string value)[] pairs) {
            object o = Parse("{}");
            var add = _jObject.GetMethod("Add", new[] { typeof(string), Type.GetType("Newtonsoft.Json.Linq.JToken, Newtonsoft.Json") });
            var fromString = Type.GetType("Newtonsoft.Json.Linq.JValue, Newtonsoft.Json").GetConstructor(new[] { typeof(string) });
            foreach (var (key, value) in pairs) {
                if (value != null) {
                    add.Invoke(o, new[] { key, fromString.Invoke(new object[] { value }) });
                }
            }
            return o.ToString();
        }

        private static Color32[] Fill(int w, int h, Color32 c) => Enumerable.Repeat(c, w * h).ToArray();
    }
}
