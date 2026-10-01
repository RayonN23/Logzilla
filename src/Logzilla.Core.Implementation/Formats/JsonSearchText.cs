using System;
using System.Text;

namespace Logzilla.Core.Implementation.Formats;

/// <summary>
/// What a search looks at in a JSON log line: the values the record holds, not the
/// record's syntax. A JSON line is shown as its message, so matching the raw bytes
/// let key names ("name", "level", "SourceContext"), quotes and the {Placeholders}
/// of a message template hit every line of the file while showing nothing that
/// explained why. Values are kept, hidden properties included, so pasting a
/// correlation id still finds its entries.
///
/// Byte-level and tolerant, like the rest of the indexing side: one pass, no
/// allocation once the buffer has grown, and a line that does not open as an
/// object is searched exactly as written.
/// </summary>
internal static class JsonSearchText
{
    /// <summary>Between values, so a search term or phrase cannot run from the end
    /// of one value into the start of the next.</summary>
    private const byte Gap = (byte)'\n';

    /// <summary>Longest "{Placeholder}" a template value is allowed to drop.</summary>
    private const int MaxPlaceholder = 128;

    /// <summary>
    /// Writes the searchable text of the line at <paramref name="b"/>[<paramref name="o"/>..]
    /// into <paramref name="buf"/> (grown as needed) and returns its length, or -1 when
    /// the line is not a JSON object and should be searched as it stands.
    /// </summary>
    public static int Project(byte[] b, int o, int len, ref byte[] buf)
    {
        ArgumentNullException.ThrowIfNull(b);
        var end = o + len;
        var p = o;
        while (p < end && b[p] is (byte)' ' or (byte)'\t' or 0xEF or 0xBB or 0xBF)
        {
            p++;
        }
        if (p >= end || b[p] != (byte)'{')
        {
            return -1;
        }
        // Unescaping only ever shortens a value, and each value adds one gap, so the
        // raw length plus one bounds the output.
        if (buf == null || buf.Length < len + 1)
        {
            buf = new byte[Math.Max(len + 1, 1024)];
        }
        var w = 0;
        var template = false;
        while (p < end)
        {
            var c = b[p];
            if (c == (byte)'"')
            {
                var close = StringEnd(b, p + 1, end);
                var next = SkipSpace(b, close + 1, end);
                if (next < end && b[next] == (byte)':')
                {
                    // A key: remembered only to know whether the value is a template.
                    template = IsTemplateKey(b, p + 1, close);
                    p = next + 1;
                    continue;
                }
                if (w > 0)
                {
                    buf[w++] = Gap;
                }
                w = Unescape(b, p + 1, close, buf, w, template);
                template = false;
                p = close + 1;
                continue;
            }
            if (c is (byte)'-' or (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'z'))
            {
                // A bare scalar: number, true, false or null.
                if (w > 0)
                {
                    buf[w++] = Gap;
                }
                while (p < end && b[p] is not ((byte)',' or (byte)'}' or (byte)']' or (byte)' '))
                {
                    buf[w++] = b[p++];
                }
                template = false;
                continue;
            }
            p++;
        }
        return w;
    }

    /// <summary>Index of the quote closing the string whose contents start at
    /// <paramref name="p"/>, or <paramref name="end"/> when it is cut off.</summary>
    private static int StringEnd(byte[] b, int p, int end)
    {
        while (p < end)
        {
            var c = b[p];
            if (c == (byte)'\\')
            {
                p += 2;
                continue;
            }
            if (c == (byte)'"')
            {
                return p;
            }
            p++;
        }
        return end;
    }

    private static int SkipSpace(byte[] b, int p, int end)
    {
        while (p < end && b[p] is (byte)' ' or (byte)'\t')
        {
            p++;
        }
        return p;
    }

    private static bool IsTemplateKey(byte[] b, int a, int z)
    {
        var key = new ReadOnlySpan<byte>(b, a, Math.Max(0, z - a));
        return key.SequenceEqual("@mt"u8) || key.SequenceEqual("MessageTemplate"u8);
    }

    /// <summary>
    /// Copies a string value's contents, unescaped, so "C:\\temp" is found by typing
    /// C:\temp. In a message template the {Placeholders} are left out: on screen they
    /// are replaced by property values, which are searched in their own right.
    /// </summary>
    private static int Unescape(byte[] b, int p, int end, byte[] buf, int w, bool template)
    {
        while (p < end)
        {
            var c = b[p];
            if (template && c == (byte)'{')
            {
                if (p + 1 < end && b[p + 1] == (byte)'{')
                {
                    // "{{" is a literal brace in a template, not a placeholder opening.
                    buf[w++] = c;
                    buf[w++] = c;
                    p += 2;
                    continue;
                }
                if (PlaceholderEnd(b, p, end) is var close and > 0)
                {
                    p = close + 1;
                    continue;
                }
            }
            if (c != (byte)'\\' || p + 1 >= end)
            {
                buf[w++] = c;
                p++;
                continue;
            }
            var e = b[p + 1];
            p += 2;
            switch (e)
            {
                case (byte)'n':
                case (byte)'r':
                case (byte)'t':
                    buf[w++] = (byte)' ';
                    break;
                case (byte)'b':
                case (byte)'f':
                    break;
                case (byte)'u' when p + 4 <= end && Hex4(b, p, out var unit):
                    p += 4;
                    if (char.IsHighSurrogate((char)unit) && p + 6 <= end && b[p] == (byte)'\\' &&
                        b[p + 1] == (byte)'u' && Hex4(b, p + 2, out var low) && char.IsLowSurrogate((char)low))
                    {
                        p += 6;
                        w = Utf8(char.ConvertToUtf32((char)unit, (char)low), buf, w);
                    }
                    else
                    {
                        w = Utf8(char.IsSurrogate((char)unit) ? '?' : unit, buf, w);
                    }
                    break;
                default:
                    // \" \\ \/ and anything unrecognised: the character itself.
                    buf[w++] = e;
                    break;
            }
        }
        return w;
    }

    /// <summary>The "}" closing a {Placeholder} that starts at <paramref name="p"/>,
    /// or -1 when this brace is literal text ("{{", or no close in reach).</summary>
    private static int PlaceholderEnd(byte[] b, int p, int end)
    {
        if (p + 1 < end && b[p + 1] == (byte)'{')
        {
            return -1;
        }
        var limit = Math.Min(end, p + MaxPlaceholder);
        for (var k = p + 1; k < limit; k++)
        {
            var c = b[k];
            if (c == (byte)'}')
            {
                return k > p + 1 ? k : -1;
            }
            if (c is (byte)'{' or (byte)' ' or (byte)'"' or (byte)'\\')
            {
                return -1;
            }
        }
        return -1;
    }

    private static bool Hex4(byte[] b, int p, out int value)
    {
        value = 0;
        for (var k = 0; k < 4; k++)
        {
            var c = b[p + k];
            int d;
            if (c >= (byte)'0' && c <= (byte)'9')
            {
                d = c - '0';
            }
            else if ((c | 0x20) >= 'a' && (c | 0x20) <= 'f')
            {
                d = (c | 0x20) - 'a' + 10;
            }
            else
            {
                return false;
            }
            value = (value << 4) | d;
        }
        return true;
    }

    /// <summary>An escaped code point written as UTF-8 — never longer than the
    /// six-byte "\uXXXX" (or twelve-byte pair) it replaces.</summary>
    private static int Utf8(int cp, byte[] buf, int w)
    {
        Span<char> chars = stackalloc char[2];
        var n = new Rune(cp).EncodeToUtf16(chars);
        return w + Encoding.UTF8.GetBytes(chars[..n], buf.AsSpan(w));
    }
}
