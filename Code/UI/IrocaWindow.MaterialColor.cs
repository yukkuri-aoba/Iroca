// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Effect = Iroca.MaterialColorSettings.Effect;

namespace Iroca {
    /// <summary>
    /// 編集中のテクスチャを使うマテリアルの色の設定(<see cref="MaterialColorSettings"/>)を見つけて、テクスチャ欄の下で
    /// 知らせる。プレビューの「アバターの色」(<see cref="PreviewView"/>)に使う設定もここで決める。
    /// </summary>
    public partial class IrocaWindow {
        // 見つけたマテリアルの読み取り結果を、同じ見た目になるもの(要約が同じ)ごとにまとめた組。使っている数の多い順。
        [System.NonSerialized] private List<List<MaterialColorSettings.Report>> _materialColorGroups;
        [System.NonSerialized] private Texture2D _materialColorsFor;
        [System.NonSerialized] private HashSet<Texture> _materialColorTargets;
        [System.NonSerialized] private int _materialColorsScene, _materialColorsSceneVersion;
        [System.NonSerialized] private bool _materialColorsInScene;
        [System.NonSerialized] private double _materialColorsScannedAt, _materialColorsReadAt;
        // 表示に使う組の番号(テクスチャを替えたら先頭に戻す)。
        [System.NonSerialized] private int _materialColorChoice;

        // 開いているシーンの Renderer を探し直す最短の間隔(マテリアルの付け替えを追う。変化の通知があったときだけ)と、
        // 見つけたマテリアルの値を読み直す間隔(インスペクタで色を変えたのを追う)。どちらもウィンドウを描いているときだけ。
        private const double MaterialColorRescanSeconds = 2.0;
        private const double MaterialColorRereadSeconds = 0.5;
        private const string MaterialColorDismissedKeyPrefix = IrocaPrefs.MaterialColorDismissedPrefix;

        /// <summary>プレビューの「アバターの色」に使う読み取り結果。マテリアルが見つからなければ null。</summary>
        internal MaterialColorSettings.Report AppearanceReport { get; private set; }

        // ───────────── 開いているテクスチャと、その書き出し物 ─────────────
        // 「このテクスチャを使うもの」を探す所(マテリアルの色の案内・メッシュの探索・Scene のクリック・書き出した画像を使う
        // マテリアルの数)は、どれも元のテクスチャと、いろかで書き出したもの(名前_recolored*)の両方を見る。その組を探すのは
        // アセットの検索なので、ここに 1 つ控えて共有する。以前は使う所がそれぞれ探していて、テクスチャを開くと 3 回、
        // その後も 2 秒ごとと Scene のクリックのたびに検索していた。
        [System.NonSerialized] private HashSet<Texture> _sourceTargets;
        [System.NonSerialized] private Texture2D _sourceTargetsFor;
        [System.NonSerialized] private bool _sourceTargetsStale;

        /// <summary>
        /// 開いているテクスチャと、その書き出し物の組(<see cref="MeshUvLocator.TargetTextures"/>)。テクスチャが無ければ null。
        /// 探し直すのは、テクスチャを替えたときと、書き出し物が増えた・消えたかもしれないとき(<see cref="MarkSourceTargetsStale"/>)。
        /// 探し直しても顔ぶれが同じなら同じ組を返す(使う側は、組が替わったかを参照で見てよい)。
        /// </summary>
        internal HashSet<Texture> SourceTargets() {
            var tex = SourceTexture;
            if (tex == null) {
                _sourceTargets = null;
                _sourceTargetsFor = null;
                return null;
            }
            bool sameTexture = _sourceTargets != null && _sourceTargetsFor == tex;
            if (sameTexture && !_sourceTargetsStale) {
                return _sourceTargets;
            }
            var fresh = MeshUvLocator.TargetTextures(tex);
            if (!sameTexture || !_sourceTargets.SetEquals(fresh)) {
                _sourceTargets = fresh;
            }
            _sourceTargetsFor = tex;
            _sourceTargetsStale = false;
            return _sourceTargets;
        }

        /// <summary>
        /// 書き出し物が増えた・消えたかもしれない(自分で書き出した・Project で取り込まれた・消された・動かされた)。
        /// 次に <see cref="SourceTargets"/> を読むときに探し直す。
        /// </summary>
        internal void MarkSourceTargetsStale() => _sourceTargetsStale = true;

        /// <summary>
        /// 読み取り結果を必要なら作り直す。Layout のときだけ動く(同じフレームの Repaint と並ぶ要素をそろえる)。
        /// テクスチャか開いているシーンが替わったら探し直し、シーンで見つけていたら時々探し直す
        /// (素材フォルダから探したときは重いので繰り返さない)。それ以外は値だけを時々読み直す。
        /// 本体の描画(テクスチャ欄の下の案内)と、プレビューの描画(<see cref="PreviewView"/> の「アバターの色」)の両方から呼ぶ。
        /// 本体が裏のタブだと本体の OnGUI が来ないので、別窓のプレビューが前のテクスチャのマテリアルの色で描いていた。
        /// 同じフレームに 2 回呼ばれても、探し直し・読み直しは間隔をあけるので 2 回目は何もしない。
        /// </summary>
        internal void RefreshMaterialColors() {
            if (Event.current.type != EventType.Layout) {
                return;
            }
            var tex = SourceTexture;
            if (tex == null) {
                _materialColorGroups = null;
                _materialColorsFor = null;
                AppearanceReport = null;
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            int scene = MeshPartController.SceneStamp();
            // 書き出し物の顔ぶれが変わったときも探し直す。以前はテクスチャを替えたときにしか組を作り直さず、書き出した画像へ
            // 差し替えたマテリアルが、案内と「アバターの色」の対象から外れたままになっていた。
            var targets = SourceTargets();
            bool retarget = tex != _materialColorsFor || scene != _materialColorsScene
                            || !ReferenceEquals(targets, _materialColorTargets);
            // 時間で決まる探し直し・読み直しは、マウスを押していない(スライダーのドラッグ中でない)ときだけ。
            // シーンの走査と素材の読み直しがドラッグ中の 1 フレームを止める。素材の値はドラッグでは変わらない。
            bool idle = GUIUtility.hotControl == 0;
            List<MaterialColorSettings.Report> reports;
            // シーンで見つけていたら、Renderer が変わったかもしれないという通知(SceneRendererChanges)が来たときだけ
            // 探し直す(通知が続いても間隔はあける)。
            if (retarget || (idle && _materialColorsInScene && SceneRendererChanges.Version != _materialColorsSceneVersion
                             && now - _materialColorsScannedAt >= MaterialColorRescanSeconds)) {
                _materialColorsSceneVersion = SceneRendererChanges.Version;
                if (tex != _materialColorsFor) {
                    _materialColorChoice = 0;
                }
                _materialColorTargets = targets;
                reports = MaterialColorSettings.FindForTexture(tex, _materialColorTargets, out _materialColorsInScene);
                _materialColorsFor = tex;
                _materialColorsScene = scene;
                _materialColorsScannedAt = now;
            } else if (idle && now - _materialColorsReadAt >= MaterialColorRereadSeconds && _materialColorGroups != null) {
                reports = new List<MaterialColorSettings.Report>();
                foreach (var g in _materialColorGroups) {
                    foreach (var r in g) {
                        var again = MaterialColorSettings.Analyze(r.material, _materialColorTargets);
                        if (again != null) {
                            reports.Add(again);
                        }
                    }
                }
            } else {
                return;
            }
            _materialColorsReadAt = now;

            var groups = new List<List<MaterialColorSettings.Report>>();
            var bySignature = new Dictionary<string, List<MaterialColorSettings.Report>>();
            foreach (var r in reports) {
                if (!bySignature.TryGetValue(r.signature, out var g)) {
                    g = new List<MaterialColorSettings.Report>();
                    bySignature.Add(r.signature, g);
                    groups.Add(g);
                }
                g.Add(r);
            }
            _materialColorGroups = groups;
            _materialColorChoice = Mathf.Clamp(_materialColorChoice, 0, Mathf.Max(0, groups.Count - 1));
            AppearanceReport = groups.Count > 0 ? groups[_materialColorChoice][0] : null;
        }

        /// <summary>
        /// テクスチャ欄の下の案内。テクスチャを使うマテリアルに色の設定があるときだけ出す。何をしているか・
        /// プレビューに入っているか・入っていないものと、色を変えても見た目に出にくい設定を知らせる。
        /// 閉じると、そのテクスチャでは Unity を閉じるまで出さない。
        /// </summary>
        private void DrawMaterialColorNotice() {
            RefreshMaterialColors();
            var groups = _materialColorGroups;
            var r = AppearanceReport;
            if (SourceTexture == null || groups == null || r == null) {
                return;
            }
            bool any = false;
            foreach (var g in groups) {
                any |= g[0].AffectsColor;
            }
            if (!any) {
                return;
            }
            if (TextureNoticeDismissed(MaterialColorDismissedKeyPrefix)) {
                return;
            }

            // テクスチャ欄は変更の検出(プレビューの作り直し)に囲まれているので、ここの操作では立てない
            // (「アバターの色」は表示用の画像だけを作り直す)。
            bool changed = GUI.changed;
            // 文面と選択肢は、読み取り結果が替わったとき(読み直しは 0.5 秒ごと)・「アバターの色」の入切・言語の切り替えでだけ
            // 作り直す(以前はイベントごとに文を組み立て、選択肢の GUIContent を作っていた)。
            var lang = Localization.CurrentLanguage;
            bool appearanceOn = _previewView != null && _previewView.appearanceMode;
            if (!ReferenceEquals(_materialNoticeFor, r) || _materialNoticeAppearance != appearanceOn || _materialNoticeLang != lang) {
                _materialNoticeText = MaterialColorNoticeText(r, appearanceOn, out _materialNoticeWarn);
                _materialNoticeFor = r;
                _materialNoticeAppearance = appearanceOn;
                _materialNoticeLang = lang;
            }
            DrawTextureNotice(MaterialColorDismissedKeyPrefix, _materialNoticeText,
                _materialNoticeWarn ? MessageType.Warning : MessageType.Info, Localization.MaterialColorDismissTooltip);
            // どのマテリアルの設定で見るかは、組が 2 つ以上あるときだけ本文の下に出す。
            if (groups.Count > 1) {
                if (!ReferenceEquals(_materialChoiceLabelsFor, groups) || _materialChoiceLabelsLang != lang) {
                    var built = new GUIContent[groups.Count];
                    for (int i = 0; i < groups.Count; i++) {
                        var g = groups[i];
                        string name = g[0].material.name;
                        built[i] = new GUIContent(g.Count > 1
                            ? string.Format(Localization.MaterialColorChoiceManyFormat, name, g.Count - 1)
                            : name);
                    }
                    _materialChoiceLabels = built;
                    _materialChoiceLabelsFor = groups;
                    _materialChoiceLabelsLang = lang;
                }
                var labels = _materialChoiceLabels;
                int choice = EditorGUILayout.Popup(
                    TempContent.Of(Localization.MaterialColorChoice, Localization.MaterialColorChoiceTooltip),
                    _materialColorChoice, labels);
                if (choice != _materialColorChoice) {
                    _materialColorChoice = choice;
                    AppearanceReport = groups[choice][0];
                    RequestRepaint();
                }
            }
            GUI.changed = changed;
        }

        // 案内の文面と選択肢の覚え(何から作ったか)。
        [System.NonSerialized] private MaterialColorSettings.Report _materialNoticeFor;
        [System.NonSerialized] private bool _materialNoticeAppearance, _materialNoticeWarn;
        [System.NonSerialized] private LanguageMode _materialNoticeLang, _materialChoiceLabelsLang;
        [System.NonSerialized] private string _materialNoticeText;
        [System.NonSerialized] private List<List<MaterialColorSettings.Report>> _materialChoiceLabelsFor;
        [System.NonSerialized] private GUIContent[] _materialChoiceLabels;

        // ───────────── 影の色を塗り先に合わせる ─────────────

        // 影の色を合わせられるマテリアル(lilToon で影を使い、元テクスチャをメインに使い、映す所を一番多く塗っているのが
        // 色の決まったゾーン)。「アバターに反映」欄の「影の色を変更先カラーに合わせる」に出す。無ければ null。
        // 色替えしていないパーツのマテリアル(同じテクスチャを共有する肌など)は入れない(合わせても影の色は変わらない)。
        // 読み直しの間隔は上と同じ。
        [System.NonSerialized] private List<Material> _shadowEligible;
        // そのうち、影の色を合わせる設定になっている数(シーンの「Iroca Recolor」のどれかに入っている)。
        [System.NonSerialized] private int _shadowMatchedCount;
        // 影の色が塗り先とずれていて、まだ合わせる設定になっていないマテリアル(案内に出す)。
        [System.NonSerialized] private List<Material> _shadowMismatch;
        // 影色テクスチャが元テクスチャと別に描かれているマテリアルとそのテクスチャ(影はその色のまま残るので案内に出す)。
        [System.NonSerialized] private List<(Material material, Texture texture)> _shadowTextures;
        [System.NonSerialized] private double _shadowMismatchReadAt;
        // 上を何の読み取り結果・どのゾーンがどこを塗るかから作ったか(テクスチャを替えた・プレビューの確定段が新しい塗る所を
        // 知らせたら、間隔を待たずに作り直す)。
        [System.NonSerialized] private List<List<MaterialColorSettings.Report>> _shadowStateGroups;
        [System.NonSerialized] private ZoneShareGrid _shadowStateShares;

        // 開いているシーンの、マテリアル → それを使う Renderer(マテリアルが映す所を求める。ShadowArea)。シーンの Renderer の
        // 顔ぶれが変わったら(SceneRendererChanges)作り直す。
        [System.NonSerialized] private Dictionary<Material, List<Renderer>> _sceneRenderersByMaterial;
        [System.NonSerialized] private int _sceneRenderersVersion = -1;

        private List<Renderer> SceneRenderersUsing(Material material) {
            if (_sceneRenderersByMaterial == null || _sceneRenderersVersion != SceneRendererChanges.Version) {
                _sceneRenderersVersion = SceneRendererChanges.Version;
                _sceneRenderersByMaterial = new Dictionary<Material, List<Renderer>>();
                foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
                    if (EditorUtility.IsPersistent(r)) {
                        continue;
                    }
                    foreach (var m in r.sharedMaterials) {
                        if (m == null) {
                            continue;
                        }
                        if (!_sceneRenderersByMaterial.TryGetValue(m, out var list)) {
                            _sceneRenderersByMaterial.Add(m, list = new List<Renderer>());
                        }
                        if (!list.Contains(r)) {
                            list.Add(r);
                        }
                    }
                }
            }
            return _sceneRenderersByMaterial.TryGetValue(material, out var found) ? found : null;
        }

        // Layout のときだけ読み直す(同じフレームの Repaint と並ぶ要素をそろえる)。シーンの部品を探すので毎フレームは読まない。
        // 案内(テクスチャ欄の下)と切り替え(「アバターに反映」欄)の両方から呼ぶ。同じフレームの 2 回目は何もしない。
        private void RefreshShadowState() {
            if (Event.current == null || Event.current.type != EventType.Layout) {
                return;
            }
            // 切り替えの高さは案内より先に(書き出し欄の高さの見積もりで)求めるので、読み取り結果もここで新しくする。
            RefreshMaterialColors();
            var groups = _materialColorGroups;
            var source = SourceTexture;
            // どのゾーンがどこを塗るか(プレビューの確定段が知らせる。まだ無ければ、どのマテリアルも選べない)。
            var shares = LivePreview.ShadowSharesOf(source);
            double now = EditorApplication.timeSinceStartup;
            // シーンの全「Iroca Recolor」を探すので、マウスを押している間(スライダーのドラッグ中)は読み直さない。
            // 読み取り結果・塗る所が替わったとき(テクスチャを替えた・マテリアルを読み直した・確定段が届いた)は待たない。
            if (ReferenceEquals(groups, _shadowStateGroups) && ReferenceEquals(shares, _shadowStateShares)
                && (now - _shadowMismatchReadAt < MaterialColorRereadSeconds || GUIUtility.hotControl != 0)) {
                return;
            }
            _shadowStateGroups = groups;
            _shadowStateShares = shares;
            _shadowMismatchReadAt = now;
            _shadowEligible = null;
            _shadowMatchedCount = 0;
            _shadowMismatch = null;
            _shadowTextures = null;
            if (source == null || groups == null || shares == null) {
                return;
            }
            var colors = ZoneColors.From(Session?.zones);
            if (colors.IsEmpty) {
                return;
            }
            // シーンの「Iroca Recolor」は、影の色を合わせられるマテリアルが見つかってから探す。
            IrocaRecolor[] components = null;
            foreach (var g in groups) {
                foreach (var r in g) {
                    var m = r.material;
                    if (r.slot != MaterialColorSettings.Slot.Main || !MaterialShadowColors.MainTextureIs(m, source)
                        || !MaterialShadowColors.IsEligible(m)) {
                        continue;
                    }
                    // このマテリアルが映す所を一番多く塗っているゾーンの色(ビルド・シーンのプレビューと同じ選び方)。
                    // 色替えしていないパーツなら選ばれない(合わせても変わらないので、切り替えにも案内にも出さない)。
                    var area = ShadowArea.Of(m, SceneRenderersUsing(m));
                    if (!MaterialShadowColors.TryChooseColors(shares, area, colors, out var sample, out var target)) {
                        continue;
                    }
                    (_shadowEligible ??= new List<Material>()).Add(m);
                    foreach (var t in MaterialShadowColors.SeparateShadowTextures(m, source)) {
                        (_shadowTextures ??= new List<(Material, Texture)>()).Add((m, t));
                    }
                    components ??= Object.FindObjectsByType<IrocaRecolor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                    if (RecipeRegistration.HasShadowMatch(components, m, source)) {
                        _shadowMatchedCount++;
                        continue;
                    }
                    if (MaterialShadowColors.NeedsMatch(m, sample, target)) {
                        (_shadowMismatch ??= new List<Material>()).Add(m);
                    }
                }
            }
        }

        /// <summary>
        /// 「アバターに反映」欄に「影の色を変更先カラーに合わせる」を出すか(影の色を合わせられるマテリアルがある)。
        /// 書き出し欄の高さの見積もり(<see cref="ExportView.GetSectionHeight"/>)と描画の両方が使う。
        /// </summary>
        internal bool HasShadowMatchToggle() {
            if (SourceTexture == null) {
                return false;
            }
            RefreshShadowState();
            return _shadowEligible != null && _shadowEligible.Count > 0;
        }

        // 切り替えのツールチップ(対象のマテリアルの名前を入れる)と、何から作ったか。
        [System.NonSerialized] private string _shadowToggleTooltip;
        [System.NonSerialized] private List<Material> _shadowToggleTooltipFor;
        [System.NonSerialized] private LanguageMode _shadowToggleTooltipLang;

        /// <summary>
        /// 「アバターに反映」欄の「影の色を変更先カラーに合わせる」。このテクスチャをメインに使う lilToon のマテリアルを
        /// まとめて切り替える(一部だけ合わせているときは混在の表示)。マテリアルごとの切り替えは「Iroca Recolor」のインスペクタ。
        /// 設定は「Iroca Recolor」が持つ。テクスチャ欄の下の案内の「影の色も合わせる」と同じ設定で、案内を閉じた後や、
        /// 合わせた後に外したいときもここで切り替えられる。
        /// </summary>
        internal void DrawShadowMatchToggle() {
            if (!HasShadowMatchToggle()) {
                return;
            }
            var eligible = _shadowEligible;
            var lang = Localization.CurrentLanguage;
            if (!ReferenceEquals(_shadowToggleTooltipFor, eligible) || _shadowToggleTooltipLang != lang) {
                var names = new List<string>(eligible.Count);
                foreach (var m in eligible) {
                    names.Add(m != null ? m.name : "");
                }
                _shadowToggleTooltip = string.Format(Localization.ShadowMatchWindowTooltipFormat,
                    string.Join(Localization.MaterialColorListSeparator, names));
                _shadowToggleTooltipFor = eligible;
                _shadowToggleTooltipLang = lang;
            }
            bool all = _shadowMatchedCount >= eligible.Count;
            bool mixed = _shadowMatchedCount > 0 && !all;
            bool changed = GUI.changed;
            EditorGUI.showMixedValue = mixed;
            bool next = EditorGUILayout.ToggleLeft(TempContent.Of(Localization.ShadowMatchHeader, _shadowToggleTooltip), all);
            EditorGUI.showMixedValue = false;
            // 表示の作り直し(ゾーンの変更の検出)は起こさない。設定はコンポーネントにあり、色替えの計算とは関係ない。
            GUI.changed = changed;
            if (next == all) {
                return;
            }
            var chosen = new List<Material>(eligible);
            // オンは確認画面(登録)を出すことがあるので、描画の途中で開かない。オフも同じ順で行う。
            EditorApplication.delayCall += () => {
                if (this == null) {
                    return;
                }
                if (next) {
                    MatchShadowColors(chosen);
                } else {
                    UnmatchShadowColors(chosen);
                }
            };
        }

        /// <summary>
        /// 影の色の案内。マテリアルの影の色は色替えしてもそのままで、元の布に合わせた色だと白に近い所に色が残って見える。
        /// ボタンで Iroca Recolor に設定を足す(マテリアルそのものは変えない)。閉じると、そのテクスチャでは Unity を閉じるまで出さない。
        /// 足した後の切り替えは「アバターに反映」欄(<see cref="DrawShadowMatchToggle"/>)。
        /// </summary>
        private void DrawShadowColorNotice() {
            // 閉じてある案内のためには読み直さない(読み直しはシーンの「Iroca Recolor」の全探索を含み、0.5 秒ごとに走る。
            // 「アバターに反映」欄の切り替えが出ているときは、そちらが読み直す)。
            if (SourceTexture == null || TextureNoticeDismissed(IrocaPrefs.ShadowColorDismissedPrefix)) {
                return;
            }
            RefreshShadowState();
            var materials = _shadowMismatch;
            var textures = _shadowTextures;
            bool mismatch = materials != null && materials.Count > 0;
            bool separate = textures != null && textures.Count > 0;
            if (!mismatch && !separate) {
                return;
            }

            // 2 つの案内は 1 つの枠にまとめる(枠ごとに余白と絵の高さを使うため)。
            string text = null;
            if (mismatch) {
                text = string.Format(Localization.ShadowColorNoticeFormat, materials[0].name);
            }
            // 影色テクスチャが別に描かれていると、影の色を合わせても影の部分はそのテクスチャの色のまま。
            if (separate) {
                string more = string.Format(Localization.ShadowColorTextureNoticeFormat, textures[0].material.name,
                    textures[0].texture.name);
                text = text == null ? more : text + "\n" + more;
            }
            // 「影の色も合わせる」は、合わせられるマテリアルがあるときだけ「閉じる」の前に出す。
            int pressed = mismatch
                ? DrawTextureNotice(IrocaPrefs.ShadowColorDismissedPrefix, text, MessageType.Info, Localization.ShadowColorDismissTooltip,
                    TempContent.Of(Localization.ShadowColorMatchButton, Localization.ShadowColorMatchButtonTooltip))
                : DrawTextureNotice(IrocaPrefs.ShadowColorDismissedPrefix, text, MessageType.Info, Localization.ShadowColorDismissTooltip);
            bool match = mismatch && pressed == 0;
            // 確認画面を出すことがあるので、描画の途中で開かない。
            if (match) {
                var chosen = new List<Material>(materials);
                EditorApplication.delayCall += () => {
                    if (this != null) {
                        MatchShadowColors(chosen);
                    }
                };
            }
        }

        /// <summary>
        /// このテクスチャのレシピを持つ Iroca Recolor に、影の色を合わせる設定を足す(Undo 可)。まだ登録していなければ
        /// 先に「アバターに非破壊で登録」する(設定の置き場がコンポーネントなので。確認画面でやめたら何もしない)。
        /// </summary>
        internal void MatchShadowColors(List<Material> materials) {
            var components = RegisteredComponents();
            if (components.Count == 0) {
                RegisterToAvatar();
                components = RegisteredComponents();
                if (components.Count == 0) {
                    return;
                }
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(Localization.ShadowColorMatchButton);
            foreach (var c in components) {
                RecipeRegistration.AddShadowMatch(c, materials);
            }
            Undo.CollapseUndoOperations(group);
            _shadowMismatchReadAt = 0;
            RequestRepaint();
        }

        /// <summary>
        /// このテクスチャのレシピを持つ、開いているシーンの Iroca Recolor から、影の色を合わせる設定を外す(Undo 可)。
        /// 合わせているかの判定(<see cref="RecipeRegistration.HasShadowMatch"/>)と同じ顔ぶれから外す。
        /// </summary>
        internal void UnmatchShadowColors(List<Material> materials) {
            var source = SourceTexture;
            if (source == null) {
                return;
            }
            var components = Object.FindObjectsByType<IrocaRecolor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(Localization.ShadowMatchHeader);
            RecipeRegistration.RemoveShadowMatch(components, materials, source);
            Undo.CollapseUndoOperations(group);
            _shadowMismatchReadAt = 0;
            RequestRepaint();
        }

        // 結び付いたレシピを持つ、開いているシーンの Iroca Recolor。
        private List<IrocaRecolor> RegisteredComponents() => RecipeRegistration.OtherUsers(BoundRecipe, null);

        /// <summary>案内の文面。warn は警告の色で出すか(プレビューに入っていない・見た目に出にくい設定がある)。</summary>
        internal static string MaterialColorNoticeText(MaterialColorSettings.Report r, bool appearanceOn, out bool warn) {
            warn = false;
            var sb = new StringBuilder();
            string material = r.material != null ? r.material.name : "";
            bool layer = r.slot != MaterialColorSettings.Slot.Main;
            if (layer) {
                sb.Append(string.Format(Localization.MaterialColorLayerNoticeFormat, material,
                    r.slot == MaterialColorSettings.Slot.Layer2nd ? Localization.MaterialColorLayer2nd : Localization.MaterialColorLayer3rd));
            } else {
                sb.Append(string.Format(Localization.MaterialColorNoticeFormat, material, ShaderLabel(r),
                    EffectList(r.effects)));
            }

            var kind = AppearanceRenderer.KindOf(r);
            if (kind != AppearanceRenderer.Kind.None) {
                sb.Append('\n').Append(!appearanceOn ? Localization.MaterialColorPreviewOff
                    : layer ? Localization.MaterialColorShownInPreviewLayer : Localization.MaterialColorShownInPreview);
            }
            var notShown = AppearanceRenderer.NotPreviewed(r);
            if (notShown != Effect.None) {
                sb.Append('\n').Append(string.Format(Localization.MaterialColorNotPreviewedFormat, EffectList(notShown)));
                warn = true;
            }
            if ((r.effects & Effect.GradationFull) != 0) {
                sb.Append('\n').Append(Localization.MaterialColorGradationFull);
                warn = true;
            }
            if (!layer && (r.effects & Effect.Tint) != 0) {
                // 明るい色にしても届く明るさの目安(掛け算の色の一番明るいチャンネル)。
                float max = Mathf.Max(r.tint.r, Mathf.Max(r.tint.g, r.tint.b));
                if (max < 0.95f) {
                    sb.Append('\n').Append(string.Format(Localization.MaterialColorDarkTintFormat, Mathf.RoundToInt(max * 100f)));
                    if (max < 0.5f) {
                        warn = true;
                    }
                }
            }
            return sb.ToString();
        }

        private static string ShaderLabel(MaterialColorSettings.Report r) {
            switch (r.family) {
                case MaterialColorSettings.Family.LilToon: return "lilToon";
                case MaterialColorSettings.Family.Poiyomi: return "Poiyomi";
                default: return r.material != null && r.material.shader != null ? r.material.shader.name : "";
            }
        }

        // 設定の名前の並び(GradationFull は Gradation と同じものなので名前にしない)。
        private static readonly (Effect effect, System.Func<string> name)[] EffectNames = {
            (Effect.Tint, () => Localization.MaterialColorTint),
            (Effect.ToneCorrection, () => Localization.MaterialColorToneCorrection),
            (Effect.Gradation, () => Localization.MaterialColorGradation),
            (Effect.Layer2nd, () => Localization.MaterialColorLayer2nd),
            (Effect.Layer3rd, () => Localization.MaterialColorLayer3rd),
            (Effect.LayerOtherUv, () => Localization.MaterialColorLayerOtherUv),
            (Effect.PoiColorAdjust, () => Localization.MaterialColorPoiColorAdjust),
            (Effect.PoiDecal, () => Localization.MaterialColorPoiDecal),
            (Effect.PoiRgbMask, () => Localization.MaterialColorPoiRgbMask),
        };

        private static string EffectList(Effect effects) {
            var names = new List<string>();
            foreach (var (e, name) in EffectNames) {
                if ((effects & e) != 0) {
                    names.Add(name());
                }
            }
            return string.Join(Localization.MaterialColorListSeparator, names);
        }
    }
}
