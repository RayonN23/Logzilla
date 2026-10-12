using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Logzilla.Core.Common;

namespace Logzilla.Core.Implementation.Formats;


// ================= JSON / CLEF line logs =================
//
// Structured logs (Serilog CLEF, Bunyan, pino, ECS, plain NDJSON) put one JSON
// object per line. Read raw they are unusable: the message is buried among
// correlation ids and the timestamp is a quoted field.
//
// Logzilla keeps its byte-level design here. At index time it only *scans* for
// the timestamp and level keys — no parsing, no decoding. The JSON is parsed
// only for the rows actually on screen, so a million-line CLEF file costs the
// same to open as a plain text one.

internal static class JsonLog
{
    private const int FatalThreshold = 60;
    private const int ErrorThreshold = 50;
    private const int WarnThreshold = 40;
    private const int InfoThreshold = 30;
    private const int DebugThreshold = 20;

    // Keys we understand, in preference order.
    public static readonly string[] TimeKeys = { "@t", "timestamp", "@timestamp", "time", "ts", "Timestamp", "date" };
    public static readonly string[] LevelKeys = { "@l", "level", "Level", "severity", "levelname", "loglevel", "lvl" };
    public static readonly string[] MsgKeys = { "@m", "@mt", "message", "msg", "Message", "MessageTemplate", "short_message" };
    public static readonly string[] ExKeys = { "@x", "exception", "Exception", "stack", "stack_trace", "err" };

    // Fields that are plumbing rather than content, hidden from the Fields panel.
    public static bool IsMeta(string k)
    {
        if (k.Length > 0 && k[0] == '@')
        {
            return true;
        }
        for (var i = 0; i < TimeKeys.Length; i++)
        {
            if (k == TimeKeys[i])
{
    return true;
}
        }
        for (var i = 0; i < LevelKeys.Length; i++)
        {
            if (k == LevelKeys[i])
{
    return true;
}
        }
        for (var i = 0; i < MsgKeys.Length; i++)
        {
            if (k == MsgKeys[i])
{
    return true;
}
        }
        for (var i = 0; i < ExKeys.Length; i++)
        {
            if (k == ExKeys[i])
{
    return true;
}
        }
        return false;
    }

    public static byte Level(string v) => v == null ? (byte)0 : Level(v.AsSpan());

    /// <summary>
    /// The level of the value at <paramref name="p"/>: what <c>Level(Scalar(b, p, end, max))</c>
    /// gives, read into a stack buffer instead. The indexer asks this of every line of a
    /// JSON log, and building a string each time was the one allocation per line left.
    /// </summary>
    public static byte LevelAt(byte[] b, int p, int end, int max)
    {
        ArgumentNullException.ThrowIfNull(b);
        Span<char> buf = stackalloc char[Math.Clamp(max, 1, 64)];
        var n = 0;
        while (p < end && (b[p] == (byte)' ' || b[p] == (byte)'\t'))
        {
            p++;
        }
        if (p >= end)
        {
            return 0;
        }
        if (b[p] == (byte)'"')
        {
            p++;
            while (p < end && b[p] != (byte)'"' && n < buf.Length)
            {
                if (b[p] == (byte)'\\' && p + 1 < end)
                {
                    p++;
                }
                buf[n++] = (char)b[p]; p++;
            }
        }
        else
        {
            while (p < end && b[p] != (byte)',' && b[p] != (byte)'}' && n < buf.Length)
            {
                buf[n++] = (char)b[p]; p++;
            }
        }
        return Level(buf[..n].Trim());
    }

    public static byte Level(ReadOnlySpan<char> v)
    {
        if (v.IsEmpty)
        {
            return 0;
        }
        // numeric levels: syslog / bunyan / pino
        int n;
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
        {
            if (n >= FatalThreshold)
            {
                return 6;
            }            // bunyan fatal
            if (n >= ErrorThreshold)
            {
                return 5;
            }            // error
            if (n >= WarnThreshold)
            {
                return 4;
            }             // warn
            if (n >= InfoThreshold)
            {
                return 3;
            }             // info
            if (n >= DebugThreshold)
            {
                return 2;
            }            // debug
            if (n > 0)
            {
                return 1;
            }              // trace
            return 6;                          // syslog 0 = emergency
        }
        switch (char.ToLowerInvariant(v[0]))
        {
            case 'v': return 1;
            case 't': return 1;
            case 'd': return 2;
            case 'i': return 3;
            case 'n': return 3;
            case 'w': return 4;
            case 'e': return 5;
            case 's': return 5;
            case 'f': return 6;
            case 'c': return 6;
            case 'a': return 6;
            case 'p': return 6;
        }
        return 0;
    }

    // ---- byte-level key scan, used while indexing ----
    // Finds  "key":  and returns the index just past the colon, or -1.
    // The quoted key is located with a vectorised span search instead of stopping at
    // every quote: the indexer asks for up to fourteen keys on every line of the file,
    // and most of those keys are not there.
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Byte-scan key finder is optimized for indexing throughput.")]
    public static int Find(byte[] b, int off, int len, string key)
    {
        var end = off + len;
        var kl = key.Length;
        // A match needs the quoted key, the colon and at least one byte after the key:
        // the quote that opens it can start no later than here.
        var lastStart = end - kl - 4;
        if (lastStart < off)
        {
            return -1;
        }

        Span<byte> quoted = kl <= 62 ? stackalloc byte[kl + 2] : new byte[kl + 2];
        quoted[0] = (byte)'"';
        for (var j = 0; j < kl; j++)
        {
            quoted[j + 1] = (byte)key[j];
        }
        quoted[kl + 1] = (byte)'"';

        var i = off;
        while (i <= lastStart)
        {
            var hit = new ReadOnlySpan<byte>(b, i, lastStart - i + kl + 2).IndexOf(quoted);
            if (hit < 0)
            {
                return -1;
            }
            i += hit;
            var p = i + kl + 2;
            while (p < end && (b[p] == (byte)' ' || b[p] == (byte)'\t'))
            {
                p++;
            }
            if (p < end && b[p] == (byte)':')
            {
                return p + 1;
            }
            i++;
        }
        return -1;
    }

    // Reads the scalar starting at p as text (strips quotes, no unescaping).
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Scalar parse supports quoted and non-quoted JSON values.")]
    public static string Scalar(byte[] b, int p, int end, int max)
    {
        while (p < end && (b[p] == (byte)' ' || b[p] == (byte)'\t'))
        {
            p++;
        }
        if (p >= end)
        {
            return null;
        }
        var sb = new StringBuilder();
        if (b[p] == (byte)'"')
        {
            p++;
            while (p < end && b[p] != (byte)'"' && sb.Length < max)
            {
                if (b[p] == (byte)'\\' && p + 1 < end)
                {
                    p++;
                }
                sb.Append((char)b[p]); p++;
            }
        }
        else
        {
            while (p < end && b[p] != (byte)',' && b[p] != (byte)'}' && sb.Length < max)
            { sb.Append((char)b[p]); p++; }
        }
        return sb.ToString().Trim();
    }

    // ---- full parse, used only for rows on screen ----
    // Flat map of key -> value. Nested objects/arrays come back as their raw text.
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Manual parser avoids allocations and external dependencies.")]
    public static List<KeyValuePair<string, string>> Parse(string s)
    {
        var outp = new List<KeyValuePair<string, string>>();
        int i = 0, n = s.Length;
        while (i < n && s[i] != '{')
        {
            i++;
        }
        if (i >= n)
        {
            return outp;
        }
        i++;
        while (i < n)
        {
            while (i < n && (s[i] == ',' || s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n'))
            {
                i++;
            }
            if (i >= n || s[i] == '}')
            {
                break;
            }
            if (s[i] != '"')
            {
                break;
            }
            var key = ReadString(s, ref i);
            while (i < n && (s[i] == ' ' || s[i] == '\t'))
            {
                i++;
            }
            if (i >= n || s[i] != ':')
            {
                break;
            }
            i++;
            while (i < n && (s[i] == ' ' || s[i] == '\t'))
            {
                i++;
            }
            if (i >= n)
            {
                break;
            }
            string val;
            if (s[i] == '"')
            {
                val = ReadString(s, ref i);
            }
            else if (s[i] == '{' || s[i] == '[')
{
    val = ReadNested(s, ref i);
}
            else
            {
                var st = i;
                while (i < n && s[i] != ',' && s[i] != '}')
                {
                    i++;
                }
                val = s.Substring(st, i - st).Trim();
            }
            outp.Add(new KeyValuePair<string, string>(key, val));
        }
        return outp;
    }

    [SuppressMessage("Major Code Smell", "S3776", Justification = "Escape handling is explicit to keep behavior deterministic.")]
    private static string ReadString(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++;                                     // opening quote
        var n = s.Length;
        while (i < n)
        {
            var c = s[i];
            if (c == '"') { i++; break; }
            if (c == '\\' && i + 1 < n)
            {
                i++;
                var e = s[i];
                if (e == 'n')
                {
                    sb.Append('\n');
                }
                else if (e == 'r')
{
    sb.Append('\r');
}
                else if (e == 't')
{
    sb.Append('\t');
}
                else if (e == 'b')
{
    sb.Append('\b');
}
                else if (e == 'f')
{
    sb.Append('\f');
}
                else if (e == 'u' && i + 4 < n)
                {
                    // \uXXXX escape: parse the four hex digits without allocating a substring
                    if (int.TryParse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                                     CultureInfo.InvariantCulture, out var cp))
                    { sb.Append((char)cp); i += 4; }
                }
                else
                {
                    sb.Append(e);
                }
                i++;
                continue;
            }
            sb.Append(c); i++;
        }
        return sb.ToString();
    }

    private static string ReadNested(string s, ref int i)
    {
        int st = i, depth = 0, n = s.Length;
        var inStr = false;
        while (i < n)
        {
            var c = s[i];
            if (inStr)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
{
    inStr = false;
}
            }
            else if (c == '"')
{
    inStr = true;
}
            else if (c == '{' || c == '[')
{
    depth++;
}
            else if (c == '}' || c == ']')
            {
                depth--;
                if (depth == 0) { i++; break; }
            }
            i++;
        }
        return s.Substring(st, Math.Min(i, n) - st);
    }

    public static string Get(List<KeyValuePair<string, string>> kv, string[] keys)
    {
        for (var k = 0; k < keys.Length; k++)

        {
            for (var i = 0; i < kv.Count; i++)

            {
                if (kv[i].Key == keys[k])
                {
                    return kv[i].Value;
                }
            }
        }
        return null;
    }

    // Fills {Placeholders} in a Serilog message template from the event's own
    // properties, which is what turns a CLEF line into a readable sentence.
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Template rendering handles escaped braces and fallbacks in one pass.")]
    public static string Render(List<KeyValuePair<string, string>> kv, string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }
        if (template.IndexOf('{') < 0)
        {
            return template;
        }
        var sb = new StringBuilder(template.Length + 32);
        int i = 0, n = template.Length;
        while (i < n)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < n && template[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                var close = template.IndexOf('}', i + 1);
                if (close < 0) { sb.Append(template, i, n - i); break; }
                var tok = template.Substring(i + 1, close - i - 1);
                var fmt = tok.IndexOf(':');
                if (fmt > 0)
                {
                    tok = tok.Substring(0, fmt);
                }
                var name = tok.TrimStart('@', '$');
                string val = null;
                for (var k = 0; k < kv.Count; k++)

                {
                    if (kv[k].Key == name) { val = kv[k].Value; break; }
                }
                sb.Append(val != null ? val : "{" + tok + "}");
                i = close + 1;
                continue;
            }
            if (c == '}' && i + 1 < n && template[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
            sb.Append(c); i++;
        }
        return sb.ToString();
    }

    private static readonly string[] RenderedMessageKey = ["@m"];
    private static readonly string[] TemplateKeys = ["@mt", "MessageTemplate"];
    private static readonly SearchValues<char> NewLineChars = SearchValues.Create("\r\n");

    // One-line summary for the log grid: flattened to a single line, with only the
    // head of any exception, because the row is one line tall.
    public static string Line(string raw)
    {
        return Compose(raw, false);
    }

    // The same entry with nothing clipped and the exception's own line breaks kept.
    // This is what the expanded row and the clipboard want: a grid row that has to
    // fit in 20 pixels is no reason for Ctrl+C to hand over a truncated stack trace.
    public static string Full(string raw)
    {
        return Compose(raw, true);
    }

    private static string Compose(string raw, bool full)
    {
        var kv = Parse(raw);
        if (kv.Count == 0)
        {
            return raw;
        }
        var m = Message(kv) ?? raw;
        var ex = Get(kv, ExKeys);
        if (!string.IsNullOrEmpty(ex))
        {
            m = full ? m + "\n" + ex : m + "   >> " + Preview(ex);
        }
        return full
            ? m.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ')
            : m.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }

    private static string Preview(string ex)
    {
        var first = ex;
        var nl = first.AsSpan().IndexOfAny(NewLineChars);
        // The whole first line of the exception: a one-line row has no room for the stack
        // trace below it, but cutting the headline at 160 characters with "..." hid the
        // part of the message that said what went wrong.
        if (nl > 0)
        {
            first = first.Substring(0, nl);
        }
        return first;
    }

    /// <summary>The event's message: rendered if the writer supplied one, otherwise the
    /// template filled from the event's own properties. Null when there is neither.</summary>
    private static string Message(List<KeyValuePair<string, string>> kv)
    {
        var m = Get(kv, RenderedMessageKey);
        if (!string.IsNullOrEmpty(m))
        {
            return m;
        }
        var t = Get(kv, TemplateKeys);
        m = !string.IsNullOrEmpty(t) ? Render(kv, t) : Get(kv, MsgKeys);
        return string.IsNullOrEmpty(m) ? null : m;
    }

    // Pretty form for the Entry panel: message, then properties, then the
    // exception with its newlines restored so a stack trace reads normally.
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Pretty printer keeps formatting and metadata filtering localized.")]
    public static string Pretty(string raw)
    {
        var kv = Parse(raw);
        if (kv.Count == 0)
        {
            return raw;
        }
        var sb = new StringBuilder();
        var t = Get(kv, TemplateKeys);
        var m = Message(kv);
        if (!string.IsNullOrEmpty(m))
        {
            AppendField(sb, "message", m);
        }
        if (!string.IsNullOrEmpty(t) && t != m)

        {
            sb.Append("template  ").Append(t).Append("\r\n");
        }
        sb.Append("\r\nPROPERTIES\r\n");
        var wid = 0;
        for (var i = 0; i < kv.Count; i++)

        {
            if (!IsMeta(kv[i].Key) && kv[i].Key.Length > wid)
            {
                wid = kv[i].Key.Length;
            }
        }
        if (wid > 28)
        {
            wid = 28;
        }
        for (var i = 0; i < kv.Count; i++)
        {
            if (IsMeta(kv[i].Key))
            {
                continue;
            }
            AppendProperty(sb, kv[i].Key, kv[i].Value, wid);
        }
        var ex = Get(kv, ExKeys);
        if (!string.IsNullOrEmpty(ex))

        {
            sb.Append("\r\nEXCEPTION\r\n").Append(ex.Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Indent for a payload printed under its own label rather than beside it.</summary>
    private const string BlockIndent = "    ";
    private const int LabelWidth = 10;

    /// <summary>
    /// A top-level label and its value. An XML payload — a plist, a SOAP body, a
    /// device response — goes under the label indented instead of beside it, because
    /// squashed onto one line it is the least readable thing in the panel.
    /// </summary>
    private static void AppendField(StringBuilder sb, string label, string value)
    {
        if (XmlText.LooksLikeXml(value))
        {
            sb.Append(label).Append("\r\n");
            AppendBlock(sb, XmlText.PrettyWithin(value));
            return;
        }
        sb.Append(label.PadRight(LabelWidth)).Append(Flatten(value)).Append("\r\n");
    }

    private static void AppendProperty(StringBuilder sb, string key, string value, int wid)
    {
        if (XmlText.LooksLikeXml(value))
        {
            sb.Append("  ").Append(key).Append("\r\n");
            AppendBlock(sb, XmlText.PrettyWithin(value));
            return;
        }
        sb.Append("  ").Append(key.PadRight(wid)).Append("  ").Append(Flatten(value)).Append("\r\n");
    }

    private static void AppendBlock(StringBuilder sb, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append(BlockIndent).Append(line).Append("\r\n");
        }
    }

    private static string Flatten(string value)
    {
        return value.Replace("\r\n", " ").Replace('\n', ' ');
    }
}
