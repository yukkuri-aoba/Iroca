// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    // iroca_recolor のうち、依頼と結果の型(JSON の形)と、apply の値。
    // 入口(Recolor・RecolorStatus)と依頼の寿命は IrocaAutomation.Recolor.cs。
    public static partial class IrocaAutomation {
        /// <summary><see cref="Recolor"/> の依頼(JSON)。</summary>
        [Serializable]
        internal class RecolorRequest {
            // テクスチャかマテリアル(メインテクスチャ)のアセットパス
            public string texture = "";
            public List<ChangeRequest> changes = new List<ChangeRequest>();
            // 右クリックと同じマスク操作(changes の後・自動調整の前に、順に当てる)
            public List<MaskRequest> masks = new List<MaskRequest>();
            // 消すゾーンの名前
            public List<string> remove = new List<string>();
            // true なら、今あるゾーンとマスクを捨ててから changes を足す
            public bool reset;
            // 土台にするプリセット(ファイルパス・名前・インライン JSON)。指定すると今の編集内容の代わりにこれを土台にする
            public string preset = "";
            // "avatar"(アバターに非破壊で登録) | "png"(画像に書き出す) | "preview"(何も保存せず比較画像だけ)
            public string apply = ApplyAvatar;
            // 登録先のシーンのオブジェクト(既定: そのテクスチャを使っているレンダラーの共通の親)
            public string avatar = "";
            // png の出力先(Assets/ の下の .png。既定: 元と同じフォルダの <名前>_recolored.png)
            public string output = "";
            // true なら、png の出力先に既にあるファイルを置き換える(既定は断る)
            public bool overwrite;
            // true(既定)なら、changes で作った・置き換えたゾーンに UI と同じ自動調整をかける
            public bool autoTune = true;
        }

        [Serializable]
        internal class ChangeRequest {
            // ゾーンの名前。今あるゾーンと同じ名前なら、そのゾーンを置き換える(頼み直し)
            public string name = "";
            // スポイトする位置 [u, v](左下原点、0..1)。iroca_inspect の uv をそのまま渡せる
            public float[] at;
            // at が無いときの、変えたい色("#RRGGBB"。Unity の色名 "navy" なども読める)
            public string from = "";
            // 変更先の色("#RRGGBB"。Unity の色名 "navy" なども読める)
            public string to = "";
        }

        /// <summary>プレビューの右クリックと同じマスク操作 1 件の依頼。</summary>
        [Serializable]
        internal class MaskRequest {
            // "paint"(ここも塗る) | "dont_paint"(ここは塗らない) | "only"(この部分だけ塗る) | "clear"(ここのマスクを消す)
            public string op = "";
            // 範囲を決める位置 [u, v](左下原点、0..1)
            public float[] at;
            // 当てるゾーンの名前。ゾーンが 1 つなら省略できる
            public string zone = "";
            // true なら全ゾーン共通(zone の代わり。dont_paint と clear だけ)
            public bool common;
            // 範囲の決め方: "ai"(AI が判定した部分) | "island"(メッシュの UV の島) | "other_meshes"(そのメッシュ以外の全部。dont_paint だけ)
            // | "mask"(その場所のマスクを入れた操作の範囲。clear だけ。AI もメッシュも使わない)。既定は clear なら "mask"、ほかは "ai"
            public string by = "";
            // by=ai の部分の大きさ: "auto"(既定) | "fine"(小さめ) | "coarse"(大きめ)
            public string size = "";
        }

        /// <summary>当てたマスク操作の結果。</summary>
        [Serializable]
        internal class MaskEditInfo {
            public string zone = "";       // 当てたゾーン(全ゾーン共通なら空)
            public string op = "";
            public string by = "";
            public float[] at;
            public float regionFraction;   // 範囲が画像全体に占める割合
            public int changedPixels;      // 書き換えた画素数(含める・除外を合わせた延べ)。0 = 何も変わっていない
        }

        [Serializable]
        internal class RecolorTextureResult {
            public bool ok;
            public string error = "";
            public string texture = "";
            public string apply = "";
            public string avatar = "";      // 登録したオブジェクト(階層のパス)
            public string recipe = "";      // 書いたレシピ
            public string output = "";      // 書き出した PNG
            public string preview = "";     // 比較画像(左=前 / 中=後 / 右=変わった画素をマゼンタ)
            public List<ZoneInfo> zones = new List<ZoneInfo>();
            public List<MaskEditInfo> maskEdits = new List<MaskEditInfo>();
            public float changedFraction;
            public float largestComponentFraction;
            public int changeComponentCount;
            public List<string> warnings = new List<string>();
            public string note = "";
        }

        [Serializable]
        internal class ZoneInfo {
            public string name = "";
            public string from = "";
            public string to = "";
            public float[] at;
            public float tolerance;
            public bool enabled;
            // 自動調整のかけ方(AutoTuneService.Run.Method: "evidence" / "conventional" / "conventional (理由)" か、
            // "skipped: 理由")。AI の証拠を渡しても使えずに従来の導出へ戻ったら "conventional (evidence not usable: 理由)"。
            // この依頼で調整しなかったゾーンは空
            public string autoTune = "";
            // このゾーンのマスクが画像全体に占める割合(除外・含める)
            public float excludedFraction;
            public float includedFraction;
            // iroca_inspect の coverage・points のときだけ: 不透明な画素のうち、このゾーンが塗るおおよその割合。測っていなければ -1
            public float approxCoverage = -1f;
            // ゾーンの id(結果には出さない)。同じ名前のゾーンが複数あっても、割合と自動調整の報告を正しいゾーンに付けるための突き合わせ用。
            [NonSerialized] internal string zoneId;
        }

        /// <summary>自動調整を待っている間の途中経過(<see cref="Recolor"/> / <see cref="RecolorStatus"/> が返す)。</summary>
        [Serializable]
        internal class RecolorPending {
            public bool ok = true;
            public bool pending = true;
            public string message = "";
            public int tunedZones;
            public int zonesToTune;
        }

        internal const string ApplyAvatar = "avatar";
        internal const string ApplyPng = "png";
        internal const string ApplyPreview = "preview";
    }
}
