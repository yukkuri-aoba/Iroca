// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 本体ウィンドウのマスク欄の「メッシュ」行。右クリックの「パーツ」操作と UV の島の表示に使うメッシュ
    /// (FBX・Prefab)を見せ、差し替えられるようにする。
    ///
    /// 以前は自動で見つけたメッシュがどこにも出ず、手動の指定も「Project で選んでから右クリック」しか
    /// なかったので、見つかったのか・どう指定するのかが分からなかった(2026-09-30 のユーザー指摘)。
    /// 状態は <see cref="MeshPartController"/> が持ち、ここは表示と入口だけ。
    /// </summary>
    internal static class MeshPartSection
    {
        private const string ShowIslandsPrefKey = "Iroca.ShowMeshIslands";
        // ツールチップに並べるメッシュ名の上限(多いアバターでツールチップが画面を覆わないように)
        private const int MaxListedMeshes = 20;
        // メッシュ欄(ラベルを除く)の最小幅。ピッカーのボタンと名前の頭が見える程度。
        private const float MinObjectFieldWidth = 30f;

        /// <summary>プレビューに UV の島の輪郭を重ねるか(ユーザーごとの表示設定)。</summary>
        public static bool ShowIslands
        {
            get => EditorPrefs.GetBool(ShowIslandsPrefKey, false);
            set => EditorPrefs.SetBool(ShowIslandsPrefKey, value);
        }

        public static void Draw(IrocaWindow host, MaskPaintView maskView)
        {
            var parts = maskView.MeshParts;
            if (parts == null || host.SourceTexture == null) return;

            EditorGUILayout.BeginHorizontal();
            // 初回はここでメッシュを探す(テクスチャ単位でキャッシュ。欄を畳んでいる間は探さない)。
            var current = parts.SourceObject;
            EditorGUI.BeginChangeCheck();
            // MinWidth: ObjectField は既定で「ラベル幅＋約 55px」を最小幅に要求する。設定列のラベル幅は
            // 最長ラベルに合わせて広がる(英語では ~176px)ので、右の「探し直す」と合わせてこの行だけが
            // 設定列(下限 320px)より広くなり、列全体がその幅で並べられて全行の右端(数値欄・ボタン)が
            // 切れていた。欄は狭くてもアイコンと名前の頭・ピッカーが見えれば使えるので、最小幅を詰める。
            var picked = EditorGUILayout.ObjectField(
                new GUIContent(Localization.MeshSource, Localization.MeshSourceTooltip),
                current, typeof(GameObject), true,
                GUILayout.MinWidth(EditorGUIUtility.labelWidth + MinObjectFieldWidth));
            if (EditorGUI.EndChangeCheck() && picked != current)
            {
                if (picked == null)
                {
                    parts.Research();
                }
                else if (parts.UseObject(picked))
                {
                    host.ShowNotification(new GUIContent(
                        string.Format(Localization.NotifyMeshFoundFormat, parts.Found.found.Count)));
                }
                else
                {
                    host.ShowNotification(new GUIContent(
                        string.Format(Localization.NotifyMeshNotUsableFormat, picked.name)));
                }
                host.RequestRepaint();
            }
            if (GUILayout.Button(new GUIContent(Localization.MeshResearch, Localization.MeshResearchTooltip),
                    EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
            {
                parts.Research();
                host.ShowNotification(new GUIContent(parts.HasMesh
                    ? string.Format(Localization.NotifyMeshFoundFormat, parts.Found.found.Count)
                    : Localization.NotifyMeshNotFound));
                host.RequestRepaint();
            }
            EditorGUILayout.EndHorizontal();

            var found = parts.Found;
            EditorGUILayout.LabelField(new GUIContent(StatusText(parts, found), MeshListTooltip(found)),
                                       EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(!parts.HasMesh))
            {
                EditorGUI.BeginChangeCheck();
                bool show = EditorGUILayout.ToggleLeft(
                    new GUIContent(Localization.MeshShowIslands, Localization.MeshShowIslandsTooltip), ShowIslands);
                if (EditorGUI.EndChangeCheck())
                {
                    ShowIslands = show;
                    host.RequestRepaint();
                }
            }
        }

        private static string StatusText(MeshPartController parts, MeshUvLocator.Result found)
        {
            if (found == null) return Localization.MeshStatusNone;
            if (found.found.Count == 0)
                return found.unreadable > 0
                    ? string.Format(Localization.MeshStatusUnreadableFormat, found.unreadable)
                    : Localization.MeshStatusNone;
            if (parts.IsManual) return string.Format(Localization.MeshStatusManualFormat, found.found.Count);
            return string.Format(found.via == "scene"
                ? Localization.MeshStatusSceneFormat
                : Localization.MeshStatusProjectFormat, found.found.Count);
        }

        /// <summary>使っているメッシュの名前(どれを拾ったかの確認用)。</summary>
        private static string MeshListTooltip(MeshUvLocator.Result found)
        {
            if (found == null || found.found.Count == 0) return Localization.MeshSourceTooltip;
            var sb = new StringBuilder(Localization.MeshListTooltipHeader);
            for (int i = 0; i < found.found.Count && i < MaxListedMeshes; i++)
                sb.Append('\n').Append(found.found[i].source.name);
            if (found.found.Count > MaxListedMeshes)
                sb.Append("\n…(+").Append(found.found.Count - MaxListedMeshes).Append(')');
            return sb.ToString();
        }
    }
}
