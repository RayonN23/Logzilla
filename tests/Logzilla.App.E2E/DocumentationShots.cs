using System;
using System.IO;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace Logzilla.App.E2E;

/// <summary>
/// Pictures for the feature overview, taken from the real app on the sample logs so
/// they always show the current build. Not tests as such: they only run when asked,
///
///   $env:LOGZILLA_E2E = '1'; $env:LOGZILLA_DOCSHOTS = 'C:\some\folder'
///   dotnet test tests\Logzilla.App.E2E
///
/// and write one PNG per picture into that folder. Every picture is rendered by
/// Logzilla's own window, so nothing else on the desktop can appear in one.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class DocumentationShots
{
    private const string Json = "sample-json-clef.log";
    private const string Plain = "sample-service.log";
    private const string Gateway = "gateway-access.log";
    private const string Postgres = "postgres-db01.log";

    private static string Folder()
    {
        var dir = Environment.GetEnvironmentVariable("LOGZILLA_DOCSHOTS");
        if (string.IsNullOrWhiteSpace(dir))
        {
            Assert.Ignore("Documentation pictures: set LOGZILLA_DOCSHOTS to a folder to take them.");
        }
        return dir;
    }

    private static LogzillaApp Open(params string[] samples)
    {
        var app = LogzillaApp.Start(samples);
        app.Resize(1440, 880);
        app.WaitForResults();
        return app;
    }

    [Test]
    public void MergedLogsAndTheirSources()
    {
        var dir = Folder();
        using var app = Open(Plain, Gateway, Postgres);
        // The three files index side by side; the last one opened is the one on view,
        // so give the other two a moment to finish before merging them.
        System.Threading.Thread.Sleep(1500);
        app.Press("MergeButton");
        app.WaitUntil(() => app.ResultCount == 30136 + 12000 + 6000, "the merged view is ready");
        app.Press("SideTab.sources");
        app.WaitForResults();

        app.SaveShot(Path.Combine(dir, "merged-window.png"));
        app.SaveShot(Path.Combine(dir, "sources-panel.png"), "SidePanel");
    }

    [Test]
    public void AJsonEntryExpanded()
    {
        var dir = Folder();
        using var app = Open(Json);
        app.Find("LogGrid").Focus();
        for (var k = 0; k < 4; k++)
        {
            app.Key(VirtualKeyShort.DOWN);
        }
        app.WaitUntil(() => app.Showing.StartsWith("showing 1 ", StringComparison.Ordinal), "the grid is at the top");

        app.SaveShot(Path.Combine(dir, "json-expanded.png"), "LogGrid");
    }

    [Test]
    public void SearchHitsHighlightedAndOnTheMinimap()
    {
        var dir = Folder();
        using var app = Open(Plain);
        app.Press("SearchMode");
        app.WaitUntil(() => app.Value("SearchMode") == "Highlight", "highlight mode is on");
        app.SetText("SearchBox", "\"Slow query\"");
        app.WaitForResults();

        app.SaveShot(Path.Combine(dir, "highlight-hits.png"), "LogGrid", "Minimap");
    }

    [Test]
    public void ATimeRangeOnTheTimeline()
    {
        var dir = Folder();
        using var app = Open(Plain);
        app.Press("TimeButton");
        app.SetText("TimeRange", "08:40 - 08:55");
        app.WaitUntil(() => app.ResultCount is > 0 and < 30136, "the time range applies");
        app.WaitForResults();

        app.SaveShot(Path.Combine(dir, "time-range.png"), "Timeline");
    }

    /// <summary>One inspector tab, cropped to the tab strip and the panel under it.</summary>
    private static void PanelShot(LogzillaApp app, string tab, string file)
    {
        app.Press("SideTab." + tab);
        app.WaitForResults();
        System.Threading.Thread.Sleep(600);   // Fields and Patterns fill in on a worker thread
        app.SaveShot(Path.Combine(Folder(), file), "SideTabs", "SidePanel");
    }

    [Test]
    public void TheAnalysisPanels()
    {
        using var app = Open(Json);
        PanelShot(app, "summary", "summary-panel.png");
        PanelShot(app, "fields", "fields-panel.png");
        PanelShot(app, "patterns", "patterns-panel.png");

        app.Find("LogGrid").Focus();
        for (var k = 0; k < 14; k++)
        {
            app.Key(VirtualKeyShort.DOWN);       // line 15: a request with properties of its own
        }
        PanelShot(app, "detail", "detail-panel.png");
    }

    [Test]
    public void TheHighlightingTab()
    {
        using var app = Open(Plain);
        app.Press("SideTab.colors");
        foreach (var text in new[] { "Slow query", "Failed to commit", "worker-5" })
        {
            app.SetText("HighlightInput", text);
            app.Press("HighlightAdd");
            app.WaitUntil(() => app.Text("HighlightInput").Length == 0, "the highlight is added");
        }
        app.SaveShot(Path.Combine(Folder(), "highlighting-window.png"));
        app.SaveShot(Path.Combine(Folder(), "highlighting-panel.png"), "SideTabs", "SidePanel");
    }

    [Test]
    public void TheAiChatTab()
    {
        // A provider has to be connected for the tab to offer its questions. The key is a
        // placeholder: nothing is sent, because nothing is asked.
        var settings = new System.Collections.Generic.Dictionary<string, string>
        {
            ["ai.enabled"] = "1",
            ["ai.provider"] = "openai",
            ["ai.key.openai"] = Logzilla.App.Infrastructure.SecretStore.Protect("sk-docs-placeholder-not-a-real-key"),
        };
        Folder();
        using var app = LogzillaApp.Start(settings, Json);
        app.Resize(1440, 880);
        app.WaitForResults();
        PanelShot(app, "ask", "ai-chat-panel.png");
    }

    [Test]
    public void MarkedEntries()
    {
        var dir = Folder();
        using var app = Open(Plain);
        app.Find("LogGrid").Focus();
        // E jumps to the next error, M marks it: four errors kept for later.
        foreach (var skip in new[] { 0, 2, 1, 3 })
        {
            for (var k = 0; k <= skip; k++)
            {
                app.Key(VirtualKeyShort.KEY_E);
            }
            app.Key(VirtualKeyShort.KEY_M);
        }
        app.Press("SideTab.marks");
        app.WaitForResults();

        app.SaveShot(Path.Combine(dir, "marks-panel.png"), "SidePanel");
    }
}
