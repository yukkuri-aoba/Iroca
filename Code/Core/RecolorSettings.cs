// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 再着色の全体設定（ゾーンに属さない処理パラメータ）の値スナップショット。
    ///
    /// <see cref="PixelProcessor.ProcessPixelsArray"/> の入口はこの型だけを受け取る。以前は
    /// プレビュー 2 段・詳細プレビュー・単体/一括エクスポート・Automation・ハーネスの各呼び出し元が
    /// セッションから 8 個の値を位置引数で並べ直しており、1 つ取り違えても（どれも float/int なので）
    /// コンパイルが通り、テストが測る経路と製品の経路が黙って食い違い得た。
    /// 全フィールドを取るコンストラクタしか無いので、設定を 1 つ足すと全呼び出し元がコンパイル
    /// エラーになり、書き漏れが起きない。
    ///
    /// 既定値もここが唯一の正（<see cref="IrocaSessionState"/> / <see cref="IrocaPresetData"/> /
    /// <see cref="ZonesJsonDefaults"/> はここの定数を参照する）。セッション・プリセット・zones JSON の
    /// 各フィールドとの対応は scripts/source_checks/test_recolor_settings.py が機械検査する。
    /// </summary>
    internal readonly struct RecolorSettings
    {
        public const float DefaultEdgeFeather = 0f;
        public const int DefaultAntiAliasCleanup = 3;
        public const int DefaultHoleFillPasses = 5;
        public const int DefaultHoleFillMinNeighbors = 4;
        public const float DefaultRelaxedSatMin = 0.02f;
        public const float DefaultRelaxedSatRamp = 0.08f;
        public const bool DefaultUseDecontamination = true;
        public const int DefaultDecontaminationRadius = 4;

        // デコンタミ半径の UI 範囲（IrocaWindow.Layout のスライダー）。プリセット JSON は手で書かれ得るので、
        // 読み込み時にこの範囲へ丸める（UI と MCP のどちらから読んでも同じ値になるよう From で行う）。
        public const int MinDecontaminationRadius = 1;
        public const int MaxDecontaminationRadius = 12;

        public readonly float edgeFeather;
        public readonly int antiAliasCleanup;
        public readonly int holeFillPasses;
        public readonly int holeFillMinNeighbors;
        public readonly float relaxedSatMin;
        public readonly float relaxedSatRamp;
        public readonly bool useDecontamination;
        public readonly int decontaminationRadius;

        public RecolorSettings(float edgeFeather, int antiAliasCleanup,
                               int holeFillPasses, int holeFillMinNeighbors,
                               float relaxedSatMin, float relaxedSatRamp,
                               bool useDecontamination, int decontaminationRadius)
        {
            this.edgeFeather = edgeFeather;
            this.antiAliasCleanup = antiAliasCleanup;
            this.holeFillPasses = holeFillPasses;
            this.holeFillMinNeighbors = holeFillMinNeighbors;
            this.relaxedSatMin = relaxedSatMin;
            this.relaxedSatRamp = relaxedSatRamp;
            this.useDecontamination = useDecontamination;
            this.decontaminationRadius = decontaminationRadius;
        }

        public static RecolorSettings Default => new RecolorSettings(
            DefaultEdgeFeather, DefaultAntiAliasCleanup,
            DefaultHoleFillPasses, DefaultHoleFillMinNeighbors,
            DefaultRelaxedSatMin, DefaultRelaxedSatRamp,
            DefaultUseDecontamination, DefaultDecontaminationRadius);

        public static RecolorSettings From(IrocaSessionState s) => new RecolorSettings(
            s.edgeFeather, s.antiAliasCleanup,
            s.holeFillPasses, s.holeFillMinNeighbors,
            s.relaxedSatMin, s.relaxedSatRamp,
            s.useDecontamination, s.decontaminationRadius);

        public static RecolorSettings From(IrocaPresetData p) => new RecolorSettings(
            p.edgeFeather, p.antiAliasCleanup,
            p.holeFillPasses, p.holeFillMinNeighbors,
            p.relaxedSatMin, p.relaxedSatRamp,
            p.useDecontamination,
            Mathf.Clamp(p.decontaminationRadius, MinDecontaminationRadius, MaxDecontaminationRadius));

        public void CopyTo(IrocaSessionState s)
        {
            s.edgeFeather = edgeFeather;
            s.antiAliasCleanup = antiAliasCleanup;
            s.holeFillPasses = holeFillPasses;
            s.holeFillMinNeighbors = holeFillMinNeighbors;
            s.relaxedSatMin = relaxedSatMin;
            s.relaxedSatRamp = relaxedSatRamp;
            s.useDecontamination = useDecontamination;
            s.decontaminationRadius = decontaminationRadius;
        }

        public void CopyTo(IrocaPresetData p)
        {
            p.edgeFeather = edgeFeather;
            p.antiAliasCleanup = antiAliasCleanup;
            p.holeFillPasses = holeFillPasses;
            p.holeFillMinNeighbors = holeFillMinNeighbors;
            p.relaxedSatMin = relaxedSatMin;
            p.relaxedSatRamp = relaxedSatRamp;
            p.useDecontamination = useDecontamination;
            p.decontaminationRadius = decontaminationRadius;
        }
    }
}
