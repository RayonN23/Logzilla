using System;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// What Ctrl+C actually puts on the clipboard. Copying one entry and copying a
/// run of them are different jobs: one gets pasted into a message and should
/// arrive bare, a run is a transcript and needs one entry per line with a source
/// label when the document is a merge.
/// </summary>
[TestFixture]
public sealed class EntryClipTests
{
    private static readonly string[] OneEntry = ["connection reset by peer"];
    private static readonly string[] ThreeEntries = ["first", "second", "third"];
    private static readonly string[] TwoEntries = ["first", "second"];
    private static readonly string[] TwoLabels = ["web01", "web02"];

    private static string Nl => Environment.NewLine;

    // ── one entry ──────────────────────────────────────────────────────

    [Test]
    public void OneEntryIsCopiedBare()
    {
        Assert.That(EntryClip.Compose(OneEntry, null), Is.EqualTo("connection reset by peer"),
            "No trailing newline: this is about to be pasted mid-sentence.");
    }

    [Test]
    public void OneEntryCarriesNoSourceLabel()
    {
        Assert.That(EntryClip.Compose(OneEntry, ["web01"]), Is.EqualTo("connection reset by peer"),
            "It was selected on purpose; which file it came from is not in doubt.");
    }

    // ── several entries ────────────────────────────────────────────────

    [Test]
    public void SeveralEntriesGoOnePerLine()
    {
        Assert.That(EntryClip.Compose(ThreeEntries, null),
            Is.EqualTo("first" + Nl + "second" + Nl + "third" + Nl));
    }

    [Test]
    public void AMergedRunCarriesItsSourceLabels()
    {
        Assert.That(EntryClip.Compose(TwoEntries, TwoLabels),
            Is.EqualTo("[web01] first" + Nl + "[web02] second" + Nl));
    }

    [Test]
    public void AMissingOrBlankLabelIsSimplyLeftOff()
    {
        Assert.That(EntryClip.Compose(TwoEntries, [""]),
            Is.EqualTo("first" + Nl + "second" + Nl));
    }

    // ── entries that span lines ────────────────────────────────────────

    [Test]
    public void AStackTraceKeepsItsOwnLineBreaks()
    {
        var copied = EntryClip.Compose(["failed\n   at Foo.Bar()"], null);

        Assert.That(copied, Is.EqualTo("failed" + Nl + "   at Foo.Bar()"));
    }

    [Test]
    public void LineBreaksInsideAnEntryAreNormalisedNotDoubled()
    {
        var copied = EntryClip.Compose(["a\r\nb", "c\nd"], null);

        Assert.That(copied, Is.EqualTo("a" + Nl + "b" + Nl + "c" + Nl + "d" + Nl));
        Assert.That(copied, Does.Not.Contain("\r\r"));
    }

    [Test]
    public void ALabelSitsInFrontOfAMultiLineEntry()
    {
        var copied = EntryClip.Compose(["boom\n   at X()", "ok"], ["api", "api"]);

        Assert.That(copied, Does.StartWith("[api] boom" + Nl + "   at X()" + Nl));
    }

    // ── time and level ─────────────────────────────────────────────────

    [Test]
    public void AnEntryLeadsWithItsTimeAndLevel()
    {
        Assert.That(EntryClip.Entry("2026-10-26 09:14:03.120", "ERROR", "connection reset by peer"),
            Is.EqualTo("2026-10-26 09:14:03.120 ERROR connection reset by peer"));
    }

    [Test]
    public void AMissingTimeOrLevelLeavesNoGap()
    {
        Assert.That(EntryClip.Entry("", "WARN", "disk low"), Is.EqualTo("WARN disk low"));
        Assert.That(EntryClip.Entry("2026-10-26 09:14:03.120", "", "disk low"), Is.EqualTo("2026-10-26 09:14:03.120 disk low"));
        Assert.That(EntryClip.Entry(null, null, "   at Foo.Bar()"), Is.EqualTo("   at Foo.Bar()"));
        Assert.That(EntryClip.Entry(null, null, null), Is.EqualTo(""));
    }

    // ── nothing to copy ────────────────────────────────────────────────

    [Test]
    public void NothingSelectedComposesToNothing()
    {
        Assert.That(EntryClip.Compose(null, null), Is.EqualTo(""));
        Assert.That(EntryClip.Compose([], null), Is.EqualTo(""));
    }

    [Test]
    public void AnEmptyEntryDoesNotThrow()
    {
        Assert.That(EntryClip.Compose([null], null), Is.EqualTo(""));
        Assert.That(EntryClip.Compose(["", "x"], null), Is.EqualTo(Nl + "x" + Nl));
    }
}
