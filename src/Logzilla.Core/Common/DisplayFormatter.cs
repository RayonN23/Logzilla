using System;
using System.Globalization;

namespace Logzilla.Core.Common;

/// <summary>Human-readable renderings of the numbers the UI shows.</summary>
public static class DisplayFormatter
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Milliseconds since the epoch as "yyyy-MM-dd HH:mm:ss.fff"; empty for NaN.</summary>
    public static string Time(double ms)
    {
        if (double.IsNaN(ms))
        {
            return string.Empty;
        }

        try
        {
            return Epoch.AddMilliseconds(ms).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }

    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }

        if (bytes < 1048576)
        {
            return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        }

        if (bytes < 1073741824)
        {
            return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        return (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
    }

    public static string Duration(double ms)
    {
        if (double.IsNaN(ms) || ms < 0)
        {
            return "-";
        }

        var seconds = ms / 1000.0;
        if (seconds < 60)
        {
            return seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        if (seconds < 3600)
        {
            return ((int)(seconds / 60)) + "m " + ((int)seconds % 60) + "s";
        }

        if (seconds < 86400)
        {
            return ((int)(seconds / 3600)) + "h " + ((int)(seconds % 3600) / 60) + "m";
        }

        return ((int)(seconds / 86400)) + "d " + ((int)(seconds % 86400) / 3600) + "h";
    }

    public static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
