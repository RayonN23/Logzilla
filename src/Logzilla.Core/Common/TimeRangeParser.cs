using System;
using System.Globalization;

namespace Logzilla.Core.Common;

// Parses and formats time ranges entered by the user in the TIME toolbar box.
// Lives in Core so it can be unit-tested without pulling in WinForms.

public static class TimeRangeParser
{
    // Splits "left SEP right" (various separators) and resolves both sides to
    // ms-since-epoch.  baseDay is the midnight of the document's first timestamped
    // day, used to anchor time-only strings like "20:00".
    public static bool TryParseRange(string text, long baseDay, out double t0, out double t1)
    {
        t0 = t1 = double.NaN;

        string left = null, right = null;
        foreach (var sep in new[] { " - ", " to ", " – ", " — " })
        {
            var idx = text.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && idx < text.Length - sep.Length)
            {
                left = text.Substring(0, idx).Trim();
                right = text.Substring(idx + sep.Length).Trim();
                break;
            }
        }
        if (left == null || left.Length == 0 || right == null || right.Length == 0)
        {
            return false;
        }

        var leftFull = TryParseDateTimeMs(left, out t0);
        var rightFull = TryParseDateTimeMs(right, out t1);

        if (leftFull && rightFull)
        {
            return true;
        }

        // Full date on the left + time-only on the right: share the same calendar day.
        if (leftFull)
        {
            if (!TryParseTimeOfDay(right, out var h1, out var m1, out var s1))
            {
                return false;
            }
            const long dayMs = 86400000L;
            var leftDay = (long)Math.Floor(t0 / dayMs) * dayMs;
            t1 = leftDay + h1 * 3600000.0 + m1 * 60000.0 + s1 * 1000.0;
            if (t1 < t0)
            {
                t1 += dayMs;
            }
            return true;
        }

        // Both time-only: anchor to the document's first timestamped day.
        if (!TryParseTimeOfDay(left, out var h0, out var m0, out var s0))
        {
            return false;
        }
        if (!TryParseTimeOfDay(right, out var rh, out var rm, out var rs))
        {
            return false;
        }
        t0 = baseDay + h0 * 3600000.0 + m0 * 60000.0 + s0 * 1000.0;
        t1 = baseDay + rh * 3600000.0 + rm * 60000.0 + rs * 1000.0;
        if (t1 < t0)
        {
            t1 += 86400000.0; // spans midnight e.g. 23:00 - 01:00
        }
        return true;
    }

    // Parses HH:mm[:ss][am|pm].  Returns false if out-of-range or unparseable.
    public static bool TryParseTimeOfDay(string s, out int h, out int m, out int sec)
    {
        h = m = sec = 0;
        s = s.Trim();
        if (s.Length == 0)
        {
            return false;
        }

        var pm = s.EndsWith("pm", StringComparison.OrdinalIgnoreCase);
        var am = s.EndsWith("am", StringComparison.OrdinalIgnoreCase);
        if (pm || am)
        {
            s = s.Substring(0, s.Length - 2).TrimEnd();
        }

        var parts = s.Split(':');
        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out h))
        {
            return false;
        }
        if (parts.Length >= 2 && !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out m))
        {
            return false;
        }
        if (parts.Length >= 3 && !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out sec))
        {
            return false;
        }

        if (pm && h < 12)
        {
            h += 12;
        }
        else if (am && h == 12)
        {
            h = 0;
        }

        return h >= 0 && h < 24 && m >= 0 && m < 60 && sec >= 0 && sec < 60;
    }

    // Parses yyyy[-/]MM[-/]dd[ T]HH:mm[:ss] to ms since Unix epoch.
    public static bool TryParseDateTimeMs(string s, out double ms)
    {
        ms = double.NaN;
        s = s.Trim();
        if (s.Length < 16)
        {
            return false;
        }
        if ((s[4] != '-' && s[4] != '/') || (s[7] != '-' && s[7] != '/'))
        {
            return false;
        }
        if (!int.TryParse(s.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var yr))
        {
            return false;
        }
        if (!int.TryParse(s.AsSpan(5, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mo))
        {
            return false;
        }
        if (!int.TryParse(s.AsSpan(8, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dy))
        {
            return false;
        }
        if (s[10] != ' ' && s[10] != 'T')
        {
            return false;
        }
        if (!int.TryParse(s.AsSpan(11, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh))
        {
            return false;
        }
        if (s.Length < 14 || s[13] != ':')
        {
            return false;
        }
        if (!int.TryParse(s.AsSpan(14, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm))
        {
            return false;
        }
        var ss = 0;
        if (s.Length >= 19 && s[16] == ':' && !int.TryParse(s.AsSpan(17, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out ss))
        {
            return false;
        }
        if (mo < 1 || mo > 12 || dy < 1 || dy > 31 || hh > 23 || mm > 59 || ss > 59)
        {
            return false;
        }

        ms = CivilDate.DaysSinceEpoch(yr, mo, dy) * 86400000.0 + hh * 3600000.0 + mm * 60000.0 + ss * 1000.0;
        return true;
    }

    // Formats a time range for display in the TIME toolbar box.
    // If both timestamps fall on the same calendar day, the right side omits the date.
    public static string FormatTimeRange(double t0, double t1)
    {
        if (double.IsNaN(t0) || double.IsNaN(t1))
        {
            return "";
        }
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dt0 = epoch.AddMilliseconds(t0);
        var dt1 = epoch.AddMilliseconds(t1);
        var left = dt0.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var right = dt0.Date == dt1.Date
            ? dt1.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : dt1.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return left + " - " + right;
    }
}
