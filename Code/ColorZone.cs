using System;
using UnityEngine;

namespace VRCAvatarColorChanger
{
    public enum SelectionMode
    {
        ColorPick,
        Rect
    }

    [Serializable]
    public class ColorZone
    {
        public string name = "Zone";
        public bool enabled = true;
        public SelectionMode mode = SelectionMode.ColorPick;

        // カラーピックモード
        public Color sampleColor = Color.white;
        public float tolerance = 0f;

        // 矩形モード（UV座標0-1）
        public Rect uvRect = new Rect(0, 0, 1, 1);

        // 変更先
        public Color targetColor = Color.white;

        // 0 = 変更先Vを完全に使用（べたごし/鮮やか）、1 = 元のVを完全保持（模様不然）
        [Range(0f, 1f)]
        public float valueBlend = 1f;

        // 0 = 硬い端、1 = とても柔らかい端（アンチエイリアス友好）
        [Range(0f, 1f)]
        public float edgeSoftness = 0f;

        // 彩度厳格化: 低彩度ピクセル（AO/影）をどの程度決定的に除外するかを制御します。
        // 高い値 = 誤検知が少ない（はみ出しが少ない）が、アンチエイリアス端にドットが残る可能性があります。
        // 低い値 = よりインクルーシブなマッチング。
        // 0 = 固定低閾値（0.02）、0.50 = デフォルト、1.0 = 最大厳格化。
        [Range(0f, 1f)]
        public float saturationStrictness = 0.50f;

        // バリューマッチング距離式における（明るさ）の重み。
        // 実際のV罰則は彩度比率（pS/sS）によって調整されます：
        // ピクセルがサンプルと同様の彩度を持つ場合、V罰則は小さい
        // （同じ素材の下の照明が異なる）。彩度が大きく異なる場合、
        // V罰則は大きい（異なる素材の可能性、例えば茶ブーツ対赤バンダナ）。
        [Range(0f, 1f)]
        public float valueWeight = 1.0f;

        // ── アドバンスモード専用パラメータ ──
        // 距離式における彩度距離の重み。高い値は彩度差に敏感になる。
        [Range(0f, 1f)]
        public float satDistWeight = 0.15f;

        // 動的彩度ランプのスケール。satRamp = Max(0.08, sS * satRampScale)
        // 大きい値 = 彩度が高いサンプルの閾値付近で段階的な遷移
        [Range(0.01f, 0.5f)]
        public float satRampScale = 0.10f;

        // レイヤーインデックス: 高いレイヤーのゾーンが低いレイヤーをオーバーライド（0 = ベースレイヤー）
        public int layerIndex = 0;

        /// <summary>
        /// [0, 1] 範囲でソフト選択用のマッチ強度を返します。
        /// 0 = マッチなし、1 = 完全マッチ、中間 = 部分的（エッジ/遷移）。
        /// </summary>
        public float GetMatchStrength(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            if (!enabled) return 0f;

            switch (mode)
            {
                case SelectionMode.ColorPick:
                    return GetColorMatchStrength(pixelColor);
                case SelectionMode.Rect:
                    return IsInRect(x, y, texWidth, texHeight) ? 1f : 0f;
                default:
                    return 0f;
            }
        }

        public bool ContainsPixel(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            return GetMatchStrength(pixelColor, x, y, texWidth, texHeight) > 0f;
        }

        private float GetColorMatchStrength(Color pixelColor)
        {
            float pH, pS, pV, sH, sS, sV;
            Color.RGBToHSV(pixelColor, out pH, out pS, out pV);
            Color.RGBToHSV(sampleColor, out sH, out sS, out sV);

            // 動的彩度閾値: サンプルが非常に彩度の高い場合（例えば鮮やかな青）、
            // ピクセルがマッチするためには比例する最小彩度を持つ必要があります。
            // これにより、同じ色相を共有する AO/影背景レイヤー（ただし彩度が非常に低い）
            // が再彩色されるのを防ぎます。
            // トレードオフ: 高い乗数は AO/影背景をブロック; FillSmallHolesHueAware
            // はマッチ境界付近の AA-エッジピクセルを空間的に回復します。
            // 低彩度サンプルの場合、閾値は固定 0.02/0.08 ランプに縮小します。
            float satMin  = Mathf.Max(0.02f, sS * saturationStrictness);
            float satRamp = Mathf.Max(0.08f, sS * satRampScale);
            float satConfidence = Mathf.Clamp01((pS - satMin) / satRamp);

            // 色相距離（円形）
            float hDist = Mathf.Abs(pH - sH);
            if (hDist > 0.5f) hDist = 1f - hDist;

            // 彩度距離（軽い重み）：完全に中立したピクセルを除外しますが、
            // 同じ素材の影/ハイライト変動を許可します。
            float sDist = Mathf.Abs(pS - sS);

            // 彩度距離の重み（アドバンスモードで調整可能）
            // 値距離: ピクセルが持つ彩度比率で調整されるため、
            // サンプルと同様の彩度を持つピクセル（同じ素材、異なる照明）
            // は小さな V 罰則を受け、彩度が非常に低いピクセル
            // （異なる素材、例えば茶色のブーツ対赤いバンダナ）は強い罰則を受けます。
            float vDist = Mathf.Abs(pV - sV);
            float sRatio = (sS > 0.01f) ? Mathf.Clamp01(pS / sS) : 1f;
            float dist = hDist + sDist * satDistWeight + vDist * valueWeight * (1f - sRatio);

            if (dist >= tolerance) return 0f;

            // ソフトエッジ: 許容範囲の外側部分での段階的フェードアウト
            float softRange = tolerance * edgeSoftness;
            float hardRange = tolerance - softRange;

            float strength;
            if (softRange < 0.0001f)
                strength = 1f; // ハードエッジモード
            else if (dist <= hardRange)
                strength = 1f;
            else
                strength = 1f - (dist - hardRange) / softRange;

            return strength * satConfidence;
        }

        private bool IsInRect(int x, int y, int texWidth, int texHeight)
        {
            float u = (float)x / texWidth;
            float v = (float)y / texHeight;
            return uvRect.Contains(new Vector2(u, v));
        }
    }
}
