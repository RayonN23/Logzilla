using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Logzilla.Core.Common;

/// <summary>
/// Indents XML that has been stuffed into a log message. Log payloads are rarely
/// well-formed documents — they are fragments, they are single-quoted inside a
/// sentence, they arrive with the closing tag missing because something upstream
/// truncated them. A real parser rejects all of that, so this is a tolerant
/// tokenizer instead: it never throws, and text it does not recognise comes back
/// exactly as it went in.
/// </summary>
public static class XmlText
{
    /// <summary>Two spaces per level, and no runaway indent on a pathological document.</summary>
    private const int IndentWidth = 2;
    private const int MaxDepth = 24;

    /// <summary>Below this many tags, a stray "&lt;" in prose is the likelier reading.</summary>
    private const int MinTags = 3;

    /// <summary>Formatting is for reading on screen; past this the payload is a data
    /// dump, and walking it stops being worth the cost on a paint path.</summary>
    private const int MaxLength = 200_000;

    private enum Kind { Open, Close, SelfClose, Standalone, Text }

    private readonly struct Token
    {
        public readonly Kind Kind;
        public readonly string Text;
        public Token(Kind kind, string text) { Kind = kind; Text = text; }
    }

    /// <summary>True when the text holds enough markup to be worth indenting.</summary>
    public static bool LooksLikeXml(string text)
    {
        return Span(text, out _, out _);
    }

    /// <summary>
    /// Indents the XML embedded in a longer message, leaving whatever sits either
    /// side of it on its own line. Returns the text unchanged when there is no
    /// markup worth formatting.
    /// </summary>
    public static string PrettyWithin(string text)
    {
        if (!Span(text, out var a, out var b))
        {
            return text;
        }
        var head = text[..a].TrimEnd();
        var tail = text[(b + 1)..].TrimEnd();
        var body = Pretty(text.Substring(a, b - a + 1));
        var sb = new StringBuilder(body.Length + head.Length + tail.Length + 4);
        if (head.Length > 0)
        {
            sb.Append(head).Append('\n');
        }
        sb.Append(body);
        if (tail.Length > 0)
        {
            sb.Append('\n').Append(tail);
        }
        return sb.ToString();
    }

    /// <summary>
    /// One element per line, nested elements indented. An element whose whole body is
    /// text stays on a single line — "&lt;key&gt;DeviceName&lt;/key&gt;" reads worse
    /// split across three.
    /// </summary>
    public static string Pretty(string xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return xml;
        }
        var tokens = Tokenize(xml);
        if (tokens.Count == 0)
        {
            return xml;
        }
        var sb = new StringBuilder(xml.Length + tokens.Count * IndentWidth);
        var depth = 0;
        for (var k = 0; k < tokens.Count; k++)
        {
            var t = tokens[k];
            if (t.Kind == Kind.Close)
            {
                depth--;
            }
            if (t.Kind == Kind.Open && k + 2 < tokens.Count &&
                tokens[k + 1].Kind == Kind.Text && tokens[k + 2].Kind == Kind.Close)
            {
                Append(sb, depth, t.Text + tokens[k + 1].Text + tokens[k + 2].Text);
                k += 2;
                continue;
            }
            Append(sb, depth, t.Text);
            if (t.Kind == Kind.Open)
            {
                depth++;
            }
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, int depth, string line)
    {
        if (sb.Length > 0)
        {
            sb.Append('\n');
        }
        sb.Append(' ', Math.Clamp(depth, 0, MaxDepth) * IndentWidth).Append(line);
    }

    /// <summary>Locates the markup inside a message: first tag opener to last "&gt;".</summary>
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Detection keeps its guards in one readable place.")]
    private static bool Span(string text, out int start, out int end)
    {
        start = -1;
        end = -1;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return false;
        }
        var a = -1;
        for (var i = 0; i + 1 < text.Length && a < 0; i++)
        {
            if (text[i] == '<' && IsTagLead(text[i + 1]))
            {
                a = i;
            }
        }
        if (a < 0)
        {
            return false;
        }
        var b = text.LastIndexOf('>');
        if (b <= a)
        {
            return false;
        }
        var tags = 0;
        for (var i = a; i < b && tags < MinTags; i++)
        {
            if (text[i] == '<' && IsTagLead(text[i + 1]))
            {
                tags++;
            }
        }
        if (tags < MinTags)
        {
            return false;
        }
        start = a;
        end = b;
        return true;
    }

    private static bool IsTagLead(char c) => char.IsLetter(c) || c is '/' or '?' or '!' or '_';

    [SuppressMessage("Major Code Smell", "S3776", Justification = "A tolerant tokenizer has to special-case comments, CDATA and quoting.")]
    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var n = s.Length;
        var i = 0;
        while (i < n)
        {
            if (!StartsTag(s, i))
            {
                var t = i;
                i++;
                while (i < n && !StartsTag(s, i))
                {
                    i++;
                }
                var body = s[t..i].Trim();
                if (body.Length > 0)
                {
                    tokens.Add(new Token(Kind.Text, body));
                }
                continue;
            }
            var close = TagEnd(s, i);
            if (close < 0)
            {
                // Unterminated tag: whatever is left is not markup any more.
                var rest = s[i..].Trim();
                if (rest.Length > 0)
                {
                    tokens.Add(new Token(Kind.Text, rest));
                }
                break;
            }
            var tag = s[i..(close + 1)];
            tokens.Add(new Token(Classify(tag), tag));
            i = close + 1;
        }
        return tokens;
    }

    private static bool StartsTag(string s, int i) =>
        s[i] == '<' && i + 1 < s.Length && IsTagLead(s[i + 1]);

    /// <summary>Index of the "&gt;" that closes the tag at <paramref name="i"/>, or -1.</summary>
    private static int TagEnd(string s, int i)
    {
        if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
        {
            var k = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
            return k < 0 ? -1 : k + 2;
        }
        if (string.CompareOrdinal(s, i, "<![CDATA[", 0, 9) == 0)
        {
            var k = s.IndexOf("]]>", i + 9, StringComparison.Ordinal);
            return k < 0 ? -1 : k + 2;
        }
        var quote = '\0';
        for (var k = i + 1; k < s.Length; k++)
        {
            var c = s[k];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return k;
            }
        }
        return -1;
    }

    private static Kind Classify(string tag)
    {
        // CDATA is character data wearing a tag, so it collapses onto its parent's
        // line like any other text body would.
        if (tag.StartsWith("<![CDATA[", StringComparison.Ordinal))
        {
            return Kind.Text;
        }
        if (tag.Length < 3 || tag[1] is '?' or '!')
        {
            return Kind.Standalone;
        }
        if (tag[1] == '/')
        {
            return Kind.Close;
        }
        return tag[^2] == '/' ? Kind.SelfClose : Kind.Open;
    }
}
