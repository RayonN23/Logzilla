using System.Linq;
using Logzilla.App.Controls;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.App.Tests;

/// <summary>CLEF's terse @-keys read as plain names, and the id worth following wins.</summary>
[TestFixture]
public sealed class FieldNamesTests
{
    [TestCase("@tr", "trace id")]
    [TestCase("@sp", "span id")]
    [TestCase("@i", "event id")]
    [TestCase("CorrelationId", "CorrelationId")]
    public void CleffIdsGetPlainNames(string key, string expected)
    {
        Assert.That(FieldNames.Display(key), Is.EqualTo(expected));
    }

    [Test]
    public void ListsKeepTheRealKeyFindable()
    {
        Assert.That(FieldNames.DisplayWithKey("@tr"), Is.EqualTo("trace id  (@tr)"));
        Assert.That(FieldNames.DisplayWithKey("Feature"), Is.EqualTo("Feature"));
    }

    [Test]
    public void AFilterIsDescribedByItsPlainName()
    {
        var f = new FieldFilter("@tr", FieldOp.Is, ["abc"]);
        Assert.That(FieldNames.Describe(f), Is.EqualTo("trace id is abc"));
        Assert.That(FieldChipBar.Parts(f, compact: false).Key, Is.EqualTo("trace id"));
        Assert.That(f.Key, Is.EqualTo("@tr"), "the filter itself keeps the real key");
    }

    [Test]
    public void TheIdTheApplicationLoggedBeatsTheTraceId()
    {
        // In the order CLEF writes them: @tr first, then the application's own properties.
        string[] keys = ["@tr", "Feature", "TraceId", "RequestId", "CorrelationId"];
        var best = keys.Where(k => FieldNames.CorrelationRank(k) >= 0).OrderBy(FieldNames.CorrelationRank).First();
        Assert.That(best, Is.EqualTo("CorrelationId"));
        Assert.That(FieldNames.CorrelationRank("@tr"), Is.GreaterThan(FieldNames.CorrelationRank("TraceId")));
        Assert.That(FieldNames.CorrelationRank("@tr"), Is.GreaterThanOrEqualTo(0), "still the fallback when nothing else is logged");
    }

    [Test]
    public void TypingTraceFindsTheTraceIdInTheEditor()
    {
        var c = new FieldCatalog();
        c.AddEntry([new("@tr", "t1"), new("Feature", "Sync")]);
        using var e = new FieldFilterEditor(c);
        e.Begin(null, FieldOp.Is, null, editing: false);
        e.KeyBox.Text = "trace";
        Assert.That(e.Suggestions.Items.Single().Text, Is.EqualTo("@tr"));
        Assert.That(e.Suggestions.Items.Single().Label, Is.EqualTo("trace id  (@tr)"));
    }
}
