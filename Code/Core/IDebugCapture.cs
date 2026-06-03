// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/VRC_AvatarColorChanger
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace VRCAvatarColorChanger
{
    /// <summary>
    /// パイプライン透明化（診断/デバッグ）機能の本体側インターフェース。
    /// 実装は独立 asmdef (VACCEditor.Debug) 側にあり、本体は型として存在を知らない。
    /// <see cref="DebugCaptureHooks.Factory"/> が null（= Code.Debug/ 未導入 or トグル OFF）の
    /// とき、本体パイプラインは何もキャプチャしないし、何も呼び出さない。
    /// </summary>
    internal interface IDebugCapture
    {
        /// <summary>
        /// ピクセル幅・高さを最初に通知する（capture 対象の解像度を確定）。
        /// </summary>
        void BeginCapture(int width, int height);

        /// <summary>
        /// 各ステップ完了直後の strength マップを記録する。
        /// 呼び出し側は内部でクローン化するため、配列は引き続き利用してよい。
        /// </summary>
        void RecordStage(string zoneId, string stageName, float[] strength, int width, int height);

        /// <summary>
        /// AA 境界 decontamination で α 分解された aaMask を記録する。
        /// </summary>
        void RecordDecontamination(string zoneId, bool[] aaMask, int width, int height);

        /// <summary>
        /// Recolor 段で各ピクセルに適用されたサブブランチを記録する。
        /// <see cref="DebugBranch"/> の値が格納された byte[] を受け取る。
        /// </summary>
        void RecordRecolorBranches(string zoneId, byte[] branchMap, int width, int height);
    }

    /// <summary>
    /// パイプラインの段階識別子。マジック文字列散在を防ぐ。
    /// 表示順は <see cref="DebugStages.OrderedAll"/> を参照。
    /// </summary>
    internal static class DebugStages
    {
        public const string Match = "Match";
        public const string HighlightPropagate = "HighlightPropagate";
        public const string FloodFill = "FloodFill";
        public const string HoleFill = "HoleFill";
        public const string BoundaryRecover = "BoundaryRecover";
        public const string Blur = "Blur";
        public const string MaskReapply = "MaskReapply";
        public const string Decontaminate = "Decontaminate";
        public const string Recolor = "Recolor";

        public static readonly string[] OrderedAll =
        {
            Match,
            HighlightPropagate,
            FloodFill,
            HoleFill,
            BoundaryRecover,
            Blur,
            MaskReapply,
            Decontaminate,
            Recolor,
        };
    }

    /// <summary>
    /// Recolor 段でピクセルに適用されたサブブランチ。byte 値として byte[] に格納する。
    /// </summary>
    internal enum DebugBranch : byte
    {
        None = 0,
        Base = 1,
        Highlight = 2,
        Shadow = 3,
        Decontaminate = 4,
    }
}
