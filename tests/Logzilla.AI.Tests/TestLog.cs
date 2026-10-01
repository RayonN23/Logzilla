using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Logzilla.AI.Tools;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Implementation;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.AI.Tests;

/// <summary>
/// A real log file indexed by the real engine, and the context the tools read it
/// through — the same path the app takes, minus the window.
/// </summary>
internal sealed class TestLog : ILogContext, IDisposable
{
    public const string PlainLog =
        "2026-03-01 08:00:00.100 INFO  [main] Started OrderService\n" +
        "2026-03-01 08:00:05.200 INFO  [pool-1] GET /orders/1 status=200 latency=41ms client=10.1.2.3\n" +
        "2026-03-01 08:00:06.300 INFO  [pool-1] GET /orders/2 status=200 latency=812ms client=10.1.2.4\n" +
        "2026-03-01 08:05:00.000 WARN  [pool-2] Retry 1 for host=db-02 user=alice@example.com\n" +
        "2026-03-01 08:05:01.000 ERROR [pool-2] Connection reset by peer password=hunter22\n" +
        "java.net.SocketException: Connection reset\n" +
        "\tat com.acme.orders.Repo.load(Repo.java:88)\n" +
        "2026-03-01 08:10:00.000 INFO  [pool-1] GET /orders/3 status=200 latency=35ms client=10.1.2.3\n" +
        "2026-03-01 08:15:00.000 ERROR [pool-2] Connection reset by peer\n" +
        "2026-03-01 08:20:00.000 INFO  [main] Recovered\n";

    private readonly string _dir;
    private readonly string _content;
    private readonly ILogIndexer _indexer = CoreServices.CreateIndexer(CoreServices.CreateClock());

    public TestLog(string content = PlainLog)
    {
        _content = content;
        _dir = Path.Combine(Path.GetTempPath(), "lz-ai-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "orders.log");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        var source = _indexer.Prepare(path);
        Document = new LogDocument { Name = source.Name };
        Document.Srcs.Add(source);
        _indexer.Run(Document, 0, null, null);
        View = Engine.Run(Document, QuerySpec.Build("", false, false, 0x7F, -1, double.NaN, double.NaN, 0, true, true), null, null);
    }

    public LogDocument Document { get; }
    public FilterResult View { get; set; }
    public string FilterSummary { get; set; } = "";
    public bool ScopeToView { get; set; }
    public IFilterEngine Engine { get; } = CoreServices.CreateFilterEngine();
    public IJsonLogReader Json { get; } = CoreServices.CreateJsonLogReader();
    public string ClockName => "the time each log wrote";

    /// <summary>Narrows the user's view, as their own filters would.</summary>
    public void Filter(string query, int levelMask = 0x7F) =>
        View = Engine.Run(Document, QuerySpec.Build(query, false, false, levelMask, -1, double.NaN, double.NaN, 0, true, true), null, null);

    public ILineReader OpenReader() => _indexer.OpenReader(Document, 1 << 16);

    public string FormatTime(LogSource source, double storedMs) =>
        DisplayFormatter.Time(TimeZones.Display(storedMs, source, TimeMode.AsWritten, TimeZoneInfo.Utc));

    public bool TryParseTime(string text, out double storedMs) => TimeRangeParser.TryParseDateTimeMs(text, out storedMs);

    public IReadOnlyList<(string Key, int Entries)> Fields() =>
        [("status", 3), ("latency", 3), ("client", 3), ("host", 1)];

    public IReadOnlyList<(string Value, int Count)> FieldValues(string key) =>
        key == "status" ? [("200", 3)] : [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A reader still open on another thread; the temp folder is cleaned up eventually.
        }
    }

    /// <summary>The 1-based line number of the first line containing <paramref name="text"/>.</summary>
    public int LineOf(string text) =>
        _content.Split('\n').Select((l, i) => (l, i)).First(x => x.l.Contains(text, StringComparison.Ordinal)).i + 1;
}
