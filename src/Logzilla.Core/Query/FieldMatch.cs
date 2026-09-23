using System;
using System.Collections.Generic;
using System.Text;

namespace Logzilla.Core.Query;

/// <summary>
/// Which of a query's field filters one log entry satisfies. Like
/// <see cref="EntryMatch"/> it is fed the entry's lines one at a time, so a field on
/// a continuation line counts for the entry that owns it.
/// </summary>
public sealed class FieldMatch
{
    private readonly FieldFilter[] _filters;
    private readonly string[] _jsonKeys;
    private readonly string[] _textKeys;
    private readonly bool[] _found;

    public FieldMatch(IReadOnlyList<FieldFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        _filters = new FieldFilter[filters.Count];
        _jsonKeys = new string[filters.Count];
        _textKeys = new string[filters.Count];
        _found = new bool[filters.Count];
        for (var i = 0; i < filters.Count; i++)
        {
            _filters[i] = filters[i];
            _jsonKeys[i] = "\"" + filters[i].Key + "\"";
            _textKeys[i] = filters[i].Key + "=";
        }
    }

    public int Count => _filters.Length;

    public void Reset() => Array.Clear(_found);

    /// <summary>True when every filter holds for the lines scanned since
    /// <see cref="Reset"/>: an is/contains filter found its value somewhere in the
    /// entry, and an is-not filter found its value nowhere.</summary>
    public bool Passed
    {
        get
        {
            for (var i = 0; i < _filters.Length; i++)
            {
                if (_found[i] == (_filters[i].Op == FieldOp.IsNot))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>True once an is-not filter has seen its value: no later line of the
    /// entry can change the outcome.</summary>
    public bool Failed
    {
        get
        {
            for (var i = 0; i < _filters.Length; i++)
            {
                if (_found[i] && _filters[i].Op == FieldOp.IsNot)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Folds one line of the entry in. <paramref name="json"/> says the line
    /// is a JSON record; a plain-text line is searched for key=value pairs, and for
    /// "key": value too, since plain-text logs often carry a JSON payload.</summary>
    public void Scan(ReadOnlySpan<char> line, bool json)
    {
        for (var i = 0; i < _filters.Length; i++)
        {
            if (_found[i])
            {
                continue;
            }
            var f = _filters[i];
            _found[i] = FieldScan.JsonHas(line, _jsonKeys[i], f.Op, f.Values) ||
                        (!json && FieldScan.TextHas(line, _textKeys[i], f.Op, f.Values));
        }
    }

    /// <summary>Characters that appear in a file exactly as typed: no JSON writer
    /// escapes them (System.Text.Json writes &lt;, &gt;, &amp;, ' and + as \uXXXX, and
    /// some writers escape /), and ASCII case folding treats them the same way the
    /// comparison does.</summary>
    private static bool IsLiteralChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or
             ' ' or '-' or '_' or '.' or ':' or '@' or '#' or '=' or ',' or ';' or
             '(' or ')' or '*' or '!' or '?' or '$' or '%' or '~' or '^' or '|';

    /// <summary>
    /// Search needles that any line holding one of the filters' values must contain:
    /// the values themselves, lower-cased, for a byte search to rule a line out before
    /// it is decoded. Null when some value cannot be looked for that way.
    /// </summary>
    public static List<byte[]> Needles(IReadOnlyList<FieldFilter> filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        var needles = new List<byte[]>();
        foreach (var f in filters)
        {
            foreach (var v in f.Values)
            {
                foreach (var c in v)
                {
                    if (!IsLiteralChar(c))
                    {
                        return null;
                    }
                }
                needles.Add(Encoding.ASCII.GetBytes(v.ToLowerInvariant()));
            }
        }
        return needles;
    }
}

/// <summary>Finds a named field's values in one line of text.</summary>
public static class FieldScan
{
    private static bool Test(ReadOnlySpan<char> value, FieldOp op, List<string> values)
    {
        for (var k = 0; k < values.Count; k++)
        {
            var hit = op == FieldOp.Contains
                ? value.Contains(values[k], StringComparison.OrdinalIgnoreCase)
                : value.Equals(values[k], StringComparison.OrdinalIgnoreCase);
            if (hit)
            {
                return true;
            }
        }
        return false;
    }

    public static bool JsonHas(ReadOnlySpan<char> line, string quotedKey, FieldOp op, List<string> values)
    {
        ArgumentNullException.ThrowIfNull(quotedKey);
        ArgumentNullException.ThrowIfNull(values);
        var pos = 0;
        while (pos < line.Length)
        {
            var k = line[pos..].IndexOf(quotedKey, StringComparison.OrdinalIgnoreCase);
            if (k < 0)
            {
                return false;
            }
            var p = pos + k + quotedKey.Length;
            pos += k + 1;
            p = SkipBlanks(line, p);
            if (p >= line.Length || line[p] != ':')
            {
                continue;   // the key's text used as a value, not a key
            }
            p = SkipBlanks(line, p + 1);
            if (p >= line.Length)
            {
                return false;
            }
            var c = line[p];
            if (c == '{' || c == '[')
            {
                continue;
            }
            if (c == '"')
            {
                var q = p + 1;
                var escaped = false;
                while (q < line.Length && line[q] != '"')
                {
                    if (line[q] == '\\')
                    {
                        escaped = true;
                        q++;
                    }
                    q++;
                }
                var raw = line[(p + 1)..Math.Min(q, line.Length)];
                if (escaped ? Test(Unescape(raw), op, values) : Test(raw, op, values))
                {
                    return true;
                }
                continue;
            }
            var e = p;
            while (e < line.Length && line[e] is not (',' or '}' or ']' or ' ' or '\t' or '\r' or '\n'))
            {
                e++;
            }
            if (Test(line[p..e], op, values))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Does some <c>key=value</c> in the line hold one of the values? The key must
    /// start a word (so <c>id=</c> does not match inside <c>userid=</c>). The value is
    /// a quoted string, or runs to the next blank or separator — the same pairs the
    /// Fields panel lists. <c>keyEquals</c> is the key followed by '=', e.g. <c>tenant=</c>.
    /// </summary>
    public static bool TextHas(ReadOnlySpan<char> line, string keyEquals, FieldOp op, List<string> values)
    {
        ArgumentNullException.ThrowIfNull(keyEquals);
        ArgumentNullException.ThrowIfNull(values);
        var pos = 0;
        while (pos < line.Length)
        {
            var k = line[pos..].IndexOf(keyEquals, StringComparison.OrdinalIgnoreCase);
            if (k < 0)
            {
                return false;
            }
            var start = pos + k;
            pos = start + 1;
            if (start > 0 && IsKeyChar(line[start - 1]))
            {
                continue;
            }
            var p = start + keyEquals.Length;
            ReadOnlySpan<char> value;
            if (p < line.Length && (line[p] == '"' || line[p] == '\''))
            {
                var close = line[(p + 1)..].IndexOf(line[p]);
                value = close < 0 ? line[(p + 1)..] : line.Slice(p + 1, close);
            }
            else
            {
                var e = p;
                while (e < line.Length && !char.IsWhiteSpace(line[e]) && line[e] is not (',' or ';' or ')' or ']' or '}' or '"' or '\''))
                {
                    e++;
                }
                value = line[p..e];
            }
            if (value.Length > 0 && Test(value, op, values))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsKeyChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-';

    private static int SkipBlanks(ReadOnlySpan<char> s, int p)
    {
        while (p < s.Length && (s[p] == ' ' || s[p] == '\t'))
        {
            p++;
        }
        return p;
    }

    private static string Unescape(ReadOnlySpan<char> raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length)
            {
                sb.Append(c);
                continue;
            }
            var e = raw[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u' when i + 4 < raw.Length &&
                              int.TryParse(raw.Slice(i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                                           System.Globalization.CultureInfo.InvariantCulture, out var cp):
                    sb.Append((char)cp);
                    i += 4;
                    break;
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();
    }
}
