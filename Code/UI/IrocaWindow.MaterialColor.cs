// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Effect = Iroca.MaterialColorSettings.Effect;

namespace Iroca
{
    /// <summary>
    /// 編集中のテクスチャを使うマテリアルの色の設定(<see cref="MaterialColorSettings"/>)を見つけて、テクスチャ欄の下で
    /// 知らせる。プレビューの「アバターの色」(<see cref="PreviewView"/>)に使う設定もここで決める。
    /// </summary>
    public partial class IrocaWindow
    {
        // 見つけたマテリアルの読み取り結果を、同じ見た目になるもの(要約が同じ)ごとにまとめた組。使っている数の多い順。
        [System.NonSerialized] private List<List<MaterialColorSettings.Report>> _materialColorGroups;
        [System.NonSerialized] private Texture2D _materialColorsFor;
        [System.NonSerialized] private HashSet<Texture> _materialColorTargets;
        [System.NonSerialized] private int _materialColorsScene;
        [System.NonSerialized] private bool _materialColorsInScene;
        [System.NonSerialized] private double _materialColorsScannedAt, _materialColorsReadAt;
        // 表示に使う組の番号(テクスチャを替えたら先頭に戻す)。
        [System.NonSerialized] private int _materialColorChoice;

        // 開いているシーンの Renderer を探し直す間隔(マテリアルの付け替えを追う)と、見つけたマテリアルの値を
        // 読み直す間隔(インスペクタで色を変えたのを追う)。どちらもウィンドウを描いているときだけ。
        private const double MaterialColorRescanSeconds = 2.0;
        private const double MaterialColorRereadSeconds = 0.5;
        private const string MaterialColorDismissedKeyPrefix = IrocaPrefs.MaterialColorDismissedPrefix;

        /// <summary>プレビューの「アバターの色」に使う読み取り結果。マテリアルが見つからなければ null。</summary>
        internal MaterialColorSettings.Report AppearanceReport { get; private set; }

        /// <summary>
        /// 読み取り結果を必要なら作り直す。Layout のときだけ動く(同じフレームの Repaint と並ぶ要素をそろえる)。
        /// テクスチャか開いているシーンが替わったら探し直し、シーンで見つけていたら時々探し直す
        /// (素材フォルダから探したときは重いので繰り返さない)。それ以外は値だけを時々読み直す。
        /// </summary>
        private void RefreshMaterialColors()
        {
            if (Event.current.type != EventType.Layout) return;
            var tex = sourceTexture;
            if (tex == null)
            {
                _materialColorGroups = null;
                _materialColorsFor = null;
                AppearanceReport = null;
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            int scene = MeshPartController.SceneStamp();
            bool retarget = tex != _materialColorsFor || scene != _materialColorsScene;
            List<MaterialColorSettings.Report> reports;
            if (retarget || (_materialColorsInScene && now - _materialColorsScannedAt >= MaterialColorRescanSeconds))
            {
                if (tex != _materialColorsFor)
                {
                    _materialColorChoice = 0;
                    _materialColorTargets = MeshUvLocator.TargetTextures(tex);
                }
                reports = MaterialColorSettings.FindForTexture(tex, _materialColorTargets, out _materialColorsInScene);
                _materialColorsFor = tex;
                _materialColorsScene = scene;
                _materialColorsScannedAt = now;
            }
            else if (now - _materialColorsReadAt >= MaterialColorRereadSeconds && _materialColorGroups != null)
            {
                reports = new List<MaterialColorSettings.Report>();
                foreach (var g in _materialColorGroups)
                foreach (var r in g)
                {
                    var again = MaterialColorSettings.Analyze(r.material, _materialColorTargets);
                    if (again != null) reports.Add(again);
                }
            }
            else return;
            _materialColorsReadAt = now;

            var groups = new List<List<MaterialColorSettings.Report>>();
            var bySignature = new Dictionary<string, List<MaterialColorSettings.Report>>();
            foreach (var r in reports)
            {
                if (!bySignature.TryGetValue(r.signature, out var g))
                {
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
        private void DrawMaterialColorNotice()
        {
            RefreshMaterialColors();
            var groups = _materialColorGroups;
            var r = AppearanceReport;
            if (sourceTexture == null || groups == null || r == null) return;
            bool any = false;
            foreach (var g in groups) any |= g[0].AffectsColor;
            if (!any) return;
            string key = MaterialColorDismissedKeyPrefix + CurrentTexturePath(sourceTexture);
            if (SessionState.GetBool(key, false)) return;

            EditorGUILayout.HelpBox(MaterialColorNoticeText(r, _previewView != null && _previewView.appearanceMode,
                out bool warn), warn ? MessageType.Warning : MessageType.Info);

            // テクスチャ欄は変更の検出(プレビューの作り直し)に囲まれているので、ここの操作では立てない
            // (「アバターの色」は表示用の画像だけを作り直す)。
            bool changed = GUI.changed;
            EditorGUILayout.BeginHorizontal();
            if (groups.Count > 1)
            {
                var labels = new GUIContent[groups.Count];
                for (int i = 0; i < groups.Count; i++)
                {
                    var g = groups[i];
                    string name = g[0].material.name;
                    labels[i] = new GUIContent(g.Count > 1
                        ? string.Format(Localization.MaterialColorChoiceManyFormat, name, g.Count - 1)
                        : name);
                }
                int choice = EditorGUILayout.Popup(
                    new GUIContent(Localization.MaterialColorChoice, Localization.MaterialColorChoiceTooltip),
                    _materialColorChoice, labels);
                if (choice != _materialColorChoice)
                {
                    _materialColorChoice = choice;
                    AppearanceReport = groups[choice][0];
                    RequestRepaint();
                }
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent(Localization.LargeSourceDismiss, Localization.MaterialColorDismissTooltip),
                    GUILayout.ExpandWidth(false)))
                SessionState.SetBool(key, true);
            EditorGUILayout.EndHorizontal();
            GUI.changed = changed;
        }

        /// <summary>案内の文面。warn は警告の色で出すか(プレビューに入っていない・見た目に出にくい設定がある)。</summary>
        internal static string MaterialColorNoticeText(MaterialColorSettings.Report r, bool appearanceOn, out bool warn)
        {
            warn = false;
            var sb = new StringBuilder();
            string material = r.material != null ? r.material.name : "";
            bool layer = r.slot != MaterialColorSettings.Slot.Main;
            if (layer)
            {
                sb.Append(string.Format(Localization.MaterialColorLayerNoticeFormat, material,
                    r.slot == MaterialColorSettings.Slot.Layer2nd ? Localization.MaterialColorLayer2nd : Localization.MaterialColorLayer3rd));
            }
            else
            {
                sb.Append(string.Format(Localization.MaterialColorNoticeFormat, material, ShaderLabel(r),
                    EffectList(r.effects)));
            }

            var kind = AppearanceRenderer.KindOf(r);
            if (kind != AppearanceRenderer.Kind.None)
            {
                sb.Append('\n').Append(!appearanceOn ? Localization.MaterialColorPreviewOff
                    : layer ? Localization.MaterialColorShownInPreviewLayer : Localization.MaterialColorShownInPreview);
            }
            var notShown = AppearanceRenderer.NotPreviewed(r);
            if (notShown != Effect.None)
            {
                sb.Append('\n').Append(string.Format(Localization.MaterialColorNotPreviewedFormat, EffectList(notShown)));
                warn = true;
            }
            if ((r.effects & Effect.GradationFull) != 0)
            {
                sb.Append('\n').Append(Localization.MaterialColorGradationFull);
                warn = true;
            }
            if (!layer && (r.effects & Effect.Tint) != 0)
            {
                // 明るい色にしても届く明るさの目安(掛け算の色の一番明るいチャンネル)。
                float max = Mathf.Max(r.tint.r, Mathf.Max(r.tint.g, r.tint.b));
                if (max < 0.95f)
                {
                    sb.Append('\n').Append(string.Format(Localization.MaterialColorDarkTintFormat, Mathf.RoundToInt(max * 100f)));
                    if (max < 0.5f) warn = true;
                }
            }
            return sb.ToString();
        }

        private static string ShaderLabel(MaterialColorSettings.Report r)
        {
            switch (r.family)
            {
                case MaterialColorSettings.Family.LilToon: return "lilToon";
                case MaterialColorSettings.Family.Poiyomi: return "Poiyomi";
                default: return r.material != null && r.material.shader != null ? r.material.shader.name : "";
            }
        }

        // 設定の名前の並び(GradationFull は Gradation と同じものなので名前にしない)。
        private static readonly (Effect effect, System.Func<string> name)[] EffectNames =
        {
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

        private static string EffectList(Effect effects)
        {
            var names = new List<string>();
            foreach (var (e, name) in EffectNames)
                if ((effects & e) != 0) names.Add(name());
            return string.Join(Localization.MaterialColorListSeparator, names);
        }
    }
}
