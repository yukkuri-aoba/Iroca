using System;
using System.Collections.Generic;

namespace VRCAvatarColorChanger
{
    [Serializable]
    public class VACCPresetData
    {
        public string name = "";
        public List<ColorZone> zones = new List<ColorZone>();
        public float edgeFeather;

        // アドバンスモード設定
        public bool advancedMode;
        public int antiAliasCleanup = 3;
        public int holeFillPasses = 5;
        public int holeFillMinNeighbors = 4;
        public float relaxedSatMin = 0.02f;
        public float relaxedSatRamp = 0.08f;
        public bool useDecontamination = true;
        public int decontaminationRadius = 4;

        // ─── マスク同梱（任意） ───
        // マスク配列の解像度。0 の場合はマスク情報なし。
        public int maskWidth;
        public int maskHeight;
        // 共通マスク（bitpack + Base64）。空文字列ならマスクなし。
        public string commonMaskBase64 = "";
        // ゾーン別マスク。全 false のゾーンはシリアライズ省略。
        public List<ZoneMaskEntry> zoneMasks = new List<ZoneMaskEntry>();
    }

    [Serializable]
    public class ZoneMaskEntry
    {
        public string zoneId = "";
        public string maskBase64 = "";
    }
}
