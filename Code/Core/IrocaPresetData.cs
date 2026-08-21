// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;

namespace Iroca
{
    [Serializable]
    public class IrocaPresetData
    {
        // スキーマバージョン。保存時に CurrentSchemaVersion を刻む。JsonUtility は欠落フィールドを
        // 型既定(0)にするため、本フィールド導入前の旧 JSON は schemaVersion=0 として読める。
        // 将来「旧版検出 → 警告/移行」の分岐点にできる(今回は刻印のみで分岐は未実装)。
        // v2: zoneIncludeMasks(含めるマスク)を追加。旧 JSON は欠落フィールド=空リストとして読める。
        public const int CurrentSchemaVersion = 2;
        public int schemaVersion;

        public string name = "";
        public List<ColorZone> zones = new List<ColorZone>();
        public float edgeFeather;

        public bool advancedMode;
        public int antiAliasCleanup = 3;
        public int holeFillPasses = 5;
        public int holeFillMinNeighbors = 4;
        public float relaxedSatMin = 0.02f;
        public float relaxedSatRamp = 0.08f;
        public bool useDecontamination = true;
        public int decontaminationRadius = 4;

        // マスク配列の解像度。0 の場合はマスク情報なし。
        public int maskWidth;
        public int maskHeight;
        // 共通マスク（bitpack + Base64）。空文字列ならマスクなし。
        public string commonMaskBase64 = "";
        // ゾーン別マスク。全 false のゾーンはシリアライズ省略。
        public List<ZoneMaskEntry> zoneMasks = new List<ZoneMaskEntry>();
        // ゾーン別「含める」マスク(強制的に色替えへ含める領域)。全 false のゾーンは省略。
        public List<ZoneMaskEntry> zoneIncludeMasks = new List<ZoneMaskEntry>();
    }

    [Serializable]
    public class ZoneMaskEntry
    {
        public string zoneId = "";
        public string maskBase64 = "";
    }
}
