// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;

namespace Iroca
{
    [Serializable]
    public class MaskZoneEntry
    {
        public string zoneId = "";
        public string maskBase64 = "";
    }

    [Serializable]
    public class MaskState
    {
        // スキーマバージョン。保存時に CurrentSchemaVersion を刻む。JsonUtility は欠落フィールドを
        // 型既定(0)にするため、本フィールド導入前の旧 JSON は schemaVersion=0 として読める。
        // 将来「旧版検出 → 警告/移行」の分岐点にできる(今回は刻印のみで分岐は未実装)。
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion;

        public int width;
        public int height;
        public string commonMaskBase64 = "";
        public List<MaskZoneEntry> zones = new List<MaskZoneEntry>();
    }
}
