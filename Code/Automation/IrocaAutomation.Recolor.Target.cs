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
    // iroca_recolor のうち、対象の解決(登録できるか・原本の読み方・書き出し先)。
    // 入口(Recolor・RecolorStatus)と依頼の寿命は IrocaAutomation.Recolor.cs。
    public static partial class IrocaAutomation {
        // EditMode テストだけが立てる。NDMF の無いホストでも登録の経路（レシピ・コンポーネント・Undo）を確かめられるよう、
        // NDMF の判定だけを「使える」とみなす。製品のコードからは立てない。
        internal static bool AssumeNdmfForTests;

        private static NdmfSupportState NdmfStateForApply =>
            AssumeNdmfForTests ? NdmfSupportState.Supported : NdmfSupport.State;

        /// <summary>
        /// apply='avatar'(アバターへの登録)ができない理由。できるなら null。
        /// Play 中に付けたコンポーネントは Play を止めると消え、書き出し画像から元へ戻したマテリアル(アセット)だけが
        /// 残る。NDMF が使えないと、登録しても色替えされず、アップロードはいろかの安全装置が止める(窓の登録は
        /// 警告を出すが、AI への結果には出せない)。どちらも登録しないで断り、できることを案内する。
        /// </summary>
        internal static string AvatarApplyBlocked(bool playing, NdmfSupportState ndmf) {
            if (playing) {
                return "apply='avatar' does not work in Play mode (a registration made now is lost when Play stops). "
                    + "Ask the user to stop Play mode, then call again. apply='preview' and 'png' work in Play mode.";
            }
            if (ndmf == NdmfSupportState.Supported) {
                return null;
            }
            string reason = ndmf == NdmfSupportState.TooOld ? "the installed NDMF is too old for this Iroca"
                : ndmf == NdmfSupportState.TooNew ? "the installed NDMF is newer than this Iroca supports"
                : "NDMF is not installed";
            return $"apply='avatar' needs NDMF to recolor the avatar non-destructively, but {reason} "
                + "(the recolor would not show, and the upload would be stopped). Use apply='png' to write a recolored "
                + "image instead, or ask the user to install a supported NDMF (the Iroca installer adds it).";
        }

        /// <summary>
        /// 作業解像度の原本を読む(<see cref="ExportPipeline.ReadSource"/>。参照 1 で返すので呼んだ側が Release する)。
        /// いろかの窓が同じ元ファイルを読んだ画素を持っていて、読んだ後にファイルが変わっていなければ、デコードし直さずに
        /// それを共有する(同じデコーダ・同じ作業解像度なので同じ画素。呼ぶたびにメインスレッドで 4K を読み直していた)。
        /// 共有した画素は窓のプレビューも読んでいるので書き換えない(色替えは写してから。<see cref="Compare"/>)。
        /// ファイルを開けなければ Locked で、<paramref name="openError"/> に OS の理由(<see cref="UnreadablePixels"/> で文にする)。
        /// </summary>
        private static ExportPipeline.SourceKind ReadWorkingPixels(string abs, Texture2D tex, out NativePixels pixels,
            out string openError) {
            openError = null;
            pixels = EditingWindows.Current?.ShareSourcePixels(tex);
            if (pixels != null) {
                return ExportPipeline.SourceKind.File;
            }
            return ExportPipeline.ReadSource(abs, tex, workingSize: true, out pixels, out openError);
        }

        // 画素を読めなかった理由(依頼した側へ返す文)。開けなかったときは、取り込み済みテクスチャ(縮小・圧縮済み)では代えない。
        private static string UnreadablePixels(ExportPipeline.SourceKind kind, string openError) =>
            kind == ExportPipeline.SourceKind.Locked
                ? $"could not open the source file ({openError}). Another app may have it open, or it may be an online-only "
                    + "file of a sync service. Try again once the file can be opened."
                : "could not read the pixels (enable Read/Write in the import settings, or use a PNG/JPG source).";

        /// <summary>
        /// 作業解像度で読んだ画素(<see cref="ExportPipeline.ReadSource"/> の workingSize)が、書き出しで読む
        /// 元の解像度の画素と同じになるか。取り込み済みテクスチャから読んだとき(PSD・TGA など)は
        /// どちらも同じ画素を読む。ファイルから読んだときは、寸法が元のファイルと同じなら縮めていない
        /// (<see cref="WorkingResolution.NativePixelsOf"/> は寸法が同じならデコードした画素をそのまま写す)。
        /// </summary>
        internal static bool IsFullResolution(ExportPipeline.SourceKind kind, Texture2D tex, int w, int h) {
            if (kind == ExportPipeline.SourceKind.ImportedTexture) {
                return true;
            }
            if (kind != ExportPipeline.SourceKind.File) {
                return false;
            }
            if (!(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) is TextureImporter ti)) {
                return false;
            }
            ti.GetSourceTextureWidthAndHeight(out int fw, out int fh);
            return fw == w && fh == h;
        }

        /// <summary>
        /// apply='png' の出力先を決めて確かめる(決まりは <see cref="AutomationOutputRules.ResolvePngOutput"/>。静的 API と共有)。
        /// 既定の出力先は「適用して保存」の既定と同じ名前。エラーなら文言、よければ null と出力先の絶対パス。
        /// </summary>
        private static string ResolvePngOutput(RecolorRequest req, string texturePath, out string outAbs)
            => AutomationOutputRules.ResolvePngOutput(req.output, ExportPipeline.BatchOutputPath(texturePath), req.overwrite,
                PhysicalPath(texturePath), "overwrite:true", out outAbs);
    }
}
