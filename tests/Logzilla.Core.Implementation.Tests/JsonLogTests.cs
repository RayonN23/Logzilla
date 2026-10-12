using System.Text;
using Logzilla.Core.Common;
using Logzilla.Core.Implementation.Formats;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// Structured log support. The sample shapes here mirror Serilog's compact
/// format (CLEF), which is what the tool was built against.
/// </summary>
[TestFixture]
public class JsonLogTests
{
    private static readonly string[] NameKey = ["Name"];
    private static readonly string[] CommandTypeKey = ["CommandType"];
    private static readonly string[] ThreadIdKey = ["ThreadId"];
    private static readonly string[] EventIdKey = ["EventId"];
    private static readonly string[] AfterKey = ["After"];

    private const string Clef =
        "{\"@t\":\"2026-06-18T16:33:45.3606976Z\"," +
        "\"@mt\":\"{Name}.ProcessMaintenance: {CommandType}\"," +
        "\"Name\":\"MaintenanceCommandManager\"," +
        "\"CommandType\":\"WindowsUpdatesPolicy\"," +
        "\"SourceContext\":\"Soti.MobiControl.WindowsModern.Service.Implementation\"," +
        "\"ThreadId\":7}";

    [Test]
    public void ParsesFlatProperties()
    {
        var kv = JsonLog.Parse(Clef);
        Assert.That(JsonLog.Get(kv, NameKey), Is.EqualTo("MaintenanceCommandManager"));
        Assert.That(JsonLog.Get(kv, CommandTypeKey), Is.EqualTo("WindowsUpdatesPolicy"));
        Assert.That(JsonLog.Get(kv, ThreadIdKey), Is.EqualTo("7"));
    }

    /// <summary>The whole point of CLEF rendering: placeholders come from the
    /// event's own properties, turning a machine record into a sentence.</summary>
    [Test]
    public void FillsMessageTemplatePlaceholders()
    {
        Assert.That(
            JsonLog.Line(Clef),
            Is.EqualTo("MaintenanceCommandManager.ProcessMaintenance: WindowsUpdatesPolicy"));
    }

    [Test]
    public void LeavesUnknownPlaceholdersVisible()
    {
        var line = "{\"@t\":\"2026-06-18T16:33:45.0000000Z\",\"@mt\":\"hello {Missing}\"}";
        Assert.That(JsonLog.Line(line), Is.EqualTo("hello {Missing}"));
    }

    [Test]
    public void HonoursTheRenderedMessageWhenPresent()
    {
        var line = "{\"@t\":\"2026-06-18T16:33:45.0000000Z\"," +
                   "\"@mt\":\"template {X}\",\"@m\":\"already rendered\",\"X\":\"1\"}";
        Assert.That(JsonLog.Line(line), Is.EqualTo("already rendered"));
    }

    [Test]
    public void UnescapesEmbeddedNewlinesAndQuotes()
    {
        var line = "{\"@t\":\"2026-06-18T16:33:45.0000000Z\"," +
                   "\"@mt\":\"said \\\"hi\\\"\",\"@x\":\"Boom\\r\\n   at Foo.Bar()\"}";
        var pretty = JsonLog.Pretty(line);
        Assert.That(pretty, Does.Contain("said \"hi\""));
        Assert.That(pretty, Does.Contain("at Foo.Bar()"));
        Assert.That(pretty, Does.Not.Contain("\\r\\n"));
    }

    [Test]
    public void SummaryLineAppendsTheExceptionHeadline()
    {
        var line = "{\"@t\":\"2026-06-18T16:33:45.0000000Z\",\"@mt\":\"failed\"," +
                   "\"@x\":\"System.InvalidOperationException: nope\\r\\n   at X()\"}";
        var summary = JsonLog.Line(line);
        Assert.That(summary, Does.Contain("failed"));
        Assert.That(summary, Does.Contain("System.InvalidOperationException: nope"));
        Assert.That(summary, Does.Not.Contain("\n"));   // must stay a single grid row
    }

    [TestCase("Verbose", 1)]
    [TestCase("Debug", 2)]
    [TestCase("Information", 3)]
    [TestCase("Warning", 4)]
    [TestCase("Error", 5)]
    [TestCase("Fatal", 6)]
    public void MapsSerilogLevelNames(string name, int expected)
    {
        Assert.That(JsonLog.Level(name), Is.EqualTo((byte)expected));
    }

    [TestCase("10", 1)]   // pino/bunyan trace
    [TestCase("20", 2)]
    [TestCase("30", 3)]
    [TestCase("40", 4)]
    [TestCase("50", 5)]
    [TestCase("60", 6)]
    public void MapsNumericLevels(string value, int expected) => Assert.That(JsonLog.Level(value), Is.EqualTo((byte)expected));

    /// <summary>The indexer reads the level without building a string; it must read the
    /// same level the string-based path did, for every shape a level value comes in.</summary>
    [TestCase("{\"@l\":\"Warning\"}")]
    [TestCase("{\"level\": \"error\" , \"x\":1}")]
    [TestCase("{\"level\":50}")]
    [TestCase("{\"level\":  30 }")]
    [TestCase("{\"level\":0}")]
    [TestCase("{\"level\":\"\"}")]
    [TestCase("{\"level\":\"  info  \"}")]
    [TestCase("{\"level\":\"\\\"fatal\\\"\"}")]
    [TestCase("{\"level\":\"VeryLongLevelNameThatGoesOnAndOnPastTheLimit\"}")]
    [TestCase("{\"level\":\"12345678901234567890123456789\"}")]
    [TestCase("{\"level\":\"crit")]
    [TestCase("{\"level\":\"érror\"}")]
    [TestCase("{\"level\":null}")]
    [TestCase("{\"level\":true}")]
    public void ReadingTheLevelInPlaceAgreesWithReadingItAsText(string json)
    {
        var b = Encoding.UTF8.GetBytes(json);
        var at = JsonLog.Find(b, 0, b.Length, json.Contains("@l") ? "@l" : "level");

        Assert.That(at, Is.GreaterThan(0));
        Assert.That(JsonLog.LevelAt(b, at, b.Length, 24), Is.EqualTo(JsonLog.Level(JsonLog.Scalar(b, at, b.Length, 24))));
    }

    [Test]
    public void FindLocatesAKeyInRawBytes()
    {
        var b = Encoding.UTF8.GetBytes(Clef);
        var at = JsonLog.Find(b, 0, b.Length, "CommandType");
        Assert.That(at > 0, Is.True);
        Assert.That(JsonLog.Scalar(b, at, b.Length, 64), Is.EqualTo("WindowsUpdatesPolicy"));
    }

    [Test]
    public void FindReturnsMinusOneWhenKeyAbsent()
    {
        var b = Encoding.UTF8.GetBytes(Clef);
        Assert.That(JsonLog.Find(b, 0, b.Length, "NotAKey"), Is.EqualTo(-1));
    }

    [Test]
    public void NestedObjectsSurviveAsRawText()
    {
        var line = "{\"@t\":\"2026-06-18T16:33:45.0000000Z\",\"@mt\":\"x\"," +
                   "\"EventId\":{\"Id\":105,\"Name\":\"ActionExecuted\"},\"After\":\"tail\"}";
        var kv = JsonLog.Parse(line);
        Assert.That(JsonLog.Get(kv, EventIdKey), Is.EqualTo("{\"Id\":105,\"Name\":\"ActionExecuted\"}"));
        Assert.That(JsonLog.Get(kv, AfterKey), Is.EqualTo("tail"));
    }

    [Test]
    public void MetadataKeysAreHiddenFromTheFieldsPanel()
    {
        Assert.That(JsonLog.IsMeta("@t"), Is.True);
        Assert.That(JsonLog.IsMeta("@mt"), Is.True);
        Assert.That(JsonLog.IsMeta("level"), Is.True);
        Assert.That(JsonLog.IsMeta("CorrelationId"), Is.False);
    }

    // ── Full: the entry with nothing clipped ───────────────────────────
    //
    // A collapsed grid row is one line tall, so Line() cuts the exception short.
    // That is a fact about the row, not about the entry, and Full() is what the
    // expanded row and the clipboard read instead.

    /// <summary>An exception headline longer than the grid preview allows.</summary>
    private const string LongException =
        "Soti.MobiControl.AppPolicy.Apple.AppleVolumePurchaseProgramException: " +
        "Apple's App License Management Account '1234' is no longer managed by this " +
        "instance of MobiControl, so the licence could not be reclaimed" +
        "\r\n   at Soti.MobiControl.WebApi.Common.Handler.Invoke()" +
        "\r\n   at Soti.MobiControl.WebApi.Common.Pipeline.Run()";

    private static string WithException(string ex) =>
        "{\"@t\":\"2026-06-18T16:33:45.0000000Z\",\"@mt\":\"An unhandled exception occurred\"," +
        "\"@x\":\"" + ex.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"}";

    [Test]
    public void TheGridSummaryShowsTheWholeExceptionHeadlineOnOneLine()
    {
        var summary = JsonLog.Line(WithException(LongException));

        Assert.That(summary, Does.Contain("so the licence could not be reclaimed"),
            "The headline used to stop at 160 characters with \"...\", hiding what went wrong.");
        Assert.That(summary, Does.Not.Contain(" ..."));
        Assert.That(summary, Does.Not.Contain("\n"), "A grid row is one line tall.");
        Assert.That(summary, Does.Not.Contain("Handler.Invoke"), "The stack trace stays in the expanded row.");
    }

    [Test]
    public void FullKeepsTheWholeExceptionHeadline()
    {
        var full = JsonLog.Full(WithException(LongException));

        Assert.That(full, Does.Contain("so the licence could not be reclaimed"),
            "The expanded row and Ctrl+C must not inherit the grid row's height limit.");
        Assert.That(full, Does.Not.Contain(" ..."));
    }

    [Test]
    public void FullKeepsTheStackTraceOnItsOwnLines()
    {
        var full = JsonLog.Full(WithException(LongException));
        var lines = full.Split('\n');

        Assert.That(lines[0], Is.EqualTo("An unhandled exception occurred"));
        Assert.That(lines, Has.Length.EqualTo(4));
        Assert.That(lines[^1].Trim(), Is.EqualTo("at Soti.MobiControl.WebApi.Common.Pipeline.Run()"));
        Assert.That(full, Does.Not.Contain("\r"), "Line breaks are normalised for the renderer.");
    }

    [Test]
    public void FullRendersTheTemplateJustLikeTheSummary()
    {
        Assert.That(JsonLog.Full(Clef), Is.EqualTo(JsonLog.Line(Clef)));
    }

    [Test]
    public void FullFallsBackToTheRawRecordWhenItIsNotJson()
    {
        const string notJson = "plain text, no braces here";
        Assert.That(JsonLog.Full(notJson), Is.EqualTo(notJson));
    }

    // ── XML payloads in the detail panel ───────────────────────────────

    [Test]
    public void AnXmlPropertyIsIndentedUnderItsKeyInTheDetailPanel()
    {
        var line = "{\"@t\":\"2026-06-18T16:33:45.0000000Z\",\"@mt\":\"Device Request\"," +
                   "\"ResponseText\":\"<?xml version=\\\"1.0\\\"?><plist><dict>" +
                   "<key>Status</key><string>Acknowledged</string></dict></plist>\"," +
                   "\"ThreadId\":7}";

        var pretty = JsonLog.Pretty(line);

        Assert.That(pretty, Does.Contain("  ResponseText\r\n"),
            "The key goes on its own line so the payload can start at the left.");
        Assert.That(pretty, Does.Contain("    <plist>\r\n"));
        Assert.That(pretty, Does.Contain("      <dict>\r\n"));
        Assert.That(pretty, Does.Contain("        <key>Status</key>\r\n"));
    }

    [Test]
    public void AnOrdinaryPropertyStillSitsBesideItsKey()
    {
        Assert.That(JsonLog.Pretty(Clef), Does.Contain("  Name"));
        Assert.That(JsonLog.Pretty(Clef), Does.Contain("MaintenanceCommandManager"));
        Assert.That(JsonLog.Pretty(Clef).Split("\r\n"),
            Has.Some.Contains("Name").And.Some.Contains("MaintenanceCommandManager"));
    }
}

[TestFixture]
public class QuerySpecTests
{
    private static QuerySpec Build(string query, bool regex = false, bool caseSensitive = false)
    {
        const int allLevels = 0x7F;
        const long allSources = -1L;
        return QuerySpec.Build(query, regex, caseSensitive, allLevels, allSources,
                               double.NaN, double.NaN, 0, true, true);
    }

    [Test]
    public void SplitsBareTermsIntoIncludes()
    {
        var s = Build("timeout retry");
        Assert.That(s.Inc.Count, Is.EqualTo(2));
        Assert.That(s.Exc, Is.Empty);
    }

    [Test]
    public void TreatsQuotedTextAsOnePhrase()
    {
        var s = Build("\"connection reset\"");
        Assert.That(s.Inc, Has.Count.EqualTo(1));
        Assert.That(Encoding.UTF8.GetString(s.Inc[0]), Is.EqualTo("connection reset"));
    }

    [Test]
    public void MinusPrefixExcludes()
    {
        var s = Build("error -heartbeat");
        Assert.That(s.Inc, Has.Count.EqualTo(1));
        Assert.That(s.Exc, Has.Count.EqualTo(1));
        Assert.That(Encoding.UTF8.GetString(s.Exc[0]), Is.EqualTo("heartbeat"));
    }

    [Test]
    public void LowercasesTermsWhenCaseInsensitive()
    {
        var s = Build("TimeOut");
        Assert.That(Encoding.UTF8.GetString(s.Inc[0]), Is.EqualTo("timeout"));
    }

    [Test]
    public void PreservesCaseWhenAsked()
    {
        var s = Build("TimeOut", caseSensitive: true);
        Assert.That(Encoding.UTF8.GetString(s.Inc[0]), Is.EqualTo("TimeOut"));
    }

    [Test]
    public void EmptyQueryMatchesEverything()
    {
        Assert.That(Build("").HasQuery, Is.False);
    }

    [Test]
    public void InvalidRegexDoesNotThrow()
    {
        var s = Build("([unclosed", regex: true);
        Assert.That(s.Rx, Is.Null);          // falls back to matching nothing rather than crashing
    }

    [Test]
    public void ContainsHonoursCaseFolding()
    {
        var hay = Encoding.UTF8.GetBytes("Connection RESET by peer");
        var needle = Encoding.UTF8.GetBytes("reset");
        Assert.That(QuerySpec.Contains(hay, 0, hay.Length, needle, false), Is.True);
        Assert.That(QuerySpec.Contains(hay, 0, hay.Length, needle, true), Is.False);
    }

    [Test]
    public void ContainsRespectsTheGivenWindow()
    {
        var hay = "aaaa target bbbb"u8.ToArray();
        var needle = "target"u8.ToArray();
        Assert.That(QuerySpec.Contains(hay, 0, hay.Length, needle, false), Is.True);
        Assert.That(QuerySpec.Contains(hay, 0, 4, needle, false), Is.False);
    }
}

[TestFixture]
public class DisplayFormatterTests
{
    [Test]
    public void DaysMatchesTheGregorianCalendar()
    {
        Assert.That(CivilDate.DaysSinceEpoch(1970, 1, 1), Is.EqualTo(0));
        Assert.That(CivilDate.DaysSinceEpoch(1970, 1, 2), Is.EqualTo(1));
        Assert.That(CivilDate.DaysSinceEpoch(1971, 1, 1), Is.EqualTo(365));
        Assert.That(CivilDate.DaysSinceEpoch(2000, 2, 29), Is.EqualTo(11016));   // leap day
    }

    [TestCase(512L, "512 B")]
    [TestCase(2048L, "2.0 KB")]
    [TestCase(5L * 1024 * 1024, "5.0 MB")]
    public void FormatsByteSizes(long bytes, string expected)
    {
        Assert.That(DisplayFormatter.Bytes(bytes), Is.EqualTo(expected));
    }

    [Test]
    public void FormatsDurations()
    {
        Assert.That(DisplayFormatter.Duration(1500), Is.EqualTo("1.5s"));
        Assert.That(DisplayFormatter.Duration(120000), Is.EqualTo("2m 0s"));
        Assert.That(DisplayFormatter.Duration(3600000), Is.EqualTo("1h 0m"));
    }

    [Test]
    public void TimeOfNaNIsEmptyRatherThanThrowing()
    {
        Assert.That(DisplayFormatter.Time(double.NaN), Is.EqualTo(""));
    }
}
