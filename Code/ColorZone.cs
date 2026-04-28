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
        // ハイライト補助マッチで対象ピクセルとみなす「鏡面反射/ハイライトらしさ」の閾値。
        // V（明度）がこれ以上 かつ S（彩度）がハイライト閾値以下のピクセルのみ対象にする。
        // これにより、光沢の白飛び部分（彩度が大きく落ちているが本来は同じ素材）を
        // マッチ対象に戻す。値は多くのアバター系テクスチャでの経験則。
        private const float HighlightValueMin = 0.80f;
        private const float HighlightSaturationMax = 0.20f;
        // ハイライト補助マッチで再評価する際の固定彩度閾値。通常パスの動的閾値より
        // 緩め（低 satMin + 小さめランプ）にして、白飛び部分を取りこぼさない。
        private const float HighlightRelaxedSatMin = 0.02f;
        private const float HighlightRelaxedSatRamp = 0.08f;

        // 低彩度サンプル時の RGB ↔ HSV ハイブリッド距離フェード閾値。
        // sample の彩度が ChromaConfidenceLo 以下なら H が信頼できないので RGB ユークリッド距離を、
        // ChromaConfidenceHi 以上なら従来通り HSV 距離を、その間は線形ブレンドする。
        private const float ChromaConfidenceLo = 0.05f;
        private const float ChromaConfidenceHi = 0.15f;

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

        // ハイライト補助マッチ: 高明度・低彩度ピクセルを補助的にマッチ
        public bool highlightRecovery = true;

        // レイヤーインデックス: 高いレイヤーのゾーンが低いレイヤーをオーバーライド（0 = ベースレイヤー）
        public int layerIndex = 0;

        // ゾーン別マスク識別用 GUID。EnsureId() で初期化されるまで空文字列。
        public string id = "";

        public void EnsureId()
        {
            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString();
        }

        /// <summary>
        /// [0, 1] 範囲でソフト選択用のマッチ強度を返します。
        /// 0 = マッチなし、1 = 完全マッチ、中間 = 部分的（エッジ/遷移）。
        /// </summary>
        public float GetMatchStrength(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            float strength, highlightPot;
            GetMatchScores(pixelColor, x, y, texWidth, texHeight, out strength, out highlightPot);
            return Mathf.Max(strength, highlightPot);
        }

        public void GetMatchScores(Color pixelColor, int x, int y, int texWidth, int texHeight, out float strength, out float highlightPot)
        {
            strength = 0f;
            highlightPot = 0f;
            if (!enabled) return;

            switch (mode)
            {
                case SelectionMode.ColorPick:
                    GetColorMatchScores(pixelColor, out strength, out highlightPot);
                    break;
                case SelectionMode.Rect:
                    if (IsInRect(x, y, texWidth, texHeight))
                        strength = 1f;
                    break;
            }
        }

        public bool ContainsPixel(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            return GetMatchStrength(pixelColor, x, y, texWidth, texHeight) > 0f;
        }

        private void GetColorMatchScores(Color pixelColor, out float strength, out float highlightPotential)
        {
            strength = 0f;
            highlightPotential = 0f;
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

            // 色相距離（色相環 [0,1) 上の最短距離）。
            // HSV の H は 0 と 1 が同じ（赤）として円環状になっているため、単純差分では
            // H=0.95 と H=0.05 の距離が 0.9 になってしまう。0.5 を超えたら反対側回りを
            // 採用することで、実際の最短距離（この例なら 0.1）に補正する。
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
            float hsvDist = hDist + sDist * satDistWeight + vDist * valueWeight * (1f - sRatio);

            // 低彩度サンプルでは色相が雑音同然になるため、RGB ユークリッド距離も使う。
            // sS が ChromaConfidenceLo 以下なら RGB のみ、ChromaConfidenceHi 以上なら HSV のみ、
            // その間は線形ブレンド。サンプル側の彩度のみで判定するのがポイント
            // （白飛びハイライト枝に処理を残すため、ピクセル側の S は判定に使わない）。
            float chromaConfidence = Mathf.Clamp01(
                (sS - ChromaConfidenceLo) / (ChromaConfidenceHi - ChromaConfidenceLo));
            float dr = pixelColor.r - sampleColor.r;
            float dg = pixelColor.g - sampleColor.g;
            float db = pixelColor.b - sampleColor.b;
            // 1/sqrt(3) で 0..1 範囲（ユニットキューブ対角）に正規化
            float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * 0.57735027f;

            float dist = Mathf.Lerp(rgbDist, hsvDist, chromaConfidence);
            // satConfidence ゲートは HSV 経路でのみ意味があるため、低彩度サンプルでは弱める。
            float gate = Mathf.Lerp(1f, satConfidence, chromaConfidence);

            if (dist >= tolerance)
            {
                strength = 0f;
            }
            else
            {
                // ソフトエッジ: 許容範囲の外側部分での段階的フェードアウト
                float softRange = tolerance * edgeSoftness;
                float hardRange = tolerance - softRange;

                float tempStrength;
                if (softRange < 0.0001f)
                    tempStrength = 1f; // ハードエッジモード
                else if (dist <= hardRange)
                    tempStrength = 1f;
                else
                    tempStrength = 1f - (dist - hardRange) / softRange;

                strength = tempStrength * gate;
            }

            // ハイライト補助分岐: 高明度・低彩度ピクセル（鏡面反射/ハイライト）に
            // 彩度差項を除いた緩和距離式でマッチを試みる
            if (!highlightRecovery) return;
            if (pV <= HighlightValueMin || pS >= HighlightSaturationMax) return;

            // FIX: 色相距離は tolerance より明らかに厳しくする。
            float highlightHueCap = Mathf.Max(0.05f, tolerance * 0.3f);
            if (hDist > highlightHueCap) return;

            float relaxedSatConf = Mathf.Clamp01((pS - HighlightRelaxedSatMin) / HighlightRelaxedSatRamp);
            if (relaxedSatConf <= 0f) return;

            float highlightDist = hDist + vDist * valueWeight * (1f - sRatio);
            if (highlightDist >= tolerance) return;

            float hlSoftRange = tolerance * edgeSoftness;
            float hlHardRange = tolerance - hlSoftRange;
            float hlStrength;
            if (hlSoftRange < 0.0001f)
                hlStrength = 1f;
            else if (highlightDist <= hlHardRange)
                hlStrength = 1f;
            else
                hlStrength = 1f - (highlightDist - hlHardRange) / hlSoftRange;

            highlightPotential = hlStrength * relaxedSatConf;
        }

        private bool IsInRect(int x, int y, int texWidth, int texHeight)
        {
            float u = (float)x / texWidth;
            float v = (float)y / texHeight;
            return uvRect.Contains(new Vector2(u, v));
        }
    }
}
