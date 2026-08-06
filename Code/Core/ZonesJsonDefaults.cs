// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca
{
    /// <summary>
    /// zones JSON スキーマ（"zones" / "settings" のフラット形式）の既定値の唯一の正。
    ///
    /// このスキーマには DTO が 2 つある:
    ///   1. Code/Automation/IrocaAutomation.cs の ZoneDto / SettingsDto
    ///      … 製品の入口（MCP・batchmode）。UnityEngine.JsonUtility でデシリアライズするため public フィールド。
    ///   2. scripts/headless-run/Harness.cs の ZoneCfg / SettingsCfg
    ///      … 検証ハーネスの入口。System.Text.Json でデシリアライズするため public プロパティ。
    ///
    /// **両者が既定値を各自リテラルで持っていたため乖離した。** 2026-08-06 時点で
    /// useFloodFill が製品 true / ハーネス false になっており、zones JSON がこのフィールドを
    /// 明示しないケースでは「テストが測る挙動」と「製品の挙動」が別物になっていた
    /// （tools/visual_review.py は 7 フィールドしか書かないため、出荷ゲートの視覚レビューまで
    /// 非製品既定で生成されていた）。Python プロキシが製品から乖離した過去の事故と同型で、
    /// 「実 C# を測る」という大原則の穴になる。
    ///
    /// したがって **既定値リテラルをここ以外に書かないこと。** 両 DTO はこのクラスを参照し、
    /// 一致は dev_safe/Tests/regression/test_zones_schema_parity.py が機械検査する。
    ///
    /// なお ColorZone のフィールド既定値とは意図的に異なるものがある（例: tolerance は
    /// ColorZone が 0＝未設定センチネル、zones JSON は 0.2＝バッチで使える値）。
    /// ここは「JSON スキーマの既定」であって「ColorZone の既定」ではない。
    /// </summary>
    internal static class ZonesJsonDefaults
    {
        // ───────── zone ─────────
        public const string Name = "Zone";
        // sample は白、target は黒。配列は DTO ごとに新しい実体が要る（共有すると
        // 1 ゾーンへの書き込みが全ゾーンに波及する）ので、定数ではなくファクトリで返す。
        public const float SampleR = 1f, SampleG = 1f, SampleB = 1f;
        public const float TargetR = 0f, TargetG = 0f, TargetB = 0f;

        public const float Tolerance = 0.2f;
        public const float ValueBlend = 1.0f;
        public const float EdgeSoftness = 0.0f;
        public const float SaturationStrictness = 0.5f;
        public const float SaturationGuard = 0.0f;
        public const float ChromaThreshold = 0.05f;
        public const float ShadowDesaturation = 0.35f;
        public const float ShadowForgivenessSatMin = 0.05f;
        public const float OutputSaturation = 1.0f;
        public const bool HighlightRecovery = false;
        public const bool HighlightBandExpand = true;
        public const bool ApplyHighlightWash = false;
        // 既定 ON: 影をスポイトしても出力が過度に明るく/ベタ塗りにならないよう、再着色アンカーを
        // 領域の代表地色から自動推定する（ColorZone.autoRecolorAnchor と同既定）。
        public const bool AutoRecolorAnchor = true;
        public const int LayerIndex = 0;
        // 連続領域モード（連結成分アンカリング）。既定 ON＝製品 UI の標準および
        // ColorZone.useFloodFill と一致。自動アンカリング（シード非依存）なのでバッチでも安全。
        // 従来どおり絞り込みたくない場合は JSON で false を明示する。
        public const bool UseFloodFill = true;

        // ───────── settings ─────────
        public const float EdgeFeather = 0.0f;
        public const int AntiAliasCleanup = 3;
        public const int HoleFillPasses = 5;
        public const int HoleFillMinNeighbors = 4;
        public const float RelaxedSatMin = 0.02f;
        public const float RelaxedSatRamp = 0.08f;
        public const bool UseDecontamination = true;
        public const int DecontaminationRadius = 4;

        /// <summary>既定のサンプル色 [r,g,b]。呼び出しごとに新しい配列を返す。</summary>
        public static float[] NewSample() { return new float[] { SampleR, SampleG, SampleB }; }

        /// <summary>既定のターゲット色 [r,g,b]。呼び出しごとに新しい配列を返す。</summary>
        public static float[] NewTarget() { return new float[] { TargetR, TargetG, TargetB }; }
    }
}
