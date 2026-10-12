using System;
using System.IO;
using System.Linq;
using System.Text;
using Logzilla.Core.Implementation.Logs;
using Logzilla.Core.Implementation.Query;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// A scan the user has typed past is abandoned at the next cancellation check. A regex
/// or field filter decodes every line, so its checks come far more often than a plain
/// search's: tens of thousands of slow lines used to run on after the result was moot.
/// </summary>
[TestFixture]
public sealed class FilterEngineCancelTests
{
    private const int Lines = 20_000;
    private const string Pattern = @"request \d+3 ";

    private string _path;
    private LogDocument _document;

    [SetUp]
    public void Load()
    {
        _path = Path.Combine(Path.GetTempPath(), "cn-cancel-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(_path, string.Concat(Enumerable.Range(0, Lines).Select(i =>
            "2026-03-01 08:00:00.000 INFO  request " + i + " user=u" + (i % 7) + "\n")), new UTF8Encoding(false));
        var indexer = new LogIndexer(TestClock.Instance);
        var source = indexer.Prepare(_path);
        _document = new LogDocument { Name = source.Name };
        _document.Srcs.Add(source);
        indexer.Run(_document, 0, progress: null, cancelled: null);
    }

    [TearDown]
    public void Remove() => File.Delete(_path);

    [Test]
    public void ARegexScanChecksForCancellationEveryFewThousandLines()
    {
        Assert.That(Checks(QuerySpec.Build(Pattern, true, false, 0x7F, -1, double.NaN, double.NaN, 0, true, true)),
            Is.GreaterThanOrEqualTo(Lines / 4096));
    }

    [Test]
    public void AFieldFilterScanChecksForCancellationEveryFewThousandLines()
    {
        var spec = QuerySpec.Build("", false, false, 0x7F, -1, double.NaN, double.NaN, 0, true, true);
        spec.Fields.Add(new FieldFilter("user", FieldOp.Is, ["u3"]));

        Assert.That(Checks(spec), Is.GreaterThanOrEqualTo(Lines / 4096));
    }

    [Test]
    public void ACancelledRegexScanStopsWithinAFewThousandLines()
    {
        var spec = QuerySpec.Build(Pattern, true, false, 0x7F, -1, double.NaN, double.NaN, 0, true, true);
        var furthest = 0;
        var checks = 0;

        var result = new FilterEngine().Run(_document, spec, () => ++checks > 1, (done, _) => furthest = done);

        Assert.That(result, Is.Null);
        Assert.That(furthest, Is.LessThanOrEqualTo(4096));
    }

    private int Checks(QuerySpec spec)
    {
        var checks = 0;
        var result = new FilterEngine().Run(_document, spec, () => { checks++; return false; }, null);
        Assert.That(result, Is.Not.Null);
        return checks;
    }
}
