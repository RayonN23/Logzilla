using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// What the date-and-time column says, whether the Date chip actually collapses it,
/// and how a long message is broken across display lines. The text and wrap helpers
/// are private statics reached by reflection; the column geometry needs a live
/// control, because its width comes from a measured font, but neither needs a file
/// attached.
/// </summary>
[TestFixture]
public sealed class LogViewTests
{
    private static string TimeText(double ms)
    {
        var m = typeof(LogView).GetMethod("TimeText", BindingFlags.NonPublic | BindingFlags.Static);
        return (string)m.Invoke(null, [ms]);
    }

    /// <summary>Reads a private instance member of a live LogView — the column
    /// geometry is computed from measured font widths, so it cannot be a static.</summary>
    private static int Geometry(LogView view, string name)
    {
        var p = typeof(LogView).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
        return (int)p.GetValue(view);
    }

    private static List<(int Start, int Len)> WrapSegments(string text, int width, float cw)
    {
        var m = typeof(LogView).GetMethod("WrapSegments", BindingFlags.NonPublic | BindingFlags.Static);
        return (List<(int Start, int Len)>)m.Invoke(null, [text, width, cw]);
    }

    /// <summary>2026-06-18 08:14:02.113 UTC as ms since the Unix epoch.</summary>
    private static double SampleMs()
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var stamp = new DateTime(2026, 6, 18, 8, 14, 2, 113, DateTimeKind.Utc);
        return (stamp - epoch).TotalMilliseconds;
    }

    // ── Time column ────────────────────────────────────────────────────

    [Test]
    public void TheTimeColumnShowsTheWholeStampIncludingTheYear()
    {
        Assert.That(TimeText(SampleMs()), Is.EqualTo("2026-06-18 08:14:02.113"));
    }

    [Test]
    public void TimeTextOfNaNIsEmpty()
    {
        Assert.That(TimeText(double.NaN), Is.EqualTo(""));
    }

    [Test]
    public void TheDateChipCollapsesTheDateAndTimeColumn()
    {
        using var view = new LogView();
        view.ShowTime = true;
        var open = Geometry(view, "TimeColW");
        view.ShowTime = false;
        var collapsed = Geometry(view, "TimeColW");

        Assert.That(open, Is.GreaterThan(collapsed),
            "Collapsing the column must make it narrower, not wider.");
        Assert.That(collapsed, Is.LessThanOrEqualTo(Dpi.S(10)),
            "A collapsed column should keep nothing but the pad before the level tag.");
    }

    [Test]
    public void TheColumnIsOpenByDefault()
    {
        using var view = new LogView();
        Assert.That(view.ShowTime, Is.True);
    }

    [Test]
    public void CollapsingTheColumnGivesTheWidthToTheMessage()
    {
        using var view = new LogView();
        view.ShowTime = true;
        var openMsgX = Geometry(view, "MessageX");
        var openColW = Geometry(view, "TimeColW");
        view.ShowTime = false;
        var collapsedMsgX = Geometry(view, "MessageX");
        var collapsedColW = Geometry(view, "TimeColW");

        // Every column right of the stamp moves over by exactly what the stamp gave up.
        Assert.That(openMsgX - collapsedMsgX, Is.EqualTo(openColW - collapsedColW));
        Assert.That(collapsedMsgX, Is.LessThan(openMsgX));
    }

    [Test]
    public void AnOpenColumnIsWideEnoughForTheWholeStamp()
    {
        using var view = new LogView();
        view.ShowTime = true;
        var stamp = Dpi.Text("0000-00-00 00:00:00.000", Theme.MonoSmall).Width;
        Assert.That(Geometry(view, "TimeColW"), Is.GreaterThanOrEqualTo(stamp),
            "The year must not be painted over by the level tag.");
    }

    // ── Wrap ───────────────────────────────────────────────────────────

    [Test]
    public void AShortMessageWrapsToASingleLine()
    {
        var segs = WrapSegments("short line", 800, 8f);
        Assert.That(segs, Has.Count.EqualTo(1));
        Assert.That(segs[0], Is.EqualTo((0, "short line".Length)));
    }

    [Test]
    public void AnEmptyMessageStillProducesOneLine()
    {
        Assert.That(WrapSegments("", 800, 8f), Has.Count.EqualTo(1));
        Assert.That(WrapSegments(null, 800, 8f), Has.Count.EqualTo(1));
    }

    [Test]
    public void ALongMessageIsBrokenAcrossSeveralLines()
    {
        var text = new string('x', 500);
        var segs = WrapSegments(text, 800, 8f); // 100 chars per line
        Assert.That(segs, Has.Count.EqualTo(5));
    }

    [Test]
    public void SegmentsCoverTheWholeMessageWithoutGapsOrOverlap()
    {
        var text = "the quick brown fox jumps over the lazy dog, " + new string('y', 300);
        var segs = WrapSegments(text, 400, 8f);
        var pos = 0;
        foreach (var (start, len) in segs)
        {
            Assert.That(start, Is.EqualTo(pos), "Segments must be contiguous.");
            pos += len;
        }
        Assert.That(pos, Is.EqualTo(text.Length), "Segments must cover the whole message.");
    }

    [Test]
    public void WrappingPrefersToBreakAfterASpace()
    {
        // 20 chars per line; the break should land after a word, not mid-word.
        var text = "alpha bravo charlie delta echo foxtrot golf hotel";
        var segs = WrapSegments(text, 160, 8f);
        for (var k = 0; k < segs.Count - 1; k++)
        {
            var (start, len) = segs[k];
            Assert.That(text[start + len - 1], Is.EqualTo(' '),
                "Every wrapped line but the last should end at a word boundary.");
        }
    }

    [Test]
    public void ANarrowerColumnProducesMoreLines()
    {
        var text = new string('x', 600);
        var wide = WrapSegments(text, 1200, 8f).Count;
        var narrow = WrapSegments(text, 400, 8f).Count;
        Assert.That(narrow, Is.GreaterThan(wide));
    }

    [Test]
    public void ALongMessageWrapsInFull()
    {
        // With Wrap on a row used to stop at 40 lines, cutting long messages short. It
        // now takes every line it needs; a row taller than the window scrolls instead.
        var text = new string('x', 4000);
        var segs = WrapSegments(text, 100, 8f);

        Assert.That(segs.Sum(s => s.Len), Is.EqualTo(text.Length), "Every character is on some line.");
    }

    [Test]
    public void ARunawayLineStillHasAWrapLimit()
    {
        var segs = WrapSegments(new string('x', 2_000_000), 100, 8f);

        Assert.That(segs, Has.Count.LessThanOrEqualTo(5000));
    }

    [Test]
    public void AZeroWidthColumnDoesNotHangOrDivideByZero()
    {
        Assert.DoesNotThrow(() => WrapSegments("some text here", 0, 8f));
        Assert.DoesNotThrow(() => WrapSegments("some text here", 400, 0f));
    }

    // ── Expanded row ───────────────────────────────────────────────────
    //
    // The expanded row lays out the whole entry, and an entry that arrives with
    // its own line breaks — a stack trace, an indented XML payload — has already
    // decided where its lines go. Running them together into one paragraph is
    // what made those entries unreadable.

    private static readonly string[] ThreeLines = ["first", "second", "third"];
    private static readonly string[] TwoLines = ["first", "second"];

    private static List<string> WrapText(string text, int width)
    {
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        var m = typeof(LogView).GetMethod("WrapText", BindingFlags.NonPublic | BindingFlags.Static);
        return (List<string>)m.Invoke(null, [g, text, Theme.MonoBody, width]);
    }

    [Test]
    public void ALineBreakInTheEntryStartsANewDisplayLine()
    {
        var lines = WrapText("first\nsecond\nthird", 4000);

        Assert.That(lines, Is.EqualTo(ThreeLines));
    }

    [Test]
    public void WindowsLineBreaksDoNotLeaveAStrayCarriageReturn()
    {
        var lines = WrapText("first\r\nsecond", 4000);

        Assert.That(lines, Is.EqualTo(TwoLines));
    }

    [Test]
    public void AnIndentedPayloadKeepsItsIndent()
    {
        var lines = WrapText("<plist>\n  <dict>\n    <key>Status</key>\n  </dict>\n</plist>", 4000);

        Assert.That(lines[1], Is.EqualTo("  <dict>"));
        Assert.That(lines[2], Is.EqualTo("    <key>Status</key>"));
    }

    [Test]
    public void ALongLineStillWrapsWithinItsOwnParagraph()
    {
        var lines = WrapText("short\n" + new string('x', 4000), 200);

        Assert.That(lines[0], Is.EqualTo("short"));
        Assert.That(lines, Has.Count.GreaterThan(2), "The long line has to break somewhere.");
    }

    [Test]
    public void ABlankLineIsStillALine()
    {
        var lines = WrapText("head\n\ntail", 4000);

        Assert.That(lines, Has.Count.EqualTo(3));
        Assert.That(lines[1], Is.EqualTo(""));
    }

    [Test]
    public void ALongPayloadIsLaidOutInFull()
    {
        var lines = WrapText(string.Join("\n", Enumerable.Repeat("<item>line</item>", 5000)), 4000);

        Assert.That(lines, Has.Count.EqualTo(5000), "An expanded row shows the whole entry, not the first screenful.");
    }

    [Test]
    public void ARunawayEntryStillCannotProduceUnboundedLayout()
    {
        var lines = WrapText(string.Join("\n", Enumerable.Repeat("x", 300_000)), 4000);

        Assert.That(lines, Has.Count.LessThanOrEqualTo(200_000));
    }

    [Test]
    public void EmptyTextStillProducesOneLine()
    {
        Assert.That(WrapText("", 4000), Has.Count.EqualTo(1));
        Assert.That(WrapText(null, 4000), Has.Count.EqualTo(1));
    }
}
