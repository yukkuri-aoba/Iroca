// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// AI の提案の範囲を、マスクの寸法の範囲にする(画素だけで済む部分。エディタの側の入口は Application の ProposalMaskRegion で、
    /// テクスチャから画素を読む所・計算の区間の数え・時間の記録はそちらが持つ。ハーネスもここを呼ぶ)。
    /// </summary>
    internal static unsafe class ProposalMaskTransfer {
        /// <summary>段の境目(計測の観察者へ渡す)。</summary>
        internal enum Stage {
            /// <summary>被覆を保って写し終えた。</summary>
            Transferred,
            /// <summary>AA 包含に使う画素を読み終えた。</summary>
            PixelsRead,
            /// <summary>AA 包含と細い描き込みの地はがしを終えた。</summary>
            AaDone,
        }

        /// <summary>
        /// 提案(<paramref name="src"/>)をマスクの寸法 mw×mh の範囲にする。同じ寸法なら提案をそのまま返す(owned = false)。
        /// 違えば自分で作って返す(owned = true。呼び出し側が Release する)。
        /// <para>
        /// マスク解像度がソースと異なる場合(実ファイル解像度で仕上げた提案 → インポート解像度のマスクキャンバス等)は
        /// 被覆保存で転写する。旧実装の最近傍サンプリングは縮小時に仕上げ済み境界の被覆を 1 セル単位で欠けさせ、
        /// 取り残し画素が再着色されて境界の点ノイズになっていた(実測: 強ドット 177個 → 被覆保存で 0)。さらにインポート縮小は
        /// マスク解像度側に新たな混合画素を作るため、提案の寄与分だけを対象に AA 遷移包含をマスク解像度で再適用する
        /// (ユーザーの既存ストロークには触れない)。
        /// </para>
        /// <para>
        /// AA 包含をやり直すのは、マスクの寸法の画素があるとき(<paramref name="pixelsWidth"/> × <paramref name="pixelsHeight"/> が
        /// mw×mh で、<paramref name="readPixels"/> がある)だけ。画素は要るときにだけ読む(読んだ画素は参照 1 で受け取り、ここで返す)。
        /// 読めない(投げた)ときは、被覆保存の転写だけで返す。<paramref name="mark"/> は段の境目の観察者(計測用。null なら呼ばない)。
        /// </para>
        /// </summary>
        public static NativeBools ToMaskSize(NativeBools src, int mw, int mh, int pixelsWidth, int pixelsHeight,
            Func<NativePixels> readPixels, out bool owned, Action<Stage> mark = null) {
            owned = false;
            if (mw == src.Width && mh == src.Height) {
                return src;
            }
            var region = NativeBools.AllocateUninitialized(mw, mh);
            owned = true;
            SamMaskRefine.TransferCoverage(src.Ptr, src.Width, src.Height, region.Ptr, mw, mh);
            mark?.Invoke(Stage.Transferred);
            if (readPixels != null && pixelsWidth == mw && pixelsHeight == mh &&
                SamMaskRefine.TryDeriveAaCropRect(region.Ptr, mw, mh,
                    out int rx0, out int ry0, out int rw, out int rh, out int aaD)) {
                // AA 包含は提案 bbox + マージンのクロップで実行する(出力は全画像実行と
                // ビット同一 — 根拠は TryDeriveAaCropRect)。小パーツ提案でもマスク全
                // 解像度の距離変換×最大 10 回が走っていた(実測 162-225ms のメイン停止)
                // のを、画素取得ごとクロップ分に抑える。提案が空なら丸ごとスキップ。
                NativePixels texPx = null, cropPx = null;
                try {
                    // 画素はテクスチャの全面を GetPixels32 と同じ値で取り(GetPixels(rect) の float→byte 丸めは
                    // テクスチャ形式によって GetPixels32 と一致する保証がない)、
                    // クロップは行コピーで切り出す。
                    texPx = readPixels();
                    Color32* cp = texPx.Ptr;
                    if (rw != mw || rh != mh) {
                        cropPx = NativePixels.Allocate(rw, rh);
                        long rowBytes = (long)rw * sizeof(Color32);
                        for (int cy = 0; cy < rh; cy++) {
                            System.Buffer.MemoryCopy(texPx.Ptr + (long)(ry0 + cy) * mw + rx0,
                                cropPx.Ptr + (long)cy * rw, rowBytes, rowBytes);
                        }
                        cp = cropPx.Ptr;
                    }
                    mark?.Invoke(Stage.PixelsRead);
                    SamMaskRefine.IncludeAaTransitionCropped(region.Ptr, mw, mh,
                        cp, rx0, ry0, rw, rh, aaD);
                    // 細い描き込みの提案は、やり直した AA 包含が落ち影を「混色」として足し直すので、
                    // 精密化と同じく最後に地を外す(細い成分が無ければ外接矩形の距離変換 1 回で戻る)。
                    SamMaskRefine.PeelThinSpill(region.Ptr, texPx.Ptr, mw, mh);
                    mark?.Invoke(Stage.AaDone);
                } catch (Exception) {
                    // 画素を取得できない場合も、被覆保存転写だけで提案を適用できる。
                } finally {
                    cropPx?.Release();
                    texPx?.Release();
                }
            }
            return region;
        }
    }
}
