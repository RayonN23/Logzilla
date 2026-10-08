using System;
using System.IO;
using System.Text;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Implementation.Logs;
using Logzilla.Core.Implementation.Query;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// Drives the indexer through real files on disk, which is the only way to prove
/// the byte-level path: line boundaries, timestamps, levels and continuation lines.
/// </summary>
[TestFixture]
public class LogIndexerTests
{
    private const string IsoLog =
        "2026-03-01 08:14:02.113 INFO  [main] Started OrderService\n" +
        "2026-03-01 08:14:07.882 WARN  [pool-1] Retry 1 for host=db-02\n" +
        "2026-03-01 08:14:09.001 ERROR [pool-1] Connection reset by peer\n" +
        "java.net.SocketException: Connection reset\n" +
        "\tat com.acme.orders.Repo.load(Repo.java:88)\n" +
        "2026-03-01 08:15:00.500 INFO  [main] Recovered\n";

    private string _directory;

    [SetUp]
    public void CreateDirectory()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cn-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void RemoveDirectory()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteLog(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static LogIndexer NewIndexer() => new(TestClock.Instance);

    private static LogDocument Load(LogIndexer indexer, string path)
    {
        var source = indexer.Prepare(path);
        var document = new LogDocument { Name = source.Name };
        document.Srcs.Add(source);
        indexer.Run(document, 0, progress: null, cancelled: null);
        return document;
    }

    [Test]
    public void PrepareDetectsIsoFormat()
    {
        var source = NewIndexer().Prepare(WriteLog("iso.log", IsoLog));

        Assert.That(source.Kind, Is.EqualTo(TsKind.Iso));
        Assert.That(source.IsJson, Is.False);
        Assert.That(source.Parser, Is.Not.Null);
    }

    [Test]
    public void PrepareRejectsAMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() => NewIndexer().Prepare(Path.Combine(_directory, "nope.log")));
    }

    [Test]
    public void PrepareRejectsABlankPath()
    {
        Assert.Throws<ArgumentException>(() => NewIndexer().Prepare("   "));
    }

    [Test]
    public void IndexesEveryLine()
    {
        var document = Load(NewIndexer(), WriteLog("iso.log", IsoLog));

        Assert.That(document.N, Is.EqualTo(6));
    }

    [Test]
    public void ReadsTimestampsAndLevels()
    {
        var document = Load(NewIndexer(), WriteLog("iso.log", IsoLog));

        Assert.That(DisplayFormatter.Time(document.Ts[0]), Is.EqualTo("2026-03-01 08:14:02.113"));
        Assert.That(document.Sev[0], Is.EqualTo(3));
        Assert.That(document.Sev[1], Is.EqualTo(4));
        Assert.That(document.Sev[2], Is.EqualTo(5));
    }

    /// <summary>A stack trace has no timestamp of its own; it belongs to the entry above.</summary>
    [Test]
    public void StackTraceLinesAreContinuationsOfTheirEntry()
    {
        var document = Load(NewIndexer(), WriteLog("iso.log", IsoLog));

        Assert.That(document.Flag[3], Is.EqualTo(1));
        Assert.That(document.Flag[4], Is.EqualTo(1));
        Assert.That(document.Ts[4], Is.EqualTo(document.Ts[2]));
        Assert.That(document.Flag[5], Is.EqualTo(0));
    }

    [Test]
    public void ReadsLinesBackByIndex()
    {
        var indexer = NewIndexer();
        var document = Load(indexer, WriteLog("iso.log", IsoLog));

        using var reader = indexer.OpenReader(document, 1 << 16);

        Assert.That(reader.Text(1), Does.Contain("Retry 1 for host=db-02"));
        Assert.That(reader.Text(4), Does.StartWith("\tat com.acme"));
    }

    [Test]
    public void HandlesWindowsLineEndingsWithoutLeavingCarriageReturns()
    {
        var indexer = NewIndexer();
        var document = Load(indexer, WriteLog("crlf.log", IsoLog.Replace("\n", "\r\n", StringComparison.Ordinal)));

        using var reader = indexer.OpenReader(document, 1 << 16);

        Assert.That(document.N, Is.EqualTo(6));
        Assert.That(reader.Text(0), Does.Not.Contain("\r"));
    }

    [Test]
    public void IndexesAFileWithNoTrailingNewline()
    {
        var document = Load(NewIndexer(), WriteLog("nonl.log", IsoLog.TrimEnd('\n')));

        Assert.That(document.N, Is.EqualTo(6));
    }

    [Test]
    public void RunAppendsOnlyNewLinesOnASecondPass()
    {
        var indexer = NewIndexer();
        var path = WriteLog("grow.log", IsoLog);
        var document = Load(indexer, path);
        File.AppendAllText(path, "2026-03-01 08:16:00.000 FATAL [main] Out of memory\n");

        indexer.Run(document, 0, progress: null, cancelled: null);

        Assert.That(document.N, Is.EqualTo(7));
        Assert.That(document.Sev[6], Is.EqualTo(6));
    }

    [Test]
    public void MergeInterleavesByTimeAndKeepsGroupsTogether()
    {
        var indexer = NewIndexer();
        var first = Load(indexer, WriteLog("a.log", IsoLog));
        var second = Load(indexer, WriteLog("b.log",
            "2026-03-01 08:14:05.000 INFO  [nginx] between the first two\n" +
            "2026-03-01 08:14:30.000 ERROR [nginx] after the stack trace\n"));

        var merged = indexer.Merge([first, second]);

        Assert.That(merged.N, Is.EqualTo(8));
        Assert.That(merged.Merged, Is.True);
        for (var i = 1; i < merged.N; i++)
        {
            Assert.That(merged.Ts[i], Is.GreaterThanOrEqualTo(merged.Ts[i - 1]), "timestamps must never go backwards");
        }

        // The Java stack trace must still sit directly under its ERROR line.
        var errorLine = Array.IndexOf(merged.Sev, (byte)5);
        Assert.That(merged.Flag[errorLine + 1], Is.EqualTo(1));
        Assert.That(merged.Flag[errorLine + 2], Is.EqualTo(1));
    }

    [Test]
    public void EveryLineOfAMergedViewReadsBackFromItsOwnFile()
    {
        // Lines alternate between the two files, so each read switches file. Each file
        // keeps its own window: the reader must hand back the right bytes either way.
        var indexer = NewIndexer();
        var a = new StringBuilder();
        var b = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            a.Append("2026-03-01 08:00:").Append((i / 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture))
             .Append('.').Append((i % 100 * 10).ToString("000", System.Globalization.CultureInfo.InvariantCulture)).Append(" INFO  a line ").Append(i).Append('\n');
            b.Append("2026-03-01 08:00:").Append((i / 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture))
             .Append('.').Append((i % 100 * 10 + 5).ToString("000", System.Globalization.CultureInfo.InvariantCulture)).Append(" INFO  b line ").Append(i).Append('\n');
        }
        var merged = indexer.Merge([Load(indexer, WriteLog("a.log", a.ToString())), Load(indexer, WriteLog("b.log", b.ToString()))]);

        // A small budget, so the windows also have to move along their files.
        using var reader = indexer.OpenReader(merged, 4096);

        Assert.That(merged.N, Is.EqualTo(4000));
        for (var i = 0; i < merged.N; i++)
        {
            var file = merged.Src[i] == 0 ? "a" : "b";
            Assert.That(reader.Text(i), Does.Contain(" " + file + " line " + (i / 2)), "line " + i);
        }
    }

    [Test]
    public void AFileThatIsNotUtf8IsReadInTheAnsiCodePage()
    {
        // "café" in Windows-1252: é is the single byte E9, which is not valid UTF-8.
        var path = Path.Combine(_directory, "ansi.log");
        var bytes = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n' };
        File.WriteAllBytes(path, bytes);
        var indexer = NewIndexer();
        var document = Load(indexer, path);

        using var reader = indexer.OpenReader(document, 1 << 16);

        Assert.That(document.Srcs[0].Encoding.CodePage, Is.Not.EqualTo(65001), "Not decoded as UTF-8.");
        Assert.That(reader.Text(0), Is.EqualTo(LogIndexer.AnsiEncoding().GetString(bytes, 0, 4)));
        Assert.That(reader.Text(0), Does.Not.Contain("�"), "No replacement characters.");
    }

    [Test]
    public void MergeRejectsAnEmptyList()
    {
        Assert.Throws<ArgumentException>(() => NewIndexer().Merge([]));
    }

    [Test]
    public void OpenReaderRejectsAZeroBuffer()
    {
        var document = Load(NewIndexer(), WriteLog("iso.log", IsoLog));

        Assert.Throws<ArgumentOutOfRangeException>(() => NewIndexer().OpenReader(document, 0));
    }

    [Test]
    public void IndexerRequiresAClock()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new LogIndexer(null));
    }
}

[TestFixture]
public class FilterEngineTests
{
    private const string Log =
        "2026-03-01 08:00:00.000 INFO  request ok\n" +
        "2026-03-01 08:00:01.000 ERROR connection reset\n" +
        "2026-03-01 08:00:02.000 INFO  request ok\n" +
        "2026-03-01 08:00:03.000 WARN  slow query\n" +
        "2026-03-01 08:00:04.000 ERROR connection timeout\n";

    private string _path;
    private LogIndexer _indexer;
    private LogDocument _document;

    [SetUp]
    public void Load()
    {
        _path = Path.Combine(Path.GetTempPath(), "cn-filter-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(_path, Log, new UTF8Encoding(false));
        _indexer = new LogIndexer(TestClock.Instance);
        var source = _indexer.Prepare(_path);
        _document = new LogDocument { Name = source.Name };
        _document.Srcs.Add(source);
        _indexer.Run(_document, 0, progress: null, cancelled: null);
    }

    [TearDown]
    public void Cleanup()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static QuerySpec Query(string text, int levelMask = 0x7F, bool filter = true) =>
        QuerySpec.Build(text, regex: false, cs: false, mask: levelMask, srcMask: -1L,
                        t0: double.NaN, t1: double.NaN, ctx: 0, group: true, filter: filter);

    [Test]
    public void EmptyQueryKeepsEveryLine()
    {
        var result = new FilterEngine().Run(_document, Query(string.Empty), null, null);

        Assert.That(result.View, Has.Count.EqualTo(5));
    }

    [Test]
    public void TermFiltersToMatchingLines()
    {
        var result = new FilterEngine().Run(_document, Query("connection"), null, null);

        Assert.That(result.View, Has.Count.EqualTo(2));
        Assert.That(result.Matches, Has.Count.EqualTo(2));
    }

    [Test]
    public void ExclusionRemovesLines()
    {
        var result = new FilterEngine().Run(_document, Query("connection -timeout"), null, null);

        Assert.That(result.View, Has.Count.EqualTo(1));
    }

    [Test]
    public void LevelMaskIsolatesASeverity()
    {
        const int errorOnly = 1 << 5;
        var result = new FilterEngine().Run(_document, Query(string.Empty, errorOnly), null, null);

        Assert.That(result.View, Has.Count.EqualTo(2));
        Assert.That(result.Counts[5], Is.EqualTo(2));
    }

    [Test]
    public void HighlightModeKeepsEveryLineButStillCountsMatches()
    {
        var result = new FilterEngine().Run(_document, Query("connection", filter: false), null, null);

        Assert.That(result.View, Has.Count.EqualTo(5));
        Assert.That(result.Matches, Has.Count.EqualTo(2));
    }

    [Test]
    public void CancellationReturnsNull()
    {
        var result = new FilterEngine().Run(_document, Query(string.Empty), () => true, null);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void RequiresADocumentAndAQuery()
    {
        Assert.Throws<ArgumentNullException>(() => new FilterEngine().Run(null, Query(string.Empty), null, null));
        Assert.Throws<ArgumentNullException>(() => new FilterEngine().Run(_document, null, null, null));
    }
}
