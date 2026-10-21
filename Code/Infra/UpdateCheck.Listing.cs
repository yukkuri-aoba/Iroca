// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Iroca
{
    // 公開リストから知らせる版を選ぶ部分。Unity を使わないので scripts/unit-run で検査する。
    internal static partial class UpdateCheck
    {
        static readonly TimeSpan RecheckInterval = TimeSpan.FromDays(1);

        /// <summary>前回読めた時刻(UTC の Ticks)から 1 日たったか。覚えが無い・読めない・未来の時刻なら読みに行く。</summary>
        internal static bool IsDue(string checkedAtTicks, DateTime nowUtc)
        {
            if (!long.TryParse(checkedAtTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                return true;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return true;
            var checkedAt = new DateTime(ticks, DateTimeKind.Utc);
            return checkedAt > nowUtc || nowUtc - checkedAt >= RecheckInterval;
        }

        /// <summary>
        /// 公開リスト(VPM のリポジトリ形式)から、入れられる最新版を探す。今の Unity より新しい Unity が要る版と、
        /// <paramref name="includePrerelease"/> でなければ先行版(<c>-beta</c> など)は除く。
        /// リストの形が読めなければ false。読めたが該当する版が無ければ true で <paramref name="latest"/> は null。
        /// </summary>
        internal static bool TryFindLatest(string listingJson, string packageId, bool includePrerelease,
            string unityVersion, out string latest)
        {
            latest = null;
            if (!(MiniJson.Parse(listingJson) is Dictionary<string, object> root)) return false;
            if (!(root.TryGetValue("packages", out var packagesObj) && packagesObj is Dictionary<string, object> packages))
                return false;
            if (!packages.TryGetValue(packageId, out var packageObj)) return true;
            if (!(packageObj is Dictionary<string, object> package
                  && package.TryGetValue("versions", out var versionsObj)
                  && versionsObj is Dictionary<string, object> versions))
                return false;

            SemVer best = default;
            foreach (var pair in versions)
            {
                if (!(pair.Value is Dictionary<string, object> entry)) continue;
                string name = entry.TryGetValue("version", out var v) && v is string s ? s : pair.Key;
                if (!SemVer.TryParse(name, out var version)) continue;
                if (version.IsPrerelease && !includePrerelease) continue;
                if (entry.TryGetValue("unity", out var u) && u is string requiredUnity
                    && !UnitySatisfies(requiredUnity, unityVersion))
                    continue;
                if (latest == null || version.CompareTo(best) > 0)
                {
                    best = version;
                    latest = name;
                }
            }
            return true;
        }

        /// <summary>
        /// 今の Unity(<c>2022.3.22f1</c> など)が、版の求める Unity(<c>2022.3</c> など。VPM の <c>unity</c> 欄)以上か。
        /// 求める側が読めないときは除かない(VCC / ALCOM も一覧には出すため)。
        /// </summary>
        internal static bool UnitySatisfies(string required, string unityVersion)
        {
            if (!TryParseMajorMinor(required, out int reqMajor, out int reqMinor)) return true;
            if (!TryParseMajorMinor(unityVersion, out int major, out int minor)) return true;
            return major != reqMajor ? major > reqMajor : minor >= reqMinor;
        }

        static string UnityMajorMinor(string unityVersion)
            => TryParseMajorMinor(unityVersion, out int major, out int minor)
                ? major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture)
                : "unknown";

        static bool TryParseMajorMinor(string s, out int major, out int minor)
        {
            major = minor = 0;
            if (string.IsNullOrEmpty(s)) return false;
            int i = 0;
            if (!ReadInt(s, ref i, out major)) return false;
            if (i >= s.Length || s[i] != '.') return false;
            i++;
            return ReadInt(s, ref i, out minor);
        }

        static bool ReadInt(string s, ref int i, out int value)
        {
            value = 0;
            int start = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9' && i - start < 9)
            {
                value = value * 10 + (s[i] - '0');
                i++;
            }
            return i > start;
        }

        /// <summary>SemVer 2.0 の版(<c>1.2.3</c>・<c>1.2.3-beta.1</c>。<c>+build</c> は比べない)。</summary>
        internal readonly struct SemVer : IComparable<SemVer>
        {
            readonly int _major, _minor, _patch;
            readonly string[] _prerelease;

            SemVer(int major, int minor, int patch, string[] prerelease)
            {
                _major = major;
                _minor = minor;
                _patch = patch;
                _prerelease = prerelease;
            }

            public bool IsPrerelease => _prerelease != null && _prerelease.Length > 0;

            public static bool TryParse(string s, out SemVer version)
            {
                version = default;
                if (string.IsNullOrEmpty(s)) return false;
                int plus = s.IndexOf('+');
                if (plus >= 0) s = s.Substring(0, plus);
                int dash = s.IndexOf('-');
                string core = dash >= 0 ? s.Substring(0, dash) : s;
                string[] pre = null;
                if (dash >= 0)
                {
                    pre = s.Substring(dash + 1).Split('.');
                    foreach (string id in pre)
                        if (id.Length == 0) return false;
                }
                string[] parts = core.Split('.');
                if (parts.Length != 3) return false;
                var n = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    int i = 0;
                    if (!ReadInt(parts[k], ref i, out n[k]) || i != parts[k].Length) return false;
                }
                version = new SemVer(n[0], n[1], n[2], pre);
                return true;
            }

            public int CompareTo(SemVer other)
            {
                if (_major != other._major) return _major.CompareTo(other._major);
                if (_minor != other._minor) return _minor.CompareTo(other._minor);
                if (_patch != other._patch) return _patch.CompareTo(other._patch);
                // 先行版は正式版より前。先行版どうしは識別子を前から比べる(数字は数として、数字は文字より前)。
                if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
                if (!other.IsPrerelease) return -1;
                int count = Math.Min(_prerelease.Length, other._prerelease.Length);
                for (int k = 0; k < count; k++)
                {
                    string a = _prerelease[k], b = other._prerelease[k];
                    bool aNum = IsNumeric(a), bNum = IsNumeric(b);
                    int c;
                    if (aNum && bNum)
                        c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                    else if (aNum != bNum)
                        c = aNum ? -1 : 1;
                    else
                        c = string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                return _prerelease.Length.CompareTo(other._prerelease.Length);
            }

            static bool IsNumeric(string s)
            {
                foreach (char ch in s)
                    if (ch < '0' || ch > '9') return false;
                return true;
            }
        }

        /// <summary>
        /// 公開リストを読むための最小の JSON 読み取り。オブジェクトは Dictionary、配列は List、文字列は string、
        /// 数は double、true/false は bool、null は null にする。読めなければ null。
        /// JsonUtility は辞書(版をキーにした versions)を読めず、Newtonsoft は VRChat 以外のプロジェクトに無いため。
        /// </summary>
        internal static class MiniJson
        {
            const int MaxDepth = 64;

            public static object Parse(string json)
            {
                if (json == null) return null;
                int i = 0;
                try
                {
                    if (json.Length > 0 && json[0] == '﻿') i = 1;
                    object value = ReadValue(json, ref i, 0);
                    SkipWhitespace(json, ref i);
                    return i == json.Length ? value : null;
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            static object ReadValue(string s, ref int i, int depth)
            {
                if (depth > MaxDepth) throw new FormatException();
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException();
                switch (s[i])
                {
                    case '{': return ReadObject(s, ref i, depth);
                    case '[': return ReadArray(s, ref i, depth);
                    case '"': return ReadString(s, ref i);
                    case 't': ReadLiteral(s, ref i, "true"); return true;
                    case 'f': ReadLiteral(s, ref i, "false"); return false;
                    case 'n': ReadLiteral(s, ref i, "null"); return null;
                    default: return ReadNumber(s, ref i);
                }
            }

            static Dictionary<string, object> ReadObject(string s, ref int i, int depth)
            {
                var result = new Dictionary<string, object>();
                i++; // {
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return result; }
                while (true)
                {
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException();
                    string key = ReadString(s, ref i);
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException();
                    i++;
                    result[key] = ReadValue(s, ref i, depth + 1);
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length) throw new FormatException();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return result; }
                    throw new FormatException();
                }
            }

            static List<object> ReadArray(string s, ref int i, int depth)
            {
                var result = new List<object>();
                i++; // [
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return result; }
                while (true)
                {
                    result.Add(ReadValue(s, ref i, depth + 1));
                    SkipWhitespace(s, ref i);
                    if (i >= s.Length) throw new FormatException();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return result; }
                    throw new FormatException();
                }
            }

            static string ReadString(string s, ref int i)
            {
                i++; // "
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw new FormatException();
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) throw new FormatException();
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length
                                || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                                throw new FormatException();
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: throw new FormatException();
                    }
                }
            }

            static double ReadNumber(string s, ref int i)
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (i == start
                    || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    throw new FormatException();
                return value;
            }

            static void ReadLiteral(string s, ref int i, string literal)
            {
                if (string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0) throw new FormatException();
                i += literal.Length;
            }

            static void SkipWhitespace(string s, ref int i)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
            }
        }
    }
}
