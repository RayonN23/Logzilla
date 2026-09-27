using System;
using System.Collections.Generic;
using System.Globalization;
using Logzilla.Core.Formats;

namespace Logzilla.Core.Logs;

/// <summary>
/// Moves times between the clock they are stored in and the one they are read in.
/// A file whose zone is known — from its own stamps, or because the user said —
/// stores real instants (ms since the epoch, UTC), so a merge interleaves by what
/// actually happened first. A file whose zone nobody knows stores its wall clock
/// as written, and is shown that way in every mode.
/// </summary>
public static class TimeZones
{
    private const double MsPerMinute = 60000.0;
    private const long TicksPerMs = 10000L;
    private static readonly long EpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    /// <summary>True when the stored times of the source are real instants.</summary>
    public static bool IsKnown(LogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Zoned || source.Declared != null;
    }

    /// <summary>Timed, with no zone in its stamps, and nobody has said which it is.</summary>
    public static bool NeedsDeclaring(LogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind != TsKind.None && !source.Zoned && source.Declared == null;
    }

    /// <summary>A stored time as it reads in the given mode.</summary>
    public static double Display(double ms, LogSource source, TimeMode mode, TimeZoneInfo local)
    {
        if (double.IsNaN(ms) || source == null || !IsKnown(source))
        {
            return ms;
        }
        switch (mode)
        {
            case TimeMode.Utc:
                return ms;
            case TimeMode.Local:
                return ToWall(local, ms);
            default:
                return source.Zoned ? ms + OffsetAt(source.Offsets, ms) * MsPerMinute : ToWall(source.Declared, ms);
        }
    }

    /// <summary>The inverse of <see cref="Display"/>: a time typed in the given mode, as stored.</summary>
    public static double Stored(double shown, LogSource source, TimeMode mode, TimeZoneInfo local)
    {
        if (double.IsNaN(shown) || source == null || !IsKnown(source))
        {
            return shown;
        }
        switch (mode)
        {
            case TimeMode.Utc:
                return shown;
            case TimeMode.Local:
                return ToInstant(local, shown);
            default:
                if (!source.Zoned)
                {
                    return ToInstant(source.Declared, shown);
                }
                // The offset in force depends on the instant being solved for; one
                // refinement settles it everywhere but inside a changeover hour.
                var guess = shown - OffsetAt(source.Offsets, shown) * MsPerMinute;
                return shown - OffsetAt(source.Offsets, guess) * MsPerMinute;
        }
    }

    /// <summary>
    /// The source whose clock stands for the whole document, for times that belong to
    /// no one line: the timeline axis, the first and last entry, the TIME box.
    /// </summary>
    public static LogSource Reference(LogDocument document)
    {
        if (document == null || document.Srcs.Count == 0)
        {
            return null;
        }
        foreach (var s in document.Srcs)
        {
            if (IsKnown(s))
            {
                return s;
            }
        }
        return document.Srcs[0];
    }

    /// <summary>The offset in minutes in force at an instant; the first one before any.</summary>
    public static int OffsetAt(IReadOnlyList<ZoneOffset> offsets, double instant)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        if (offsets.Count == 0)
        {
            return 0;
        }
        int lo = 0, hi = offsets.Count - 1, found = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (offsets[mid].FromMs <= instant)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return offsets[found].Minutes;
    }

    /// <summary>An instant as the wall clock of <paramref name="zone"/> reads it.</summary>
    public static double ToWall(TimeZoneInfo zone, double instantMs)
    {
        if (zone == null || double.IsNaN(instantMs) || !TryDate(instantMs, DateTimeKind.Utc, out var dt))
        {
            return instantMs;
        }
        return instantMs + zone.GetUtcOffset(dt).TotalMilliseconds;
    }

    /// <summary>
    /// A wall-clock time in <paramref name="zone"/> as an instant. A time the clocks
    /// skipped takes the standard offset; one they repeated is read as standard time,
    /// the second time round — the stamp alone cannot say which was meant.
    /// </summary>
    public static double ToInstant(TimeZoneInfo zone, double wallMs)
    {
        if (zone == null || double.IsNaN(wallMs) || !TryDate(wallMs, DateTimeKind.Unspecified, out var dt))
        {
            return wallMs;
        }
        return wallMs - zone.GetUtcOffset(dt).TotalMilliseconds;
    }

    /// <summary>
    /// Re-reads one source's stored times after its declared zone changes, in a
    /// document that holds it. The source's <see cref="LogSource.Declared"/> is left
    /// for the caller to set once every document has been shifted.
    /// </summary>
    public static void Shift(LogDocument document, LogSource source, TimeZoneInfo from, TimeZoneInfo to)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        var si = document.Srcs.IndexOf(source);
        if (si < 0 || source.Zoned)
        {
            return;
        }
        var back = from == null ? null : new ZoneClock(from);
        var ahead = to == null ? null : new ZoneClock(to);
        var ts = document.Ts;
        for (var i = 0; i < document.N; i++)
        {
            if (document.Src[i] != si || double.IsNaN(ts[i]))
            {
                continue;
            }
            var wall = back == null ? ts[i] : back.ToWall(ts[i]);
            ts[i] = ahead == null ? wall : ahead.ToInstant(wall);
        }
    }

    /// <summary>
    /// A guess at the zone of stamps that carry none, from when the file was last
    /// saved: a log that was still being written ends at about that moment. Only a
    /// suggestion — a file that stopped long before it was saved says nothing.
    /// </summary>
    public static ZoneHint Suggest(double lastWallMs, DateTime lastWriteUtc, TimeZoneInfo local)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (double.IsNaN(lastWallMs) || lastWriteUtc == DateTime.MinValue)
        {
            return ZoneHint.None;
        }
        var savedUtc = (DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc).Ticks - EpochTicks) / (double)TicksPerMs;
        var offset = ToWall(local, savedUtc) - savedUtc;
        if (offset == 0)
        {
            return ZoneHint.None;   // on this machine the two read the same
        }
        // Close enough to be the same moment, and never so loose the two could be confused.
        var tolerance = Math.Min(10 * MsPerMinute, Math.Abs(offset) / 3);
        if (Math.Abs(lastWallMs - (savedUtc + offset)) <= tolerance)
        {
            return ZoneHint.Local;
        }
        if (Math.Abs(lastWallMs - savedUtc) <= tolerance)
        {
            return ZoneHint.Utc;
        }
        return ZoneHint.None;
    }

    /// <summary>"UTC", "UTC+02:00", "UTC-05:30".</summary>
    public static string FormatOffset(int minutes)
    {
        if (minutes == 0)
        {
            return "UTC";
        }
        var a = Math.Abs(minutes);
        return "UTC" + (minutes < 0 ? "-" : "+") +
               (a / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
               (a % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>How a source's zone reads in the side panel.</summary>
    public static string Describe(LogSource source, TimeZoneInfo local)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == TsKind.None)
        {
            return "no timestamps";
        }
        if (source.Zoned)
        {
            var seen = new List<int>();
            foreach (var o in source.Offsets)
            {
                if (!seen.Contains(o.Minutes))
                {
                    seen.Add(o.Minutes);
                }
            }
            if (seen.Count == 0)
            {
                return "in the stamps";
            }
            var parts = new List<string>();
            foreach (var m in seen)
            {
                parts.Add(FormatOffset(m));
            }
            return string.Join(" / ", parts) + ", from the stamps";
        }
        if (source.Declared == null)
        {
            return "not stated";
        }
        return Name(source.Declared, local) switch
        {
            "local" => "local time, set by you",
            "utc" => "UTC, set by you",
            var n => "UTC" + n + ", set by you",
        };
    }

    /// <summary>A declared zone as it is remembered: "utc", "local" or "+05:30".</summary>
    public static string Name(TimeZoneInfo zone, TimeZoneInfo local)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (local != null && (ReferenceEquals(zone, local) || zone.Id == local.Id))
        {
            return "local";
        }
        if (zone.Id == TimeZoneInfo.Utc.Id)
        {
            return "utc";
        }
        var minutes = (int)zone.BaseUtcOffset.TotalMinutes;
        return minutes == 0 ? "utc" : FormatOffset(minutes)["UTC".Length..];
    }

    /// <summary>The inverse of <see cref="Name"/>; null for anything it does not recognise.</summary>
    public static TimeZoneInfo FromName(string name, TimeZoneInfo local)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        name = name.Trim();
        if (name.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return local;
        }
        if (name.Equals("utc", StringComparison.OrdinalIgnoreCase) || name.Equals("z", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }
        if (name.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
        {
            name = name[3..].Trim();
        }
        return TryParseOffset(name, out var minutes) ? Fixed(minutes) : null;
    }

    /// <summary>"+2", "+02", "+0200", "+02:00", "-5:30" in minutes.</summary>
    public static bool TryParseOffset(string text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        text = text.Trim();
        var sign = 1;
        if (text[0] == '+' || text[0] == '-')
        {
            sign = text[0] == '-' ? -1 : 1;
            text = text[1..];
        }
        int h, m = 0;
        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out h) ||
                !int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out m))
            {
                return false;
            }
        }
        else if (text.Length == 4)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var hm))
            {
                return false;
            }
            h = hm / 100;
            m = hm % 100;
        }
        else if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out h))
        {
            return false;
        }
        if (h > 14 || m > 59 || (h * 60 + m) % 15 != 0 || h * 60 + m > 14 * 60)
        {
            return false;
        }
        minutes = sign * (h * 60 + m);
        return true;
    }

    /// <summary>A zone that is always <paramref name="minutes"/> from UTC.</summary>
    public static TimeZoneInfo Fixed(int minutes)
    {
        if (minutes == 0)
        {
            return TimeZoneInfo.Utc;
        }
        var name = FormatOffset(minutes);
        return TimeZoneInfo.CreateCustomTimeZone(name, TimeSpan.FromMinutes(minutes), name, name);
    }

    internal static bool TryDate(double ms, DateTimeKind kind, out DateTime date)
    {
        date = default;
        if (ms < -62135596800000.0 || ms > 253402300799000.0)
        {
            return false;
        }
        date = new DateTime(EpochTicks + (long)(ms * TicksPerMs), kind);
        return true;
    }
}

/// <summary>
/// Converts between one zone's wall clock and instants, remembering the offset of
/// the quarter hour last asked about. Log times arrive in order, and the clocks
/// only ever change on a quarter hour, so a file of millions of lines asks the
/// time zone rules a handful of times rather than once a line.
/// </summary>
public sealed class ZoneClock
{
    private const double Quarter = 15 * 60000.0;

    private readonly TimeZoneInfo _zone;
    private readonly bool _fixed;
    private double _wallKey = double.NaN, _wallOffset;
    private double _instantKey = double.NaN, _instantOffset;

    public ZoneClock(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        _zone = zone;
        _fixed = !zone.SupportsDaylightSavingTime;
        _wallOffset = _instantOffset = zone.BaseUtcOffset.TotalMilliseconds;
    }

    public double ToInstant(double wallMs)
    {
        if (double.IsNaN(wallMs) || _fixed)
        {
            return wallMs - _wallOffset;
        }
        var key = Math.Floor(wallMs / Quarter);
        if (key != _wallKey)
        {
            _wallKey = key;
            _wallOffset = wallMs - TimeZones.ToInstant(_zone, wallMs);
        }
        return wallMs - _wallOffset;
    }

    public double ToWall(double instantMs)
    {
        if (double.IsNaN(instantMs) || _fixed)
        {
            return instantMs + _instantOffset;
        }
        var key = Math.Floor(instantMs / Quarter);
        if (key != _instantKey)
        {
            _instantKey = key;
            _instantOffset = TimeZones.ToWall(_zone, instantMs) - instantMs;
        }
        return instantMs + _instantOffset;
    }
}
