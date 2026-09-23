using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Implementation;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>
/// Building field filters up in the UI: how clicks combine into filters, what the
/// editor suggests and commits, and how the chip strip lays itself out.
/// </summary>
[TestFixture]
public sealed class FieldFilterUiTests
{
    private static readonly string[] DeviceActionAndSync = ["DeviceAction", "Sync"];
    private static readonly string[] SyncOnly = ["Sync"];
    private static readonly string[] OneAndTwo = ["one", "two"];

    private static string[] S(params string[] values) => values;

    // ── combining filters ──────────────────────────────────────────────

    [Test]
    public void ASecondValueForTheSameFieldJoinsTheFirstFilter()
    {
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "Feature", FieldOp.Is, ["DeviceAction"]);
        FieldFilterList.Add(list, "feature", FieldOp.Is, ["Sync"]);

        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].Values, Is.EqualTo(DeviceActionAndSync));
    }

    [Test]
    public void DifferentFieldsAreSeparateFilters()
    {
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "Feature", FieldOp.Is, ["Sync"]);
        FieldFilterList.Add(list, "CorrelationId", FieldOp.Is, ["c-1"]);
        Assert.That(list, Has.Count.EqualTo(2));
    }

    [Test]
    public void HidingAValueTakesItOutOfTheShowFilter()
    {
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "Feature", FieldOp.Is, ["DeviceAction", "Sync"]);
        FieldFilterList.Add(list, "Feature", FieldOp.IsNot, ["DeviceAction"]);

        Assert.That(list.Select(f => f.ToString()), Is.EqualTo(S("Feature is Sync", "Feature is not DeviceAction")));
    }

    [Test]
    public void AFilterLeftWithNoValuesDisappears()
    {
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "Feature", FieldOp.IsNot, ["Sync"]);
        FieldFilterList.Add(list, "Feature", FieldOp.Is, ["Sync"]);
        Assert.That(list.Select(f => f.ToString()), Is.EqualTo(S("Feature is Sync")));
    }

    [Test]
    public void ContainsNeverCancelsAnything()
    {
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "Path", FieldOp.IsNot, ["/health"]);
        FieldFilterList.Add(list, "Path", FieldOp.Contains, ["/health"]);
        Assert.That(list, Has.Count.EqualTo(2));
    }

    [Test]
    public void FiltersAreReplacedNotChanged()
    {
        // A query on the scan thread may hold the old one.
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "Feature", FieldOp.Is, ["DeviceAction"]);
        var before = list[0];
        FieldFilterList.Add(list, "Feature", FieldOp.Is, ["Sync"]);
        Assert.That(before.Values, Is.EqualTo(S("DeviceAction")));
        Assert.That(list[0], Is.Not.SameAs(before));
    }

    [Test]
    public void AnEditedFilterKeepsItsPlaceInTheRow()
    {
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "A", FieldOp.Is, ["1"]);
        FieldFilterList.Add(list, "B", FieldOp.Is, ["2"]);
        FieldFilterList.Add(list, "C", FieldOp.Is, ["3"]);

        FieldFilterList.Replace(list, 1, "B", FieldOp.Contains, ["x"]);

        Assert.That(list.Select(f => f.ToString()), Is.EqualTo(S("A is 1", "B contains x", "C is 3")));
    }

    [Test]
    public void EditingAFilterIntoAnotherMergesThem()
    {
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "Feature", FieldOp.Is, ["DeviceAction"]);
        FieldFilterList.Add(list, "Name", FieldOp.Is, ["x"]);
        FieldFilterList.Replace(list, 1, "Feature", FieldOp.Is, ["Sync"]);
        Assert.That(list.Select(f => f.ToString()), Is.EqualTo(S("Feature is DeviceAction or Sync")));
    }

    [Test]
    public void EditingAwayEveryValueRemovesTheFilter()
    {
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "Feature", FieldOp.Is, ["DeviceAction"]);
        FieldFilterList.Replace(list, 0, "Feature", FieldOp.Is, []);
        Assert.That(list, Is.Empty);
    }

    [Test]
    public void InvertingFlipsIsAndIsNot()
    {
        var list = new List<FieldFilter>();
        FieldFilterList.Add(list, "Feature", FieldOp.Is, ["Sync"]);
        FieldFilterList.Invert(list, 0);
        Assert.That(list.Single().ToString(), Is.EqualTo("Feature is not Sync"));
        Assert.That(FieldFilterList.CanInvert(new FieldFilter("P", FieldOp.Contains, ["x"])), Is.False);
    }

    [Test]
    public void AFilterSurvivesASetupFile()
    {
        var f = new FieldFilter("CorrelationId", FieldOp.IsNot, ["a=b|c", "d"]);
        var back = FieldFilterList.Parse(FieldFilterList.Serialize(f));
        Assert.That(back.ToString(), Is.EqualTo(f.ToString()));
    }

    [Test]
    public void AValueThatSpansLinesIsLeftOutOfASetupFile()
    {
        Assert.That(FieldFilterList.Serialize(new FieldFilter("M", FieldOp.Is, ["a\nb"])), Is.Null);
        Assert.That(FieldFilterList.Parse(FieldFilterList.Serialize(new FieldFilter("M", FieldOp.Is, ["a\nb", "c"]))).Values,
            Is.EqualTo(S("c")));
        Assert.That(FieldFilterList.Parse("garbage"), Is.Null);
        Assert.That(FieldFilterList.Parse("9|K|v"), Is.Null, "an unknown operator");
    }

    // ── the catalog ────────────────────────────────────────────────────

    private sealed class LinesReader : ILineReader
    {
        private readonly string[] _lines;
        public LinesReader(string[] lines) { _lines = lines; }
        public int Locate(int index, out byte[] buffer) { buffer = Encoding.UTF8.GetBytes(_lines[index]); return 0; }
        public string Text(int index) => _lines[index];
        public void Dispose() { }
    }

    private static FieldCatalog Catalog(bool json, params string[] lines)
    {
        var doc = new LogDocument { Name = "t" };
        doc.Srcs.Add(new LogSource { Path = "t", Name = "t", Label = "t", IsJson = json });
        for (var i = 0; i < lines.Length; i++)
        {
            doc.Flag[i] = (byte)(lines[i].StartsWith(' ') ? 1 : 0);
            doc.Ts[i] = double.NaN;
        }
        doc.N = lines.Length;
        return FieldCatalog.Build(doc, new LinesReader(lines), CoreServices.CreateJsonLogReader());
    }

    [Test]
    public void TheCatalogListsJsonFieldsMostCommonFirst()
    {
        var c = Catalog(true,
            "{\"@t\":\"2026-01-01T00:00:00Z\",\"@mt\":\"x\",\"@tr\":\"t1\",\"Feature\":\"Sync\",\"CorrelationId\":\"c-1\"}",
            "{\"@t\":\"2026-01-01T00:00:01Z\",\"@mt\":\"x\",\"Feature\":\"Sync\",\"Props\":{\"Tenant\":\"acme\"}}",
            "{\"@t\":\"2026-01-01T00:00:02Z\",\"@mt\":\"x\",\"Feature\":\"DeviceAction\"}");

        var keys = c.Keys().Select(k => k.Key).ToList();
        Assert.That(keys[0], Is.EqualTo("Feature"));
        Assert.That(keys, Does.Contain("Tenant"), "nested objects contribute their keys");
        Assert.That(keys, Does.Contain("@tr"), "a trace id is worth filtering on");
        Assert.That(keys, Does.Not.Contain("@t").And.Not.Contain("@mt").And.Not.Contain("Props"));
        Assert.That(c.ValuesOf("feature").Select(v => v.Value), Is.EqualTo(SyncOnly.Concat(["DeviceAction"])));
        Assert.That(c.ValuesOf("Feature")[0].Count, Is.EqualTo(2));
        Assert.That(c.Spelling("correlationid"), Is.EqualTo("CorrelationId"));
        Assert.That(c.Scanned, Is.EqualTo(3));
    }

    [Test]
    public void TheCatalogReadsKeyValuePairsFromPlainTextAndTheirContinuations()
    {
        var c = Catalog(false,
            "2026-03-01 08:00:00.000 INFO  ok tenant=acme user=\"Jane Doe\"",
            "2026-03-01 08:00:01.000 ERROR failed tenant=globex",
            "   caused by order=42",
            "2026-03-01 08:00:02.000 INFO  ok tenant=acme");

        Assert.That(c.Keys()[0], Is.EqualTo(("tenant", 3)));
        Assert.That(c.ValuesOf("user").Single().Value, Is.EqualTo("Jane Doe"));
        Assert.That(c.ValuesOf("order").Single().Value, Is.EqualTo("42"), "a continuation line counts for its entry");
    }

    [Test]
    public void AFieldCountsOncePerEntry()
    {
        var c = new FieldCatalog();
        c.AddEntry([new("Id", "one"), new("Id", "two")]);
        Assert.That(c.Keys().Single().Entries, Is.EqualTo(1));
        Assert.That(c.ValuesOf("Id").Select(v => v.Value), Is.EquivalentTo(OneAndTwo));
    }

    // ── the editor ─────────────────────────────────────────────────────

    private static FieldFilterEditor Editor(out List<FieldFilterEditor.CommitArgs> commits)
    {
        var c = new FieldCatalog();
        for (var i = 0; i < 5; i++)
        {
            c.AddEntry([new("Feature", i < 3 ? "DeviceAction" : "Sync"), new("CorrelationId", "c-" + i)]);
        }
        c.AddEntry([new("Name", "x")]);
        var editor = new FieldFilterEditor(c);
        var list = new List<FieldFilterEditor.CommitArgs>();
        editor.Committed += (_, a) => list.Add(a);
        commits = list;
        editor.Begin(null, FieldOp.Is, null, editing: false);
        return editor;
    }

    [Test]
    public void TheEditorOffersTheLogsFieldsAndNarrowsThemAsYouType()
    {
        using var e = Editor(out _);
        e.ShowKeys();
        Assert.That(e.Suggestions.Items.Select(i => i.Text), Is.EqualTo(S("CorrelationId", "Feature", "Name")));

        e.KeyBox.Text = "fea";
        Assert.That(e.Suggestions.Items.Select(i => i.Text), Is.EqualTo(S("Feature")));
        Assert.That(e.Suggestions.Highlight, Is.Zero, "typing highlights the best match, ready for Enter");
    }

    [Test]
    public void EnterOnAFieldMovesOnToItsValues()
    {
        using var e = Editor(out _);
        e.KeyBox.Text = "fea";
        e.PressEnterInKeyBox();

        Assert.That(e.KeyBox.Text, Is.EqualTo("Feature"), "the highlighted field is taken, spelled as the log spells it");
        e.ShowValues();
        Assert.That(e.Suggestions.Items.Select(i => i.Text), Is.EqualTo(S("DeviceAction", "Sync")), "most common first");
        Assert.That(e.Suggestions.Items[0].Right, Is.EqualTo("3"));
    }

    [Test]
    public void TypingAValueAndPressingEnterCommitsIt()
    {
        using var e = Editor(out var commits);
        e.KeyBox.Text = "CorrelationId";
        e.PressEnterInKeyBox();
        e.ValueBox.Text = "c-3";
        e.PressEnterInValueBox();

        Assert.That(commits.Single().Key, Is.EqualTo("CorrelationId"));
        Assert.That(commits.Single().Values, Is.EqualTo(S("c-3")));
        Assert.That(commits.Single().Op, Is.EqualTo(FieldOp.Is));
    }

    [Test]
    public void TickedValuesAreCommittedTogether()
    {
        using var e = Editor(out var commits);
        e.KeyBox.Text = "Feature";
        e.PressEnterInKeyBox();
        e.ShowValues();
        e.ClickSuggestion(0);   // ticks DeviceAction
        e.ClickSuggestion(1);   // ticks Sync
        e.ChooseOp(FieldOp.IsNot);
        e.PressEnterInValueBox();

        Assert.That(commits.Single().Values, Is.EqualTo(DeviceActionAndSync));
        Assert.That(commits.Single().Op, Is.EqualTo(FieldOp.IsNot));
    }

    [Test]
    public void EnterWithNothingTickedTakesTheHighlightedValue()
    {
        using var e = Editor(out var commits);
        e.KeyBox.Text = "Feature";
        e.PressEnterInKeyBox();
        e.ShowValues();
        e.Suggestions.Step(1);
        e.Suggestions.Step(1);
        e.PressEnterInValueBox();
        Assert.That(commits.Single().Values, Is.EqualTo(SyncOnly));
    }

    [Test]
    public void NothingIsCommittedWithoutAFieldOrAValue()
    {
        using var e = Editor(out var commits);
        e.PressEnterInValueBox();
        Assert.That(commits, Is.Empty, "no field");

        e.KeyBox.Text = "Unknown";
        e.PressEnterInKeyBox();
        e.PressEnterInValueBox();
        Assert.That(commits, Is.Empty, "no value, and nothing seen to suggest");
    }

    [Test]
    public void EditingStartsWithTheFiltersValuesTicked()
    {
        using var e = Editor(out var commits);
        e.Begin("Feature", FieldOp.Is, ["Sync"], editing: true);
        e.ShowValues();
        Assert.That(e.Suggestions.Items[0].Text, Is.EqualTo("Sync"));
        Assert.That(e.Suggestions.Items[0].Checked, Is.True);
        e.PressEnterInValueBox();
        Assert.That(commits.Single().Values, Is.EqualTo(SyncOnly));
    }

    // ── the chip strip ─────────────────────────────────────────────────

    [Test]
    public void AChipReadsAsASentence()
    {
        var (key, op, values, more) = FieldChipBar.Parts(new FieldFilter("Feature", FieldOp.IsNot, ["A", "B", "C"]), compact: false);
        Assert.That((key, op, string.Join(" or ", values), more), Is.EqualTo(("Feature", "is not", "A or B or C", false)));

        var (_, _, shortValues, isMore) = FieldChipBar.Parts(new FieldFilter("Feature", FieldOp.Is, ["A", "B", "C"]), compact: true);
        Assert.That(shortValues, Is.EqualTo(S("A", "+2 more")));
        Assert.That(isMore, Is.True);
        Assert.That(FieldChipBar.Shorten(new string('x', 80)), Has.Length.EqualTo(40).And.EndsWith("…"));
    }

    [Test]
    public void ChipsWrapOntoMoreRowsInsteadOfBeingCutOff()
    {
        using var bar = new FieldChipBar { Width = 1400 };
        var filters = Enumerable.Range(0, 3).Select(i => new FieldFilter("Key" + i, FieldOp.Is, ["value-" + i])).ToArray();
        bar.Filters = filters;
        var oneRow = bar.Relayout(1400);

        bar.Filters = Enumerable.Range(0, 30).Select(i => new FieldFilter("SomeLongerKey" + i, FieldOp.Is, ["value-" + i])).ToArray();
        var manyRows = bar.Relayout(1400);

        Assert.That(manyRows, Is.GreaterThan(oneRow));
    }

    [Test]
    public void TheChipBodyEditsAndItsCrossRemoves()
    {
        using var bar = new FieldChipBar { Width = 1000, Height = 60 };
        bar.Filters = [new FieldFilter("Feature", FieldOp.Is, ["Sync"])];
        bar.Relayout(1000);
        var anchor = bar.ChipAnchor(0);          // bottom-left of the chip
        var body = new Point(anchor.X + 5, anchor.Y - 10);

        Assert.That(bar.HitTest(body, out var onClose), Is.Zero);
        Assert.That(onClose, Is.False);

        var edits = 0;
        var removes = 0;
        bar.EditRequested += delegate { edits++; };
        bar.RemoveRequested += delegate { removes++; };
        var x = body.X;
        while (bar.HitTest(new Point(x + 1, body.Y), out _) == 0)
        {
            x++;
        }
        Assert.That(bar.HitTest(new Point(x - 2, body.Y), out var nearEnd), Is.Zero);
        Assert.That(nearEnd, Is.True, "the right end of a chip is its ×");
        Assert.That(edits + removes, Is.Zero);
    }

    // ── recognising correlation ids ────────────────────────────────────

    [TestCase("CorrelationId", true)]
    [TestCase("correlation_id", true)]
    [TestCase("X-Correlation-ID", true)]
    [TestCase("TraceId", true)]
    [TestCase("trace.id", true)]
    [TestCase("@tr", true)]
    [TestCase("RequestId", true)]
    [TestCase("Feature", false)]
    [TestCase("ThreadId", false)]
    public void CorrelationKeysAreRecognised(string key, bool expected)
    {
        Assert.That(FieldNames.CorrelationRank(key) >= 0, Is.EqualTo(expected));
    }
}
