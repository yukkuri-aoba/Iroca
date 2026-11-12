// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 開いているシーンの Renderer の顔ぶれ・素材の並びが変わったかもしれない、という通知の版の番号。
    /// Unity の変更の通知(<see cref="ObjectChangeEvents"/>。インスペクタ・Undo・プレハブの更新などで出る)のうち、
    /// GameObject の作成・削除・構造の変化・プレハブの更新・シーンの変化と、Renderer の値の変化で上がる。
    /// シーンの全部の Renderer を探し直すのは重いので、探し直すかをこれで決める(版が前と同じなら探さない)。
    /// Undo を通さずにスクリプトが素材を差し替えたときは通知が来ない。
    /// <para>
    /// 通知を受け取り始めるのは、誰かが最初に <see cref="Version"/> を読んだとき(読むのは編集画面とインスペクタだけ)。
    /// 以前は読み込みのたびに全員の環境で受け取り始めていたので、いろかを開かない人のエディタでも、シーンの変更のたびに
    /// 通知の中身を調べていた。
    /// </para>
    /// </summary>
    internal static class SceneRendererChanges {
        private static int s_version;
        private static bool s_listening;

        /// <summary>今の版(変わったかもしれないたびに 1 上がる)。メインスレッドで読む。</summary>
        public static int Version {
            get {
                if (!s_listening) {
                    s_listening = true;
                    ObjectChangeEvents.changesPublished -= OnChangesPublished;
                    ObjectChangeEvents.changesPublished += OnChangesPublished;
                    // 受け取り始める前の変化は数えていない。前の値を覚えている人(いないはずだが)が見逃さないよう、ひとつ上げる。
                    unchecked { s_version++; }
                }
                return s_version;
            }
        }

        private static void OnChangesPublished(ref ObjectChangeEventStream stream) {
            for (int i = 0; i < stream.length; i++) {
                if (!Affects(ref stream, i)) {
                    continue;
                }
                unchecked { s_version++; }
                return;
            }
        }

        private static bool Affects(ref ObjectChangeEventStream stream, int i) {
            switch (stream.GetEventType(i)) {
                case ObjectChangeKind.ChangeScene:
                case ObjectChangeKind.CreateGameObjectHierarchy:
                case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                case ObjectChangeKind.ChangeGameObjectStructure:
                case ObjectChangeKind.DestroyGameObjectHierarchy:
                case ObjectChangeKind.UpdatePrefabInstances:
                    return true;
                case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    // Transform を動かすなどは数が多いので、Renderer(素材の並びを持つ)の変化だけを数える。
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var e);
                    return EditorUtility.InstanceIDToObject(e.instanceId) is Renderer;
                default:
                    // アセット(マテリアルの値など)の変化は、見つけた素材の値の読み直しが拾う。親子・並び順の変化は
                    // Renderer の顔ぶれを変えない。
                    return false;
            }
        }
    }
}
