// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Camereo
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;

namespace Camereo
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
        public int width;
        public int height;
        public string commonMaskBase64 = "";
        public List<MaskZoneEntry> zones = new List<MaskZoneEntry>();
    }
}
