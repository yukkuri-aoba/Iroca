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
        public int holeFillPasses = 3;
        public int holeFillMinNeighbors = 4;
        public float relaxedSatMin = 0.02f;
        public float relaxedSatRamp = 0.08f;
    }
}
