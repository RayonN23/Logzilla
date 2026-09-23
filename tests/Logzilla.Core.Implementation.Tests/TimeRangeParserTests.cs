using System;
using Logzilla.Core.Common;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

[TestFixture]
public class TimeRangeParserTests
{
    // ── TryParseDateTimeMs ────────────────────────────────────────────────────

    [TestCase("2026-01-15 08:00:00", true)]
    [TestCase("2026/01/15 08:00:00", true)]
    [TestCase("2026-01-15T08:00:00", true)]
    [TestCase("2026-01-15 08:00", true)]       // seconds optional
    public void TryParseDateTimeMs_ValidFormats_ReturnsTrue(string s, bool expected)
    {
        Assert.That(TimeRangeParser.TryParseDateTimeMs(s, out _), Is.EqualTo(expected));
    }

    [Test]
    public void TryParseDateTimeMs_ProducesCorrectMs()
    {
        // 2026-01-15 08:30:45 UTC
        Assert.That(TimeRangeParser.TryParseDateTimeMs("2026-01-15 08:30:45", out var ms), Is.True);
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var expected = (new DateTime(2026, 1, 15, 8, 30, 45, DateTimeKind.Utc) - epoch).TotalMilliseconds;
        Assert.That(ms, Is.EqualTo(expected).Within(1.0));
    }

    [Test]
    public void TryParseDateTimeMs_WithoutSeconds_ZeroSeconds()
    {
        Assert.That(TimeRangeParser.TryParseDateTimeMs("2026-01-15 08:30", out var ms), Is.True);
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var expected = (new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc) - epoch).TotalMilliseconds;
        Assert.That(ms, Is.EqualTo(expected).Within(1.0));
    }

    [TestCase("2026-01-15 25:00:00")]     // hour out of range
    [TestCase("2026-01-15 08:60:00")]     // minute out of range
    [TestCase("2026-01-15 08:00:60")]     // second out of range
    [TestCase("2026-13-15 08:00:00")]     // month out of range
    [TestCase("2026-01-32 08:00:00")]     // day out of range
    [TestCase("2026-01-15X08:00:00")]     // bad separator at [10]
    [TestCase("2026.01.15 08:00:00")]     // bad separator at [4]/[7]
    [TestCase("2026-01-15 08-00")]        // colon missing at [13]
    [TestCase("short")]                   // too short
    [TestCase("")]                        // empty
    public void TryParseDateTimeMs_InvalidInputs_ReturnsFalse(string s)
    {
        Assert.That(TimeRangeParser.TryParseDateTimeMs(s, out _), Is.False);
    }

    // ── TryParseTimeOfDay ─────────────────────────────────────────────────────

    [Test]
    public void TryParseTimeOfDay_HHmm()
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay("20:00", out var h, out var m, out var s), Is.True);
        Assert.That(h, Is.EqualTo(20));
        Assert.That(m, Is.EqualTo(0));
        Assert.That(s, Is.EqualTo(0));
    }

    [Test]
    public void TryParseTimeOfDay_HHmmss()
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay("08:30:15", out var h, out var m, out var s), Is.True);
        Assert.That(h, Is.EqualTo(8));
        Assert.That(m, Is.EqualTo(30));
        Assert.That(s, Is.EqualTo(15));
    }

    [Test]
    public void TryParseTimeOfDay_Pm_AddsToHour()
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay("8pm", out var h, out _, out _), Is.True);
        Assert.That(h, Is.EqualTo(20));
    }

    [Test]
    public void TryParseTimeOfDay_12pm_StaysTwelve()
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay("12pm", out var h, out _, out _), Is.True);
        Assert.That(h, Is.EqualTo(12));
    }

    [Test]
    public void TryParseTimeOfDay_12am_BecomesZero()
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay("12am", out var h, out _, out _), Is.True);
        Assert.That(h, Is.EqualTo(0));
    }

    [Test]
    public void TryParseTimeOfDay_11pm_BecomesTwentyThree()
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay("11pm", out var h, out _, out _), Is.True);
        Assert.That(h, Is.EqualTo(23));
    }

    [Test]
    public void TryParseTimeOfDay_AmNoChange_UnderTwelve()
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay("8am", out var h, out _, out _), Is.True);
        Assert.That(h, Is.EqualTo(8));
    }

    [Test]
    public void TryParseTimeOfDay_WithColon_AmPm()
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay("8:30pm", out var h, out var m, out _), Is.True);
        Assert.That(h, Is.EqualTo(20));
        Assert.That(m, Is.EqualTo(30));
    }

    [Test]
    public void TryParseTimeOfDay_LeadingTrailingWhitespace()
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay("  09:00  ", out var h, out var m, out _), Is.True);
        Assert.That(h, Is.EqualTo(9));
        Assert.That(m, Is.EqualTo(0));
    }

    [TestCase("25:00")]   // hour > 23
    [TestCase("08:60")]   // minute > 59
    [TestCase("08:00:60")] // second > 59
    [TestCase("")]         // empty
    [TestCase("abc")]      // non-numeric
    public void TryParseTimeOfDay_InvalidInputs_ReturnsFalse(string s)
    {
        Assert.That(TimeRangeParser.TryParseTimeOfDay(s, out _, out _, out _), Is.False);
    }

    // ── TryParseRange ─────────────────────────────────────────────────────────

    [Test]
    public void TryParseRange_TwoFullDates_BothParsed()
    {
        var ok = TimeRangeParser.TryParseRange("2026-01-15 08:00:00 - 2026-01-15 09:00:00", 0, out var t0, out var t1);
        Assert.That(ok, Is.True);
        Assert.That(t1 - t0, Is.EqualTo(3600000.0).Within(1.0));
    }

    [Test]
    public void TryParseRange_FullDatePlusTimeOnly_SharesCalendarDay()
    {
        var ok = TimeRangeParser.TryParseRange("2026-01-15 08:00:00 - 09:30:00", 0, out var t0, out var t1);
        Assert.That(ok, Is.True);
        Assert.That(t1 - t0, Is.EqualTo(5400000.0).Within(1.0));
    }

    [Test]
    public void TryParseRange_FullDatePlusTimeOnly_MidnightSpan_AddsTwentyFourH()
    {
        // Left is 23:00, right is 01:00 on the same full-date left side — should add 24 h
        var ok = TimeRangeParser.TryParseRange("2026-01-15 23:00:00 - 01:00:00", 0, out var t0, out var t1);
        Assert.That(ok, Is.True);
        Assert.That(t1, Is.GreaterThan(t0));
        Assert.That(t1 - t0, Is.EqualTo(2 * 3600000.0).Within(1.0));
    }

    [Test]
    public void TryParseRange_BothTimeOnly_AnchoredToBaseDay()
    {
        const long baseDay = 20L * 86400000L; // Jan 21 1970
        var ok = TimeRangeParser.TryParseRange("08:00 - 09:00", baseDay, out var t0, out var t1);
        Assert.That(ok, Is.True);
        Assert.That(t0, Is.EqualTo(baseDay + 8 * 3600000.0).Within(1.0));
        Assert.That(t1 - t0, Is.EqualTo(3600000.0).Within(1.0));
    }

    [Test]
    public void TryParseRange_BothTimeOnly_MidnightSpan_AddsTwentyFourH()
    {
        var ok = TimeRangeParser.TryParseRange("23:00 - 01:00", 0, out var t0, out var t1);
        Assert.That(ok, Is.True);
        Assert.That(t1, Is.GreaterThan(t0));
        Assert.That(t1 - t0, Is.EqualTo(2 * 3600000.0).Within(1.0));
    }

    [Test]
    public void TryParseRange_Separator_To()
    {
        var ok = TimeRangeParser.TryParseRange("08:00 to 09:00", 0, out _, out _);
        Assert.That(ok, Is.True);
    }

    [Test]
    public void TryParseRange_Separator_EnDash()
    {
        var ok = TimeRangeParser.TryParseRange("08:00 – 09:00", 0, out _, out _);
        Assert.That(ok, Is.True);
    }

    [Test]
    public void TryParseRange_Separator_EmDash()
    {
        var ok = TimeRangeParser.TryParseRange("08:00 — 09:00", 0, out _, out _);
        Assert.That(ok, Is.True);
    }

    [Test]
    public void TryParseRange_PmFormat_Parsed()
    {
        var ok = TimeRangeParser.TryParseRange("8pm - 9pm", 0, out var t0, out var t1);
        Assert.That(ok, Is.True);
        Assert.That(t1 - t0, Is.EqualTo(3600000.0).Within(1.0));
    }

    [TestCase("")]
    [TestCase("20:00")]                      // no separator
    [TestCase("20:00-21:00")]               // separator has no spaces
    [TestCase("badleft - 21:00")]            // left side unparseable
    [TestCase("2026-01-15 20:00 - bad")]     // right side unparseable after full left date
    public void TryParseRange_InvalidInputs_ReturnsFalse(string s)
    {
        Assert.That(TimeRangeParser.TryParseRange(s, 0, out _, out _), Is.False);
    }

    // ── FormatTimeRange ───────────────────────────────────────────────────────

    [Test]
    public void FormatTimeRange_SameDay_OmitsDateOnRight()
    {
        Assert.That(TimeRangeParser.TryParseDateTimeMs("2026-01-15 08:00:00", out var t0), Is.True);
        Assert.That(TimeRangeParser.TryParseDateTimeMs("2026-01-15 09:30:00", out var t1), Is.True);
        var result = TimeRangeParser.FormatTimeRange(t0, t1);
        Assert.That(result, Is.EqualTo("2026-01-15 08:00:00 - 09:30:00"));
    }

    [Test]
    public void FormatTimeRange_DifferentDays_IncludesDateOnBothSides()
    {
        Assert.That(TimeRangeParser.TryParseDateTimeMs("2026-01-15 22:00:00", out var t0), Is.True);
        Assert.That(TimeRangeParser.TryParseDateTimeMs("2026-01-16 02:00:00", out var t1), Is.True);
        var result = TimeRangeParser.FormatTimeRange(t0, t1);
        Assert.That(result, Is.EqualTo("2026-01-15 22:00:00 - 2026-01-16 02:00:00"));
    }

    [Test]
    public void FormatTimeRange_NaN_ReturnsEmpty()
    {
        Assert.That(TimeRangeParser.FormatTimeRange(double.NaN, 0), Is.EqualTo(""));
        Assert.That(TimeRangeParser.FormatTimeRange(0, double.NaN), Is.EqualTo(""));
        Assert.That(TimeRangeParser.FormatTimeRange(double.NaN, double.NaN), Is.EqualTo(""));
    }

    [Test]
    public void FormatTimeRange_RoundTrips_WithTryParseRange()
    {
        // Format then parse back — should recover the original timestamps.
        Assert.That(TimeRangeParser.TryParseDateTimeMs("2026-06-01 14:00:00", out var t0), Is.True);
        Assert.That(TimeRangeParser.TryParseDateTimeMs("2026-06-01 16:30:00", out var t1), Is.True);
        var formatted = TimeRangeParser.FormatTimeRange(t0, t1);
        var ok = TimeRangeParser.TryParseRange(formatted, 0, out var rt0, out var rt1);
        Assert.That(ok, Is.True);
        Assert.That(rt0, Is.EqualTo(t0).Within(1000.0)); // within 1 s (seconds-level format)
        Assert.That(rt1, Is.EqualTo(t1).Within(1000.0));
    }
}
