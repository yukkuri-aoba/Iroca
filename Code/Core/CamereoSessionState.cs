// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Camereo
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;

namespace Camereo
{
    /// <summary>
    /// 編集 UI の表示レベル。Simple=色＋おおまかな調整のみ（色変更で自動調整）、
    /// Normal=従来の標準的な調整項目（自動実行なし）、Advanced=内部パラメータまで全表示。
    /// </summary>
    internal enum EditMode { Simple, Normal, Advanced }

    /// <summary>
    /// 編集状態の永続表現。ゾーン定義・処理パラメータ・マスク状態をまとめて保持し、
    /// EditorWindow に [SerializeField] で持たせることで Unity 標準の SerializedObject /
    /// Undo に乗せる。GUI・ファイル I/O・AssetDatabase に依存しない純粋データ。
    /// 既定値は現行 CamereoWindow にハードコードされていた値を 1:1 で移管したもので、
    /// プリセット未読込時の唯一の基準値となる。
    /// </summary>
    [Serializable]
    internal class CamereoSessionState
    {
        public List<ColorZone> zones = new List<ColorZone>();
        public float edgeFeather = 0f;
        public int antiAliasCleanup = 3;
        public bool useDecontamination = true;
        public int decontaminationRadius = 4;
        // かんたんモード（Simple）と自動調整はまだ実用段階でないため UI から隠している（2026-06 一時対応）。
        // 既定を通常モードにする。再有効化するときは Simple に戻し、CamereoWindow 側のコメントアウト
        // （DrawModeToggle のモード選択・Auto Tune ボタン・ScheduleAutoTune/ProcessPendingAutoTune）も解除する。
        public EditMode editMode = EditMode.Normal;
        public int holeFillPasses = 5;
        public int holeFillMinNeighbors = 4;
        public float relaxedSatMin = 0.02f;
        public float relaxedSatRamp = 0.08f;
        public MaskState maskState = new MaskState();

        public static CamereoSessionState CreateDefault() => new CamereoSessionState();
    }
}
