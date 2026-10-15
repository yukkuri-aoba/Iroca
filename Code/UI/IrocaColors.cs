// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    internal static class IrocaColors
    {
        public static Color ActiveMaskTarget =>
            EditorGUIUtility.isProSkin
                ? new Color(0.45f, 0.65f, 0.95f)
                : new Color(0.30f, 0.55f, 0.85f);

        public static Color ExcludeButton =>
            EditorGUIUtility.isProSkin
                ? new Color(1f, 0.55f, 0.55f)
                : new Color(0.85f, 0.30f, 0.30f);

        public static Color IncludeButton =>
            EditorGUIUtility.isProSkin
                ? new Color(0.55f, 1f, 0.55f)
                : new Color(0.30f, 0.75f, 0.30f);

        // ブラシカーソルは「いま塗ろうとしているマスクの色」を映す。オーバーレイと同じ
        // 赤=除外 / 緑=含める にしておかないと、含めるマスクを塗っている最中に赤い
        // カーソルが出て意味が食い違う（2026-09-11 の UX 見直し。以前はカーソル色が
        // 塗る/消すの別を表しており、マスクの種類とは無関係だった）。
        public static Color BrushCursorExclude => new Color(1f, 0f, 0f, 0.5f);
        public static Color BrushCursorInclude => new Color(0f, 1f, 0f, 0.5f);
        // 消しゴムはどちらの種類でも「取り除く」操作なので、種類の色を出さず中立の白にする。
        public static Color BrushCursorErase => new Color(1f, 1f, 1f, 0.45f);

        // UV の島の輪郭。マスクの塗り(赤=除外 / 緑=含める / ゾーン色)と取り違えないよう線だけで出し、
        // どんな生地の上でも見えるよう、暗い影を 1px ずらして下に敷く。
        public static Color MeshIslandLine => new Color(1f, 0.9f, 0.2f, 0.9f);
        public static Color MeshIslandLineShadow => new Color(0f, 0f, 0f, 0.6f);

        // Scene でモデルをクリックした場所。マスクの赤・緑、ゾーン色の塗り、島の輪郭の黄と別の色にする。
        public static Color SceneHighlight => new Color(0.25f, 0.9f, 1f, 0.95f);
    }
}
