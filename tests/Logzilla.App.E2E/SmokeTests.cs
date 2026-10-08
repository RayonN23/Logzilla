using System.Collections.Generic;
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
