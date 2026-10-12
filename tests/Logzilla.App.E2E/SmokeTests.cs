using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FlaUI.Core.WindowsAPI;
using Logzilla.App.Infrastructure;
using NUnit.Framework;

namespace Logzilla.App.E2E;

/// <summary>
/// The first end-to-end scenarios: each starts the real app on a sample log and
/// drives it as a user would. Each one covers a bug that unit tests could not see,
/// because it lived in how the window, its controls and the keyboard fit together.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SmokeTests
{
    private const string Json = "sample-json-clef.log";
    private const string Plain = "sample-service.log";

    [Test]
    public void OpeningALogShowsItsEntries()
    {
        using var app = LogzillaApp.Start(Plain);

        app.WaitForResults();

        Assert.That(app.ResultCount, Is.GreaterThan(1000));
        Assert.That(app.Showing, Does.StartWith("showing 1 to "));
        app.Screenshot("opened");
    }

    [Test]
    public void SearchingAJsonLogMatchesWhatItSaysNotItsKeyNames()
    {
        using var app = LogzillaApp.Start(Json);
        app.WaitForResults();
        var all = app.ResultCount;

        // A key name present on every line, and a placeholder in every message template.
        app.SetText("SearchBox", "sourcecontext");
        app.WaitForResults();
        Assert.That(app.ResultCount, Is.Zero, "A key name is not something the log says.");

        app.SetText("SearchBox", "ProcessMaintenance");
        app.WaitForResults();
        Assert.That(app.ResultCount, Is.GreaterThan(0).And.LessThan(all));
        app.Screenshot("searched");
    }

    [Test]
    public void WithWrapOnTheLastResultCanStillBeReached()
    {
        using var app = LogzillaApp.Start(Json);
        app.WaitForResults();
        app.Resize(1100, 700);
        app.SetText("SearchBox", "ProcessMaintenance");
        app.WaitForResults();

        app.Press("WrapChip");
        app.WaitUntil(() => app.IsOn("WrapChip"), "Wrap is on");
        app.Screenshot("wrapped");

        // Either every result fits, or the bar is there to scroll to the rest.
        var showing = app.Showing;
        var count = app.ResultCount;
        var allShown = showing.EndsWith(" to " + count + " of " + count, System.StringComparison.Ordinal);
        Assert.That(allShown || app.Value("GridVerticalScroll") != "none", Is.True,
            "Results run off the bottom (" + showing + ") but the scroll bar is gone.");

        // And End really gets to the last one.
        app.Find("LogGrid").Focus();
        app.Key(VirtualKeyShort.END);
        app.WaitUntil(() => app.Showing.EndsWith(" of " + count, System.StringComparison.Ordinal) &&
                            app.Showing.Contains(" to " + count + " ", System.StringComparison.Ordinal),
            "the last result is on screen");
        app.Screenshot("end");
    }

    [Test]
    public void EveryInspectorTabStaysInsideANarrowWindow()
    {
        using var app = LogzillaApp.Start(Plain);
        app.WaitForResults();

        app.Resize(900, 650);
        var strip = app.Find("SideTabs");
        app.WaitUntil(() =>
        {
            var bounds = strip.BoundingRectangle;
            var tab = app.Find("SideTab.ask").BoundingRectangle;
            return tab.Right <= bounds.Right && tab.Bottom <= bounds.Bottom && tab.Width > 0;
        }, "the Ask tab sits inside the tab strip");
        app.Screenshot("narrow");

        app.Press("SideTab.ask");
        app.WaitUntil(() => !app.Find("AskPanel").IsOffscreen, "the Ask tab opens");
    }

    [Test]
    public void EachTabKeepsItsOwnSearch()
    {
        // The team's report: a search typed on one log followed you to every other tab.
        using var app = LogzillaApp.Start(Plain, "gateway-access.log");
        app.WaitForResults();
        System.Threading.Thread.Sleep(1000);   // the other file finishes indexing too
        app.SetText("SearchBox", "nginx");
        app.WaitForResults();
        var searched = app.ResultCount;

        app.Window.SetForeground();
        app.Find("LogGrid").Focus();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
        app.WaitUntil(() => app.Text("SearchBox").Length == 0, "the other tab shows its own, empty search");
        app.WaitUntil(() => app.ResultCount == 30136, "the other log is unfiltered");

        app.Find("LogGrid").Focus();
        app.Chord(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
        app.WaitUntil(() => app.Text("SearchBox") == "nginx", "the first tab's search comes back");
        app.WaitUntil(() => app.ResultCount == searched, "with the same results");
        app.Screenshot("back");
    }

    [Test]
    public void AHighlightCanBeAddedRightOnTheHighlightingTab()
    {
        using var app = LogzillaApp.Start(Plain);
        app.WaitForResults();

        Assert.That(app.Find("SideTab.colors").Name, Is.EqualTo("Highlighting"));
        Assert.That(app.Find("SideTab.marks").Name, Is.EqualTo("Bookmarks"));
        Assert.That(app.Find("SideTab.ask").Name, Is.EqualTo("AI Chat"));

        app.Press("SideTab.colors");
        app.WaitUntil(() => !app.Find("HighlightInput").IsOffscreen, "the highlight bar shows");
        app.SetText("HighlightInput", "Slow query");
        app.Press("HighlightAdd");

        app.WaitUntil(() => app.Text("HighlightInput").Length == 0, "the bar clears once the rule is added");
        app.Screenshot("added");
    }

    [Test]
    public void ARegexThatDoesNotCompileIsReportedNotSearched()
    {
        // A half-typed "(" used to leave every entry showing under a red "no hits", as if
        // the pattern had been searched for and matched it all.
        using var app = LogzillaApp.Start(Plain);
        app.WaitForResults();
        var all = app.ResultCount;
        app.Press("RegexChip");
        app.WaitUntil(() => app.IsOn("RegexChip"), "regex is on");

        app.SetText("SearchBox", "Slow (query");
        app.WaitUntil(() => app.Find("HitCount").Name == "invalid regex", "the hit count says the pattern is invalid");
        Assert.That(app.ResultCount, Is.EqualTo(all));
        app.Screenshot("invalid");
        // The status bar's message stops short of the counts on the right, with "…".
        app.Resize(900, 600);
        app.Screenshot("invalid-narrow");

        app.SetText("SearchBox", "Slow (query)?");
        app.WaitUntil(() => app.Find("HitCount").Name.EndsWith(" hits", System.StringComparison.Ordinal), "a valid pattern counts its hits");
        Assert.That(app.ResultCount, Is.LessThan(all));
    }

    [Test]
    public void TheCommandBarFitsANarrowWindow()
    {
        // Under ~1000px the right-hand group (Merge, Wrap, …) was drawn over "Filter to
        // matches". Merge, also in the File menu, now steps aside until there is room.
        using var app = LogzillaApp.Start(Plain, "gateway-access.log");
        app.WaitForResults();

        app.Resize(900, 650);
        app.WaitUntil(() => app.Find("SearchMode").BoundingRectangle.Right <= app.Find("WrapChip").BoundingRectangle.Left,
            "the search mode switch ends before the Wrap chip");
        Assert.That(VisibleMerge(app), Is.Null, "Merge has stepped aside");
        app.Screenshot("narrow");

        app.Resize(1440, 880);
        app.WaitUntil(() => VisibleMerge(app) is { } merge &&
                            app.Find("SearchMode").BoundingRectangle.Right <= merge.BoundingRectangle.Left,
            "Merge is back, clear of the search mode switch");
        app.Screenshot("wide");
    }

    private static FlaUI.Core.AutomationElements.AutomationElement VisibleMerge(LogzillaApp app)
    {
        var merge = app.Window.FindFirstDescendant(cf => cf.ByAutomationId("MergeButton"));
        return merge == null || merge.IsOffscreen ? null : merge;
    }

    [Test]
    public void LiveTailPicksUpNewLinesAndARotatedFile()
    {
        // Following now reads on a thread of its own; it must still see the file grow,
        // and start over when the file is truncated as a log rotation does.
        var dir = Path.Combine(Path.GetTempPath(), "logzilla-e2e-tail-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "growing.log");
        File.WriteAllText(path, Lines(0, 50));
        try
        {
            using var app = LogzillaApp.Start(path);
            app.WaitForResults();
            Assert.That(app.ResultCount, Is.EqualTo(50));

            app.Press("TailChip");
            app.WaitUntil(() => app.IsOn("TailChip"), "live tail is on");

            File.AppendAllText(path, Lines(50, 20_000));
            app.WaitUntil(() => app.ResultCount == 20_050, "the appended lines are read");
            app.Screenshot("grown");

            File.WriteAllText(path, Lines(0, 10));
            app.WaitUntil(() => app.ResultCount == 10, "the rotated file is read again from the top");
            app.Screenshot("rotated");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException ex)
            {
                TestContext.Out.WriteLine("Leaving " + dir + ": " + ex.Message);
            }
        }
    }

    private static string Lines(int from, int count)
    {
        var sb = new StringBuilder();
        for (var i = from; i < from + count; i++)
        {
            sb.Append("2026-03-01 08:")
              .Append((i / 60 % 60).ToString("00", CultureInfo.InvariantCulture)).Append(':')
              .Append((i % 60).ToString("00", CultureInfo.InvariantCulture))
              .Append(".000 INFO  request ").Append(i).Append(" served\n");
        }
        return sb.ToString();
    }

    [Test]
    public void TypingInTheAskBoxDoesNotTriggerGridShortcuts()
    {
        // A connected provider, so the question box is enabled. The key is a dummy and
        // nothing is ever sent: the test types letters but never presses Enter.
        var settings = new Dictionary<string, string>
        {
            ["ai.enabled"] = "1",
            ["ai.provider"] = "openai",
            ["ai.model.openai"] = "gpt-test",
            ["ai.key.openai"] = SecretStore.Protect("sk-e2e-dummy-not-a-real-key"),
        };
        using var app = LogzillaApp.Start(settings, Plain);
        app.WaitForResults();
        var before = app.ResultCount;
        app.Press("SideTab.ask");
        app.WaitUntil(() => app.Find("AskInput").IsEnabled, "the question box is enabled");

        // W shows warnings and errors only, E jumps to the next error, M marks a row.
        app.Type("AskInput", "we make wise moves");

        Assert.That(app.Text("AskInput"), Is.EqualTo("we make wise moves"), "Every letter reached the box.");
        Assert.That(app.ResultCount, Is.EqualTo(before), "W did not filter the grid to warnings.");
        app.Screenshot("typed");
    }
}
