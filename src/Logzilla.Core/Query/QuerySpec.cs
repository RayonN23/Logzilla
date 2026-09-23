using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using Logzilla.Core.Common;

namespace Logzilla.Core.Query;

public partial class QuerySpec
{
    private List<string> IncS { get; } = [];
    private List<string> ExcS { get; } = [];
    public List<byte[]> Inc { get; } = [];
    public List<byte[]> Exc { get; } = [];
    public Regex Rx { get; set; }
    public bool Cs { get; set; }
    public bool NeedsDecode { get; set; }                  // non-ASCII query: compare as text, not bytes
    public int LevelMask { get; set; } = 0x7F;
    public long SrcMask { get; set; } = -1;                // which source files are shown
    public double T0 { get; set; } = double.NaN;
    public double T1 { get; set; } = double.NaN;
    public int Ctx { get; set; }
    public bool Group { get; set; } = true;
    public bool FilterMode { get; set; } = true;
    public bool HasQuery => Rx != null || Inc.Count > 0 || Exc.Count > 0;

    public List<FieldFilter> Fields { get; } = [];

    [SuppressMessage("Major Code Smell", "S107", Justification = "Builder composes full filter state in one call site.")]
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Parsing logic is centralized to avoid duplication.")]
    public static QuerySpec Build(string q, bool regex, bool cs, int mask, long srcMask,
        double t0, double t1, int ctx, bool group, bool filter)
    {
        var s = new QuerySpec
        {
            Cs = cs,
            LevelMask = mask,
            SrcMask = srcMask,
            T0 = t0,
            T1 = t1,
            Ctx = ctx,
            Group = group,
            FilterMode = filter
        };
        if (string.IsNullOrEmpty(q))
        {
            return s;
        }

        if (regex)
        {
            try { s.Rx = new Regex(q, cs ? RegexOptions.None : RegexOptions.IgnoreCase); }
            catch (ArgumentException ex)
            {
                Debug.WriteLine("Invalid regex query: " + ex.Message);
                s.Rx = null;
            }
            return s;
        }
        foreach (Match m in MyRegex().Matches(q))
        {
            var v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (v.Length == 0)
            {
                continue;
            }

            var cmp = cs ? v : v.ToLowerInvariant();
            foreach (var c in cmp.Where(c => c > 127))
            {
                s.NeedsDecode = true;
            }

            if (m.Groups[1].Value == "-") { s.ExcS.Add(cmp); s.Exc.Add(Encoding.UTF8.GetBytes(cmp)); }
            else { s.IncS.Add(cmp); s.Inc.Add(Encoding.UTF8.GetBytes(cmp)); }
        }
        return s;
    }

    public bool MatchText(string line)
    {
        var hay = Cs ? line : line.ToLowerInvariant();
        return !IncS.Any(t => hay.IndexOf(t, StringComparison.Ordinal) < 0) &&
               ExcS.All(t => hay.IndexOf(t, StringComparison.Ordinal) < 0);
    }

    public bool MatchBytes(byte[] b, int o, int len) => Inc.All(t => Contains(b, o, len, t, Cs)) && Exc.All(t => !Contains(b, o, len, t, Cs));

    /// <summary>Fresh per-entry match state, sized for this query's include terms.</summary>
    public EntryMatch NewMatch() => new(Inc.Count);

    /// <summary>
    /// Folds one physical line into an entry's match state. Call it for the entry's
    /// own line and then for each of its continuation lines; the entry matches when
    /// every include term has been seen somewhere in it and no exclude term has.
    /// </summary>
    public void ScanBytes(byte[] b, int o, int len, EntryMatch m)
    {
        ArgumentNullException.ThrowIfNull(m);
        // Case-insensitive, the line is folded once and every term is then an exact
        // search over the folded copy — the same test Contains makes, without
        // re-folding the line for each term or stopping at every candidate letter.
        var hay = new ReadOnlySpan<byte>(b, o, len);
        var folded = Cs;
        for (var k = 0; k < Inc.Count; k++)
        {
            if (!m.Has(k) && Occurs(ref hay, ref folded, Inc[k]))
            {
                m.Include(k);
            }
        }
        for (var k = 0; k < Exc.Count && !m.Excluded; k++)
        {
            if (Occurs(ref hay, ref folded, Exc[k]))
            {
                m.Exclude();
            }
        }
    }

    [ThreadStatic]
    private static byte[] FoldBuffer;

    private static bool Occurs(ref ReadOnlySpan<byte> hay, ref bool folded, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > hay.Length)
        {
            return false;
        }
        if (!folded)
        {
            var buf = FoldBuffer;
            if (buf == null || buf.Length < hay.Length)
            {
                buf = FoldBuffer = new byte[Math.Max(hay.Length, 1024)];
            }
            AsciiTable.ToLowerAscii(hay, buf);
            hay = new ReadOnlySpan<byte>(buf, 0, hay.Length);
            folded = true;
        }
        return hay.IndexOf(needle) >= 0;
    }

    /// <summary>Text-mode counterpart of <see cref="ScanBytes"/>, for UTF-16 sources
    /// and queries carrying non-ASCII characters.</summary>
    public void ScanText(string line, EntryMatch m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var hay = Cs ? line : line.ToLowerInvariant();
        for (var k = 0; k < IncS.Count; k++)
        {
            if (!m.Has(k) && hay.Contains(IncS[k], StringComparison.Ordinal))
            {
                m.Include(k);
            }
        }
        for (var k = 0; k < ExcS.Count && !m.Excluded; k++)
        {
            if (hay.Contains(ExcS[k], StringComparison.Ordinal))
            {
                m.Exclude();
            }
        }
    }

    /// <summary>
    /// Does <paramref name="needle"/> occur in the window? Case-insensitive compares the
    /// haystack folded through <see cref="AsciiTable.ToLowerAscii(byte)"/> against the needle
    /// as given, so the needle is expected to be lower-case already.
    ///
    /// Candidate positions are found with the vectorised span searches rather than a
    /// byte-at-a-time loop: this runs for every line on every filter pass.
    /// </summary>
    public static bool Contains(byte[] hay, int off, int len, byte[] needle, bool cs)
    {
        var n = needle.Length;
        if (n == 0 || n > len)
        {
            return false;
        }

        var window = new ReadOnlySpan<byte>(hay, off, len);
        if (cs)
        {
            return window.IndexOf(needle) >= 0;
        }

        // Folded, a haystack byte can only equal the needle's first byte when it is that
        // byte or its upper-case form. An upper-case first byte can never be produced by
        // folding, so such a needle cannot match at all.
        var first = needle[0];
        byte upper;
        if (first >= (byte)'a' && first <= (byte)'z')
        {
            upper = (byte)(first - 32);
        }
        else if (first >= (byte)'A' && first <= (byte)'Z')
        {
            return false;
        }
        else
        {
            upper = first;
        }

        var tail = new ReadOnlySpan<byte>(needle, 1, n - 1);
        var lower = AsciiTable.Lower;
        var pos = 0;
        var lastStart = len - n;
        while (pos <= lastStart)
        {
            // Only positions where the whole needle still fits are worth finding.
            var hit = window.Slice(pos, lastStart - pos + 1).IndexOfAny(first, upper);
            if (hit < 0)
            {
                return false;
            }
            var at = pos + hit;
            var cand = window.Slice(at + 1, n - 1);
            var k = 0;
            while (k < tail.Length && lower[cand[k]] == tail[k])
            {
                k++;
            }
            if (k == tail.Length)
            {
                return true;
            }
            pos = at + 1;
        }
        return false;
    }

    [GeneratedRegex("(-?)(?:\"([^\"]*)\"|(\\S+))")]
    private static partial Regex MyRegex();
}
