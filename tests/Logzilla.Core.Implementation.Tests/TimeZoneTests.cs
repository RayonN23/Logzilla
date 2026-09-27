using System;
using System.IO;
using System.Text;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Implementation.Formats;
using Logzilla.Core.Implementation.Logs;
using Logzilla.Core.Logs;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// Time zones: reading the offset a stamp carries, storing instants for files whose
/// zone is known, converting for display, and re-sorting a merge when a file's zone
/// is declared after the fact.
/// </summary>
[TestFixture]
public class TimeZoneTests
{
    private const double Hour = 3600000.0;
    private static readonly int[] FirstLine = [0];

    /// <summary>
    /// A zone with European rules at +01:00, built here so the tests do not depend on
    /// the machine's own zone. In 2026 its clocks go forward on 29 March at 02:00 and
    /// back on 25 October at 03:00.
    /// </summary>
    private static readonly TimeZoneInfo Central = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Central", TimeSpan.FromHours(1), "Test Central", "Test Central", "Test Central Summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    private string _directory;

    [SetUp]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cn-tz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void RemoveDirectory()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static double Ms(int y, int mo, int d, int h, int mi, int s = 0) =>
        CivilDate.DaysSinceEpoch(y, mo, d) * 86400000.0 + h * Hour + mi * 60000.0 + s * 1000.0;

    private string WriteLog(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static LogIndexer NewIndexer() => new(TestClock.Instance);

    private static LogDocument Load(LogIndexer indexer, string path, TimeZoneInfo declared = null)
    {
        var source = indexer.Prepare(path);
        source.Declared = declared;
        var document = new LogDocument { Name = source.Name };
        document.Srcs.Add(source);
        indexer.Run(document, 0, progress: null, cancelled: null);
        return document;
    }

    private static int? OffsetOf(TsKind kind, string line)
    {
        var p = new TsParser(TestClock.Instance) { Kind = kind };
        var b = Encoding.UTF8.GetBytes(line);
        Assert.That(p.Parse(b, 0, b.Length, out _), Is.True, "no timestamp found in: " + line);
        return p.LastOffset;
    }

    // ── reading the zone off a stamp ─────────────────────────────────

    [TestCase("2026-03-01T08:14:02.113Z INFO up", 0)]
    [TestCase("2026-03-01T08:14:02.1134567Z INFO up", 0)]
    [TestCase("2026-03-01T08:14:02+02:00 INFO up", 120)]
    [TestCase("2026-03-01 08:14:02.113+0530 INFO up", 330)]
    [TestCase("2026-03-01T08:14:02-05:00 INFO up", -300)]
    [TestCase("2026-03-01T08:14:02+01 INFO up", 60)]
    public void AnIsoStampReportsItsOffset(string line, int minutes)
    {
        Assert.That(OffsetOf(TsKind.Iso, line), Is.EqualTo(minutes));
    }

    [TestCase("2026-03-01 08:14:02.113 INFO up")]
    [TestCase("2026-03-01 08:14:02 - worker started")]
    public void AnIsoStampWithoutAZoneReportsNone(string line)
    {
        Assert.That(OffsetOf(TsKind.Iso, line), Is.Null);
    }

    [TestCase("10.1.2.3 - - [01/Mar/2026:08:14:02 +0000] \"GET / HTTP/1.1\" 200", 0)]
    [TestCase("10.1.2.3 - - [01/Mar/2026:08:14:02 -0700] \"GET / HTTP/1.1\" 200", -420)]
    public void AClfStampReportsItsOffset(string line, int minutes)
    {
        Assert.That(OffsetOf(TsKind.Clf, line), Is.EqualTo(minutes));
    }

    [Test]
    public void TheParserStillReturnsTheWallClockAsWritten()
    {
        var p = new TsParser(TestClock.Instance) { Kind = TsKind.Iso };
        var b = Encoding.UTF8.GetBytes("2026-03-01T08:14:02+02:00 INFO up");
        p.Parse(b, 0, b.Length, out var ms);

        Assert.That(ms, Is.EqualTo(Ms(2026, 3, 1, 8, 14, 2)), "Applying the offset is the indexer's call, not the parser's.");
    }

    [Test]
    public void AStampWithoutAZoneDoesNotInheritTheLastOne()
    {
        var p = new TsParser(TestClock.Instance) { Kind = TsKind.Iso };
        var zoned = Encoding.UTF8.GetBytes("2026-03-01T08:14:02Z up");
        var plain = Encoding.UTF8.GetBytes("2026-03-01 08:14:03 up");
        p.Parse(zoned, 0, zoned.Length, out _);
        p.Parse(plain, 0, plain.Length, out _);

        Assert.That(p.LastOffset, Is.Null);
    }

    // ── indexing: instants where the zone is known ───────────────────

    [Test]
    public void AFileWhoseStampsCarryAZoneStoresInstants()
    {
        var doc = Load(NewIndexer(), WriteLog("zoned.log",
            "2026-03-01T10:00:00+02:00 INFO started\n" +
            "2026-03-01T10:00:05+02:00 INFO ready\n"));

        Assert.That(doc.Srcs[0].Zoned, Is.True);
        Assert.That(doc.Ts[0], Is.EqualTo(Ms(2026, 3, 1, 8, 0)), "10:00 at +02:00 happened at 08:00 UTC.");
        Assert.That(doc.Srcs[0].Offsets, Has.Count.EqualTo(1));
        Assert.That(doc.Srcs[0].Offsets[0].Minutes, Is.EqualTo(120));
    }

    [Test]
    public void AFileWithoutZonesStoresTheWallClock()
    {
        var doc = Load(NewIndexer(), WriteLog("plain.log", "2026-03-01 10:00:00 INFO started\n"));

        Assert.That(doc.Srcs[0].Zoned, Is.False);
        Assert.That(doc.Ts[0], Is.EqualTo(Ms(2026, 3, 1, 10, 0)));
    }

    [Test]
    public void AClefLogInUtcIsZonedAndUnchanged()
    {
        var doc = Load(NewIndexer(), WriteLog("clef.log",
            "{\"@t\":\"2026-06-18T16:33:45.3604385Z\",\"@mt\":\"Hello\"}\n"));

        Assert.That(doc.Srcs[0].IsJson, Is.True);
        Assert.That(doc.Srcs[0].Zoned, Is.True);
        Assert.That(DisplayFormatter.Time(doc.Ts[0]), Is.EqualTo("2026-06-18 16:33:45.360"));
    }

    [Test]
    public void AChangeOfOffsetMidFileIsRecordedAndReadsBackAsWritten()
    {
        // The clocks go back at 03:00 local: 02:59:59 summer time, then 02:00:00 winter.
        var doc = Load(NewIndexer(), WriteLog("dst.log",
            "2026-10-25T02:59:59+02:00 INFO before\n" +
            "2026-10-25T02:00:00+01:00 INFO after\n"));

        var s = doc.Srcs[0];
        Assert.That(s.Offsets, Has.Count.EqualTo(2));
        Assert.That(doc.Ts[1], Is.GreaterThan(doc.Ts[0]), "Stored as instants, the second line comes after the first.");
        Assert.That(DisplayFormatter.Time(TimeZones.Display(doc.Ts[0], s, TimeMode.AsWritten, Central)), Is.EqualTo("2026-10-25 02:59:59.000"));
        Assert.That(DisplayFormatter.Time(TimeZones.Display(doc.Ts[1], s, TimeMode.AsWritten, Central)), Is.EqualTo("2026-10-25 02:00:00.000"));
    }

    [Test]
    public void ADeclaredZoneIsAppliedWhileIndexing()
    {
        var doc = Load(NewIndexer(), WriteLog("plain.log", "2026-03-01 10:00:00 INFO started\n"), TimeZones.Fixed(120));

        Assert.That(doc.Ts[0], Is.EqualTo(Ms(2026, 3, 1, 8, 0)));
    }

    [Test]
    public void ContinuationLinesShareTheirEntrysInstant()
    {
        var doc = Load(NewIndexer(), WriteLog("trace.log",
            "2026-03-01T10:00:00+02:00 ERROR boom\n" +
            "\tat Foo.Bar()\n"));

        Assert.That(doc.Ts[1], Is.EqualTo(doc.Ts[0]));
    }

    // ── merging by instant ───────────────────────────────────────────

    [Test]
    public void MergingInterleavesByWhatHappenedFirstNotByTheWallClock()
    {
        var indexer = NewIndexer();
        // 10:00 at +02:00 is 08:00 UTC, an hour before the UTC file's 09:00.
        var a = Load(indexer, WriteLog("a.log", "2026-03-01T10:00:00+02:00 INFO from a\n"));
        var b = Load(indexer, WriteLog("b.log", "2026-03-01T09:00:00Z INFO from b\n"));

        var m = indexer.Merge([b, a]);

        Assert.That(m.SrcOf(0).Name, Is.EqualTo("a.log"));
        Assert.That(m.SrcOf(1).Name, Is.EqualTo("b.log"));
    }

    [Test]
    public void DeclaringAZoneAfterAMergeReordersItAndMarksFollow()
    {
        var indexer = NewIndexer();
        var utc = Load(indexer, WriteLog("utc.log",
            "2026-03-01T09:00:00Z INFO u1\n" +
            "2026-03-01T10:00:00Z INFO u2\n"));
        var plain = Load(indexer, WriteLog("plain.log",
            "2026-03-01 09:30:00 ERROR p1\n" +
            "\tat Foo.Bar()\n"));
        var m = indexer.Merge([utc, plain]);
        Assert.That(m.SrcOf(1).Name, Is.EqualTo("plain.log"), "Undeclared, 09:30 as written sits between 09:00 and 10:00.");
        m.Marks.Add(1);

        // Written at +02:00, 09:30 was 07:30 UTC: before everything in the other file.
        var source = m.Srcs[1];
        var zone = TimeZones.Fixed(120);
        TimeZones.Shift(m, source, source.Declared, zone);
        source.Declared = zone;
        indexer.Reorder(m);

        Assert.That(m.SrcOf(0).Name, Is.EqualTo("plain.log"));
        Assert.That(m.Flag[1], Is.EqualTo(1), "The stack trace travels with its entry.");
        Assert.That(m.SrcOf(1).Name, Is.EqualTo("plain.log"));
        Assert.That(m.SrcOf(2).Name, Is.EqualTo("utc.log"));
        Assert.That(m.Marks, Is.EquivalentTo(FirstLine), "The mark follows its line.");
    }

    [Test]
    public void ShiftingBackToUndeclaredRestoresTheWallClock()
    {
        var doc = Load(NewIndexer(), WriteLog("plain.log", "2026-07-01 10:00:00 INFO started\n"));
        var s = doc.Srcs[0];

        TimeZones.Shift(doc, s, null, Central);
        s.Declared = Central;
        Assert.That(doc.Ts[0], Is.EqualTo(Ms(2026, 7, 1, 8, 0)), "Summer time there is +02:00.");

        TimeZones.Shift(doc, s, Central, null);
        s.Declared = null;
        Assert.That(doc.Ts[0], Is.EqualTo(Ms(2026, 7, 1, 10, 0)));
    }

    // ── display ──────────────────────────────────────────────────────

    [Test]
    public void AZonedTimeReadsInEachMode()
    {
        var s = new LogSource { Kind = TsKind.Iso, Zoned = true };
        s.Offsets.Add(new ZoneOffset(0, -300));
        var instant = Ms(2026, 7, 1, 12, 0);

        Assert.That(TimeZones.Display(instant, s, TimeMode.Utc, Central), Is.EqualTo(Ms(2026, 7, 1, 12, 0)));
        Assert.That(TimeZones.Display(instant, s, TimeMode.Local, Central), Is.EqualTo(Ms(2026, 7, 1, 14, 0)));
        Assert.That(TimeZones.Display(instant, s, TimeMode.AsWritten, Central), Is.EqualTo(Ms(2026, 7, 1, 7, 0)));
    }

    [TestCase(TimeMode.AsWritten)]
    [TestCase(TimeMode.Local)]
    [TestCase(TimeMode.Utc)]
    public void StoredUndoesDisplay(TimeMode mode)
    {
        var zoned = new LogSource { Kind = TsKind.Iso, Zoned = true };
        zoned.Offsets.Add(new ZoneOffset(0, 330));
        var declared = new LogSource { Kind = TsKind.Iso, Declared = Central };
        var instant = Ms(2026, 7, 1, 12, 0);

        foreach (var s in new[] { zoned, declared })
        {
            var shown = TimeZones.Display(instant, s, mode, Central);
            Assert.That(TimeZones.Stored(shown, s, mode, Central), Is.EqualTo(instant));
        }
    }

    [TestCase(TimeMode.AsWritten)]
    [TestCase(TimeMode.Local)]
    [TestCase(TimeMode.Utc)]
    public void AFileWhoseZoneNobodyKnowsReadsAsWrittenInEveryMode(TimeMode mode)
    {
        var s = new LogSource { Kind = TsKind.Iso };
        var wall = Ms(2026, 7, 1, 12, 0);

        Assert.That(TimeZones.Display(wall, s, mode, Central), Is.EqualTo(wall));
        Assert.That(TimeZones.NeedsDeclaring(s), Is.True);
    }

    [Test]
    public void AFileWithoutTimestampsNeverNeedsAZone()
    {
        Assert.That(TimeZones.NeedsDeclaring(new LogSource { Kind = TsKind.None }), Is.False);
    }

    [Test]
    public void ReferenceIsTheFirstFileWhoseZoneIsKnown()
    {
        var doc = new LogDocument();
        doc.Srcs.Add(new LogSource { Name = "plain", Kind = TsKind.Iso });
        doc.Srcs.Add(new LogSource { Name = "zoned", Kind = TsKind.Iso, Zoned = true });

        Assert.That(TimeZones.Reference(doc).Name, Is.EqualTo("zoned"));
    }

    [Test]
    public void TimesTheClocksSkippedOrRepeatedStillConvert()
    {
        // 02:30 never happened on 29 March; it happened twice on 25 October.
        Assert.That(TimeZones.ToInstant(Central, Ms(2026, 3, 29, 2, 30)), Is.EqualTo(Ms(2026, 3, 29, 1, 30)));
        Assert.That(TimeZones.ToInstant(Central, Ms(2026, 10, 25, 2, 30)), Is.EqualTo(Ms(2026, 10, 25, 1, 30)));
    }

    [Test]
    public void TheCachedClockAgreesWithTheZoneRulesAcrossBothChanges()
    {
        var clock = new ZoneClock(Central);
        foreach (var day in new[] { Ms(2026, 3, 28, 0, 0), Ms(2026, 10, 24, 0, 0) })
        {
            for (var t = day; t < day + 72 * Hour; t += 7 * 60000.0 + 13)
            {
                Assert.That(clock.ToWall(t), Is.EqualTo(TimeZones.ToWall(Central, t)));
                Assert.That(clock.ToInstant(t), Is.EqualTo(TimeZones.ToInstant(Central, t)));
            }
        }
    }

    // ── the suggestion ───────────────────────────────────────────────

    [Test]
    public void ALogEndingAtItsSaveTimeOnTheLocalClockLooksLocal()
    {
        var saved = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);   // 14:00 in Central
        Assert.That(TimeZones.Suggest(Ms(2026, 7, 1, 13, 58), saved, Central), Is.EqualTo(ZoneHint.Local));
    }

    [Test]
    public void ALogEndingAtItsSaveTimeInUtcLooksUtc()
    {
        var saved = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.That(TimeZones.Suggest(Ms(2026, 7, 1, 11, 59), saved, Central), Is.EqualTo(ZoneHint.Utc));
    }

    [Test]
    public void ALogThatStoppedLongBeforeItWasSavedSuggestsNothing()
    {
        var saved = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.That(TimeZones.Suggest(Ms(2026, 6, 30, 9, 0), saved, Central), Is.EqualTo(ZoneHint.None));
    }

    [Test]
    public void OnAUtcMachineTheSuggestionHasNothingToTellApart()
    {
        var saved = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.That(TimeZones.Suggest(Ms(2026, 7, 1, 12, 0), saved, TimeZoneInfo.Utc), Is.EqualTo(ZoneHint.None));
    }

    // ── names and offsets ────────────────────────────────────────────

    [TestCase("+02:00", 120)]
    [TestCase("+2", 120)]
    [TestCase("-0530", -330)]
    [TestCase("5:45", 345)]
    [TestCase("-12", -720)]
    public void OffsetsAreParsed(string text, int minutes)
    {
        Assert.That(TimeZones.TryParseOffset(text, out var m), Is.True);
        Assert.That(m, Is.EqualTo(minutes));
    }

    [TestCase("")]
    [TestCase("+15")]
    [TestCase("+02:07")]
    [TestCase("two")]
    public void NonsenseOffsetsAreRejected(string text)
    {
        Assert.That(TimeZones.TryParseOffset(text, out _), Is.False);
    }

    [Test]
    public void DeclaredZonesRoundTripThroughTheirNames()
    {
        Assert.That(TimeZones.Name(Central, Central), Is.EqualTo("local"));
        Assert.That(TimeZones.Name(TimeZoneInfo.Utc, Central), Is.EqualTo("utc"));
        Assert.That(TimeZones.Name(TimeZones.Fixed(-330), Central), Is.EqualTo("-05:30"));

        Assert.That(TimeZones.FromName("local", Central), Is.SameAs(Central));
        Assert.That(TimeZones.FromName("utc", Central), Is.SameAs(TimeZoneInfo.Utc));
        Assert.That(TimeZones.FromName("-05:30", Central).BaseUtcOffset, Is.EqualTo(TimeSpan.FromMinutes(-330)));
        Assert.That(TimeZones.FromName("", Central), Is.Null);
    }

    [Test]
    public void ZonesAreDescribedForTheSidePanel()
    {
        var zoned = new LogSource { Kind = TsKind.Iso, Zoned = true };
        zoned.Offsets.Add(new ZoneOffset(0, 60));
        zoned.Offsets.Add(new ZoneOffset(1, 120));

        Assert.That(TimeZones.Describe(zoned, Central), Is.EqualTo("UTC+01:00 / UTC+02:00, from the stamps"));
        Assert.That(TimeZones.Describe(new LogSource { Kind = TsKind.Iso }, Central), Is.EqualTo("not stated"));
        Assert.That(TimeZones.Describe(new LogSource { Kind = TsKind.Iso, Declared = TimeZoneInfo.Utc }, Central), Is.EqualTo("UTC, set by you"));
        Assert.That(TimeZones.Describe(new LogSource { Kind = TsKind.None }, Central), Is.EqualTo("no timestamps"));
    }
}
