using System.Collections.Generic;
using System.Text;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Implementation.Formats;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// Timestamp detection and parsing. These run on raw bytes, so the tests feed
/// bytes too rather than going through a file.
/// </summary>
[TestFixture]
public class TsParserTests
{
    private static byte[] B(string s) { return Encoding.UTF8.GetBytes(s); }

    private static List<byte[]> Sample(params string[] lines)
    {
        var outp = new List<byte[]>();
        foreach (var l in lines) 
        {
            outp.Add(B(l));
        }
        return outp;
    }

    private static double Parse(TsKind kind, string line, int year = 2026, long baseDay = 0)
    {
        var p = new TsParser(TestClock.Instance) { Kind = kind, Year = year, BaseDayMs = baseDay };
        var b = B(line);
        Assert.That(p.Parse(b, 0, b.Length, out var ms), Is.True, "no timestamp found in: " + line);
        return ms;
    }

    [TestCase("2026-03-01 08:14:02.113 INFO started", TsKind.Iso)]
    [TestCase("2026/03/01 08:14:02 INFO started", TsKind.Slash)]
    [TestCase("10.1.2.3 - - [01/Mar/2026:08:14:02 +0000] \"GET / HTTP/1.1\" 200", TsKind.Clf)]
    [TestCase("03/01/2026 8:14:02 AM  INFO started", TsKind.Us)]
    [TestCase("Mar  1 08:14:02 web01 sshd[1]: accepted", TsKind.Syslog)]
    [TestCase("08:14:02.113 INFO started", TsKind.TimeOnly)]
    public void DetectsEachSupportedFormat(string line, TsKind expected)
    {
        Assert.That(TsParser.Detect(Sample(line, line, line, line)), Is.EqualTo(expected));
    }

    [Test]
    public void DetectReturnsNoneForUnstructuredText()
    {
        Assert.That(TsParser.Detect(Sample("hello world", "nothing here", "still nothing")), Is.EqualTo(TsKind.None));
    }

    [Test]
    public void IsoKeepsMillisecondPrecision()
    {
        var ms = Parse(TsKind.Iso, "2026-03-01 08:14:02.113 INFO x");
        Assert.That(DisplayFormatter.Time(ms), Is.EqualTo("2026-03-01 08:14:02.113"));
    }

    [Test]
    public void ClfMonthNameIsUnderstood()
    {
        var ms = Parse(TsKind.Clf, "10.1.2.3 - - [01/Mar/2026:08:14:02 +0000] \"GET / HTTP/1.1\" 200");
        Assert.That(DisplayFormatter.Time(ms), Is.EqualTo("2026-03-01 08:14:02.000"));
    }

    [Test]
    public void UsFormatAppliesPmOffset()
    {
        var ms = Parse(TsKind.Us, "03/01/2026 8:14:02 PM  INFO x");
        Assert.That(DisplayFormatter.Time(ms), Is.EqualTo("2026-03-01 20:14:02.000"));
    }

    /// <summary>Windows' default short date drops leading zeroes, so
    /// "3/1/2026 8:14:02 AM" has to parse as readily as "03/01/2026 08:14:02".</summary>
    [Test]
    public void UsFormatAcceptsSingleDigitParts()
    {
        var ms = Parse(TsKind.Us, "3/1/2026 8:14:02 AM  INFO x");
        Assert.That(DisplayFormatter.Time(ms), Is.EqualTo("2026-03-01 08:14:02.000"));
    }

    [Test]
    public void UsFormatMapsTwelveAmToMidnight()
    {
        var ms = Parse(TsKind.Us, "03/01/2026 12:30:00 AM  INFO x");
        Assert.That(DisplayFormatter.Time(ms), Is.EqualTo("2026-03-01 00:30:00.000"));
    }

    [Test]
    public void SyslogTakesTheYearFromTheFile()
    {
        var ms = Parse(TsKind.Syslog, "Mar  1 08:14:02 web01 sshd[1]: x", year: 2026);
        Assert.That(DisplayFormatter.Time(ms), Is.EqualTo("2026-03-01 08:14:02.000"));
    }

    /// <summary>
    /// Time-only logs carry no date, so midnight has to be inferred from the
    /// clock going backwards. Without this a night-time log folds onto itself.
    /// </summary>
    [Test]
    public void TimeOnlyRollsOverAtMidnight()
    {
        var day = CivilDate.DaysSinceEpoch(2026, 5, 4) * 86400000L;
        var p = new TsParser(TestClock.Instance) { Kind = TsKind.TimeOnly, BaseDayMs = day };

        double a, b, c;
        var l1 = B("23:59:58.100 INFO tick");
        var l2 = B("23:59:59.900 INFO tick");
        var l3 = B("00:00:01.000 ERROR rolled over");

        Assert.That(p.Parse(l1, 0, l1.Length, out a), Is.True);
        Assert.That(p.Parse(l2, 0, l2.Length, out b), Is.True);
        Assert.That(p.Parse(l3, 0, l3.Length, out c), Is.True);

        Assert.That(c > b, Is.True, "the entry after midnight must sort after the one before it");
        Assert.That(DisplayFormatter.Time(c), Does.StartWith("2026-05-05"));
    }

    [Test]
    public void RejectsImpossibleDates()
    {
        var p = new TsParser(TestClock.Instance) { Kind = TsKind.Iso };
        var b = B("2026-13-45 99:99:99 INFO x");
        double ms;
        var found = p.Parse(b, 0, b.Length, out ms);
        Assert.That(!found || double.IsNaN(ms), Is.True);
    }
}

[TestFixture]
public class LevelScanTests
{
    private static byte Scan(string line)
    {
        var b = Encoding.UTF8.GetBytes(line);
        return LevelScan.Scan(b, 0, b.Length);
    }

    [TestCase("2026-03-01 08:14:02 TRACE x", 1)]
    [TestCase("2026-03-01 08:14:02 DEBUG x", 2)]
    [TestCase("2026-03-01 08:14:02 INFO x", 3)]
    [TestCase("2026-03-01 08:14:02 WARN x", 4)]
    [TestCase("2026-03-01 08:14:02 WARNING x", 4)]
    [TestCase("2026-03-01 08:14:02 ERROR x", 5)]
    [TestCase("2026-03-01 08:14:02 FATAL x", 6)]
    [TestCase("2026-03-01 08:14:02 CRITICAL x", 6)]
    public void ReadsCommonSeverityWords(string line, int expected)
    {
        Assert.That(Scan(line), Is.EqualTo((byte)expected));
    }

    [Test]
    public void IsCaseInsensitive()
    {
        Assert.That(Scan("2026-03-01 08:14:02 error something failed"), Is.EqualTo((byte)5));
    }

    /// <summary>A severity word must stand alone, or identifiers like
    /// "ERRORS_TOTAL" would be misread as an error.</summary>
    [Test]
    public void IgnoresSeverityWordsEmbeddedInIdentifiers()
    {
        Assert.That(Scan("2026-03-01 08:14:02 ERRORS_TOTAL=4 counter updated"), Is.EqualTo((byte)0));
        Assert.That(Scan("2026-03-01 08:14:02 metric infoCount=3"), Is.EqualTo((byte)0));
    }

    [Test]
    public void ReturnsNoneWhenAbsent()
    {
        Assert.That(Scan("2026-03-01 08:14:02 plain message with no level"), Is.EqualTo((byte)0));
    }
}
