using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Logzilla.App.Infrastructure;

/// <summary>How a run of answer text is drawn.</summary>
internal enum RunKind { Plain, Bold, Code, Cite }

/// <summary>A run of text in one style. <see cref="Line"/> is the cited line number for a citation.</summary>
internal readonly record struct AskRun(RunKind Kind, string Text, int Line = 0);

/// <summary>One paragraph of an answer: plain, a bullet, a heading, or a line of a code block.</summary>
internal sealed class AskParagraph
{
    public bool Bullet;
    public string Marker = "•";
    public bool Heading;
    public bool Code;
    public readonly List<AskRun> Runs = new();
}

/// <summary>
/// Reads the light Markdown models answer in — paragraphs, bullets, headings, **bold**,
/// `code`, fenced code blocks — plus the [#1234] citations the assistant is told to
/// use. Anything else is shown as the text it is; nothing here can fail on odd input.
/// </summary>
internal static partial class AskMarkup
{
    [GeneratedRegex(@"\[(#\d{1,9}(?:\s*[,;]\s*#?\d{1,9})*)\]")]
    private static partial Regex CiteGroup { get; }

    [GeneratedRegex(@"\d{1,9}")]
    private static partial Regex Number { get; }

    [GeneratedRegex(@"^\s{0,3}(?:[-*•+]|(\d{1,3})[.)])\s+")]
    private static partial Regex BulletStart { get; }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex HeadingStart { get; }

    public static List<AskParagraph> Parse(string text)
    {
        var paragraphs = new List<AskParagraph>();
        if (string.IsNullOrEmpty(text))
        {
            return paragraphs;
        }
        var inFence = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                var code = new AskParagraph { Code = true };
                code.Runs.Add(new AskRun(RunKind.Code, raw.Length == 0 ? " " : raw));
                paragraphs.Add(code);
                continue;
            }
            if (raw.Trim().Length == 0)
            {
                continue;
            }
            var p = new AskParagraph();
            var line = raw;
            var bullet = BulletStart.Match(line);
            if (bullet.Success)
            {
                p.Bullet = true;
                p.Marker = bullet.Groups[1].Success ? bullet.Groups[1].Value + "." : "•";
                line = line[bullet.Length..];
            }
            else
            {
                var heading = HeadingStart.Match(line);
                if (heading.Success)
                {
                    p.Heading = true;
                    line = line[heading.Length..];
                }
            }
            Inline(line.Trim(), p.Runs, p.Heading);
            paragraphs.Add(p);
        }
        return paragraphs;
    }

    /// <summary>Every line number cited anywhere in the text, in order, once each.</summary>
    public static List<int> Citations(string text)
    {
        var seen = new List<int>();
        foreach (var p in Parse(text))
        {
            foreach (var r in p.Runs)
            {
                if (r.Kind == RunKind.Cite && !seen.Contains(r.Line))
                {
                    seen.Add(r.Line);
                }
            }
        }
        return seen;
    }

    private static void Inline(string s, List<AskRun> runs, bool bold)
    {
        var plain = bold ? RunKind.Bold : RunKind.Plain;
        var i = 0;
        var start = 0;
        while (i < s.Length)
        {
            if (s[i] == '`')
            {
                var end = s.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    Flush(s, start, i, runs, plain);
                    runs.Add(new AskRun(RunKind.Code, s[(i + 1)..end]));
                    i = start = end + 1;
                    continue;
                }
            }
            else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush(s, start, i, runs, plain);
                    Cites(s[(i + 2)..end], runs, RunKind.Bold);
                    i = start = end + 2;
                    continue;
                }
            }
            i++;
        }
        Flush(s, start, s.Length, runs, plain);
    }

    private static void Flush(string s, int from, int to, List<AskRun> runs, RunKind kind)
    {
        if (to > from)
        {
            Cites(s[from..to], runs, kind);
        }
    }

    /// <summary>Splits [#12] and [#12, #34] out of a stretch of text as citation runs.</summary>
    private static void Cites(string s, List<AskRun> runs, RunKind kind)
    {
        var at = 0;
        foreach (Match m in CiteGroup.Matches(s))
        {
            if (m.Index > at)
            {
                runs.Add(new AskRun(kind, s[at..m.Index]));
            }
            foreach (Match n in Number.Matches(m.Groups[1].Value))
            {
                var line = int.Parse(n.Value, NumberStyles.None, CultureInfo.InvariantCulture);
                runs.Add(new AskRun(RunKind.Cite, "#" + line.ToString("N0", CultureInfo.InvariantCulture), line));
            }
            at = m.Index + m.Length;
        }
        if (at < s.Length)
        {
            runs.Add(new AskRun(kind, s[at..]));
        }
    }
}
