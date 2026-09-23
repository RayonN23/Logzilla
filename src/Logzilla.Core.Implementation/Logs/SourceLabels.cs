using Logzilla.Core.Logs;
namespace Logzilla.Core.Implementation.Logs;

// Sequential / near-sequential line reader over the raw file.
// Merged files often share a long prefix (app-2026-03-01-web01.log ...).
// Dropping it makes the source column readable without losing what tells them apart.
internal static class SourceLabels
{
    public static void Apply(LogDocument m)
    {
        var n = m.Srcs.Count;
        if (n < 2)
        {
            return;
        }
        var a = m.Srcs[0].Label;
        var pre = a.Length;
        for (var i = 1; i < n; i++)
        {
            var b = m.Srcs[i].Label;
            var k = 0;
            while (k < pre && k < b.Length && char.ToLowerInvariant(a[k]) == char.ToLowerInvariant(b[k]))
            {
                k++;
            }
            pre = k;
        }
        if (pre < 4)
        {
            return;
        }
        while (pre > 0 && "-_. ".IndexOf(a[pre - 1]) < 0)
        {
            pre--;
        }
        if (pre < 4)
        {
            return;
        }

        var cut = new string[n];
        for (var i = 0; i < n; i++)
        {
            var t = m.Srcs[i].Label;
            cut[i] = pre < t.Length ? t.Substring(pre) : t;
            if (cut[i].Length < 3)
            {
                return;              // too aggressive, leave names alone
            }
        }
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (cut[i] == cut[j])
                {
                    return;            // would create duplicates
                }
            }
        }
        for (var i = 0; i < n; i++)
        {
            m.Srcs[i].Label = cut[i];
        }
    }
}

// ================= JSON / CLEF line logs =================
//
// Structured logs (Serilog CLEF, Bunyan, pino, ECS, plain NDJSON) put one JSON
// object per line. Read raw they are unusable: the message is buried among
// correlation ids and the timestamp is a quoted field.
//
// Logzilla keeps its byte-level design here. At index time it only *scans* for
// the timestamp and level keys — no parsing, no decoding. The JSON is parsed
// only for the rows actually on screen, so a million-line CLEF file costs the
// same to open as a plain text one.
