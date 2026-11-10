// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Iroca.EditorTests {
    /// <summary>
    /// VRChat SDK のビルド・アップロードの台本(<c>Sdk/SdkBuildScenario.cs</c>)が使う合成アバター。
    /// 第三者のアバターを使わないため、メッシュ 1 枚(板)・生成したテクスチャ・マテリアル・いろかのレシピだけをコードで組み立てる。
    /// SDK の型を使わない部分(いろかの内部の型が要る部分)だけをここへ置く。SDK の asmdef は Iroca の内部を見られないため。
    /// テクスチャは左半分が赤(色替え対象・緑へ)、右半分が青(対象外)。
    /// </summary>
    public sealed class SdkBuildFixture : IDisposable {
        private const int Size = 64;

        /// <summary>左半分・右半分の中心の UV。ここの画素を読んで比べる(境界から離れた平らな面。ミップや圧縮のにじみを避ける)。</summary>
        public static readonly Vector2[] SampleUv = { new Vector2(0.25f, 0.5f), new Vector2(0.75f, 0.5f) };

        public GameObject Avatar { get; private set; }
        public Renderer Renderer { get; private set; }
        public string SourceTexturePath { get; private set; }
        public string MaterialPath { get; private set; }

        /// <summary>一時アセットのフォルダ(Dispose で消える)。</summary>
        public string Folder => _assets.Folder;

        /// <summary>SampleUv の位置の、元の色と、色替え後の期待値(レシピから作り直したもの)。</summary>
        public Color32[] Original { get; private set; }
        public Color32[] Expected { get; private set; }

        private TestAssets _assets;
        private string _cacheFile;
        private byte[] _texBytes;
        private byte[] _matBytes;

        public static SdkBuildFixture Create() {
            var f = new SdkBuildFixture();
            try {
                f.Build();
            } catch {
                f.Dispose();
                throw;
            }
            return f;
        }

        private void Build() {
            _assets = TestAssets.Create();
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++) {
                for (int x = 0; x < Size; x++) {
                    px[y * Size + x] = x < Size / 2 ? new Color32(200, 40, 40, 255) : new Color32(40, 40, 200, 255);
                }
            }
            SourceTexturePath = _assets.WritePng("src", px, Size, Size, readable: true);
            // VRChat 向けのアバターと同じく mip streaming をオンにする(オフだと NDMF が差し込みテクスチャを警告する。NdmfProcessAvatarTests と同じ)。
            var importer = (TextureImporter)AssetImporter.GetAtPath(SourceTexturePath);
            importer.streamingMipmaps = true;
            importer.SaveAndReimport();
            var src = AssetDatabase.LoadAssetAtPath<Texture2D>(SourceTexturePath);

            var mat = new Material(Shader.Find("Standard")) { mainTexture = src };
            MaterialPath = _assets.Folder + "/m.mat";
            AssetDatabase.CreateAsset(mat, MaterialPath);

            var mesh = new Mesh {
                name = "quad",
                vertices = new[] { new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(0.5f, 1, 0), new Vector3(-0.5f, 1, 0) },
                uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                triangles = new[] { 0, 2, 1, 0, 3, 2 },
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, _assets.Folder + "/quad.asset");

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
            _cacheFile = RecipeTextureBuilder.CachePath(RecipeTextureBuilder.CacheKey(recipe, src));

            Avatar = new GameObject("IrocaSdkSmokeAvatar");
            var outfit = new GameObject("outfit");
            outfit.transform.SetParent(Avatar.transform, false);
            outfit.AddComponent<MeshFilter>().sharedMesh = mesh;
            Renderer = outfit.AddComponent<MeshRenderer>();
            Renderer.sharedMaterial = mat;
            outfit.AddComponent<IrocaRecolor>().recipes.Add(recipe);

            // 期待値: NDMF のテストと同じく、レシピから作り直した画素(GPU を使わずに出せる)。
            if (!RecipeTextureBuilder.TryRecolor(recipe, out var recolored, out int w, out int h, out var failure)) {
                throw new InvalidOperationException("期待値を作れない: " + failure);
            }
            Original = new Color32[SampleUv.Length];
            Expected = new Color32[SampleUv.Length];
            for (int i = 0; i < SampleUv.Length; i++) {
                int x = (int)(SampleUv[i].x * w), y = (int)(SampleUv[i].y * h);
                Original[i] = px[y * Size + x];
                Expected[i] = recolored[y * w + x];
            }

            AssetDatabase.SaveAssets();
            _texBytes = File.ReadAllBytes(TestAssets.Abs(SourceTexturePath));
            _matBytes = File.ReadAllBytes(TestAssets.Abs(MaterialPath));
        }

        /// <summary>ビルドの前後で元のアセット(テクスチャ・マテリアルのファイル)と、シーン上のアバターが変わっていないかを確かめ、変わったものの説明を返す。</summary>
        public List<string> FindChanges() {
            var changes = new List<string>();
            AssetDatabase.SaveAssets();
            if (!BytesEqual(_texBytes, File.ReadAllBytes(TestAssets.Abs(SourceTexturePath)))) {
                changes.Add("元のテクスチャのファイルが変わった");
            }
            if (!BytesEqual(_matBytes, File.ReadAllBytes(TestAssets.Abs(MaterialPath)))) {
                changes.Add("元のマテリアルのファイルが変わった");
            }
            if (Avatar == null) {
                changes.Add("シーンのアバターが無くなった");
                return changes;
            }
            if (Avatar.GetComponentsInChildren<IrocaRecolor>(true).Length != 1) {
                changes.Add("シーンのアバターのいろかのコンポーネントが消えた(ビルドは複製でやるはず)");
            }
            var mat = Renderer != null ? Renderer.sharedMaterial : null;
            if (mat == null || AssetDatabase.GetAssetPath(mat) != MaterialPath) {
                changes.Add("シーンのアバターのマテリアルが元のアセットでなくなった");
            } else if (AssetDatabase.GetAssetPath(mat.mainTexture) != SourceTexturePath) {
                changes.Add("シーンのアバターのマテリアルのテクスチャが元のアセットでなくなった");
            }
            return changes;
        }

        private static bool BytesEqual(byte[] a, byte[] b) {
            if (a.Length != b.Length) {
                return false;
            }
            for (int i = 0; i < a.Length; i++) {
                if (a[i] != b[i]) {
                    return false;
                }
            }
            return true;
        }

        public void Dispose() {
            RecipeTextureBuilder.WaitForCacheWrites();
            if (_cacheFile != null && File.Exists(_cacheFile)) {
                File.Delete(_cacheFile);
            }
            if (Avatar != null) {
                UnityEngine.Object.DestroyImmediate(Avatar);
                Avatar = null;
            }
            _assets?.Dispose();
            _assets = null;
        }
    }
}
