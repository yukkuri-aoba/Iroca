// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 書き出し(「適用して保存」と自動化 API。0.4.0 の計画の 4-3、A3): 原本を元の解像度で読む → 色替え → PNG →
    /// 書き込みと取り込み。画素も PNG も Mono のヒープの外に置く(以前は管理配列で、8K で原本 256MB と PNG が載った。
    /// 広がった Mono のヒープは OS へ返らない)。PNG のバイト列は、管理配列の版(<see cref="ExportPipeline.EncodePng"/>。
    /// いまは比較画像などの小さい PNG に使う)と同じ(TextureIOTests・WindowOutputParityTests)。出力先などの決まりごとと、
    /// 原本を読む口(<see cref="ExportPipeline.ReadSource"/>。書き出しは元の解像度で読む)は <see cref="ExportPipeline"/>。
    /// </summary>
    internal static unsafe class ExportService {
        /// <summary>
        /// <paramref name="pixels"/> をその場で色替えする(背景のスレッドで呼んでよい)。有効なゾーンが無ければ何もしない。
        /// </summary>
        internal static void Recolor(NativePixels pixels, RecolorInput input, CancellationToken cancellationToken = default) {
            if (!input.HasZones) {
                return;
            }
            PixelProcessor.ProcessPixelsNative(pixels.Ptr, pixels.Width, pixels.Height,
                input.Masks, input.Zones, input.Settings, cancellationToken);
        }

        /// <summary>
        /// 画素を PNG にする(背景のスレッドで呼んでよい。<see cref="TextureIO.EncodePng"/>)。返す PNG はヒープの外にあり、
        /// 呼ぶ側が Dispose する。
        /// </summary>
        internal static NativeArray<byte> EncodePng(NativePixels pixels) {
            try { return TextureIO.EncodePng(pixels); } catch (System.InvalidOperationException) {
                throw new System.Exception(Localization.ExportPngEncodeEmpty);
            }
        }

        /// <summary>
        /// PNG を書き込み、Assets 配下なら取り込む(メインスレッド専用)。
        /// 書き込みはアトミック(書き潰す相手が元テクスチャそのものであり得るので、途中で落ちても原本を失わない)。
        /// 出力先のフォルダが無ければ作る。<paramref name="inheritImportSettings"/> なら取り込み前に元の .meta を写す。
        /// 戻り値は出力の Assets 相対パス(Assets の外へ書いたときは null)。
        /// </summary>
        internal static string WriteAndImport(string outputPath, NativeArray<byte> png, string srcPath, bool inheritImportSettings) {
            TextureIO.WriteAllBytes(outputPath, png);
            string rel = PathUtils.ToAssetsRelativeOrNull(outputPath);
            if (rel == null) {
                return null;
            }
            if (inheritImportSettings && !string.IsNullOrEmpty(srcPath)) {
                ExportPipeline.PreApplyImportSettings(srcPath, rel);
            }
            AssetDatabase.ImportAsset(rel);
            return rel;
        }
    }
}
