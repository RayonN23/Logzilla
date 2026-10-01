using System;
using System.Collections.Generic;
using System.Linq;
using Logzilla.AI.Privacy;
using Logzilla.AI.Tools;
using NUnit.Framework;

namespace Logzilla.AI.Tests;

/// <summary>
/// The tools a model calls, run against a real indexed log. What matters: counts come
/// from the engine, lines carry their numbers, nothing personal gets out, and bad
/// input comes back as text the model can act on rather than an exception.
/// </summary>
[TestFixture]
public sealed class LogToolsTests
{
    private static readonly string[] ToolNames =
        ["get_overview", "search_log", "get_entries", "count_over_time", "find_patterns", "list_fields", "get_field_values"];

    private TestLog _log;
    private LogTools _tools;
    private List<ToolActivity> _activity;
    private List<string> _sent;

    [SetUp]
    public void SetUp()
    {
        _log = new TestLog();
        _tools = new LogTools(_log, _log.Json, new Redactor());
        _activity = new List<ToolActivity>();
        _sent = new List<string>();
        _tools.Activity += _activity.Add;
        _tools.Sending += (_, text) => _sent.Add(text);
    }

    [TearDown]
    public void TearDown() => _log.Dispose();

    [Test]
    public void EveryToolHasANameAndADescription()
    {
        Assert.That(_tools.Definitions.Select(d => d.Name), Is.EquivalentTo(ToolNames));
        Assert.That(_tools.Definitions.All(d => d.Description.Length > 20), Is.True);
    }

    [Test]
    public void TheOverviewCountsEntriesByLevel()
    {
        var text = _tools.GetOverview();

        Assert.That(text, Does.Contain("Entries: 8"), "The stack trace belongs to its entry, not counted on its own.");
        Assert.That(text, Does.Contain("ERROR 2"));
        Assert.That(text, Does.Contain("WARN 1"));
        Assert.That(text, Does.Contain("INFO 5"));
        Assert.That(text, Does.Contain("From 2026-03-01 08:00:00.100 to 2026-03-01 08:20:00.000"));
    }

    [Test]
    public void SearchReportsTheCountAndCitesLineNumbers()
    {
        var text = _tools.SearchLog("connection reset");

        Assert.That(text, Does.StartWith("Matched 2 entries (ERROR 2)."));
        Assert.That(text, Does.Contain("#" + _log.LineOf("08:05:01")));
        Assert.That(text, Does.Contain("#" + _log.LineOf("08:15:00")));
        Assert.That(text, Does.Contain("(+2 lines)"), "The stack trace is mentioned, not dumped.");
    }

    [Test]
    public void SearchFindsATermThatIsOnlyInTheStackTrace()
    {
        Assert.That(_tools.SearchLog("Repo.java"), Does.StartWith("Matched 1 entries"));
    }

    [Test]
    public void SearchByMinimumLevel()
    {
        Assert.That(_tools.SearchLog(minLevel: "warn"), Does.StartWith("Matched 3 entries"));
        Assert.That(_tools.SearchLog(minLevel: "warning"), Does.StartWith("Matched 3 entries"), "Common spellings are understood.");
    }

    [Test]
    public void SearchByTimeRange()
    {
        Assert.That(_tools.SearchLog(from: "2026-03-01 08:05", to: "2026-03-01 08:11"), Does.StartWith("Matched 3 entries"));
    }

    [Test]
    public void SearchReportsWhatItRanSoTheUserCanApplyIt()
    {
        _tools.SearchLog("reset", minLevel: "error");

        var search = _activity.Single().Search;
        Assert.That(search, Is.Not.Null);
        Assert.That(search.Query, Is.EqualTo("reset"));
        Assert.That(search.MinLevel, Is.EqualTo("error"));
        Assert.That(_activity.Single().Summary, Does.Contain("2 entries"));
    }

    [Test]
    public void ABadRegexComesBackAsAnExplanation()
    {
        var text = _tools.SearchLog("(unclosed", regex: true);

        Assert.That(text, Does.StartWith("That regular expression is not valid"));
    }

    [Test]
    public void AnUnknownLevelComesBackAsAnExplanation()
    {
        Assert.That(_tools.SearchLog(minLevel: "loud"), Does.StartWith("Unknown level"));
    }

    [Test]
    public void AnUnreadableTimeComesBackAsAnExplanation()
    {
        Assert.That(_tools.SearchLog(from: "yesterday-ish"), Does.StartWith("Could not read the time"));
    }

    [Test]
    public void GetEntriesReadsWholeEntriesAroundALine()
    {
        var error = _log.LineOf("08:05:01");
        var text = _tools.GetEntries(error, before: 1, after: 1);

        Assert.That(text, Does.Contain(">> #" + error));
        Assert.That(text, Does.Contain("java.net.SocketException: Connection reset"), "The whole entry, stack trace included.");
        Assert.That(text, Does.Contain("#" + _log.LineOf("08:05:00")), "One entry before.");
        Assert.That(text, Does.Contain("#" + _log.LineOf("08:10:00")), "One entry after.");
        Assert.That(text, Does.Not.Contain("#" + _log.LineOf("08:20:00")));
    }

    [Test]
    public void GetEntriesOnAStackTraceLineReadsItsWholeEntry()
    {
        var text = _tools.GetEntries(_log.LineOf("Repo.java"), before: 0, after: 0);

        Assert.That(text, Does.Contain(">> #" + _log.LineOf("08:05:01")));
    }

    [Test]
    public void GetEntriesOutsideTheLogSaysSo()
    {
        Assert.That(_tools.GetEntries(9999), Does.StartWith("There is no line 9999"));
    }

    [Test]
    public void CountOverTimeSplitsTheSpanIntoSlices()
    {
        var text = _tools.CountOverTime(slices: 4);
        var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();

        Assert.That(rows, Has.Count.EqualTo(4));
        Assert.That(rows.Sum(r => int.Parse(r.Split("total ")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)), Is.EqualTo(8));
    }

    [Test]
    public void PatternsGroupEntriesThatDifferOnlyInTheirNumbers()
    {
        var text = _tools.FindPatterns();

        Assert.That(text, Does.Contain("3  37.5%  INFO  [pool-<n>] GET /orders/<n> status=<n> latency=<n>ms client=<ip>"));
    }

    [Test]
    public void RarestPatternsComeFirstWhenAsked()
    {
        var text = _tools.FindPatterns(rarest: true, limit: 1);

        Assert.That(text, Does.Not.Contain("GET /orders"), "The common request shape is not the rarest.");
    }

    [Test]
    public void FieldsAndValuesComeFromTheHost()
    {
        Assert.That(_tools.ListFields(), Does.Contain("status — 3"));
        Assert.That(_tools.GetFieldValues("status"), Does.Contain("200 — 3"));
        Assert.That(_tools.GetFieldValues(""), Does.StartWith("Give a field name"));
    }

    [Test]
    public void NothingPersonalLeavesThroughAnyTool()
    {
        _tools.GetOverview();
        _tools.SearchLog();
        _tools.GetEntries(_log.LineOf("08:05:00"), 5, 5);
        _tools.FindPatterns();

        var everything = string.Join("\n", _sent);
        Assert.That(everything, Does.Not.Contain("alice@example.com"));
        Assert.That(everything, Does.Not.Contain("hunter22"));
        Assert.That(everything, Does.Not.Contain("10.1.2.3"));
        Assert.That(everything, Does.Contain("«EMAIL_1»"));
    }

    [Test]
    public void ScopedToTheViewTheToolsSeeOnlyWhatTheUserFiltered()
    {
        _log.Filter("", levelMask: (1 << 5) | (1 << 6) | 1);   // ERROR and above
        _log.ScopeToView = true;

        Assert.That(_tools.GetOverview(), Does.Contain("Entries: 2"));
        Assert.That(_tools.SearchLog("GET"), Does.StartWith("Matched 0 entries"), "The requests are INFO, outside the user's view.");
    }

    [Test]
    public void WithNoLogOpenTheToolsSaySo()
    {
        var empty = new EmptyContext();
        var tools = new LogTools(empty, _log.Json, new Redactor());

        Assert.That(tools.GetOverview(), Is.EqualTo("No log is open in Logzilla."));
    }

    [Test]
    public void OutputIsCappedSoOneCallCannotFloodTheModel()
    {
        var big = string.Concat(Enumerable.Range(0, 3000).Select(i =>
            "2026-03-01 08:00:" + (i % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + " INFO " + new string('x', 200) + " " + i + "\n"));
        using var log = new TestLog(big);
        var tools = new LogTools(log, log.Json, new Redactor());

        Assert.That(tools.SearchLog(limit: 100).Length, Is.LessThanOrEqualTo(LogTools.MaxOutputChars + 100));
    }

    private sealed class EmptyContext : ILogContext
    {
        public Core.Logs.LogDocument Document => null;
        public Core.Query.FilterResult View => null;
        public string FilterSummary => "";
        public bool ScopeToView => false;
        public Core.Query.IFilterEngine Engine => null;
        public string ClockName => "";
        public Core.Logs.ILineReader OpenReader() => throw new InvalidOperationException();
        public string FormatTime(Core.Logs.LogSource source, double storedMs) => "";
        public bool TryParseTime(string text, out double storedMs) { storedMs = double.NaN; return false; }
        public IReadOnlyList<(string Key, int Entries)> Fields() => [];
        public IReadOnlyList<(string Value, int Count)> FieldValues(string key) => [];
    }
}
