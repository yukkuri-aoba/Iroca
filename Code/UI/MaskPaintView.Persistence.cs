// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Iroca
{
    // MaskPaintView: マスクの永続化(MaskCache ファイル/セッション同期/レガシー移行/エンコード/プリセット連携)。
    internal partial class MaskPaintView
    {
        // ───────────────────────── Mask Persistence ────────────────────

        private string MaskTexturePath()
        {
            var sourceTexture = _host.SourceTexture;
            if (sourceTexture == null) return null;
            string path = AssetDatabase.GetAssetPath(sourceTexture);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        // 旧 SessionState キー（Phase 6 で MaskFileStore に移行済み）。
        // 残存データをマイグレートするためのみ残し、新規書き込みには使わない。
        private static string LegacyMaskIndexSessionKey(string path) => "Iroca_MaskIndex_" + path;
        private static string LegacyMaskArraySessionKey(string path, string targetKey) => "Iroca_Mask_" + path + ":" + targetKey;
        private static string LegacySingleMaskSessionKey(string path) => "Iroca_Mask_" + path;

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
        /// 永続化する。同時に _session.maskState を bool[] バッファの内容で更新する。
        /// ディスク書き込みに失敗したときだけ false（呼び出し側で通知に使う）。
        /// </summary>
        public bool SaveToSession()
        {
            // bool[] バッファを _session.maskState（RLE 文字列）に書き戻す。
            SyncBuffersToState();

            string path = MaskTexturePath();
            if (path == null) return true;

            var ms = _host.Session?.maskState;
            bool ok = MaskFileStore.SaveMask(path, ms, _maskLoadFailed);

            // 有効な内容を書き込めたら通常動作へ復帰する（空保存スキップの場合は
            // 解除しない: 解除すると次の空保存が未読ファイルを削除してしまう）。
            bool hasContent = ms != null &&
                (!string.IsNullOrEmpty(ms.commonMaskBase64) || (ms.zones != null && ms.zones.Count > 0));
            if (ok && hasContent) _maskLoadFailed = false;
            return ok;
        }

        /// <summary>
        /// 現在の bool[] バッファの内容を RLE エンコードして _session.maskState に書き戻す。
        /// </summary>
        public void SyncBuffersToState()
        {
            var session = _host.Session;
            if (session == null) return;
            var ms = session.maskState ?? (session.maskState = new MaskState());
            ms.width = maskWidth;
            ms.height = maskHeight;

            ms.commonMaskBase64 = (exclusionMask != null && AnyTrue(exclusionMask))
                ? EncodeMask(exclusionMask, maskWidth, maskHeight)
                : "";

            ms.zones.Clear();
            foreach (var kv in zoneMasks)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null || !AnyTrue(kv.Value)) continue;
                ms.zones.Add(new MaskZoneEntry
                {
                    zoneId = kv.Key,
                    maskBase64 = EncodeMask(kv.Value, maskWidth, maskHeight),
                });
            }
        }

        /// <summary>
        /// _session.maskState（RLE 文字列）を bool[] バッファに展開する。
        /// </summary>
        public void SyncBuffersFromState()
        {
            var session = _host.Session;
            if (session == null || session.maskState == null) return;
            var ms = session.maskState;

            exclusionMask = null;
            zoneMasks.Clear();

            if (ms.width <= 0 || ms.height <= 0) return;
            maskWidth = ms.width;
            maskHeight = ms.height;

            if (!string.IsNullOrEmpty(ms.commonMaskBase64))
            {
                var arr = DecodeMask(ms.commonMaskBase64, out int w, out int h);
                if (arr != null && w == maskWidth && h == maskHeight)
                    exclusionMask = arr;
            }

            if (ms.zones != null)
            {
                foreach (var entry in ms.zones)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.zoneId)) continue;
                    var arr = DecodeMask(entry.maskBase64, out int w, out int h);
                    if (arr != null && w == maskWidth && h == maskHeight)
                        zoneMasks[entry.zoneId] = arr;
                }
            }
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

            exclusionMask = null;
            zoneMasks.Clear();
            _maskLoadFailed = false;

            // 1) MaskCache ファイルからの読み込みを最優先する（Editor 再起動を跨ぐ正規ストア）。
            var fileState = MaskFileStore.LoadMask(path, out bool unreadable);
            if (fileState != null && fileState.width > 0 && fileState.height > 0)
            {
                _host.Session.maskState = fileState;
                SyncBuffersFromState();
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

                maskWidth = idx.width;
                maskHeight = idx.height;

                if (idx.hasCommon)
                {
                    string enc = SessionState.GetString(LegacyMaskArraySessionKey(path, CommonMaskKey), null);
                    if (!string.IsNullOrEmpty(enc))
                        exclusionMask = DecodeMask(enc, out _, out _);
                }

                if (idx.zoneIds != null)
                {
                    foreach (var zid in idx.zoneIds)
                    {
                        if (string.IsNullOrEmpty(zid)) continue;
                        string enc = SessionState.GetString(LegacyMaskArraySessionKey(path, zid), null);
                        if (string.IsNullOrEmpty(enc)) continue;
                        var arr = DecodeMask(enc, out _, out _);
                        if (arr != null) zoneMasks[zid] = arr;
                    }
                }

                EraseLegacySessionEntries(path, idx);
                // bool[] → MaskState → File へ確定保存
                SaveToSession();
                return true;
            }

            // 2b) 旧々形式: 単一マスクのみ。共通マスクへ昇格。
            string legacyEncoded = SessionState.GetString(LegacySingleMaskSessionKey(path), null);
            if (string.IsNullOrEmpty(legacyEncoded)) return false;

            var legacy = DecodeMask(legacyEncoded, out int lw, out int lh);
            if (legacy == null)
            {
                SessionState.EraseString(LegacySingleMaskSessionKey(path));
                return false;
            }

            maskWidth = lw;
            maskHeight = lh;
            exclusionMask = legacy;

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
            exclusionMask = null;
            zoneMasks.Clear();
            isPainting = false;
            _maskStrokeStarted = false;
            lastPaintUV = -Vector2.one;
            TextureSlot.Release(ref maskOverlayTexture);
            TextureSlot.Release(ref zoneMaskOverlayTexture);
            maskDirty = true;
        }

        // ─────────────────────── Encode / Decode ────────────────────

        private static bool AnyTrue(bool[] arr)
        {
            if (arr == null) return false;
            for (int i = 0; i < arr.Length; i++) if (arr[i]) return true;
            return false;
        }

        /// <summary>
        /// bool 配列を RLE 圧縮 + Base64 文字列にエンコード(実体は Core の MaskRle)。
        /// </summary>
        public static string EncodeMask(bool[] mask, int w, int h) => MaskRle.Encode(mask, w, h);

        /// <summary>
        /// EncodeMask の逆。デコード失敗時は null を返す(実体は Core の MaskRle)。
        /// </summary>
        public static bool[] DecodeMask(string encoded, out int w, out int h) =>
            MaskRle.Decode(encoded, out w, out h);

        // ───────────────────────── プリセット連携 ────────────────────

        /// <summary>
        /// プリセット内のマスクデータで現在のマスク状態を置き換える。
        /// </summary>
        public void ApplyFromPreset(IrocaPresetData data)
        {
            if (data == null || data.maskWidth <= 0 || data.maskHeight <= 0) return;

            maskWidth = data.maskWidth;
            maskHeight = data.maskHeight;
            exclusionMask = null;
            zoneMasks.Clear();

            if (!string.IsNullOrEmpty(data.commonMaskBase64))
            {
                var m = DecodeMask(data.commonMaskBase64, out int w, out int h);
                if (m != null && w == maskWidth && h == maskHeight)
                    exclusionMask = m;
            }

            if (data.zoneMasks != null)
            {
                foreach (var e in data.zoneMasks)
                {
                    if (e == null || string.IsNullOrEmpty(e.zoneId)) continue;
                    var m = DecodeMask(e.maskBase64, out int w, out int h);
                    if (m == null || w != maskWidth || h != maskHeight) continue;
                    zoneMasks[e.zoneId] = m;
                }
            }

            SaveToSession();
        }

        /// <summary>
        /// 現在のマスク状態を IrocaPresetData の commonMaskBase64 / zoneMasks フィールドに書き出す。
        /// </summary>
        public void WriteToPreset(IrocaPresetData data)
        {
            if (data == null || maskWidth <= 0 || maskHeight <= 0) return;

            bool includedAnything = false;

            if (exclusionMask != null && AnyTrue(exclusionMask))
            {
                data.commonMaskBase64 = EncodeMask(exclusionMask, maskWidth, maskHeight);
                includedAnything = true;
            }

            foreach (var kv in zoneMasks)
            {
                if (kv.Value == null || !AnyTrue(kv.Value)) continue;
                data.zoneMasks.Add(new ZoneMaskEntry
                {
                    zoneId = kv.Key,
                    maskBase64 = EncodeMask(kv.Value, maskWidth, maskHeight),
                });
                includedAnything = true;
            }

            if (includedAnything)
            {
                data.maskWidth = maskWidth;
                data.maskHeight = maskHeight;
            }
        }
    }
}
