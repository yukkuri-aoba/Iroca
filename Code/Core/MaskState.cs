using System;
using System.Collections.Generic;

namespace VRCAvatarColorChanger
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
