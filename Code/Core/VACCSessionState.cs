using System;
using System.Collections.Generic;

namespace VRCAvatarColorChanger
{
    /// <summary>
    /// 編集状態の永続表現。ゾーン定義・処理パラメータ・マスク状態をまとめて保持し、
    /// EditorWindow に [SerializeField] で持たせることで Unity 標準の SerializedObject /
    /// Undo に乗せる。GUI・ファイル I/O・AssetDatabase に依存しない純粋データ。
    /// 既定値は現行 VACCWindow にハードコードされていた値を 1:1 で移管したもので、
    /// プリセット未読込時の唯一の基準値となる。
    /// </summary>
    [Serializable]
    internal class VACCSessionState
    {
        public List<ColorZone> zones = new List<ColorZone>();
        public float edgeFeather = 0f;
        public int antiAliasCleanup = 3;
        public bool useDecontamination = true;
        public int decontaminationRadius = 4;
        public bool advancedMode;
        public int holeFillPasses = 5;
        public int holeFillMinNeighbors = 4;
        public float relaxedSatMin = 0.02f;
        public float relaxedSatRamp = 0.08f;
        public MaskState maskState = new MaskState();

        public static VACCSessionState CreateDefault() => new VACCSessionState();
    }
}
