using System;
using System.Collections.Generic;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;

namespace Logzilla.Core.Implementation.Formats;


internal sealed class TsParser : ITimestampParser
{
    /// <summary>Format detection never reads the year, so it needs no real clock.</summary>
    private sealed class FixedClock : ICurrentTimeSupplier
    {
        public static readonly FixedClock Instance = new();

        public DateTime Now => new(2000, 1, 1);
    }

    private long _dayShift;                 // accumulated midnight rollovers
    private double _lastTod = -1;

    public TsParser(ICurrentTimeSupplier clock)
    {
        Year = clock.Now.Year;
    }

    public TsKind Kind { get; set; } = TsKind.None;

    /// <summary>Anchor the stamp to the JSON time key rather than scanning loose text.</summary>
    public bool IsJson { get; set; }

    /// <summary>Index just past the timestamp that was matched.</summary>
    public int LastEnd { get; set; }

    /// <summary>UTC offset in minutes of the stamp last matched, or null if it had none.</summary>
    public int? LastOffset { get; private set; }

    /// <summary>Syslog carries no year, so one is supplied from the file's own timestamp.</summary>
    public int Year { get; set; }

    /// <summary>Midnight of the first day, for logs that carry only a time of day.</summary>
    public long BaseDayMs { get; set; }

    private static readonly string[] Mon = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

    private static bool Dig(byte c) { return c >= 48 && c <= 57; }
    private static int D2(byte[] b, int i) { return (b[i] - 48) * 10 + (b[i + 1] - 48); }
    private static int D4(byte[] b, int i) { return (b[i] - 48) * 1000 + (b[i + 1] - 48) * 100 + (b[i + 2] - 48) * 10 + (b[i + 3] - 48); }

    private static int MonthAt(byte[] b, int i)
    {
        for (var m = 0; m < 12; m++)
        {
            var s = Mon[m];
            byte c0 = AsciiTable.ToLowerAscii(b[i]), c1 = AsciiTable.ToLowerAscii(b[i + 1]), c2 = AsciiTable.ToLowerAscii(b[i + 2]);
            if (c0 == (byte)(s[0] + 32) && c1 == (byte)(s[1] + 32) && c2 == (byte)(s[2] + 32))
            {
                return m + 1;
            }
        }
        return 0;
    }

    // Reads between one and max digits, advancing i. Returns -1 if there is no digit.
    private static int Take(byte[] b, ref int i, int max, int end)
    {
        if (i >= end || !Dig(b[i]))
        {
            return -1;
        }
        int v = 0, n = 0;
        while (i < end && n < max && Dig(b[i])) { v = v * 10 + (b[i] - 48); i++; n++; }
        return v;
    }

    private static int Frac(byte[] b, int i, int end)
    {
        // ".123" / ",123" -> milliseconds
        if (i >= end)
        {
            return 0;
        }
        if (b[i] != (byte)'.' && b[i] != (byte)',')
        {
            return 0;
        }
        int v = 0, k = 0;
        for (var j = i + 1; j < end && k < 3 && Dig(b[j]); j++, k++)
        {
            v = v * 10 + (b[j] - 48);
        }
        while (k < 3 && k > 0) { v *= 10; k++; }
        return v;
    }

    // Consumes ".123", "Z" and "+01:00" so the caller knows where the stamp ends,
    // and reads the zone: "Z" is 0, "+01", "+0100" and "+01:00" are 60.
    private int TailEnd(byte[] b, int i, int lim)
    {
        if (i < lim && (b[i] == (byte)'.' || b[i] == (byte)','))
        {
            var j = i + 1;
            while (j < lim && Dig(b[j]))
            {
                j++;
            }
            if (j > i + 1)
            {
                i = j;
            }
        }
        if (i < lim && (b[i] == (byte)'Z' || b[i] == (byte)'z'))
        {
            i++;
            LastOffset = 0;
        }
        else if (i + 2 < lim && (b[i] == (byte)'+' || b[i] == (byte)'-') && Dig(b[i + 1]))
        {
            var j = i + 1;
            while (j < lim && (Dig(b[j]) || b[j] == (byte)':'))
            {
                j++;
            }
            if (j - i <= 6)
            {
                LastOffset = Offset(b, i, j);
                i = j;
            }
        }
        return i;
    }

    // "+01", "+0100" or "+01:00" between i and end, in minutes; null for anything else.
    private static int? Offset(byte[] b, int i, int end)
    {
        int v = 0, n = 0;
        for (var j = i + 1; j < end; j++)
        {
            if (Dig(b[j]))
            {
                v = v * 10 + (b[j] - 48);
                n++;
            }
        }
        int minutes;
        if (n == 2)
        {
            minutes = v * 60;
        }
        else if (n == 4)
        {
            minutes = v / 100 * 60 + v % 100;
        }
        else
        {
            return null;
        }
        if (minutes > 14 * 60 || minutes % 15 != 0)
        {
            return null;
        }
        return b[i] == (byte)'-' ? -minutes : minutes;
    }

    // Returns true and sets ms. Scans the first `win` bytes for the pattern.
    public bool Parse(byte[] b, int off, int len, out double ms)
    {
        ms = double.NaN;
        LastOffset = null;
        var end = off + Math.Min(len, 56);
        switch (Kind)
        {
            case TsKind.Iso:
                for (var p = off; p + 19 <= end; p++)
                {
                    if (b[p + 4] != (byte)'-' || b[p + 7] != (byte)'-')
                    {
                        continue;
                    }
                    if (!Dig(b[p]) || !Dig(b[p + 1]) || !Dig(b[p + 2]) || !Dig(b[p + 3]))
                    {
                        continue;
                    }
                    if (!Dig(b[p + 5]) || !Dig(b[p + 6]) || !Dig(b[p + 8]) || !Dig(b[p + 9]))
                    {
                        continue;
                    }
                    if (b[p + 10] != (byte)'T' && b[p + 10] != (byte)' ')
                    {
                        continue;
                    }
                    if (b[p + 13] != (byte)':' || b[p + 16] != (byte)':')
                    {
                        continue;
                    }
                    if (!Dig(b[p + 11]) || !Dig(b[p + 14]) || !Dig(b[p + 17]))
                    {
                        continue;
                    }
                    ms = Make(D4(b, p), D2(b, p + 5), D2(b, p + 8), D2(b, p + 11), D2(b, p + 14), D2(b, p + 17),
                              Frac(b, p + 19, off + len));
                    LastEnd = TailEnd(b, p + 19, off + len);
                    return true;
                }
                return false;

            case TsKind.Slash:
                for (var p = off; p + 19 <= end; p++)
                {
                    if (b[p + 4] != (byte)'/' || b[p + 7] != (byte)'/')
                    {
                        continue;
                    }
                    if (!Dig(b[p]) || !Dig(b[p + 3]) || !Dig(b[p + 5]) || !Dig(b[p + 8]))
                    {
                        continue;
                    }
                    if (b[p + 13] != (byte)':' || b[p + 16] != (byte)':')
                    {
                        continue;
                    }
                    ms = Make(D4(b, p), D2(b, p + 5), D2(b, p + 8), D2(b, p + 11), D2(b, p + 14), D2(b, p + 17),
                              Frac(b, p + 19, off + len));
                    LastEnd = TailEnd(b, p + 19, off + len);
                    return true;
                }
                return false;

            case TsKind.Clf:  // 01/Mar/2026:08:14:02
                for (var p = off; p + 20 <= end; p++)
                {
                    if (b[p + 2] != (byte)'/' || b[p + 6] != (byte)'/' || b[p + 11] != (byte)':')
                    {
                        continue;
                    }
                    if (!Dig(b[p]) || !Dig(b[p + 1]) || !Dig(b[p + 7]))
                    {
                        continue;
                    }
                    var mo = MonthAt(b, p + 3); if (mo == 0)
{
    continue;
}
                    if (b[p + 14] != (byte)':' || b[p + 17] != (byte)':')
                    {
                        continue;
                    }
                    ms = Make(D4(b, p + 7), mo, D2(b, p), D2(b, p + 12), D2(b, p + 15), D2(b, p + 18), 0);
                    LastEnd = p + 20;
                    // CLF always writes its zone: "[01/Mar/2026:08:14:02 +0000]".
                    if (p + 26 <= off + len && b[p + 20] == (byte)' ' && (b[p + 21] == (byte)'+' || b[p + 21] == (byte)'-')
                        && Dig(b[p + 22]) && Dig(b[p + 23]) && Dig(b[p + 24]) && Dig(b[p + 25]))
                    {
                        LastOffset = Offset(b, p + 21, p + 26);
                    }
                    return true;
                }
                return false;

            case TsKind.Us:   // 3/1/2026 8:14:02[.123] [AM|PM]  or  03/01/2026 08:14:02
                // Windows' own "M/d/yyyy h:mm:ss tt" drops the leading zero on the
                // month, day and hour, so every part is read as one or two digits
                // rather than at a fixed offset.
                for (var p = off; p < end; p++)
                {
                    if (!Dig(b[p]))
                    {
                        continue;
                    }
                    if (p > off && AsciiTable.IsWord(b[p - 1]))
                    {
                        continue;
                    }   // don't start mid-number

                    int q = p, fin = off + len;
                    var mo = Take(b, ref q, 2, end);
                    if (mo < 0 || q >= end || b[q] != (byte)'/')
                    {
                        continue;
                    }
                    q++;
                    var da = Take(b, ref q, 2, end);
                    if (da < 0 || q >= end || b[q] != (byte)'/')
                    {
                        continue;
                    }
                    q++;
                    var yr = Take(b, ref q, 4, end);
                    if (yr < 1000)
                    {
                        continue;
                    }

                    while (q < end && (b[q] == (byte)' ' || b[q] == (byte)','))
                    {
                        q++;
                    }
                    var h = Take(b, ref q, 2, end);
                    if (h < 0 || q >= end || b[q] != (byte)':')
                    {
                        continue;
                    }
                    q++;
                    var mi = Take(b, ref q, 2, end);
                    if (mi < 0 || q >= end || b[q] != (byte)':')
                    {
                        continue;
                    }
                    q++;
                    var se = Take(b, ref q, 2, end);
                    if (se < 0)
                    {
                        continue;
                    }

                    var fr = Frac(b, q, fin);
                    if (q < fin && (b[q] == (byte)'.' || b[q] == (byte)','))
                    {
                        q++;
                        while (q < fin && Dig(b[q]))
                        {
                            q++;
                        }
                    }

                    while (q < fin && b[q] == (byte)' ')
                    {
                        q++;
                    }
                    if (q + 1 < fin && AsciiTable.ToLowerAscii(b[q + 1]) == (byte)'m')
                    {
                        var a = AsciiTable.ToLowerAscii(b[q]);
                        if (a == (byte)'p' && h < 12)
                        {
                            h += 12;
                        }
                        else if (a == (byte)'a' && h == 12)
{
    h = 0;
}
                        if (a == (byte)'p' || a == (byte)'a')
                        {
                            q += 2;
                        }
                    }

                    ms = Make(yr, mo, da, h, mi, se, fr);
                    LastEnd = q;
                    return true;
                }
                return false;

            case TsKind.Syslog: // Mar  1 08:14:02
                for (var p = off; p + 14 <= end; p++)
                {
                    var mo = MonthAt(b, p); if (mo == 0)
{
    continue;
}
                    var q = p + 3;
                    while (q < end && b[q] == (byte)' ')
                    {
                        q++;
                    }
                    if (q >= end || !Dig(b[q]))
                    {
                        continue;
                    }
                    var day = b[q] - 48; q++;
                    if (q < end && Dig(b[q])) { day = day * 10 + (b[q] - 48); q++; }
                    while (q < end && b[q] == (byte)' ')
                    {
                        q++;
                    }
                    if (q + 8 > end || !Dig(b[q]) || b[q + 2] != (byte)':' || b[q + 5] != (byte)':')
                    {
                        continue;
                    }
                    ms = Make(Year, mo, day, D2(b, q), D2(b, q + 3), D2(b, q + 6), 0);
                    LastEnd = q + 8;
                    return true;
                }
                return false;

            case TsKind.TimeOnly:
                for (var p = off; p + 8 <= end; p++)
                {
                    if (b[p + 2] != (byte)':' || b[p + 5] != (byte)':')
                    {
                        continue;
                    }
                    if (!Dig(b[p]) || !Dig(b[p + 1]) || !Dig(b[p + 3]) || !Dig(b[p + 6]))
                    {
                        continue;
                    }
                    var tod = D2(b, p) * 3600000.0 + D2(b, p + 3) * 60000.0 + D2(b, p + 6) * 1000.0
                              + Frac(b, p + 8, off + len);
                    if (_lastTod >= 0 && tod < _lastTod - 43200000.0)
                    {
                        _dayShift += 86400000L;
                    }
                    _lastTod = tod;
                    ms = BaseDayMs + _dayShift + tod;
                    LastEnd = TailEnd(b, p + 8, off + len);
                    return true;
                }
                return false;
        }
        return false;
    }

    private static double Make(int y, int mo, int d, int h, int mi, int s, int frac)
    {
        if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || s > 60)
        {
            return double.NaN;
        }
        return CivilDate.DaysSinceEpoch(y, mo, d) * 86400000.0 + h * 3600000.0 + mi * 60000.0 + s * 1000.0 + frac;
    }

    public static TsKind Detect(List<byte[]> sample)
    {
        var best = TsKind.None; var bestHits = 0;
        TsKind[] order = { TsKind.Iso, TsKind.Slash, TsKind.Clf, TsKind.Us, TsKind.Syslog, TsKind.TimeOnly };
        foreach (var k in order)
        {
            var p = new TsParser(FixedClock.Instance) { Kind = k, BaseDayMs = 0 };
            var hits = 0;
            foreach (var line in sample)
            {
                double ms;
                if (p.Parse(line, 0, line.Length, out ms) && !double.IsNaN(ms))
                {
                    hits++;
                }
            }
            if (hits > bestHits) { bestHits = hits; best = k; }
            if (bestHits >= sample.Count * 0.9 && k != TsKind.TimeOnly)
            {
                break;
            }
        }
        if (sample.Count == 0 || bestHits < sample.Count * 0.5)
        {
            return TsKind.None;
        }
        return best;
    }
}
