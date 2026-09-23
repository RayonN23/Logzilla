using System;

namespace Logzilla.Core.Logs;

/// <summary>Reads individual lines of a document back from disk on demand.</summary>
public interface ILineReader : IDisposable
{
    /// <summary>Places line <paramref name="index"/> in a buffer and returns its offset there.</summary>
    int Locate(int index, out byte[] buffer);

    string Text(int index);
}
