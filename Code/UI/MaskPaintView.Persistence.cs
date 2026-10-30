// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    internal partial class MaskPaintView
    {

        private string MaskTexturePath()
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null) return null;
            string path = AssetDatabase.GetAssetPath(sourceTexture);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        // 旧 SessionState キー（Phase 6 で MaskFileStore に移行済み）。
        // 残存データをマイグレートするためのみ残し、新規書き込みには使わない。
        private static string LegacyMaskIndexSessionKey(string path) => IrocaPrefs.LegacyMaskIndexPrefix + path;
        private static string LegacyMaskArraySessionKey(string path, string targetKey) => IrocaPrefs.LegacyMaskPrefix + path + ":" + targetKey;
        private static string LegacySingleMaskSessionKey(string path) => IrocaPrefs.LegacyMaskPrefix + path;

        [System.Serializable]
        private class LegacyMaskIndex
        {
            public bool hasCommon;
            public List<string> zoneIds = new List<string>();
            public int width;
            public int height;
        }

        /// <summary>
        /// 現在の全マスク（共通 + 各ゾーン）をプロジェクト下の MaskCache ファイルへ
        /// 永続化する。ディスク書き込みに失敗したときだけ false（呼び出し側で通知に使う）。
        /// </summary>
        public bool SaveToSession() => SaveMasks(_host.Editing.ToSaveState().maskState);

        /// <summary>保存形式にしたマスクを MaskCache ファイルへ書く(<see cref="SaveToSession"/> と同じ)。</summary>
        public bool SaveMasks(MaskState ms)
        {
            string path = MaskTexturePath();
            if (path == null) return true;

            bool ok = MaskFileStore.SaveMask(path, ms, _maskLoadFailed);

            // 有効な内容を書き込めたら通常動作へ復帰する（空保存スキップの場合は
            // 解除しない: 解除すると次の空保存が未読ファイルを削除してしまう）。
            bool hasContent = ms != null &&
                (!string.IsNullOrEmpty(ms.commonMaskBase64)
                 || (ms.zones != null && ms.zones.Count > 0)
                 || (ms.zoneIncludes != null && ms.zoneIncludes.Count > 0));
            if (ok && hasContent) _maskLoadFailed = false;
            return ok;
        }

        /// <summary>
        /// MaskCache ファイルから全マスクを復元する。
        /// ファイルが見つからない場合は、旧 SessionState 形式の残存データを
        /// 一度だけマイグレートして MaskCache へ書き出し、SessionState 側を消去する。
        /// </summary>
        public void RestoreFromSession()
        {
            string path = MaskTexturePath();
            if (path == null) return;

            // テクスチャ(セッション)切替: AI 提案の積み上げ・進行中推論は旧テクスチャ由来なので破棄
            _suggestController?.OnSourceChangedOrClosing();

            Workspace.Clear();
            _maskLoadFailed = false;

            // 1) MaskCache ファイルからの読み込みを最優先する（Editor 再起動を跨ぐ正規ストア）。
            var fileState = MaskFileStore.LoadMask(path, out bool unreadable);
            if (fileState != null && fileState.width > 0 && fileState.height > 0)
            {
                // 展開規則(寸法不一致・壊れた項目は捨てる)は Core の MaskStateCodec と同じ(MaskWorkspace.DecodeFrom)。
                Workspace.DecodeFrom(fileState);
                maskDirty = true;
                return;
            }

            // ファイルは存在するのに使える状態を得られなかった（IO失敗 or 形状不正の JSON）。
            // このセッションでの空保存による削除・無退避上書きを抑止する。
            _maskLoadFailed = unreadable || fileState != null;

            // 2) ファイルが無ければ、旧 SessionState 形式（Phase 6 以前のデータ）からマイグレート。
            if (TryMigrateFromLegacySessionState(path))
            {
                maskDirty = true;
                return;
            }
        }

        /// <summary>
        /// レシピから読んだ編集状態(<c>maskState</c> にマスクが入っている)のマスクを編集中のマスクへ展開し、編集状態からは外す
        /// (文書に文字列を持たない)。MaskCache(UserSettings)は読まない(レシピが正。次の保存で MaskCache も同じ内容に揃う)。
        /// </summary>
        public void RestoreFromRecipeState()
        {
            if (MaskTexturePath() == null) return;
            _suggestController?.OnSourceChangedOrClosing();
            _maskLoadFailed = false;
            var session = _host.Session;
            if (session.maskState != null) Workspace.DecodeFrom(session.maskState);
            session.maskState = new MaskState();
            maskDirty = true;
        }

        /// <summary>
        /// 旧 SessionState 形式（Phase 6 以前）のマスクデータを MaskCache ファイルへ移行する。
        /// 一度移行が完了したら SessionState 側のキーは消去する。
        /// 戻り値: マイグレーションによりバッファが復元できたら true。
        /// </summary>
        private bool TryMigrateFromLegacySessionState(string path)
        {
            // 2a) インデックス形式（共通 + ゾーン別）。
            string indexJson = SessionState.GetString(LegacyMaskIndexSessionKey(path), null);
            if (!string.IsNullOrEmpty(indexJson))
            {
                LegacyMaskIndex idx = null;
                try
                {
                    idx = JsonUtility.FromJson<LegacyMaskIndex>(indexJson);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Iroca] Legacy mask index decode failed: {ex.Message}");
                }
                if (idx == null || idx.width <= 0 || idx.height <= 0)
                {
                    SessionState.EraseString(LegacyMaskIndexSessionKey(path));
                    return false;
                }

                Workspace.Reset(idx.width, idx.height);

                // 寸法の合わない層は受け取らない(SetCommon・SetLayer)。
                if (idx.hasCommon)
                {
                    string enc = SessionState.GetString(LegacyMaskArraySessionKey(path, CommonMaskKey), null);
                    if (!string.IsNullOrEmpty(enc))
                        Workspace.SetCommon(MaskRle.DecodeBits(enc));
                }

                if (idx.zoneIds != null)
                {
                    foreach (var zid in idx.zoneIds)
                    {
                        if (string.IsNullOrEmpty(zid)) continue;
                        string enc = SessionState.GetString(LegacyMaskArraySessionKey(path, zid), null);
                        if (string.IsNullOrEmpty(enc)) continue;
                        Workspace.SetLayer(false, zid, MaskRle.DecodeBits(enc));
                    }
                }

                EraseLegacySessionEntries(path, idx);
                SaveToSession();
                return true;
            }

            // 2b) 旧々形式: 単一マスクのみ。共通マスクへ昇格。
            string legacyEncoded = SessionState.GetString(LegacySingleMaskSessionKey(path), null);
            if (string.IsNullOrEmpty(legacyEncoded)) return false;

            var legacy = MaskRle.DecodeBits(legacyEncoded);
            if (legacy == null)
            {
                SessionState.EraseString(LegacySingleMaskSessionKey(path));
                return false;
            }

            Workspace.Reset(legacy.Width, legacy.Height);
            Workspace.SetCommon(legacy);

            SessionState.EraseString(LegacySingleMaskSessionKey(path));
            SaveToSession();
            return true;
        }

        private static void EraseLegacySessionEntries(string path, LegacyMaskIndex idx)
        {
            SessionState.EraseString(LegacyMaskIndexSessionKey(path));
            SessionState.EraseString(LegacyMaskArraySessionKey(path, CommonMaskKey));
            if (idx.zoneIds != null)
            {
                foreach (var zid in idx.zoneIds)
                {
                    if (string.IsNullOrEmpty(zid)) continue;
                    SessionState.EraseString(LegacyMaskArraySessionKey(path, zid));
                }
            }
            SessionState.EraseString(LegacySingleMaskSessionKey(path));
        }

        /// <summary>
        /// テクスチャ切り替え時にバッファを破棄する。
        /// </summary>
        public void ClearBuffersOnTextureChange()
        {
            _overlayJob.Cancel();
            _pendingOverlayResult = null;
            Workspace.Clear();
            isPainting = false;
            _maskStrokeStarted = false;
            lastPaintUV = -Vector2.one;
            TextureSlot.Release(ref maskOverlayTexture);
            TextureSlot.Release(ref zoneMaskOverlayTexture);
            overlayBuiltW = overlayBuiltH = 0;
            maskDirty = true;
        }

        /// <summary>
        /// bool 配列を RLE 圧縮 + Base64 文字列にエンコード(実体は Core の MaskRle。テストが使う)。
        /// </summary>
        public static string EncodeMask(bool[] mask, int w, int h) => MaskRle.Encode(mask, w, h);

        /// <summary>
        /// EncodeMask の逆。デコード失敗時は null を返す(実体は Core の MaskRle)。
        /// </summary>
        public static bool[] DecodeMask(string encoded, out int w, out int h) =>
            MaskRle.Decode(encoded, out w, out h);

        /// <summary>
        /// 現在のマスク状態を IrocaPresetData の commonMaskBase64 / zoneMasks フィールドに書き出す。
        /// </summary>
        public void WriteToPreset(IrocaPresetData data)
        {
            if (data == null || maskWidth <= 0 || maskHeight <= 0) return;

            bool includedAnything = false;

            var common = Workspace.Common;
            if (common != null && common.Any())
            {
                data.commonMaskBase64 = MaskRle.EncodeBits(common);
                includedAnything = true;
            }

            // |= は短絡しないので、除外が書けても含めるマスクも必ず書く(|| にしない)。
            includedAnything |= EncodePresetEntries(Workspace.Layers(false), data.zoneMasks);
            includedAnything |= EncodePresetEntries(Workspace.Layers(true), data.zoneIncludeMasks);

            if (includedAnything)
            {
                data.maskWidth = maskWidth;
                data.maskHeight = maskHeight;
            }
        }

        /// <summary>
        /// 全 false でないマスクを dst へ書き足す(除外・含めるで共通)。何か足したら true。
        /// </summary>
        private static bool EncodePresetEntries(IEnumerable<KeyValuePair<string, NativeBitMask>> src, List<ZoneMaskEntry> dst)
        {
            bool added = false;
            foreach (var kv in src)
            {
                if (!kv.Value.Any()) continue;
                dst.Add(new ZoneMaskEntry
                {
                    zoneId = kv.Key,
                    maskBase64 = MaskRle.EncodeBits(kv.Value),
                });
                added = true;
            }
            return added;
        }
    }
}
