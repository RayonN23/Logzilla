using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Implementation.Common;
using Logzilla.Core.Implementation.Formats;
using Logzilla.Core.Implementation.Logs;
using Logzilla.Core.Implementation.Query;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.Core.Implementation;

/// <summary>
/// The composition root for the engine. Hosts call this and depend only on the
/// interfaces in <c>Logzilla.Core</c>; every concrete type here is internal.
/// </summary>
public static class CoreServices
{
    public static ICurrentTimeSupplier CreateClock() => new SystemTimeSupplier();

    public static ILogIndexer CreateIndexer(ICurrentTimeSupplier clock) => new LogIndexer(clock);

    public static IFilterEngine CreateFilterEngine() => new FilterEngine();

    public static IJsonLogReader CreateJsonLogReader() => new JsonLogReader();
}
