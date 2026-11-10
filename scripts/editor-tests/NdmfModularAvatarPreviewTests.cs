// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
#if IROCA_NDMF_PRESENT && IROCA_MA_PRESENT
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Iroca.NdmfIntegration;
using nadena.dev.modular_avatar.core;
using nadena.dev.modular_avatar.core.editor;
using nadena.dev.ndmf;
using nadena.dev.ndmf.preview;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Iroca.EditorTests {
    /// <summary>
    /// 本物の Modular Avatar の Material Setter / Material Swap のプレビュー(<see cref="MaterialSetterPreview"/>)が
    /// プロキシの素材を差し替えた後に、いろかのプレビューのノードが色替えを映すこと。NDMF の描画の流れは、実際と同じく
    /// 「元の Renderer とプロキシの組に、フィルタのノードを順に当てる」で組む(NdmfPreviewFilterTests は上流の差し替えを手で真似る)。
    /// MA が入ったホストでだけコンパイルされる(asmdef の IROCA_MA_PRESENT)。
    /// </summary>
    public class NdmfModularAvatarPreviewTests {
        private const int W = 16, H = 16;
        private TestAssets _assets;
        private readonly List<Object> _created = new List<Object>();
        private readonly List<string> _cacheFiles = new List<string>();
        private LivePreview.Target _saved;

        [SetUp]
        public void SetUp() {
            _assets = TestAssets.Create();
            _saved = LivePreview.Current;
            LivePreview.SetTarget(null, null);
        }

        [TearDown]
        public void TearDown() {
            LivePreview.SetTarget(_saved?.source, _saved?.boundRecipe);
            foreach (var o in _created) {
                if (o != null) {
                    Object.DestroyImmediate(o);
                }
            }
            _created.Clear();
            RecipeTextureBuilder.WaitForCacheWrites();
            foreach (var f in _cacheFiles) {
                if (System.IO.File.Exists(f)) {
                    System.IO.File.Delete(f);
                }
            }
            _cacheFiles.Clear();
            _assets.Dispose();
        }

        private T Track<T>(T o) where T : Object { _created.Add(o); return o; }

        private static System.Type DescriptorType() {
            foreach (var a in System.AppDomain.CurrentDomain.GetAssemblies()) {
                var t = a.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor", false);
                if (t != null) {
                    return t;
                }
            }
            return null;
        }

        private GameObject AvatarRoot() {
            var go = Track(new GameObject("avatar"));
            var descriptor = DescriptorType();
            if (descriptor == null) {
                Assert.Ignore("VRChat SDK の無いホストでは、Modular Avatar のアバターを組めない");
            }
            go.AddComponent(descriptor);
            return go;
        }

        private MeshRenderer Mesh(string name, Material mat, GameObject parent) {
            var go = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            return r;
        }

        private Renderer ProxyOf(Renderer original) {
            var copy = Track(Object.Instantiate(original.gameObject));
            copy.hideFlags = HideFlags.HideAndDontSave;
            return copy.GetComponent<Renderer>();
        }

        private IrocaRecipe RedToGreen(Texture2D src) {
            var zone = new ColorZone {
                sampleColor = new Color(200 / 255f, 40 / 255f, 40 / 255f),
                sampleColorSet = true,
                targetColor = new Color(0.1f, 0.6f, 0.2f),
                tolerance = 0.2f,
            };
            zone.EnsureId();
            var state = new IrocaSessionState();
            state.zones.Add(zone);
            var recipe = RecipeStore.Create(src, state, _assets.Folder);
            _cacheFiles.Add(RecipeTextureBuilder.CachePath(RecipeTextureBuilder.CacheKey(recipe, src)));
            return recipe;
        }

        // 範囲の Renderer 1 つ(元の素材は元テクスチャを使わない)・MA が差し替える先(元テクスチャを使う素材)・色替えのレシピ。
        private (Material plain, Material target, MeshRenderer swapped, GameObject avatar, IrocaRecipe recipe) Scene() {
            string path = _assets.WritePng("src", TestAssets.Solid(W, H, new Color32(200, 40, 40, 255)), W, H);
            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var target = Track(new Material(Shader.Find("Standard")) { name = "target", mainTexture = src });
            var plain = Track(new Material(Shader.Find("Standard")) { name = "plain" });
            var avatar = AvatarRoot();
            var swapped = Mesh("swapped", plain, avatar);
            Mesh("other", plain, avatar);
            var recipe = RedToGreen(src);
            avatar.AddComponent<IrocaRecolor>().recipes.Add(recipe);
            return (plain, target, swapped, avatar, recipe);
        }

        // NDMF が 1 つの Renderer に当てる流れ: MA のノード(プロキシの素材を差し替える)→ いろかのノード(複製へ差し替える)。
        private void RunBothFilters(MeshRenderer original, Renderer proxy, out Material afterMa, out string texAfterIroca) {
            var pairs = new List<(Renderer, Renderer)> { (original, proxy) };
            var ma = new MaterialSetterPreview();
            var maGroups = ma.GetTargetGroups(new ComputeContext("ma"));
            var maGroup = RenderGroup.For(original);
            Assert.IsTrue(maGroups.Contains(maGroup), "MA のノードが差し替える Renderer を見つけていない");
            var maNode = TestAssets.Wait(ma.Instantiate(maGroup, pairs, new ComputeContext("ma-node")));
            try {
                maNode.OnFrame(original, proxy);
                afterMa = proxy.sharedMaterial;

                var iroca = new IrocaPreviewFilter();
                var groups = iroca.FindTargets(new ComputeContext("iroca"));
                int at = groups.FindIndex(g => IrocaPreviewFilter.IsGroupOf(g, original));
                Assert.GreaterOrEqual(at, 0, "いろかのノードが範囲の Renderer に付いていない");
                var group = groups[at];
                var node = TestAssets.Wait(iroca.Instantiate(group, pairs, new ComputeContext("iroca-node")));
                try {
                    maNode.OnFrame(original, proxy);   // NDMF は毎フレーム、プロキシを元へ戻してから各ノードを順に呼ぶ
                    node.OnFrame(original, proxy);
                    var tex = proxy.sharedMaterial.mainTexture;   // ノードを閉じると複製は消えるので、ここで名前を取る
                    texAfterIroca = tex != null ? tex.name : "(null)";
                } finally {
                    node.Dispose();
                }
            } finally {
                maNode.Dispose();
            }
        }

        // NDMF の部品の一覧はエディタのコマを挟んで更新される。同じ実行で前の試験のアバターが残した一覧を引かないよう、組んだ後に
        // 2 コマ待つ(同期の [Test] だと 2 件目の MA の解析が新しい部品を見つけない)。
        [UnityTest]
        public IEnumerator MaterialSetter_SwapsTheMaterial_AndIrocaShowsTheRecolor() {
            var s = Scene();
            var setterGo = Track(new GameObject("setter"));
            setterGo.transform.SetParent(s.avatar.transform, false);
            var setter = setterGo.AddComponent<ModularAvatarMaterialSetter>();
            setter.Objects.Add(new MaterialSwitchObject {
                Object = new AvatarObjectReference { referencePath = "swapped" },
                Material = s.target,
                MaterialIndex = 0,
            });

            yield return null;
            yield return null;
            RunBothFilters(s.swapped, ProxyOf(s.swapped), out var afterMa, out var texAfterIroca);
            Assert.AreSame(s.target, afterMa, "MA が素材を差し替えていない(この試験の前提)");
            StringAssert.EndsWith("(Iroca)", texAfterIroca, "MA で差し替えた素材に色替えが映らない");
            Assert.AreSame(s.plain, s.swapped.sharedMaterial, "元の Renderer を書き換えた");
        }

        [UnityTest]
        public IEnumerator MaterialSwap_SwapsTheMaterial_AndIrocaShowsTheRecolor() {
            var s = Scene();
            var swapGo = Track(new GameObject("swap"));
            swapGo.transform.SetParent(s.avatar.transform, false);
            var swap = swapGo.AddComponent<ModularAvatarMaterialSwap>();
            swap.Root = new AvatarObjectReference { referencePath = AvatarObjectReference.AVATAR_ROOT };
            swap.Swaps = new List<MatSwap> { new MatSwap { From = s.plain, To = s.target } };

            yield return null;
            yield return null;
            RunBothFilters(s.swapped, ProxyOf(s.swapped), out var afterMa, out var texAfterIroca);
            Assert.AreSame(s.target, afterMa, "MA が素材を差し替えていない(この試験の前提)");
            StringAssert.EndsWith("(Iroca)", texAfterIroca, "MA で差し替えた素材に色替えが映らない");
        }
    }
}
#endif
