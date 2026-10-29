// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using F = Iroca.EditorTests.OldFormatFixtures;

namespace Iroca.EditorTests
{
    /// <summary>
    /// 古い版のいろかが書いたプリセット・セッションのファイル・レシピを、今の版が同じ中身として読むか（0.4.0 の計画の 0-2）。
    /// フェーズ 4 で編集中の文書（EditingSession）・保存と復元（SessionPersistence）・プリセットの読み込み（PresetApplier）を
    /// 切り出すときに、保存される名前や移行の手順を落とさないための固定。見本は <see cref="OldFormatFixtures"/>。
    /// <list type="bullet">
    /// <item>v0.1.0（VACC）のプリセット: 優先度を layerIndex（大きいほど上）で持ち、後の版で足した項目が無い。
    /// ウィンドウは layerIndex の大きい順に並べ替え、無い項目は今の既定値になる。</item>
    /// <item>v0.3.0 のプリセット・セッションのファイル・レシピ: ゾーン・設定・マスクをそのまま読む。共通のマスクは
    /// 旧 bitpack（"R:" の付かない形）で書いてあり、読んでから今の形（RLE）で書き戻す。</item>
    /// </list>
    /// </summary>
    public class OldFormatLoadTests
    {
        private const int W = 16, H = 8;

        private TestAssets _assets;
        private string _path;
        private string _guid;
        private Texture2D _texture;
        private string _prefKey;
        private string _prefBefore;   // null = キーが無かった
        private IrocaWindow _win;

        [SetUp]
        public void SetUp()
        {
            _assets = TestAssets.Create();
            _path = _assets.WritePng("old", Scene(), W, H);
            _guid = AssetDatabase.AssetPathToGUID(_path);
            _texture = AssetDatabase.LoadAssetAtPath<Texture2D>(_path);
            _prefKey = IrocaWindow.LastTextureGuidPrefKey;
            _prefBefore = EditorPrefs.HasKey(_prefKey) ? EditorPrefs.GetString(_prefKey) : null;
            EditorPrefs.SetString(_prefKey, _guid);
        }

        [TearDown]
        public void TearDown()
        {
            IrocaAutomation.CancelRecolorJob();
            if (_win != null)
            {
                Undo.ClearUndo(_win);
                Object.DestroyImmediate(_win);
            }
            _win = null;
            if (_prefBefore == null) EditorPrefs.DeleteKey(_prefKey);
            else EditorPrefs.SetString(_prefKey, _prefBefore);
            DeleteCacheFiles();
            _assets.Dispose();
        }

        // ───────────── v0.1.0 のプリセット ─────────────

        [Test]
        public void V010Preset_InTheWindow_SortsByTheOldPriority_AndFillsNewFieldsWithTodaysDefaults()
        {
            OpenWindow();
            _win._presetsView.LoadPreset(WriteFile("old-vacc.json", F.V010Preset));

            var zones = _win.Session.zones;
            CollectionAssert.AreEqual(new[] { "skirt", "ribbon", "shirt" }, zones.Select(z => z.name).ToArray(),
                "古い優先度(layerIndex の大きいほうが上)の並びにする");
            var expected = new[]
            {
                V010Zone("skirt", Blue, Yellow, 0.375f, 2),
                V010Zone("ribbon", Green, Purple, 0.25f, 1),
                V010Zone("shirt", Red, Teal, 0.25f, 0),
            };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.IsNotEmpty(zones[i].id, "ID を振る");
                expected[i].id = zones[i].id;
                AssertSameZone(expected[i], zones[i]);
            }
            var s = _win.Session;
            Assert.AreEqual(0.5f, s.edgeFeather);
            Assert.AreEqual(2, s.antiAliasCleanup);
            Assert.AreEqual(4, s.holeFillPasses);
            Assert.AreEqual(3, s.holeFillMinNeighbors);
            Assert.AreEqual(0.03125f, s.relaxedSatMin);
            Assert.AreEqual(0.0625f, s.relaxedSatRamp);
            Assert.AreEqual(RecolorSettings.DefaultUseDecontamination, s.useDecontamination, "v0.1.0 に無い設定は今の既定値");
            Assert.AreEqual(RecolorSettings.DefaultDecontaminationRadius, s.decontaminationRadius, "v0.1.0 に無い設定は今の既定値");
            AssertRecolors(s, "v0.1.0 のゾーンは sampleColorSet が無いが、色が既定(白)でないので当たる");
        }

        // 自動化 API もウィンドウと同じ規則(PresetApplier)で古い優先度を並べ替える。0.3.x までは並べ替えず、
        // ウィンドウと重なりの勝ち負けが食い違っていた(0.4.0 の 4-3 で直した)。
        [Test]
        public void V010Preset_ByAutomation_SortsByTheOldPriorityLikeTheWindow()
        {
            string preset = _assets.Folder + "/old-vacc.json";
            WriteFile("old-vacc.json", F.V010Preset);
            string json = IrocaAutomation.Recolor("{\"texture\":\"" + _path + "\",\"apply\":\"preview\",\"preset\":\"" + preset
                + "\",\"changes\":[],\"autoTune\":false}");
            var r = JsonUtility.FromJson<IrocaAutomation.RecolorTextureResult>(json);
            Assert.IsTrue(r.ok, json);
            CollectionAssert.AreEqual(new[] { "skirt", "ribbon", "shirt" }, r.zones.Select(z => z.name).ToArray(),
                "ウィンドウで読み込んだときと同じ並び(古い優先度の大きい順)");
        }

        // ───────────── v0.3.0 のプリセット・セッション・レシピ ─────────────

        [Test]
        public void V030Preset_InTheWindow_RestoresZonesSettingsAndMasks()
        {
            OpenWindow();
            var presets = _win._presetsView;
            presets.presetApplyMasks = true;
            presets.LoadPreset(WriteFile("old-iroca.json", F.V030Preset));

            AssertV030Zones(_win.Session.zones, inPreset: true);
            AssertV030Settings(_win.Session, withEditMode: false);   // 編集モードはプリセットに入れない
            _win._maskView.SyncBuffersToState();
            AssertV030Masks(_win.Session.maskState);
        }

        [Test]
        public void V030SessionFiles_AreRestoredWhenTheWindowOpens()
        {
            Directory.CreateDirectory(SessionFileStore.CacheDir);
            Directory.CreateDirectory(MaskFileStore.CacheDir);
            // ファイルの名前(GUID + 拡張子)も、利用者のディスクにある形のまま。
            File.WriteAllText(Path.Combine(SessionFileStore.CacheDir, _guid + ".iroca-session.json"), F.V030SessionFile);
            File.WriteAllText(Path.Combine(MaskFileStore.CacheDir, _guid + ".iroca-mask.json"), F.V030MaskFile);
            OpenWindow();

            AssertV030Zones(_win.Session.zones, inPreset: false);
            AssertV030Settings(_win.Session, withEditMode: true);
            _win._maskView.SyncBuffersToState();
            AssertV030Masks(_win.Session.maskState);
        }

        [Test]
        public void V030Recipe_LoadsTheSameStateAsTodaysFormat()
        {
            var recipe = RecipeStore.Create(_texture, IrocaSessionState.CreateDefault(), _assets.Folder);
            recipe.sessionJson = F.V030RecipeSession;
            recipe.formatVersion = 1;
            var loaded = RecipeStore.Load(recipe);
            Assert.IsNotNull(loaded, "読めない");

            AssertV030Zones(loaded.zones, inPreset: false);
            AssertV030Settings(loaded, withEditMode: true);
            var masks = MaskStateCodec.ToSnapshot(loaded.maskState);
            Assert.IsNotNull(masks, "マスクを読めない");
            AssertRecolors(loaded, "v0.3.0 のレシピ");

            // 今の形で同じ中身を書いたものと、同じ画素になる(旧 bitpack のマスクも同じに読む)。
            var today = TodaysV030State();
            var a = Scene();
            var b = Scene();
            Assert.IsTrue(SessionRecolor.Apply(a, W, H, loaded));
            Assert.IsTrue(SessionRecolor.Apply(b, W, H, today));
            for (int i = 0; i < a.Length; i++)
                Assert.AreEqual(b[i], a[i], $"画素 (x={i % W}, y={i / W}) が今の形で書いたものと違う");
        }

        // ───────────── 期待する中身 ─────────────

        private static readonly Color Red = new Color(0.75f, 0.125f, 0.125f, 1f);
        private static readonly Color Blue = new Color(0.125f, 0.375f, 0.75f, 1f);
        private static readonly Color Green = new Color(0.125f, 0.625f, 0.25f, 1f);
        private static readonly Color Teal = new Color(0.125f, 0.625f, 0.625f, 1f);
        private static readonly Color Yellow = new Color(0.875f, 0.75f, 0.125f, 1f);
        private static readonly Color Purple = new Color(0.625f, 0.25f, 0.75f, 1f);

        // v0.1.0 の ColorZone にあった 15 項目だけを見本と同じ値にし、ほかは今の既定値のまま。
        private static ColorZone V010Zone(string name, Color sample, Color target, float tolerance, int layer) => new ColorZone
        {
            name = name, enabled = true, mode = SelectionMode.ColorPick, sampleColor = sample, tolerance = tolerance,
            uvRect = new Rect(0, 0, 1, 1), targetColor = target, valueBlend = 0.875f, edgeSoftness = 0.125f,
            saturationStrictness = 0.5f, valueWeight = 1f, satDistWeight = 0.25f, satRampScale = 0.125f,
            highlightRecovery = true, layerIndex = layer,
        };

        // v0.3.0 の ColorZone の 36 項目すべてを見本と同じ値にする。
        private static ColorZone V030Zone(string name, string id, Color sample, Color target, float tolerance,
                                          bool enabled, bool wash, bool anchor, Vector2 sampleUV)
        {
            var z = new ColorZone
            {
                name = name, enabled = enabled, mode = SelectionMode.ColorPick, sampleColor = sample, sampleColorSet = true,
                tolerance = tolerance, uvRect = new Rect(0, 0, 1, 1), useFloodFill = true, seedUV = new Vector2(-1f, -1f),
                sampleUV = sampleUV, targetColor = target, targetColorSet = true, valueBlend = 0.875f, outputSaturation = 1f,
                edgeSoftness = 0.125f, saturationStrictness = 0.5f, valueWeight = 1f, satDistWeight = 0.25f,
                satRampScale = 0.125f, shadowDesaturation = 0.375f, shadowForgivenessSatMin = 0.0625f, shadowValueFloor = 0f,
                partSatCeiling = 0f, partHueBand = 0f, chromaCeiling = 0f, holeFillMargin = 0f, chromaThreshold = 0.0625f,
                saturationGuard = 0f, highlightRecovery = true, highlightBandExpand = true, applyHighlightWash = wash,
                autoHighlightSample = false, autoRecolorAnchor = anchor, layerIndex = 0, id = id,
            };
            if (name == "shirt") z.extraSamples.Add(new Color(0.875f, 0.25f, 0.25f, 1f));
            return z;
        }

        // プリセットのゾーンはスポイト位置を持たない(書くときに外し、読むときにも外す)。
        private static ColorZone[] V030Zones(bool inPreset)
        {
            Vector2 none = new Vector2(-1f, -1f);
            return new[]
            {
                V030Zone("shirt", F.ShirtId, Red, Teal, 0.25f, true, false, true, inPreset ? none : new Vector2(0.25f, 0.5f)),
                V030Zone("skirt", F.SkirtId, Blue, Yellow, 0.375f, true, true, false, inPreset ? none : new Vector2(0.75f, 0.5f)),
                V030Zone("ribbon", F.RibbonId, Green, Purple, 0.25f, false, false, true, none),
            };
        }

        private static void AssertV030Zones(List<ColorZone> zones, bool inPreset)
        {
            var expected = V030Zones(inPreset);
            Assert.AreEqual(expected.Length, zones.Count, "ゾーンの数");
            for (int i = 0; i < expected.Length; i++) AssertSameZone(expected[i], zones[i]);
        }

        private static void AssertV030Settings(IrocaSessionState s, bool withEditMode)
        {
            Assert.AreEqual(0.5f, s.edgeFeather);
            Assert.AreEqual(2, s.antiAliasCleanup);
            Assert.AreEqual(false, s.useDecontamination);
            Assert.AreEqual(3, s.decontaminationRadius);
            Assert.AreEqual(4, s.holeFillPasses);
            Assert.AreEqual(3, s.holeFillMinNeighbors);
            Assert.AreEqual(0.03125f, s.relaxedSatMin);
            Assert.AreEqual(0.0625f, s.relaxedSatRamp);
            if (withEditMode) Assert.AreEqual(EditMode.Advanced, s.editMode);
        }

        private static void AssertV030Masks(MaskState ms)
        {
            Assert.AreEqual((W, H), (ms.width, ms.height), "マスクの寸法");
            AssertMask(Rect(0, 0, 4, 2), ms.commonMaskBase64, "共通の塗らない(旧 bitpack で書いてあった)");
            Assert.AreEqual(F.ShirtId, ms.zones.Single().zoneId);
            AssertMask(Rect(4, 6, 4, 2), ms.zones.Single().maskBase64, "shirt の塗らない");
            Assert.AreEqual(F.SkirtId, ms.zoneIncludes.Single().zoneId);
            AssertMask(Rect(8, 0, 4, 2), ms.zoneIncludes.Single().maskBase64, "skirt のここも塗る");
        }

        // 同じ中身を今の形(RLE のマスク)で組み立てたもの。
        private static IrocaSessionState TodaysV030State()
        {
            var s = new IrocaSessionState
            {
                edgeFeather = 0.5f, antiAliasCleanup = 2, useDecontamination = false, decontaminationRadius = 3,
                editMode = EditMode.Advanced, holeFillPasses = 4, holeFillMinNeighbors = 3,
                relaxedSatMin = 0.03125f, relaxedSatRamp = 0.0625f,
            };
            s.zones.AddRange(V030Zones(inPreset: false));
            MaskStateCodec.Encode(s.maskState, W, H, Rect(0, 0, 4, 2),
                new Dictionary<string, bool[]> { [F.ShirtId] = Rect(4, 6, 4, 2) },
                new Dictionary<string, bool[]> { [F.SkirtId] = Rect(8, 0, 4, 2) });
            return s;
        }

        // ───────────── 道具 ─────────────

        private void OpenWindow()
        {
            _win = ScriptableObject.CreateInstance<IrocaWindow>();
            Assert.AreSame(_texture, _win.SourceTexture, "窓がテスト用のテクスチャを開いていない");
        }

        private string WriteFile(string name, string text)
        {
            string path = TestAssets.Abs(_assets.Folder + "/" + name);
            File.WriteAllText(path, text);
            return path;
        }

        private void DeleteCacheFiles()
        {
            foreach (var dir in new[] { SessionFileStore.CacheDir, MaskFileStore.CacheDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, _guid + "*")) File.Delete(f);
            }
        }

        // 左半分が赤・右半分が青(縦に陰影)、真ん中の下に緑の 2×2。行 0 = 画像の下端。
        private static Color32[] Scene()
        {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float s = 0.7f + 0.3f * y / (H - 1);
                for (int x = 0; x < W; x++)
                {
                    px[y * W + x] = x < W / 2
                        ? new Color32((byte)(190 * s), (byte)(32 * s), (byte)(32 * s), 255)
                        : new Color32((byte)(32 * s), (byte)(96 * s), (byte)(190 * s), 255);
                    if (x >= 7 && x < 9 && y < 2) px[y * W + x] = new Color32(32, 160, 64, 255);
                }
            }
            return px;
        }

        private static bool[] Rect(int x0, int y0, int w, int h)
        {
            var r = new bool[W * H];
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    r[y * W + x] = true;
            return r;
        }

        private static void AssertSameZone(ColorZone expected, ColorZone actual)
            => Assert.AreEqual(JsonUtility.ToJson(expected), JsonUtility.ToJson(actual), $"ゾーン {expected.name} の中身");

        private static void AssertMask(bool[] expected, string encoded, string what)
        {
            var actual = MaskRle.Decode(encoded, out int w, out int h);
            Assert.IsNotNull(actual, what + ": 読めない");
            Assert.AreEqual((W, H), (w, h), what + ": 寸法");
            CollectionAssert.AreEqual(expected, actual, what);
        }

        private static void AssertRecolors(IrocaSessionState state, string what)
        {
            var px = Scene();
            Assert.IsTrue(SessionRecolor.Apply(px, W, H, state), what + ": 有効なゾーンが無い");
            var before = Scene();
            Assert.IsTrue(px.Where((c, i) => !c.Equals(before[i])).Any(), what + ": 色替えが起きない");
        }
    }
}
