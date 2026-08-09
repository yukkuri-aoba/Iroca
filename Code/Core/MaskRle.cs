// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マスク bool 配列の永続化コーデック(RLE + Base64)。
    /// UI(MaskPaintView)と headless 検証の両方から使うため Core に置く。
    /// フォーマット: "R:" プレフィックス + Base64(4byte W + 4byte H + 1byte 開始値 + uint32[] ランレングス列)。
    /// プレフィックスなしは旧 bitpack フォーマット(1bit/px)として後方互換デコードする。
    /// </summary>
    internal static class MaskRle
    {
        /// <summary>bool 配列を RLE 圧縮 + Base64 文字列にエンコード。</summary>
        public static string Encode(bool[] mask, int w, int h)
        {
            if (mask == null || mask.Length == 0) return "";
            int len = mask.Length;

            var runs = new List<uint>();
            bool curVal = mask[0];
            uint count = 0;
            for (int i = 0; i < len; i++)
            {
                if (mask[i] == curVal)
                {
                    count++;
                }
                else
                {
                    runs.Add(count);
                    curVal = mask[i];
                    count = 1;
                }
            }
            runs.Add(count);

            byte[] bytes = new byte[9 + runs.Count * 4];
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(w), 0, bytes, 0, 4);
            System.Buffer.BlockCopy(System.BitConverter.GetBytes(h), 0, bytes, 4, 4);
            bytes[8] = mask[0] ? (byte)1 : (byte)0;
            for (int i = 0; i < runs.Count; i++)
                System.Buffer.BlockCopy(System.BitConverter.GetBytes(runs[i]), 0, bytes, 9 + i * 4, 4);

            return "R:" + System.Convert.ToBase64String(bytes);
        }

        /// <summary>Encode の逆。デコード失敗時は null を返す。</summary>
        public static bool[] Decode(string encoded, out int w, out int h)
        {
            w = 0; h = 0;
            if (string.IsNullOrEmpty(encoded)) return null;

            if (encoded.StartsWith("R:", System.StringComparison.Ordinal))
            {
                try
                {
                    byte[] bytes = System.Convert.FromBase64String(encoded.Substring(2));
                    if (bytes.Length < 9) return null;
                    w = System.BitConverter.ToInt32(bytes, 0);
                    h = System.BitConverter.ToInt32(bytes, 4);
                    if (w <= 0 || h <= 0) return null;
                    // w * h の int オーバーフローを弾く。w=h=65536 だと len=0 になり、
                    // 破損データに対して「成功・空マスク」を黙って返していた（レビュー §4 中）。
                    long lenLong = (long)w * h;
                    if (lenLong > int.MaxValue) return null;
                    int len = (int)lenLong;
                    bool curVal = bytes[8] != 0;
                    bool[] mask = new bool[len];
                    int pos = 0;
                    int byteIdx = 9;
                    while (pos < len && byteIdx + 4 <= bytes.Length)
                    {
                        uint run = System.BitConverter.ToUInt32(bytes, byteIdx);
                        byteIdx += 4;
                        bool fillVal = curVal;
                        uint end = (uint)System.Math.Min((long)pos + run, len);
                        while (pos < (int)end)
                            mask[pos++] = fillVal;
                        curVal = !curVal;
                    }
                    // Encode は必ず全画素分のランを書く。ここで埋め切れていない = 切断データ。
                    // 「成功・途中まで正しいマスク」として返すと、欠けた部分が「除外なし」に化けて
                    // ユーザーが守ったつもりの画素が変換される。
                    if (pos < len) return null;
                    return mask;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Iroca] Mask decode failed: {ex.Message}");
                    return null;
                }
            }

            // 旧 bitpack フォーマット（後方互換）
            try
            {
                byte[] packed = System.Convert.FromBase64String(encoded);
                if (packed.Length < 9) return null;
                w = System.BitConverter.ToInt32(packed, 0);
                h = System.BitConverter.ToInt32(packed, 4);
                if (w <= 0 || h <= 0) return null;
                int len = w * h;
                if (packed.Length < 8 + (len + 7) / 8) return null;
                bool[] mask = new bool[len];
                for (int i = 0; i < len; i++)
                    mask[i] = (packed[8 + i / 8] & (1 << (i % 8))) != 0;
                return mask;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Iroca] Mask decode (legacy bitpack) failed: {ex.Message}");
                return null;
            }
        }
    }
}
