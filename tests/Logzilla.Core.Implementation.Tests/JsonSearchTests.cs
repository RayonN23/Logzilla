using System;
using System.IO;
using System.Text;
using Logzilla.Core.Implementation.Formats;
using Logzilla.Core.Implementation.Logs;
using Logzilla.Core.Implementation.Query;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// Searching a JSON log searches what the records say, not how they are written.
/// Matching the raw line let key names and template placeholders — "name",
/// "SourceContext", "{CommandType}" — hit every line in the file, so a search
/// returned far more than anything on screen could explain.
/// </summary>
[TestFixture]
public class JsonSearchTests
{
    private const string Log =
        "{\"@t\":\"2026-06-18T16:33:45Z\",\"@mt\":\"{Name}.ProcessMaintenance: {CommandType}\",\"Name\":\"MaintenanceCommandManager\",\"CommandType\":\"PushNotification\",\"SourceContext\":\"Soti.Service\",\"CorrelationId\":\"136ea017\",\"ThreadId\":7}\n" +
        "{\"@t\":\"2026-06-18T16:33:46Z\",\"@mt\":\"Device {DeviceId} checked in\",\"DeviceId\":\"ABC-42\",\"SourceContext\":\"Soti.Service\",\"CorrelationId\":\"77aa\",\"ThreadId\":9}\n" +
        "{\"@t\":\"2026-06-18T16:33:47Z\",\"@l\":\"Error\",\"@m\":\"Upload to C:\\\\temp\\\\out failed: \\\"disk full\\\"\",\"SourceContext\":\"Soti.Upload\"}\n";

    private static readonly int[] First = [0];
    private static readonly int[] Second = [1];
    private static readonly int[] Third = [2];

    private string _path;
    private LogDocument _document;

    [SetUp]
    public void Load()
    {
        _path = Path.Combine(Path.GetTempPath(), "lz-json-search-" + Guid.NewGuid().ToString("N") + ".log");
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

    private int[] Find(string query, bool regex = false)
    {
        var spec = QuerySpec.Build(query, regex: regex, cs: false, mask: 0x7F, srcMask: -1L,
            t0: double.NaN, t1: double.NaN, ctx: 0, group: true, filter: true);
        return new FilterEngine().Run(_document, spec, null, null).View.ToArray();
    }

    [Test]
    public void TheFixtureIsReadAsJson()
    {
        Assert.That(_document.Srcs[0].IsJson, Is.True);
        Assert.That(_document.N, Is.EqualTo(3));
    }

    [TestCase("name")]
    [TestCase("sourcecontext")]
    [TestCase("commandtype")]
    [TestCase("correlationid")]
    [TestCase("\"@t\"")]
    [TestCase("deviceid")]
    public void KeyNamesAndPlaceholdersMatchNothing(string query)
    {
        Assert.That(Find(query), Is.Empty);
    }

    [Test]
    public void MessageWordsAndRenderedValuesStillMatch()
    {
        Assert.That(Find("processmaintenance"), Is.EqualTo(First));
        Assert.That(Find("pushnotification"), Is.EqualTo(First), "A value the template puts on screen.");
        Assert.That(Find("checked in"), Is.EqualTo(Second));
        Assert.That(Find("\"checked in\""), Is.EqualTo(Second));
    }

    [Test]
    public void HiddenPropertyValuesStaySearchable()
    {
        Assert.That(Find("136ea017"), Is.EqualTo(First), "Pasting a correlation id finds its entries.");
        Assert.That(Find("soti.upload"), Is.EqualTo(Third));
    }

    [Test]
    public void EscapedTextIsFoundAsItReads()
    {
        Assert.That(Find(@"C:\temp\out"), Is.EqualTo(Third));
        Assert.That(Find("failed: \"disk"), Is.EqualTo(Third), "A quote inside the message is text.");
        Assert.That(Find(@"\""disk"), Is.Empty, "The backslash escaping it is not.");
        Assert.That(Find("disk full"), Is.EqualTo(Third));
    }

    [Test]
    public void APhraseCannotRunFromOneValueIntoTheNext()
    {
        // "MaintenanceCommandManager" is followed by "PushNotification" in the record.
        Assert.That(Find("\"MaintenanceCommandManager PushNotification\""), Is.Empty);
        Assert.That(Find("managerpush"), Is.Empty);
    }

    [Test]
    public void ARegexSeesTheSameText()
    {
        Assert.That(Find("^.*SourceContext", regex: true), Is.Empty);
        Assert.That(Find(@"checked\s+in", regex: true), Is.EqualTo(Second));
    }

    [Test]
    public void ProjectionKeepsValuesAndDropsSyntax()
    {
        var line = Encoding.UTF8.GetBytes("{\"@mt\":\"Hi {User}, {{literal}}\",\"User\":\"ann\",\"n\":12,\"ok\":true,\"u\":\"caf\\u00e9\"}");
        byte[] buf = null;

        var n = JsonSearchText.Project(line, 0, line.Length, ref buf);

        Assert.That(Encoding.UTF8.GetString(buf, 0, n), Is.EqualTo("Hi , {{literal}}\nann\n12\ntrue\ncafé"));
    }

    [Test]
    public void ALineThatIsNotAnObjectIsSearchedAsWritten()
    {
        var line = Encoding.UTF8.GetBytes("Starting service, config=prod");
        byte[] buf = null;

        Assert.That(JsonSearchText.Project(line, 0, line.Length, ref buf), Is.EqualTo(-1));
    }

    [Test]
    public void ACutOffRecordDoesNotThrow()
    {
        var line = Encoding.UTF8.GetBytes("{\"@m\":\"half a mess\\");
        byte[] buf = null;

        var n = JsonSearchText.Project(line, 0, line.Length, ref buf);

        Assert.That(Encoding.UTF8.GetString(buf, 0, n), Does.StartWith("half a mess"));
    }
}
