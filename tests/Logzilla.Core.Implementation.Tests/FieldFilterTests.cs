using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Logzilla.Core.Implementation.Logs;
using Logzilla.Core.Implementation.Query;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// Field filters: "CorrelationId is …", "Feature is A or B", "path contains /orders".
/// One filter's values are alternatives, separate filters must all hold, and an entry
/// is judged as a whole — header line and continuations together.
/// </summary>
[TestFixture]
public class FieldFilterTests
{
    private static bool Json(string line, string key, FieldOp op, params string[] values) =>
        FieldScan.JsonHas(line, "\"" + key + "\"", op, new List<string>(values));

    private static bool Text(string line, string key, FieldOp op, params string[] values) =>
        FieldScan.TextHas(line, key + "=", op, new List<string>(values));

    // ── one line, JSON ─────────────────────────────────────────────────

    private const string Clef =
        "{\"@t\":\"2026-06-18T16:33:45Z\",\"Feature\":\"DeviceAction\",\"CorrelationId\":\"136ea017-3946\"," +
        "\"ThreadId\":7,\"Ok\":true,\"Props\":{\"Tenant\":\"acme\",\"Tags\":[\"a\",\"b\"]},\"Msg\":\"Feature\"}";

    [Test]
    public void AJsonStringValueMatchesExactlyButIgnoringCase()
    {
        Assert.That(Json(Clef, "Feature", FieldOp.Is, "DeviceAction"), Is.True);
        Assert.That(Json(Clef, "feature", FieldOp.Is, "deviceaction"), Is.True, "keys and values ignore case");
        Assert.That(Json(Clef, "Feature", FieldOp.Is, "Device"), Is.False, "is means the whole value");
        Assert.That(Json(Clef, "Feature", FieldOp.Contains, "vicea"), Is.True);
    }

    [Test]
    public void AnyOfSeveralValuesWillDo()
    {
        Assert.That(Json(Clef, "Feature", FieldOp.Is, "Sync", "DeviceAction"), Is.True);
        Assert.That(Json(Clef, "Feature", FieldOp.Is, "Sync", "Inventory"), Is.False);
    }

    [Test]
    public void NumbersBooleansAndNestedKeysAreFound()
    {
        Assert.That(Json(Clef, "ThreadId", FieldOp.Is, "7"), Is.True);
        Assert.That(Json(Clef, "ThreadId", FieldOp.Is, "77"), Is.False);
        Assert.That(Json(Clef, "Ok", FieldOp.Is, "true"), Is.True);
        Assert.That(Json(Clef, "Tenant", FieldOp.Is, "acme"), Is.True, "a key inside a nested object");
    }

    [Test]
    public void TheKeysNameUsedAsAValueIsNotTheKey()
    {
        // "Msg":"Feature" must not read as a Feature key whose value follows.
        const string line = "{\"Msg\":\"Feature\",\"Other\":\"DeviceAction\"}";
        Assert.That(Json(line, "Feature", FieldOp.Is, "DeviceAction"), Is.False);
    }

    [Test]
    public void ObjectsAndArraysAreNotValues()
    {
        Assert.That(Json(Clef, "Props", FieldOp.Contains, "acme"), Is.False);
        Assert.That(Json(Clef, "Tags", FieldOp.Contains, "a"), Is.False);
    }

    [Test]
    public void EscapedStringsCompareUnescaped()
    {
        const string line = "{\"Path\":\"C:\\\\logs\\\\app.log\",\"Q\":\"say \\\"hi\\\"\"}";
        Assert.That(Json(line, "Path", FieldOp.Is, "C:\\logs\\app.log"), Is.True);
        Assert.That(Json(line, "Q", FieldOp.Is, "say \"hi\""), Is.True);
    }

    [Test]
    public void SpacesAroundTheColonAreAllowed()
    {
        Assert.That(Json("{ \"Feature\" : \"X\" }", "Feature", FieldOp.Is, "X"), Is.True);
    }

    [Test]
    public void EveryOccurrenceOfTheKeyIsTried()
    {
        const string line = "{\"a\":{\"Id\":\"one\"},\"b\":{\"Id\":\"two\"}}";
        Assert.That(Json(line, "Id", FieldOp.Is, "two"), Is.True);
    }

    // ── one line, plain text ───────────────────────────────────────────

    private const string Plain =
        "2026-03-01 08:00:01.000 ERROR [orders] tenant=acme user=\"Jane Doe\" path=/api/orders/42, latency=984ms userid=7";

    [Test]
    public void KeyValuePairsInPlainTextAreFound()
    {
        Assert.That(Text(Plain, "tenant", FieldOp.Is, "ACME"), Is.True);
        Assert.That(Text(Plain, "user", FieldOp.Is, "jane doe"), Is.True, "a quoted value keeps its spaces");
        Assert.That(Text(Plain, "path", FieldOp.Is, "/api/orders/42"), Is.True, "the comma ends the value");
        Assert.That(Text(Plain, "latency", FieldOp.Contains, "984"), Is.True);
    }

    [Test]
    public void AKeyMustStartAWord()
    {
        Assert.That(Text(Plain, "id", FieldOp.Is, "7"), Is.False, "id= inside userid= is not the id field");
        Assert.That(Text(Plain, "userid", FieldOp.Is, "7"), Is.True);
    }

    // ── whole entries through the filter engine ────────────────────────

    private string _dir;

    [SetUp]
    public void CreateDirectory()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cn-fields-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void RemoveDirectory()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private LogDocument Load(string name, string content, Encoding encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        var indexer = new LogIndexer(TestClock.Instance);
        var source = indexer.Prepare(path);
        var document = new LogDocument { Name = source.Name };
        document.Srcs.Add(source);
        indexer.Run(document, 0, null, null);
        return document;
    }

    private static FilterResult Run(LogDocument document, string query, params FieldFilter[] filters)
    {
        var spec = QuerySpec.Build(query, false, false, 0x7F, -1, double.NaN, double.NaN, 0, true, true);
        spec.Fields.AddRange(filters);
        return new FilterEngine().Run(document, spec, null, null);
    }

    private static FieldFilter F(string key, FieldOp op, params string[] values) => new(key, op, values);

    private static string JsonLine(int second, string feature, string correlation, string level = "Information") =>
        "{\"@t\":\"2026-06-18T16:33:" + second.ToString("00", System.Globalization.CultureInfo.InvariantCulture) +
        "Z\",\"@l\":\"" + level + "\",\"@mt\":\"step\",\"Feature\":\"" + feature + "\",\"CorrelationId\":\"" + correlation + "\"}\n";

    private LogDocument JsonDoc() => Load("clef.log",
        JsonLine(1, "DeviceAction", "c-1") +
        JsonLine(2, "Sync", "c-1") +
        JsonLine(3, "DeviceAction", "c-2", "Error") +
        JsonLine(4, "Inventory", "c-2") +
        JsonLine(5, "Sync", "c-3"));

    private static readonly int[] OnlyC1 = [0, 1];
    private static readonly int[] DeviceActionOrSync = [0, 1, 2, 4];
    private static readonly int[] C2DeviceAction = [2];
    private static readonly int[] AllButSync = [0, 2, 3];
    private static readonly int[] TheC2Entries = [2, 3];
    private static readonly int[] FirstOnly = [0];
    private static readonly string[] SyncThenDeviceAction = ["Sync", "DeviceAction"];

    [Test]
    public void FilteringOnACorrelationIdShowsItsWholeConversation()
    {
        Assert.That(Run(JsonDoc(), "", F("CorrelationId", FieldOp.Is, "c-1")).View, Is.EqualTo(OnlyC1));
    }

    [Test]
    public void ValuesOfOneFilterAreAlternatives()
    {
        Assert.That(Run(JsonDoc(), "", F("Feature", FieldOp.Is, "DeviceAction", "Sync")).View, Is.EqualTo(DeviceActionOrSync));
    }

    [Test]
    public void SeparateFiltersMustAllHold()
    {
        var r = Run(JsonDoc(), "", F("Feature", FieldOp.Is, "DeviceAction"), F("CorrelationId", FieldOp.Is, "c-2"));
        Assert.That(r.View, Is.EqualTo(C2DeviceAction));
    }

    [Test]
    public void IsNotHidesTheValue()
    {
        Assert.That(Run(JsonDoc(), "", F("Feature", FieldOp.IsNot, "Sync")).View, Is.EqualTo(AllButSync));
    }

    [Test]
    public void IsNotKeepsEntriesThatDoNotHaveTheFieldAtAll()
    {
        Assert.That(Run(JsonDoc(), "", F("Missing", FieldOp.IsNot, "x")).View, Has.Count.EqualTo(5));
    }

    [Test]
    public void ContainsMatchesPartOfAValue()
    {
        Assert.That(Run(JsonDoc(), "", F("CorrelationId", FieldOp.Contains, "c-")).View, Has.Count.EqualTo(5));
    }

    [Test]
    public void FieldFiltersNarrowTheViewEvenInHighlightMode()
    {
        var spec = QuerySpec.Build("step", false, false, 0x7F, -1, double.NaN, double.NaN, 0, true, filter: false);
        spec.Fields.Add(F("CorrelationId", FieldOp.Is, "c-1"));
        var r = new FilterEngine().Run(JsonDoc(), spec, null, null);
        Assert.That(r.View, Is.EqualTo(OnlyC1));
        Assert.That(r.Matches, Is.EqualTo(OnlyC1), "the text query still decides the hits among what is shown");
    }

    [Test]
    public void FieldFiltersCombineWithTheTextQueryAndTheLevels()
    {
        var doc = JsonDoc();
        Assert.That(Run(doc, "step", F("CorrelationId", FieldOp.Is, "c-2")).Matches, Is.EqualTo(TheC2Entries));
        var errorsOnly = QuerySpec.Build("", false, false, 1 | (1 << 5), -1, double.NaN, double.NaN, 0, true, true);
        errorsOnly.Fields.Add(F("CorrelationId", FieldOp.Is, "c-2"));
        Assert.That(new FilterEngine().Run(doc, errorsOnly, null, null).View, Is.EqualTo(C2DeviceAction));
    }

    private const string PlainLog =
        "2026-03-01 08:00:00.000 INFO  request ok tenant=acme\n" +
        "2026-03-01 08:00:01.000 ERROR Request failed tenant=globex\n" +
        "java.lang.IllegalStateException: order=42\n" +
        "\tat com.acme.Repo.load(Repo.java:88)\n" +
        "2026-03-01 08:00:04.000 INFO  recovered tenant=acme order=42\n";

    private static readonly int[] TheErrorEntry = [1, 2, 3];
    private static readonly int[] TheInfoEntries = [0, 4];

    [Test]
    public void APlainTextFieldOnAContinuationLineCountsForItsEntry()
    {
        var r = Run(Load("plain.log", PlainLog), "", F("tenant", FieldOp.Is, "globex"), F("order", FieldOp.Is, "42"));
        Assert.That(r.View, Is.EqualTo(TheErrorEntry), "tenant is on the header, order three lines down");
    }

    [Test]
    public void IsNotLooksAtTheWholeEntryToo()
    {
        var r = Run(Load("plain.log", PlainLog), "", F("tenant", FieldOp.IsNot, "globex"));
        Assert.That(r.View, Is.EqualTo(TheInfoEntries));
    }

    [Test]
    public void ValuesThatNeedDecodingStillMatch()
    {
        // Non-ASCII values skip the byte prefilter and are compared as text.
        var doc = Load("utf8.log", JsonLine(1, "Café", "c-1") + JsonLine(2, "Cafe", "c-2"));
        Assert.That(Run(doc, "", F("Feature", FieldOp.Is, "CAFÉ")).View, Is.EqualTo(FirstOnly));
    }

    [TestCase("utf-16")]
    [TestCase("utf-16BE")]
    public void Utf16LogsAreFilteredByFieldToo(string encoding)
    {
        var doc = Load("u16.log", PlainLog.Replace("\n", "\r\n", StringComparison.Ordinal), Encoding.GetEncoding(encoding));
        Assert.That(Run(doc, "", F("tenant", FieldOp.Is, "globex")).View, Is.EqualTo(TheErrorEntry));
    }

    [Test]
    public void AFilterKeepsItsValuesDistinctAndInOrder()
    {
        var f = new FieldFilter(" Feature ", FieldOp.Is, ["Sync", "sync", "", "DeviceAction"]);
        Assert.That(f.Key, Is.EqualTo("Feature"));
        Assert.That(f.Values, Is.EqualTo(SyncThenDeviceAction));
        Assert.That(f.ToString(), Is.EqualTo("Feature is Sync or DeviceAction"));
    }

    [Test]
    public void NeedlesAreOnlyOfferedForValuesThatAppearAsTyped()
    {
        Assert.That(FieldMatch.Needles([F("CorrelationId", FieldOp.Is, "136EA017-3946")]),
            Has.Count.EqualTo(1).And.Some.EqualTo(Encoding.ASCII.GetBytes("136ea017-3946")));
        Assert.That(FieldMatch.Needles([F("Path", FieldOp.Is, "/api/x")]), Is.Null, "some writers escape /");
        Assert.That(FieldMatch.Needles([F("Html", FieldOp.Is, "<b>")]), Is.Null, "System.Text.Json writes < as \\u003C");
        Assert.That(FieldMatch.Needles([F("Name", FieldOp.Is, "José")]), Is.Null);
    }
}
