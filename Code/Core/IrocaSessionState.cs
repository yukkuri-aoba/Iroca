// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;

namespace Iroca
{
    /// <summary>
    /// 旧・編集 UI の表示レベル（Simple=かんたん / Normal=通常 / Advanced=上級）。
    /// モード切替 UI は 2026-09 に廃止し、既定は Normal。値はセッションとレシピの JSON に
    /// JsonUtility（enum は整数）で残るので、並び順と値を変えない。
    /// 現在 Simple を見るのは IrocaWindow.ConfirmAutoTuneOverwriteIfNeeded（上書き確認の省略）だけで、
    /// Advanced は参照なし。
    /// </summary>
    internal enum EditMode { Simple, Normal, Advanced }

    /// <summary>
    /// 編集状態の永続表現。ゾーン定義・処理パラメータ・マスク状態をまとめて保持し、
    /// EditorWindow に [SerializeField] で持たせることで Unity 標準の SerializedObject /
    /// Undo に乗せる。GUI・ファイル I/O・AssetDatabase に依存しない純粋データ。
    /// 処理パラメータの既定値は RecolorSettings の定数を参照する（プリセット・zones JSON と同じ値）。
    /// </summary>
    [Serializable]
    internal class IrocaSessionState
    {
        public List<ColorZone> zones = new List<ColorZone>();
        public float edgeFeather = RecolorSettings.DefaultEdgeFeather;
        public int antiAliasCleanup = RecolorSettings.DefaultAntiAliasCleanup;
        public bool useDecontamination = RecolorSettings.DefaultUseDecontamination;
        public int decontaminationRadius = RecolorSettings.DefaultDecontaminationRadius;
        // モード切替 UI は廃止済み（EditMode の doc 参照）。既定は通常モード（Normal）。
        // 古いセッション・レシピの値を読めるよう、フィールドは保存形式の互換のために残す。
        public EditMode editMode = EditMode.Normal;
        public int holeFillPasses = RecolorSettings.DefaultHoleFillPasses;
        public int holeFillMinNeighbors = RecolorSettings.DefaultHoleFillMinNeighbors;
        public float relaxedSatMin = RecolorSettings.DefaultRelaxedSatMin;
        public float relaxedSatRamp = RecolorSettings.DefaultRelaxedSatRamp;
        public MaskState maskState = new MaskState();

        public static IrocaSessionState CreateDefault() => new IrocaSessionState();
    }
}
