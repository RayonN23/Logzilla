using System;
using Logzilla.Core.Common;

namespace Logzilla.Core.Implementation.Formats;


internal static class LevelScan
{
    private static readonly string[] Words = {
        "TRACE","VERBOSE","TRC","FINEST","FINER","DEBUG","DBG","FINE","INFORMATION","INFO","INF","NOTICE",
        "WARNING","WARN","WRN","ERROR","ERR","SEVERE","EXCEPTION","FAILED","FAIL","FATAL","CRITICAL","CRIT",
        "PANIC","EMERG","ALERT" };
    private static readonly byte[] Vals = { 1,1,1,1,1,2,2,2,3,3,3,3,4,4,4,5,5,5,5,5,5,6,6,6,6,6,6 };

    /// <summary>True for a lower-cased byte that some word in <see cref="Words"/> starts
    /// with. Most word starts in a line head are timestamp digits or ordinary words,
    /// and this settles those without walking the whole list.</summary>
    private static readonly bool[] Starts = BuildStarts();

    private static bool[] BuildStarts()
    {
        var t = new bool[256];
        foreach (var w in Words)
        {
            t[(byte)(w[0] + 32)] = true;
        }
        return t;
    }

    public static byte Scan(byte[] b, int off, int len)
    {
        return Scan(b, off, len, out _, out _);
    }

    // Scans the head of a line for a severity word standing on its own.
    public static byte Scan(byte[] b, int off, int len, out int start, out int end)
    {
        start = -1; end = -1;
        var stop = off + Math.Min(len, 80);
        var prevWord = false;
        for (var i = off; i < stop; i++)
        {
            var isw = AsciiTable.IsWord(b[i]);
            if (isw && !prevWord && Starts[AsciiTable.ToLowerAscii(b[i])])
            {
                var c = AsciiTable.ToLowerAscii(b[i]);
                for (var w = 0; w < Words.Length; w++)
                {
                    var s = Words[w];
                    if ((byte)(s[0] + 32) != c)
                    {
                        continue;
                    }
                    var n = s.Length;
                    if (i + n > off + len)
                    {
                        continue;
                    }
                    var ok = true;
                    for (var k = 1; k < n; k++)

                    {
                        if (AsciiTable.ToLowerAscii(b[i + k]) != (byte)(s[k] + 32)) { ok = false; break; }
                    }
                    if (!ok)
                    {
                        continue;
                    }
                    var after = i + n;
                    if (after < off + len && AsciiTable.IsWord(b[after]))
                    {
                        continue;
                    }
                    start = i; end = after;
                    return Vals[w];
                }
            }
            prevWord = isw;
        }
        return 0;
    }
}
