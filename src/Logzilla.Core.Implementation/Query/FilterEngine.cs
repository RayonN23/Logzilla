using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Logzilla.Core.Implementation.Logs;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.Core.Implementation.Query;

internal sealed class FilterEngine : IFilterEngine
{
    /// <summary>Lines between cancellation checks and progress reports.</summary>
    private const int PollStride = 1 << 16;

    public FilterResult Run(LogDocument f, QuerySpec s, Func<bool> cancelled, Action<int, int> progress)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(s);

        var r = new FilterResult();
        r.SrcCounts = new int[Math.Max(1, f.Srcs.Count)];
        var n = f.N;
        var keep = new byte[n];
        var hasQ = s.HasQuery;
        var timeF = !double.IsNaN(s.T0);
        var match = s.NewMatch();
        var rx = s.Rx == null ? null : new RegexScan(ScanRegex(s.Rx, n));
        var fields = s.Fields.Count == 0 ? null : new FieldScanState(s.Fields);

        using (var feed = new LineReader(f, 1 << 22))
        {
            var i = 0;
            // Entries are variable length, so the poll is a threshold rather than a
            // mask on the line index — an entry that spans a multiple of the stride
            // must not be able to skip the cancellation check.
            var nextPoll = 0;
            while (i < n)
            {
                if (i >= nextPoll)
                {
                    nextPoll = i + PollStride;
                    if (cancelled != null && cancelled())
                    {
                        return null;
                    }
                    if (progress != null)
                    {
                        progress(i, n);
                    }
                }

                // One pass per entry, not per line: a plain-text entry owns every
                // continuation line under it, and the query is answered against all
                // of them together.
                var last = i;
                if (s.Group)
                {
                    while (last + 1 < n && f.Flag[last + 1] == 1)
                    {
                        last++;
                    }
                }

                var ok = ((s.LevelMask >> f.Sev[i]) & 1) == 1;
                if (ok && ((s.SrcMask >> f.Src[i]) & 1L) == 0)
                {
                    ok = false;
                }
                if (ok && timeF) { var t = f.Ts[i]; if (double.IsNaN(t) || t < s.T0 || t > s.T1)
{
    ok = false;
} }

                if (ok && fields != null && !fields.Passes(f, feed, i, last))
                {
                    ok = false;
                }

                if (ok && hasQ)
                {
                    if (Hits(f, s, feed, i, last, match, rx))
                    {
                        r.Matches.Add(i);
                    }
                    else if (s.FilterMode)
                    {
                        ok = false;
                    }
                }
                if (ok)
                {
                    for (var k = i; k <= last; k++)
                    {
                        keep[k] = 1;
                    }
                }
                i = last + 1;
            }
        }

        if (s.Ctx > 0 && hasQ && s.FilterMode)
        {
            var outk = (byte[])keep.Clone();
            for (var j = 0; j < r.Matches.Count; j++)
            {
                var i = r.Matches[j];
                int lo = Math.Max(0, i - s.Ctx), hi = Math.Min(n - 1, i + s.Ctx);
                for (var k = lo; k <= hi; k++)
                {
                    outk[k] = 1;
                }
            }
            keep = outk;
        }

        // Sized exactly before filling: grown by doubling, a view of a few million lines
        // leaves several abandoned copies of itself behind for the collector.
        var kept = n - keep.AsSpan().Count((byte)0);
        r.View.Capacity = kept;
        for (var ix = 0; ix < n; ix++)

        {
            if (keep[ix] != 0)
            {
                r.View.Add(ix);
                r.Counts[f.Sev[ix]]++;
                r.SrcCounts[f.Src[ix]]++;
                var t = f.Ts[ix];
                if (!double.IsNaN(t))
                {
                    if (double.IsNaN(r.Lo) || t < r.Lo)
                    {
                        r.Lo = t;
                    }
                    if (double.IsNaN(r.Hi) || t > r.Hi)
                    {
                        r.Hi = t;
                    }
                }
            }
        }

        BuildViz(f, r);
        return r;
    }

    /// <summary>A joined entry past this many characters is a payload dump, not
    /// something a regex over the whole entry is going to say anything useful about.</summary>
    private const int MaxRegexEntryChars = 1 << 20;

    /// <summary>
    /// Does the query hit anywhere in the entry spanning lines <paramref name="first"/>
    /// to <paramref name="last"/>? Literal terms accumulate across the entry's lines,
    /// so "error unknown" matches when "error" is on the header line and "unknown" is
    /// three lines into the stack trace.
    /// </summary>
    private static bool Hits(LogDocument f, QuerySpec s, LineReader feed, int first, int last, EntryMatch m, RegexScan rx)
    {
        if (rx != null)
        {
            return rx.Hits(f, feed, first, last);
        }
        m.Reset();
        for (var i = first; i <= last; i++)
        {
            byte[] b; var o = feed.Locate(i, out b);
            var len = Math.Min(f.Len[i], b.Length - o);
            var sr = f.Srcs[f.Src[i]];
            if (s.NeedsDecode || sr.Stride == 2)
            {
                s.ScanText(sr.Encoding.GetString(b, o, len), m);
            }
            else
            {
                s.ScanBytes(b, o, len, m);
            }
            // An exclude term anywhere in the entry settles it; an include term found
            // early does not, because a later line may still carry an exclude term.
            if (m.Excluded)
            {
                return false;
            }
        }
        return m.Matched;
    }

    /// <summary>Documents at least this long get a compiled copy of the query regex.
    /// Compiling costs a few milliseconds up front and pays for itself many times over
    /// across a big file; below this the interpreter finishes first.</summary>
    private const int CompileRegexLines = 50_000;

    private static Regex ScanRegex(Regex rx, int lines)
    {
        if (lines < CompileRegexLines || (rx.Options & RegexOptions.Compiled) != 0)
        {
            return rx;
        }
        try
        {
            // Same pattern, options and timeout, so it matches exactly what the
            // interpreted one would; only the engine differs.
            return new Regex(rx.ToString(), rx.Options | RegexOptions.Compiled, rx.MatchTimeout);
        }
        catch (ArgumentException)
        {
            return rx;
        }
    }

    /// <summary>
    /// Answers the field filters for one entry at a time. A line is only decoded when
    /// it could hold one of the filtered values: for plain ASCII values a byte search
    /// rules most lines out first, so a filter over a big file costs
    /// about what searching for the id as text does.
    /// </summary>
    private sealed class FieldScanState
    {
        private readonly FieldMatch _match;
        private readonly List<byte[]> _needles;
        private char[] _chars = new char[4096];

        public FieldScanState(List<FieldFilter> filters)
        {
            _match = new FieldMatch(filters);
            _needles = FieldMatch.Needles(filters);
        }

        public bool Passes(LogDocument f, LineReader feed, int first, int last)
        {
            _match.Reset();
            for (var i = first; i <= last && !_match.Failed; i++)
            {
                var o = feed.Locate(i, out var b);
                var len = Math.Min(f.Len[i], b.Length - o);
                var src = f.Srcs[f.Src[i]];
                if (_needles != null && src.Stride == 1 && !AnyNeedle(b, o, len))
                {
                    continue;
                }
                var max = src.Encoding.GetMaxCharCount(len);
                if (max > _chars.Length)
                {
                    _chars = new char[Math.Max(max, _chars.Length * 2)];
                }
                var used = src.Encoding.GetChars(b, o, len, _chars, 0);
                _match.Scan(new ReadOnlySpan<char>(_chars, 0, used), src.IsJson);
            }
            return _match.Passed;
        }

        private bool AnyNeedle(byte[] b, int o, int len)
        {
            foreach (var needle in _needles)
            {
                if (QuerySpec.Contains(b, o, len, needle, false))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Runs the query regex over an entry decoded into one reused character buffer, so
    /// a scan does not allocate a string for every line it looks at. A multi-line entry
    /// is joined with '\n', exactly as the text it would otherwise be built into.
    /// </summary>
    private sealed class RegexScan
    {
        private readonly Regex _rx;
        private char[] _chars = new char[4096];

        public RegexScan(Regex rx) { _rx = rx; }

        public bool Hits(LogDocument f, LineReader feed, int first, int last)
        {
            var used = 0;
            for (var i = first; i <= last && used < MaxRegexEntryChars; i++)
            {
                if (used > 0)
                {
                    Reserve(used, 1);
                    _chars[used++] = '\n';
                }
                var o = feed.Locate(i, out var b);
                var len = Math.Min(f.Len[i], b.Length - o);
                var enc = f.Srcs[f.Src[i]].Encoding;
                Reserve(used, enc.GetMaxCharCount(len));
                used += enc.GetChars(b, o, len, _chars, used);
            }
            return _rx.IsMatch(new ReadOnlySpan<char>(_chars, 0, used));
        }

        private void Reserve(int used, int more)
        {
            if (used + more > _chars.Length)
            {
                Array.Resize(ref _chars, Math.Max(used + more, _chars.Length * 2));
            }
        }
    }

    private static void BuildViz(LogDocument f, FilterResult r)
    {
        r.Bucket = new int[FilterResult.Buckets];
        r.BucketWarn = new int[FilterResult.Buckets];
        r.BucketErr = new int[FilterResult.Buckets];
        r.Density = new byte[FilterResult.Slots];
        var cnt = r.View.Count;
        if (cnt == 0)
        {
            return;
        }

        var span = (!double.IsNaN(r.Lo) && r.Hi > r.Lo) ? (r.Hi - r.Lo) : 0;
        for (var k = 0; k < cnt; k++)
        {
            var i = r.View[k];
            var b = 0;
            if (span > 0)
            {
                var t = f.Ts[i];
                b = double.IsNaN(t) ? 0 : (int)((t - r.Lo) / span * (FilterResult.Buckets - 1));
            }
            else
            {
                b = (int)((long)k * (FilterResult.Buckets - 1) / Math.Max(1, cnt - 1));
            }

            if (b < 0)
            {
                b = 0;
            }
            else if (b >= FilterResult.Buckets)
            {
                b = FilterResult.Buckets - 1;
            }

            r.Bucket[b]++;
            var sv = f.Sev[i];
            switch (sv)
            {
                case >= 5:
                    r.BucketErr[b]++;
                    break;
                case 4:
                    r.BucketWarn[b]++;
                    break;
            }

            var slot = (int)((long)k * (FilterResult.Slots - 1) / Math.Max(1, cnt - 1));
            var cur = r.Density[slot];
            var want = sv >= 6 ? (byte)3 : sv == 5 ? (byte)2 : sv == 4 ? (byte)1 : (byte)0;
            if (want > cur)
            {
                r.Density[slot] = want;
            }
        }
    }
}
