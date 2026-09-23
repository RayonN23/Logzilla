using System;
using Logzilla.Core.Logs;

namespace Logzilla.Core.Query;

/// <summary>Applies a query to a document and returns the surviving lines.</summary>
public interface IFilterEngine
{
    FilterResult Run(LogDocument document, QuerySpec query, Func<bool> cancelled, Action<int, int> progress);
}
