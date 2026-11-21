// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 利用者のゾーンの操作(スポイト・色の欄・塗り先・シード・詳細設定の押せる条件)のうち、画素とゾーンの値だけで済む規則。
    /// 窓(EditingSession・プレビューの入力・ゾーンのカード)・自動化・ハーネスの --script が同じものを使う(写しを作らない)。
    /// Undo・編集の知らせ・名前の文言は呼ぶ側が持つ。
    /// </summary>
    internal static class ZoneEdits {
        /// <summary>
        /// 新しいゾーン(リストには足さない)。許容範囲は zones JSON の既定(<see cref="ZonesJsonDefaults.Tolerance"/>。
        /// 「色を選べば何か変わる」状態から始める。EditingSession.NewZoneInitialTolerance の説明)、ほかは <see cref="ColorZone"/> の既定。
        /// </summary>
        public static ColorZone NewZone(string name) {
            var zone = new ColorZone {
                name = name,
                tolerance = ZonesJsonDefaults.Tolerance,
            };
            zone.EnsureId();
            return zone;
        }

        /// <summary>
        /// スポイトした画素の色(窓のプレビューのクリックと同じ: UV(下原点)→ 画素(<see cref="PreviewCoords.UvToPixel"/>)の
        /// 1 画素。α は不透明にする。サンプルカラーは照合の基準で α は使わない)。
        /// </summary>
        public static Color SampleColorAt(NativePixels px, float u, float v) => SampleColorAt(px, px.Width, px.Height, u, v);

        /// <summary>寸法を別に渡す版(窓は原本の寸法で引く)。</summary>
        public static Color SampleColorAt(NativePixels px, int w, int h, float u, float v) {
            PreviewCoords.UvToPixel(u, v, w, h, out int x, out int y);
            Color32 c = px.Get(y * w + x);
            return new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
        }

        /// <summary>スポイトで新しいゾーンを作ったときの色と位置(位置は自動調整が AI 提案の証拠に使う)。</summary>
        public static void SetNewSample(ColorZone zone, Color picked, Vector2 uv) {
            zone.sampleColor = picked;
            zone.sampleColorSet = true;
            zone.sampleUV = uv;
        }

        /// <summary>スポイトで取った色が、今のサンプル色を変えるか(まだ色を指定していないゾーンも変える)。</summary>
        public static bool SampleColorChanges(ColorZone zone, Color picked) => zone.sampleColor != picked || !zone.sampleColorSet;

        /// <summary>
        /// 今のゾーンへスポイトで取った色と位置を書く。色が変わるなら、前の島から導いた値を既定へ戻す(<see cref="ClearIslandDerived"/>)。
        /// colorChanged は <see cref="SampleColorChanges"/> を書く前に見たもの。
        /// </summary>
        public static void ApplySample(ColorZone zone, Color picked, Vector2 uv, bool colorChanged) {
            if (colorChanged) {
                zone.sampleColor = picked;
                zone.sampleColorSet = true;
                // 主サンプル変更で陳腐化する、前の島から導いた値を捨てる（ColorField と同じ挙動）。
                ClearIslandDerived(zone);
            }
            zone.sampleUV = uv;
        }

        /// <summary>
        /// サンプル色を取り直したとき、前のサンプルの島(スポイトした部位)から導いた値を既定へ戻す: 内部サンプルと、
        /// 部位の包絡ゲート(shadowValueFloor・partSatCeiling・partHueBand)・彩度天井(chromaCeiling)・穴埋めの余白
        /// (holeFillMargin)・照合の上限距離(matchCutoff)。残すと、調整し直さずに別の部位の色を取ったとき、前の部位の範囲で新しい部位を切る
        /// (点検 2026-11-09 自-3)。詳細設定で手で入れた値も消す(どれも島から導く値という定義。利用者の判断で推奨どおり)。
        /// 許容範囲などほかの調整値は残す。
        /// </summary>
        public static void ClearIslandDerived(ColorZone zone) {
            if (zone == null) {
                return;
            }
            zone.extraSamples?.Clear();
            ZoneAutoTuner.ResetIslandDerived(zone);   // 6 項目の一覧は Core に 1 つだけ置く
        }

        /// <summary>塗り先の色を選ぶ(窓のカラーフィールドと同じ: 選んだ印を立てる。白を選んでも既定の白と区別する)。</summary>
        public static void SetTarget(ColorZone zone, Color target) {
            zone.targetColor = target;
            zone.targetColorSet = true;
        }

        /// <summary>
        /// 塗り先の色の欄に色を書く(窓のカラーフィールド)。欄が返した色をそのまま書き、前の色から変わったときだけ選んだ印を立てる
        /// (<see cref="SetTarget"/>)。変わったら true。Undo の記録は呼ぶ側(欄が書く前に記録する)。
        /// </summary>
        public static bool SetTargetFromField(ColorZone zone, Color color) {
            Color prev = zone.targetColor;
            zone.targetColor = color;
            if (color == prev) {
                return false;
            }
            SetTarget(zone, color);
            return true;
        }

        /// <summary>
        /// サンプル色の欄に色を書く(窓のカラーフィールド。スポイトではない)。欄が返した色をそのまま書き、前の色から変わったときだけ:
        /// 指定済みの印を立て(意図的な白を未指定の白と区別する)、スポイト位置を外し(欄の色はクリックした画素の色ではないので、
        /// 自動調整が AI 提案の証拠を取る位置にならない。次のスポイトで付き直す)、前の島から導いた値を既定へ戻す
        /// (<see cref="ClearIslandDerived"/>)。変わったら true。Undo の記録と編集の知らせは呼ぶ側(欄が書く前に記録するので、
        /// 色と位置が対で戻る)。
        /// </summary>
        public static bool SetSampleFromField(ColorZone zone, Color color) {
            Color prev = zone.sampleColor;
            zone.sampleColor = color;
            if (color == prev) {
                return false;
            }
            zone.sampleColorSet = true;
            zone.ClearSampleUV();
            ClearIslandDerived(zone);
            return true;
        }

        /// <summary>連続領域の起点(シード)の「未設定」(負の値)。</summary>
        public static readonly Vector2 NoFloodFillSeed = new Vector2(-1f, -1f);

        /// <summary>
        /// シードを置ける・外せるゾーンか(窓のシードの入力と、詳細設定の「指定」「クリア」が出る条件: スポイトの選択で連続領域モード)。
        /// </summary>
        public static bool AcceptsFloodFillSeed(ColorZone zone)
            => zone != null && zone.mode == SelectionMode.ColorPick && zone.useFloodFill;

        /// <summary>シードが置かれているか(窓の「クリア」が押せるか)。</summary>
        public static bool HasFloodFillSeed(ColorZone zone) => zone.seedUV.x >= 0f;

        /// <summary>シードを uv(下原点)に置くと値が変わるか(同じ位置なら窓は記録も知らせもしない)。</summary>
        public static bool FloodFillSeedChanges(ColorZone zone, Vector2 uv) => zone.seedUV != uv;

        /// <summary>シードを置く。Undo の記録と編集の知らせは呼ぶ側(<see cref="FloodFillSeedChanges"/> を見てから記録して呼ぶ)。</summary>
        public static void SetFloodFillSeed(ColorZone zone, Vector2 uv) => zone.seedUV = uv;

        /// <summary>シードを外す(自動アンカリングへ戻す)。Undo の記録と編集の知らせは呼ぶ側。</summary>
        public static void ClearFloodFillSeed(ColorZone zone) => zone.seedUV = NoFloodFillSeed;

        /// <summary>
        /// 穴埋めの余白(<see cref="ColorZone.holeFillMargin"/>)が効くか。穴埋めは、縁を混色の割合で塗るゾーン
        /// (境界クリーンアップ ON・エッジぼかし 0・色のあるサンプル。<see cref="PixelProcessor.IsMixtureBandZone"/>)では走らない。
        /// 窓は効かないときスライダーを押せなくする。
        /// </summary>
        public static bool HoleFillMarginApplies(ColorZone zone, bool useDecontamination, float edgeFeather)
            => zone != null && !PixelProcessor.IsMixtureBandZone(zone, useDecontamination, edgeFeather);
    }
}
