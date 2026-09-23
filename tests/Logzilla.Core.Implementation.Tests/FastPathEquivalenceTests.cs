using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Logzilla.Core.Common;
using Logzilla.Core.Implementation.Formats;
using Logzilla.Core.Implementation.Logs;
using Logzilla.Core.Implementation.Query;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;
using NUnit.Framework;

namespace Logzilla.Core.Implementation.Tests;

/// <summary>
/// The byte scanners on the indexing and search hot paths use vectorised span
/// searches. Each is checked here against the plain byte-at-a-time loop it replaced,
/// kept below as the reference, over inputs built to hit the awkward cases: matches at
/// either edge of the window, near misses, case, bytes above 127 and short windows.
/// </summary>
[TestFixture]
public class FastPathEquivalenceTests
{
    private const int Rounds = 20000;

    // Letters the needles are made of, so random haystacks actually contain near misses.
    private static readonly byte[] Alphabet = Encoding.ASCII.GetBytes("aAbBeErR:\"@lt \t{}0x");

    private static byte[] RandomBytes(Random rng, int len, bool highBytes)
    {
        var b = new byte[len];
        for (var i = 0; i < len; i++)
        {
            b[i] = highBytes && rng.Next(8) == 0 ? (byte)rng.Next(128, 256) : Alphabet[rng.Next(Alphabet.Length)];
        }
        return b;
    }

    // ── references: the loops these fast paths replaced ────────────────

    private static bool ContainsReference(byte[] hay, int off, int len, byte[] needle, bool cs)
    {
        var n = needle.Length;
        if (n == 0 || n > len)
        {
            return false;
        }
        var first = needle[0];
        var last = off + len - n;
        for (var i = off; i <= last; i++)
        {
            var c = hay[i];
            if (!cs)
            {
                c = AsciiTable.ToLowerAscii(c);
            }
            if (c != first)
            {
                continue;
            }
            var k = 1;
            for (; k < n; k++)
            {
                var d = hay[i + k];
                if (!cs)
                {
                    d = AsciiTable.ToLowerAscii(d);
                }
                if (d != needle[k])
                {
                    break;
                }
            }
            if (k == n)
            {
                return true;
            }
        }
        return false;
    }

    private static int FindReference(byte[] b, int off, int len, string key)
    {
        var end = off + len;
        var kl = key.Length;
        for (var i = off; i + kl + 3 < end; i++)
        {
            if (b[i] != (byte)'"')
            {
                continue;
            }
            var j = 0;
            while (j < kl && b[i + 1 + j] == (byte)key[j])
            {
                j++;
            }
            if (j != kl)
            {
                continue;
            }
            var p = i + 1 + kl;
            if (p >= end || b[p] != (byte)'"')
            {
                continue;
            }
            p++;
            while (p < end && (b[p] == (byte)' ' || b[p] == (byte)'\t'))
            {
                p++;
            }
            if (p < end && b[p] == (byte)':')
            {
                return p + 1;
            }
        }
        return -1;
    }

    // ── case folding ───────────────────────────────────────────────────

    [Test]
    public void SpanFoldingMatchesTheLookupTableForEveryByteAtEveryLength()
    {
        var all = new byte[256 * 3 + 7];
        for (var i = 0; i < all.Length; i++)
        {
            all[i] = (byte)(i * 7 + 3);
        }
        for (var len = 0; len <= 200; len++)
        {
            for (var start = 0; start < 5; start++)
            {
                var src = new ReadOnlySpan<byte>(all, start, len);
                var dst = new byte[len];
                AsciiTable.ToLowerAscii(src, dst);
                for (var i = 0; i < len; i++)
                {
                    Assert.That(dst[i], Is.EqualTo(AsciiTable.ToLowerAscii(src[i])), $"len {len} start {start} at {i}");
                }
            }
        }
    }

    [Test]
    public void SpanFoldingRejectsAShortDestination()
    {
        Assert.Throws<ArgumentException>(() => AsciiTable.ToLowerAscii(new byte[4], new byte[3]));
    }

    // ── literal search ─────────────────────────────────────────────────

    [Test]
    public void ContainsAgreesWithTheByteLoop()
    {
        var rng = new Random(1234);
        for (var r = 0; r < Rounds; r++)
        {
            var hay = RandomBytes(rng, rng.Next(0, 90), highBytes: true);
            var off = hay.Length == 0 ? 0 : rng.Next(0, hay.Length);
            var len = rng.Next(0, hay.Length - off + 1);
            // Needles are often lifted straight out of the haystack, so real matches
            // (including ones touching either edge of the window) are common.
            byte[] needle;
            if (hay.Length > 0 && rng.Next(2) == 0)
            {
                var a = rng.Next(0, hay.Length);
                needle = hay.AsSpan(a, rng.Next(0, Math.Min(6, hay.Length - a) + 1)).ToArray();
            }
            else
            {
                needle = RandomBytes(rng, rng.Next(0, 5), highBytes: true);
            }
            if (rng.Next(2) == 0)
            {
                needle = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(needle).ToLowerInvariant());
            }
            foreach (var cs in new[] { false, true })
            {
                Assert.That(QuerySpec.Contains(hay, off, len, needle, cs),
                    Is.EqualTo(ContainsReference(hay, off, len, needle, cs)),
                    $"round {r} cs {cs}");
            }
        }
    }

    [Test]
    public void AnUpperCaseNeedleNeverMatchesCaseInsensitively()
    {
        // The needle is lower-cased by QuerySpec.Build; folding the haystack can never
        // produce the upper-case letter, so this was always false and must stay so.
        var hay = Encoding.ASCII.GetBytes("ERROR error");
        Assert.That(QuerySpec.Contains(hay, 0, hay.Length, Encoding.ASCII.GetBytes("Error"), false), Is.False);
    }

    [Test]
    public void ScanBytesAgreesWithMatchBytes()
    {
        var rng = new Random(99);
        var words = new[] { "err", "Err", "ERROR", "time out", "r:", "\"@l\"", "x", "0x", "zz" };
        for (var r = 0; r < Rounds; r++)
        {
            var q = new StringBuilder();
            for (var t = rng.Next(1, 4); t > 0; t--)
            {
                var w = words[rng.Next(words.Length)];
                q.Append(rng.Next(4) == 0 ? "-" : "").Append(w.Contains(' ', StringComparison.Ordinal) ? "\"" + w + "\"" : w).Append(' ');
            }
            var cs = rng.Next(2) == 0;
            var spec = QuerySpec.Build(q.ToString(), false, cs, 0x7F, -1, double.NaN, double.NaN, 0, true, true);
            var hay = RandomBytes(rng, rng.Next(0, 70), highBytes: false);
            var m = spec.NewMatch();
            spec.ScanBytes(hay, 0, hay.Length, m);
            Assert.That(m.Matched, Is.EqualTo(spec.MatchBytes(hay, 0, hay.Length)), $"round {r}: {q} cs {cs}");
        }
    }

    // ── JSON key scan ──────────────────────────────────────────────────

    [Test]
    public void FindAgreesWithTheByteLoop()
    {
        var rng = new Random(42);
        var keys = new[] { "@l", "@t", "level", "l", "" };
        var parts = new[] { "\"@l\"", "\"@l\" :", "\"@l\":", "\"@t\":\"x\"", "\"level\"\t:", "\"lev", "\"", ":", " ", "{", "\"l\":1" };
        for (var r = 0; r < Rounds; r++)
        {
            var sb = new StringBuilder();
            for (var k = rng.Next(0, 8); k > 0; k--)
            {
                sb.Append(parts[rng.Next(parts.Length)]);
            }
            var b = Encoding.ASCII.GetBytes(sb.ToString());
            var off = b.Length == 0 ? 0 : rng.Next(0, b.Length);
            var len = rng.Next(0, b.Length - off + 1);
            foreach (var key in keys)
            {
                Assert.That(JsonLog.Find(b, off, len, key), Is.EqualTo(FindReference(b, off, len, key)),
                    $"round {r} key '{key}' in '{sb}' [{off},{len}]");
            }
        }
    }

    // ── severity words ─────────────────────────────────────────────────

    [TestCase("2026-03-01 08:00:00.000 WARN  x", 4)]
    [TestCase("2026-03-01 08:00:00.000 [information] x", 3)]
    [TestCase("12:00:01 Critical failure", 6)]
    [TestCase("12:00:01 0error 1info", 0)]
    [TestCase("panic: runtime error", 6)]
    [TestCase("finest detail", 1)]
    public void LevelScanStillFindsTheSameWords(string line, int expected)
    {
        var b = Encoding.ASCII.GetBytes(line);
        Assert.That(LevelScan.Scan(b, 0, b.Length), Is.EqualTo((byte)expected));
    }

    // ── indexer line splitting ─────────────────────────────────────────

    private string _dir;

    [SetUp]
    public void CreateDirectory()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cn-fast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void RemoveDirectory()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static LogDocument Index(string path, LogIndexer indexer = null)
    {
        indexer ??= new LogIndexer(TestClock.Instance);
        var source = indexer.Prepare(path);
        var document = new LogDocument { Name = source.Name };
        document.Srcs.Add(source);
        indexer.Run(document, 0, null, null);
        return document;
    }

    // ── UTF-16 ─────────────────────────────────────────────────────────
    //
    // A UTF-16 log used to be split on the 0x0A *byte*. Every line after the first then
    // started one byte early and read as CJK noise, the format sample was misaligned
    // the same way so no timestamps were ever found, big-endian files were cut in the
    // middle of characters, and a one-byte phantom line trailed the last one.

    private static readonly string[] Utf16Lines =
    {
        // U+410A is 0A 41 in little-endian and 41 0A in big-endian; U+0A41 U+0100 puts
        // a 0A byte straight before a 00 byte, off the character boundary. Neither is
        // a line feed.
        "2026-03-01 08:00:00.000 INFO  first 䄊 still ੁĀ first",
        "2026-03-01 08:00:01.000 ERROR second",
        "   at Stack.Frame()",
        "2026-03-01 08:00:02.000 WARN  third",
    };

    [TestCase("utf-16")]
    [TestCase("utf-16BE")]
    public void Utf16LogsIndexLineForLine(string encoding)
    {
        var path = Path.Combine(_dir, encoding + ".log");
        File.WriteAllText(path, string.Join("\r\n", Utf16Lines) + "\r\n", Encoding.GetEncoding(encoding));

        var document = Index(path);
        using var reader = new LineReader(document, 1 << 16);

        Assert.That(document.Srcs[0].Kind, Is.EqualTo(Logzilla.Core.Formats.TsKind.Iso), "the format sample must be read a character at a time");
        Assert.That(document.N, Is.EqualTo(Utf16Lines.Length), "no split inside a character, no phantom last line");
        for (var i = 0; i < Utf16Lines.Length; i++)
        {
            Assert.That(reader.Text(i), Is.EqualTo(Utf16Lines[i]), $"line {i}");
        }
        Assert.That(document.Sev[0], Is.EqualTo(3));
        Assert.That(document.Sev[1], Is.EqualTo(5));
        Assert.That(document.Sev[3], Is.EqualTo(4));
        Assert.That(document.Flag[2], Is.EqualTo(1), "a stack frame continues the entry above it");
        Assert.That(document.Ts[1] - document.Ts[0], Is.EqualTo(1000));
    }

    [TestCase("utf-16")]
    [TestCase("utf-16BE")]
    public void AFollowedUtf16LogPicksUpAppendedLines(string encoding)
    {
        var enc = Encoding.GetEncoding(encoding);
        var path = Path.Combine(_dir, "follow-" + encoding + ".log");
        File.WriteAllText(path, Utf16Lines[0] + "\r\n", enc);
        var indexer = new LogIndexer(TestClock.Instance);
        var document = Index(path, indexer);

        // Appended without a second byte-order mark.
        using (var fs = new FileStream(path, FileMode.Append))
        {
            var more = enc.GetBytes(Utf16Lines[1] + "\r\n" + Utf16Lines[3] + "\r\n");
            fs.Write(more, 0, more.Length);
        }
        indexer.Run(document, 0, null, null);

        using var reader = new LineReader(document, 1 << 16);
        Assert.That(document.N, Is.EqualTo(3));
        Assert.That(reader.Text(1), Is.EqualTo(Utf16Lines[1]));
        Assert.That(reader.Text(2), Is.EqualTo(Utf16Lines[3]));
        Assert.That(document.Sev[2], Is.EqualTo(4));
    }

    [Test]
    public void Utf16LinesAcrossReadBoundariesAreIndexedWhole()
    {
        // Past one 8 MB read, with odd-length lines so the read boundary lands at every
        // position inside a line over the course of the file.
        var path = Path.Combine(_dir, "large16.log");
        var lines = new List<string>();
        long chars = 0;
        for (var i = 0; chars < (5L << 20); i++)
        {
            var l = "2026-03-01 08:00:00.000 " + (i % 11 == 0 ? "ERROR" : "INFO ") + " line " + i + new string('~', i % 61);
            lines.Add(l);
            chars += l.Length + 2;
        }
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Encoding.Unicode);

        var document = Index(path);
        using var reader = new LineReader(document, 1 << 16);

        Assert.That(document.N, Is.EqualTo(lines.Count));
        long offset = 2;   // after the byte-order mark
        for (var i = 0; i < lines.Count; i++)
        {
            if (document.Off[i] != offset || document.Len[i] != lines[i].Length * 2 ||
                document.Sev[i] != (i % 11 == 0 ? 5 : 3))
            {
                Assert.Fail($"line {i}: offset {document.Off[i]} length {document.Len[i]} severity {document.Sev[i]}");
            }
            offset += (lines[i].Length + 2) * 2;
        }
        var boundary = 0;
        while (document.Off[boundary + 1] < (8L << 20) + 2)
        {
            boundary++;
        }
        Assert.That(reader.Text(boundary), Is.EqualTo(lines[boundary]), "the line straddling the first read boundary");
    }

    [Test]
    public void TheUtf16LineFeedSearchOnlyStopsOnWholeCharacters()
    {
        // Little-endian: 41 0A | 00 01 | 0A 00  -> the only line feed is the third character.
        var le = new byte[] { 0x41, 0x0A, 0x00, 0x01, 0x0A, 0x00 };
        Assert.That(LogIndexer.NextLineFeed16(le, 0, le.Length, bigEndian: false), Is.EqualTo(4));
        // Big-endian: 0A 00 | 0A 41 | 00 0A  -> likewise.
        var be = new byte[] { 0x0A, 0x00, 0x0A, 0x41, 0x00, 0x0A };
        Assert.That(LogIndexer.NextLineFeed16(be, 0, be.Length, bigEndian: true), Is.EqualTo(4));
        // A half character at the end of the data is not a line feed.
        Assert.That(LogIndexer.NextLineFeed16(new byte[] { 0x41, 0x00, 0x0A }, 0, 3, bigEndian: false), Is.EqualTo(-1));
    }

    [Test]
    public void LinesAcrossReadBoundariesAreIndexedWhole()
    {
        // Larger than one 8 MB read, so lines straddle the boundary between reads.
        var path = Path.Combine(_dir, "large.log");
        var lines = new List<string>();
        long size = 0;
        for (var i = 0; size < (9L << 20); i++)
        {
            var l = "2026-03-01 08:00:00.000 " + (i % 13 == 0 ? "ERROR" : "INFO ") + " line " + i + new string('.', i % 157);
            lines.Add(l);
            size += l.Length + 1;
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n");

        var document = Index(path);
        using var reader = new LineReader(document, 1 << 16);

        Assert.That(document.N, Is.EqualTo(lines.Count));
        long offset = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (document.Off[i] != offset || document.Len[i] != lines[i].Length)
            {
                Assert.Fail($"line {i}: offset {document.Off[i]} length {document.Len[i]}, expected {offset} {lines[i].Length}");
            }
            if (document.Sev[i] != (i % 13 == 0 ? 5 : 3))
            {
                Assert.Fail($"line {i}: severity {document.Sev[i]}");
            }
            offset += lines[i].Length + 1;
        }
        var boundary = 0;
        while (document.Off[boundary + 1] < (8L << 20))
        {
            boundary++;
        }
        Assert.That(reader.Text(boundary), Is.EqualTo(lines[boundary]), "the line straddling the first read boundary");
    }

    [Test]
    public void ALongLineAppendedWhileFollowingIsReadWhole()
    {
        var path = Path.Combine(_dir, "grow.log");
        File.WriteAllText(path, "2026-03-01 08:00:00.000 INFO  start\n");
        var indexer = new LogIndexer(TestClock.Instance);
        var document = Index(path, indexer);

        // Following re-reads with a buffer sized to the few new bytes, which must still
        // take in a line far longer than the smallest such buffer.
        var longLine = "2026-03-01 08:00:01.000 ERROR " + new string('x', 20000);
        File.AppendAllText(path, longLine + "\n2026-03-01 08:00:02.000 INFO  after\n");
        indexer.Run(document, 0, null, null);

        using var reader = new LineReader(document, 1 << 16);
        Assert.That(document.N, Is.EqualTo(3));
        Assert.That(reader.Text(1), Is.EqualTo(longLine));
        Assert.That(reader.Text(2), Is.EqualTo("2026-03-01 08:00:02.000 INFO  after"));
        Assert.That(document.Sev[1], Is.EqualTo(5));
    }

    [Test]
    public void ALineReaderOnASmallFileStillReadsALongLine()
    {
        var path = Path.Combine(_dir, "small.log");
        var line = "2026-03-01 08:00:00.000 INFO  " + new string('y', 5000);
        File.WriteAllText(path, "a\n" + line + "\nb\n");
        var document = Index(path);
        // The reader sizes itself to the file, not the 4 MB asked for, and must still
        // hand back a line that is most of the file.
        using var reader = new LineReader(document, 1 << 22);
        Assert.That(reader.Text(1), Is.EqualTo(line));
        Assert.That(reader.Text(2), Is.EqualTo("b"));
        Assert.That(reader.Text(0), Is.EqualTo("a"));
    }

    // ── regex over a big document ──────────────────────────────────────

    [Test]
    public void ARegexOverABigDocumentFindsTheSameEntriesAsTheTextSays()
    {
        // Big enough for the filter to switch to a compiled copy of the regex.
        var path = Path.Combine(_dir, "big.log");
        var sb = new StringBuilder();
        var expected = new List<int>();
        var line = 0;
        for (var e = 0; line < 60000; e++)
        {
            var header = line;
            var failing = e % 97 == 0;
            sb.Append("2026-03-01 08:00:00.000 ").Append(failing ? "ERROR request failed" : "INFO  ok").Append(" id=").Append(e).Append('\n');
            line++;
            if (failing)
            {
                sb.Append("System.TimeoutException: timed out\n");
                line++;
                if (e % 2 == 0)
                {
                    expected.Add(header);   // only these carry the frame the regex needs
                    sb.Append("   at Db.Query()\n");
                    line++;
                }
            }
        }
        File.WriteAllText(path, sb.ToString());
        var document = Index(path);

        var spec = QuerySpec.Build("failed[\\s\\S]*timed out\\n +at Db", true, false, 0x7F, -1,
            double.NaN, double.NaN, 0, true, true);
        var result = new FilterEngine().Run(document, spec, null, null);

        Assert.That(result.Matches, Is.EqualTo(expected));
        Assert.That(spec.Rx.Options & RegexOptions.Compiled, Is.EqualTo(RegexOptions.None),
            "The query's own regex is left alone; only the scan uses a compiled copy.");
    }
}
