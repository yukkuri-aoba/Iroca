// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca {
    /// <summary>
    /// 配布モデル(MobileSAM)の onnx ファイル名。パスを組む唯一の正。製品は MaskSuggestBridge(置き場の規則)から、
    /// ハーネスは推論の口(onnxruntime)から読む(ハーネスが取り込む Ops 配下に置く)。
    /// ダウンロード表(MaskSuggestModelDownload.Files)はリテラルのまま持ち、scripts/source_checks/test_model_file_names.py が
    /// ここと一致することを見る。
    /// </summary>
    internal static class SamModelFiles {
        internal const string EncoderFileName = "mobile_sam_encoder.onnx";
        internal const string DecoderFileName = "mobile_sam_decoder.onnx";
    }
}
