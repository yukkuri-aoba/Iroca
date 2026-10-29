// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// 書き出し(「適用して保存」・一括書き出し。0.4.0 の計画の 4-3、A3): 原本を元の解像度で読む → 色替え → PNG →
    /// 書き込みと取り込み。画素も PNG も Mono のヒープの外に置く(以前は管理配列で、8K で原本 256MB と PNG が載った。
    /// 広がった Mono のヒープは OS へ返らない)。読み方・PNG のバイト列・書き込み方は管理配列の経路
    /// (<see cref="ExportPipeline"/>)と同じ(TextureIOTests・WindowOutputParityTests)。
    /// </summary>
    internal static unsafe class ExportService
    {
        /// <summary>
        /// 書き出しの原本を元の解像度で読む(メインスレッド専用。参照 1 で返すので、呼ぶ側が Release する)。
        /// PNG/JPG はファイルを直接デコードするので取り込み設定(Max Size・圧縮・Read/Write)の影響を受けない。
        /// PSD/TGA/EXR 等は Unity が取り込み時に変換しているので LoadImage で読めず、取り込み済みテクスチャの画素を使う
        /// (縮小・圧縮を受けている)。どちらも読めなければ <see cref="ExportPipeline.SourceKind.Unavailable"/> で、画素は null。
        /// </summary>
        internal static ExportPipeline.SourceKind ReadSource(string srcPath, Texture2D imported, out NativePixels pixels)
        {
            pixels = TextureIO.ReadSourceFile(srcPath, imported, workingSize: false);
            if (pixels != null) return ExportPipeline.SourceKind.File;
            if (!TextureIO.IsReadable(imported)) return ExportPipeline.SourceKind.Unavailable;
            // 取り込み済みテクスチャの寸法のまま(GetPixels32 と同じ値。非圧縮の形式は管理配列を経由しない)。
            pixels = WorkingResolution.NativePixelsOf(imported, null);
            return ExportPipeline.SourceKind.ImportedTexture;
        }

        /// <summary>
        /// <paramref name="pixels"/> をその場で色替えする(背景のスレッドで呼んでよい)。有効なゾーンが無ければ何もしない。
        /// </summary>
        internal static void Recolor(NativePixels pixels, RecolorInput input, CancellationToken cancellationToken = default)
        {
            if (!input.HasZones) return;
            PixelProcessor.ProcessPixelsNative(pixels.Ptr, pixels.Width, pixels.Height,
                input.Masks, input.Zones, input.Settings, cancellationToken);
        }

        /// <summary>
        /// 画素を PNG にする(背景のスレッドで呼んでよい。<see cref="TextureIO.EncodePng"/>)。返す PNG はヒープの外にあり、
        /// 呼ぶ側が Dispose する。
        /// </summary>
        internal static NativeArray<byte> EncodePng(NativePixels pixels)
        {
            try { return TextureIO.EncodePng(pixels); }
            catch (System.InvalidOperationException)
            {
                throw new System.Exception(Localization.ExportPngEncodeEmpty);
            }
        }

        /// <summary>
        /// PNG を書き込み、Assets 配下なら取り込む(メインスレッド専用)。
        /// 書き込みはアトミック(書き潰す相手が元テクスチャそのものであり得るので、途中で落ちても原本を失わない)。
        /// 出力先のフォルダが無ければ作る。<paramref name="inheritImportSettings"/> なら取り込み前に元の .meta を写す。
        /// 戻り値は出力の Assets 相対パス(Assets の外へ書いたときは null)。
        /// </summary>
        internal static string WriteAndImport(string outputPath, NativeArray<byte> png, string srcPath, bool inheritImportSettings)
        {
            TextureIO.WriteAllBytes(outputPath, png);
            string rel = PathUtils.ToAssetsRelativeOrNull(outputPath);
            if (rel == null) return null;
            if (inheritImportSettings && !string.IsNullOrEmpty(srcPath))
                ExportPipeline.PreApplyImportSettings(srcPath, rel);
            AssetDatabase.ImportAsset(rel);
            return rel;
        }
    }
}
