using System;
using System.IO;
using System.Text;
using Logzilla.Core.Implementation.Logs;
using Logzilla.Core.Implementation.Query;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// Searching a plain-text log, where one entry is a header line plus every
/// continuation line under it.
///
/// A JSON log puts the whole event on one line, so a query there sees all of it.
/// A plain-text log spreads the same event over a header line, a stack trace and
/// a wrapped payload — and the engine used to test only the header line, letting
/// its continuations inherit that verdict. "error unknown" therefore found
/// nothing when the two words were three lines apart, and searching a stack trace
/// found nothing at all. The query is answered for the entry as a whole.
/// </summary>
[TestFixture]
public class FilterEngineEntryTests
{
    // Entry 0 is line 0. Entry 1 is lines 1-3: a header, the exception, one frame.
    // Entry 2 is line 4.
    private const string Log =
        "2026-03-01 08:00:00.000 INFO  request ok\n" +
        "2026-03-01 08:00:01.000 ERROR Request failed for tenant=acme\n" +
        "java.net.UnknownHostException: no route to db-02\n" +
        "\tat com.acme.orders.Repo.load(Repo.java:88)\n" +
        "2026-03-01 08:00:04.000 INFO  recovered\n";

    private const int Header = 1;
    private const int Exception = 2;
    private const int Frame = 3;

    private static readonly int[] TheWholeEntry = [Header, Exception, Frame];
    private static readonly int[] HeaderOnly = [Header];
    private static readonly int[] FrameOnly = [Frame];
    private static readonly int[] LastEntryOnly = [4];
    private static readonly int[] TheInfoEntries = [0, 4];

    private string _path;
    private LogDocument _document;

    [SetUp]
    public void Load()
    {
        _path = Path.Combine(Path.GetTempPath(), "cn-entry-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(_path, Log, new UTF8Encoding(false));
        var indexer = new LogIndexer(TestClock.Instance);
        var source = indexer.Prepare(_path);
        _document = new LogDocument { Name = source.Name };
        _document.Srcs.Add(source);
        indexer.Run(_document, 0, progress: null, cancelled: null);
    }

    [TearDown]
    public void Cleanup()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static QuerySpec Query(string text, bool regex = false, bool group = true, bool filter = true,
                                   int mask = 0x7F) =>
        QuerySpec.Build(text, regex: regex, cs: false, mask: mask, srcMask: -1L,
                        t0: double.NaN, t1: double.NaN, ctx: 0, group: group, filter: filter);

    private static FilterResult Run(LogDocument document, QuerySpec spec) =>
        new FilterEngine().Run(document, spec, null, null);

    [Test]
    public void TheFixtureReallyHasAThreeLineEntry()
    {
        Assert.That(_document.N, Is.EqualTo(5));
        Assert.That(_document.Flag[Header], Is.EqualTo(0), "The header line owns the entry.");
        Assert.That(_document.Flag[Exception], Is.EqualTo(1));
        Assert.That(_document.Flag[Frame], Is.EqualTo(1));
    }

    // ── terms spread across an entry ───────────────────────────────────

    [Test]
    public void TermsMayLandOnDifferentLinesOfTheSameEntry()
    {
        // "Request failed" is on the header, "Unknown" three characters into the
        // exception below it. This is the reported bug, in one assertion.
        var result = Run(_document, Query("failed unknown"));

        Assert.That(result.Matches, Is.EquivalentTo(HeaderOnly));
        Assert.That(result.View, Is.EquivalentTo(TheWholeEntry));
    }

    [Test]
    public void ATermFoundOnlyInAStackTraceStillMatchesItsEntry()
    {
        var result = Run(_document, Query("Repo.java"));

        Assert.That(result.Matches, Is.EquivalentTo(HeaderOnly));
        Assert.That(result.View, Is.EquivalentTo(TheWholeEntry));
    }

    [Test]
    public void AMatchedEntryBringsItsContinuationsWithIt()
    {
        var result = Run(_document, Query("tenant=acme"));

        Assert.That(result.View, Is.EquivalentTo(TheWholeEntry),
            "A stack trace is part of the entry that owns it, not a separate row to drop.");
    }

    [Test]
    public void AnEntryCountsAsOneHitHoweverManyOfItsLinesMatch()
    {
        // "com" appears on the header (acme) and again on the frame.
        var result = Run(_document, Query("com"));

        Assert.That(result.Matches, Has.Count.EqualTo(1),
            "Hit counts and jump-to-next-hit are per entry, not per physical line.");
    }

    // ── exclusion ──────────────────────────────────────────────────────

    [Test]
    public void AnExcludeTermAnywhereInTheEntryDropsTheWholeEntry()
    {
        var result = Run(_document, Query("failed -UnknownHostException"));

        Assert.That(result.Matches, Is.Empty);
        Assert.That(result.View, Is.Empty);
    }

    [Test]
    public void AnExcludeTermOnTheHeaderStillDropsTheEntry()
    {
        var result = Run(_document, Query("Repo.java -tenant=acme"));

        Assert.That(result.View, Is.Empty);
    }

    // ── regex ──────────────────────────────────────────────────────────

    [Test]
    public void ARegexSeesTheWholeEntryAtOnce()
    {
        var result = Run(_document, Query("failed[\\s\\S]*UnknownHost", regex: true));

        Assert.That(result.Matches, Is.EquivalentTo(HeaderOnly));
    }

    [Test]
    public void ARegexStillMatchesASingleLineEntry()
    {
        var result = Run(_document, Query("^2026.*recovered$", regex: true));

        Assert.That(result.Matches, Is.EquivalentTo(LastEntryOnly));
    }

    // ── the grouping toggle still means something ──────────────────────

    [Test]
    public void UngroupedSearchGoesBackToOneLineAtATime()
    {
        var result = Run(_document, Query("failed unknown", group: false));

        Assert.That(result.Matches, Is.Empty,
            "With grouping off, both terms have to be on the same physical line.");
    }

    [Test]
    public void UngroupedSearchMatchesAContinuationLineOnItsOwn()
    {
        var result = Run(_document, Query("Repo.java", group: false));

        Assert.That(result.Matches, Is.EquivalentTo(FrameOnly));
        Assert.That(result.View, Is.EquivalentTo(FrameOnly));
    }

    // ── the rest of the filter is unchanged ────────────────────────────

    [Test]
    public void HidingASeverityHidesThatEntrysContinuationsToo()
    {
        const int withoutError = 0x7F & ~(1 << 5);
        var result = Run(_document, Query(string.Empty, mask: withoutError));

        Assert.That(result.View, Is.EquivalentTo(TheInfoEntries));
    }

    [Test]
    public void HighlightModeKeepsEveryLineAndStillFindsTheEntry()
    {
        var result = Run(_document, Query("failed unknown", filter: false));

        Assert.That(result.View, Has.Count.EqualTo(5));
        Assert.That(result.Matches, Is.EquivalentTo(HeaderOnly));
    }

    [Test]
    public void AnEmptyQueryKeepsEveryLine()
    {
        var result = Run(_document, Query(string.Empty));

        Assert.That(result.View, Has.Count.EqualTo(5));
    }
}
