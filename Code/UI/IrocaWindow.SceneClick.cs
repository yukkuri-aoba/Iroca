// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// Scene でモデルをクリックすると、その場所をプレビューで示す。
    /// <para>
    /// テクスチャの「どこが袖か」は、モデルの作りを知らないと分からない。ボタンやモードを足すと手数が
    /// 増えるので、Scene のふつうのクリックを横から見るだけにする(イベントは消費しない。Unity の
    /// 選択はいつもどおり起きる)。選択や設定は変えない表示だけの機能なので、別の目的でクリックしても害は無い。
    /// </para>
    /// <list type="bullet">
    /// <item>開いているテクスチャの部分 → その位置と UV の島をプレビューに重ねる</item>
    /// <item>何も開いていない → クリックした部分のテクスチャを開く</item>
    /// <item>別のテクスチャの部分 → 「ここは〇〇のテクスチャです」と案内する(勝手には切り替えない)</item>
    /// <item>何もない所 → 表示を消す</item>
    /// </list>
    /// </summary>
    public partial class IrocaWindow
    {
        // 押した位置と離した位置がこれ以上離れたら、クリックではなくドラッグ(範囲選択など)とみなす。
        private const float SceneClickSlopPx = 4f;
        private const string SceneClickUsedPrefKey = "Iroca.SceneClickUsed";

        [System.NonSerialized] private bool _sceneDownArmed;
        [System.NonSerialized] private Vector2 _sceneDownPos;
        [System.NonSerialized] private SceneView _sceneDownView;

        // 別のテクスチャの部分をクリックしたときの案内。出したときに開いていたテクスチャから変わったら出さない。
        // 当たりも持っておき、「開く」で開いたらすぐその場所を示す。
        [System.NonSerialized] private Texture2D _sceneOtherTexture;
        [System.NonSerialized] private Texture2D _sceneOtherTextureFor;
        [System.NonSerialized] private SceneMeshPicker.Hit _sceneOtherHit;

        /// <summary>一度でも Scene のクリックで場所を表示したか(プレビュー上の使い方の案内を出すかどうか)。</summary>
        internal static bool SceneClickUsed
        {
            get => s_sceneClickUsed ??= EditorPrefs.GetBool(SceneClickUsedPrefKey, false);
            set
            {
                if (s_sceneClickUsed == value) return;
                s_sceneClickUsed = value;
                EditorPrefs.SetBool(SceneClickUsedPrefKey, value);
            }
        }
        // 毎回の描画で EditorPrefs を読まないための控え。
        private static bool? s_sceneClickUsed;

        /// <summary>直前のクリックが別のテクスチャの部分だったときの、そのテクスチャ(無ければ null)。</summary>
        internal Texture2D SceneOtherTexture =>
            _sceneOtherTexture != null && _sceneOtherTextureFor == sourceTexture ? _sceneOtherTexture : null;

        private void InstallSceneClick()
        {
            SceneView.duringSceneGui -= OnSceneGuiForClick;
            SceneView.duringSceneGui += OnSceneGuiForClick;
        }

        private void UninstallSceneClick()
        {
            SceneView.duringSceneGui -= OnSceneGuiForClick;
            _sceneDownArmed = false;
        }

        private void OnSceneGuiForClick(SceneView view)
        {
            var e = Event.current;
            if (e == null) return;
            // rawType: 先に誰かがイベントを使って(Use)いても、元の種類で判定する。こちらは使わない。
            switch (e.rawType)
            {
                case EventType.MouseDown:
                    // Alt+ドラッグは視点の回転。右・中ボタンも視点操作なので見ない。
                    _sceneDownArmed = e.button == 0 && !e.alt;
                    _sceneDownPos = e.mousePosition;
                    _sceneDownView = view;
                    break;
                case EventType.MouseDrag:
                    if (_sceneDownArmed && (e.mousePosition - _sceneDownPos).sqrMagnitude > SceneClickSlopPx * SceneClickSlopPx)
                        _sceneDownArmed = false;
                    break;
                case EventType.MouseUp:
                    if (!_sceneDownArmed || e.button != 0) break;
                    _sceneDownArmed = false;
                    if (view != _sceneDownView || e.alt) break;
                    if ((e.mousePosition - _sceneDownPos).sqrMagnitude > SceneClickSlopPx * SceneClickSlopPx) break;
                    // Scene ビューの描画の途中なので、想定外の例外で Scene ビューやほかのツールの処理を止めない。
                    try { HandleSceneClick(HandleUtility.GUIPointToWorldRay(e.mousePosition)); }
                    catch (System.Exception ex) { Debug.LogException(ex); }
                    break;
            }
        }

        /// <summary>Scene の光線 1 本ぶんの処理(クリック 1 回)。</summary>
        internal void HandleSceneClick(Ray ray)
        {
            var hit = SceneMeshPicker.Pick(ray);
            if (hit == null)
            {
                ClearSceneClick();
                return;
            }

            // 何も開いていなければ、クリックした部分のテクスチャを開く(作業中のものが切り替わる心配は無い)。
            if (sourceTexture == null)
            {
                var open = MeshUvLocator.EditableSourceOf(MeshUvLocator.MainTexture(hit.material));
                if (open == null) return;
                SwitchSourceTexture(open);
                ShowNotification(new GUIContent(string.Format(Localization.NotifySceneClickOpenedFormat, open.name)));
            }

            _sceneOtherTexture = null;
            _sceneOtherHit = null;
            if (!ShowSceneHit(hit))
            {
                // 別のテクスチャの部分。何のテクスチャか分からない物(地面の単色マテリアル等)は黙って消すだけ。
                _previewView.ClearSceneHighlight();
                _sceneOtherTexture = MeshUvLocator.EditableSourceOf(MeshUvLocator.MainTexture(hit.material));
                _sceneOtherTextureFor = sourceTexture;
                _sceneOtherHit = _sceneOtherTexture != null ? hit : null;
            }
            RequestRepaint();
        }

        /// <summary>当たった部分が開いているテクスチャを使っていれば、その場所をプレビューに出して true。</summary>
        private bool ShowSceneHit(SceneMeshPicker.Hit hit)
        {
            if (sourceTexture == null || hit?.material == null) return false;
            var targets = MeshUvLocator.TargetTextures(sourceTexture);
            if (!MeshUvLocator.UsesTexture(hit.material, targets, out string prop)) return false;
            _previewView.ShowSceneHighlight(SceneClickHighlight.Build(
                sourceTexture, hit, hit.material.GetTextureScale(prop), hit.material.GetTextureOffset(prop)));
            SceneClickUsed = true;
            return true;
        }

        /// <summary>プレビュー上の案内の「開く」: 直前にクリックした部分のテクスチャを開き、その場所を示す。</summary>
        internal void OpenSceneOtherTexture()
        {
            var tex = SceneOtherTexture;
            var hit = _sceneOtherHit;
            if (tex == null) return;
            _sceneOtherTexture = null;
            _sceneOtherHit = null;
            SwitchSourceTexture(tex);
            if (hit != null) ShowSceneHit(hit);
            RequestRepaint();
        }

        private void ClearSceneClick()
        {
            _previewView?.ClearSceneHighlight();
            _sceneOtherTexture = null;
            _sceneOtherHit = null;
            RequestRepaint();
        }
    }
}
