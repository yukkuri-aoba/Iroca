// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;

namespace Iroca {
    [Serializable]
    public class MaskZoneEntry {
        public string zoneId = "";
        public string maskBase64 = "";
    }

    /// <summary>
    /// 範囲の操作 1 回(右クリックの「ここも塗る / ここは塗らない / この部分だけ塗る」)が、1 つの層で新しく立てた画素。
    /// 「ここのマスクを消す」が、入れたときの範囲をそっくり消すのに使う。色替えの結果には関わらない。
    /// </summary>
    [Serializable]
    public class MaskPatchEntry {
        // 層のゾーン。空 = 全ゾーン共通の除外。
        public string zoneId = "";
        // 含めるの層か(false = 除外)。
        public bool include;
        // 塗った画素(マスクの層と同じ書き方・同じ寸法)。
        public string maskBase64 = "";
    }

    [Serializable]
    public class MaskState {
        // スキーマバージョン。保存時に CurrentSchemaVersion を刻む。JsonUtility は欠落フィールドを
        // 型既定(0)にするため、本フィールド導入前の旧 JSON は schemaVersion=0 として読める。
        // 将来「旧版検出 → 警告/移行」の分岐点にできる(今回は刻印のみで分岐は未実装)。
        // v2: zoneIncludes(含めるマスク)を追加。旧 JSON は欠落フィールド=空リストとして読める。
        // 旧バージョンの Iroca は zoneIncludes を無視して読む(含めるマスクだけが落ちる)。
        // v3: patches(範囲の操作が塗った画素の記録)を追加。旧 JSON は欠落フィールド=空リスト(記録なし)として読める。
        // 旧バージョンの Iroca は patches を無視して読む(マスクはそのまま。記録だけが落ちる)。
        public const int CurrentSchemaVersion = 3;
        public int schemaVersion;

        public int width;
        public int height;
        public string commonMaskBase64 = "";
        public List<MaskZoneEntry> zones = new List<MaskZoneEntry>();
        // ゾーン別「含める」マスク(強制的に色替えへ含める領域)。共通版は存在しない。
        public List<MaskZoneEntry> zoneIncludes = new List<MaskZoneEntry>();
        // 範囲の操作が塗った画素の記録(古い順)。塗った画素が残っている操作の分だけを持つ。
        public List<MaskPatchEntry> patches = new List<MaskPatchEntry>();
    }
}
