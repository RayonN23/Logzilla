using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Logzilla.Core.Common;
using Logzilla.Core.Formats;
using Logzilla.Core.Implementation.Formats;
using Logzilla.Core.Logs;

namespace Logzilla.Core.Implementation.Logs;


internal sealed class LogIndexer : ILogIndexer
{
    private const int FormatSampleLines = 300;
    private const int MaxLabelLength = 22;
    private const long MillisecondsPerDay = 86400000L;

    private readonly ICurrentTimeSupplier _clock;

    public LogIndexer(ICurrentTimeSupplier clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public LogSource Prepare(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Log file not found.", path);
        }

        var source = new LogSource
        {
            Path = path,
            Name = info.Name,
            Label = Path.GetFileNameWithoutExtension(path),
            Size = info.Length,
        };
        if (source.Label.Length > MaxLabelLength)
        {
            source.Label = source.Label[..MaxLabelLength];
        }

        DetectEncoding(source);

        var sample = Sample(source, FormatSampleLines);
        source.IsJson = DetectJson(sample);
        source.Kind = TsParser.Detect(sample);

        // Syslog and time-only stamps carry no date; the file's own timestamp is the
        // best available anchor, with the clock as a last resort.
        var stamp = info.LastWriteTime == DateTime.MinValue ? _clock.Now : info.LastWriteTime;
        source.Parser = new TsParser(_clock)
        {
            Kind = source.Kind,
            IsJson = source.IsJson,
            Year = stamp.Year,
            BaseDayMs = CivilDate.DaysSinceEpoch(stamp.Year, stamp.Month, stamp.Day) * MillisecondsPerDay,
        };
        source.Zoned = DetectZoned(source, sample);
        source.IndexedTo = source.DataStart;
        return source;
    }

    public ILineReader OpenReader(LogDocument document, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        return new LineReader(document, bufferSize);
    }

    /// <summary>A JSON log is one object per line carrying a recognisable time key.</summary>
    private static bool DetectJson(List<byte[]> sample)
    {
        int braces = 0, timed = 0, total = 0;
        foreach (var line in sample)
        {
            var i = 0;
            while (i < line.Length && (line[i] == 32 || line[i] == 9))
            {
                i++;
            }

            if (i >= line.Length)
            {
                continue;
            }

            total++;
            if (line[i] != (byte)'{')
            {
                continue;
            }

            braces++;
            foreach (var key in JsonLog.TimeKeys)
            {
                if (JsonLog.Find(line, i, line.Length - i, key) >= 0)
                {
                    timed++;
                    break;
                }
            }
        }

        return total > 0 && braces >= total * 0.8 && timed >= braces * 0.5;
    }

    /// <summary>
    /// True when most stamps in the sample carry a zone. Read with a parser of its own:
    /// a time-only parser counts midnight rollovers, and the sample must not leave the
    /// real one a day ahead.
    /// </summary>
    private bool DetectZoned(LogSource source, List<byte[]> sample)
    {
        if (source.Kind == TsKind.None)
        {
            return false;
        }
        var probe = new TsParser(_clock) { Kind = source.Kind, IsJson = source.IsJson };
        int parsed = 0, zoned = 0;
        foreach (var line in sample)
        {
            var p = 0;
            if (source.IsJson)
            {
                p = -1;
                for (var k = 0; k < JsonLog.TimeKeys.Length && p < 0; k++)
                {
                    p = JsonLog.Find(line, 0, line.Length, JsonLog.TimeKeys[k]);
                }
                if (p < 0)
                {
                    continue;
                }
            }
            if (!probe.Parse(line, p, line.Length - p, out var ms) || double.IsNaN(ms))
            {
                continue;
            }
            parsed++;
            if (probe.LastOffset.HasValue)
            {
                zoned++;
            }
        }
        return parsed > 0 && zoned * 2 >= parsed;
    }

    /// <summary>
    /// Turns each stamp into what the document stores for its file: the instant, when
    /// the stamps carry a zone or the user has declared one; the wall clock otherwise.
    /// </summary>
    private sealed class StampZone
    {
        private readonly LogSource _source;
        private readonly ZoneClock _declared;
        private int _current;

        public StampZone(LogSource source)
        {
            _source = source;
            if (source.Offsets.Count > 0)
            {
                _current = source.Offsets[^1].Minutes;
            }
            if (!source.Zoned && source.Declared != null)
            {
                _declared = new ZoneClock(source.Declared);
            }
        }

        public double Store(double wall, int? offset)
        {
            if (double.IsNaN(wall))
            {
                return wall;
            }
            if (!_source.Zoned)
            {
                return _declared == null ? wall : _declared.ToInstant(wall);
            }
            // A stamp that drops its zone in a file whose stamps carry one keeps the last.
            var minutes = offset ?? _current;
            var instant = wall - minutes * 60000.0;
            var list = _source.Offsets;
            if (list.Count == 0 || list[^1].Minutes != minutes)
            {
                list.Add(new ZoneOffset(instant, minutes));
            }
            _current = minutes;
            return instant;
        }
    }

    private const int EncodingProbeBytes = 1 << 16;
    private const int ReadBufferBytes = 1 << 23;
    private const int FileStreamBufferBytes = 1 << 16;
    private const int SampleBufferBytes = 1 << 20;

    private static void DetectEncoding(LogSource f)
    {
        using (var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var head = new byte[Math.Min(EncodingProbeBytes, (int)Math.Min(fs.Length, EncodingProbeBytes))];
            var n = fs.Read(head, 0, head.Length);
            f.Stride = 1; f.DataStart = 0; f.Encoding = new UTF8Encoding(false);
            if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) { f.DataStart = 3; return; }
            if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) { f.Encoding = Encoding.Unicode; f.Stride = 2; f.DataStart = 2; return; }
            if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) { f.Encoding = Encoding.BigEndianUnicode; f.Stride = 2; f.DataStart = 2; return; }
            try
            {
                // Only a character the probe cut in half may go unchecked, and only when
                // the file carries on past the probe. Trimming four bytes unconditionally
                // let a small file's last accented character pass as UTF-8.
                var strict = new UTF8Encoding(false, true);
                strict.GetDecoder().GetCharCount(head, 0, n, flush: fs.Length <= n);
                f.Encoding = new UTF8Encoding(false);
            }
            catch (DecoderFallbackException ex)
            {
                Debug.WriteLine("Fallback to system encoding: " + ex.Message);
                f.Encoding = AnsiEncoding();
            }
        }
    }

    /// <summary>
    /// The machine's ANSI code page (Windows-1252 on most Western systems), for a file
    /// that is not valid UTF-8. Encoding.Default is no help: on .NET Core it is always
    /// UTF-8, so the "fallback" decoded the same bytes the same way and every accented
    /// character in such a file came out as a replacement mark.
    /// </summary>
    internal static Encoding AnsiEncoding()
    {
        var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.Latin1;
    }

    // Reads from src.IndexedTo to the end of the file, appending lines to the document.
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Indexer hot path favors throughput and minimal allocations.")]
    public void Run(LogDocument doc, int si, Action<long, long> progress, Func<bool> cancelled)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentOutOfRangeException.ThrowIfNegative(si);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(si, doc.Srcs.Count);

        var f = doc.Srcs[si];
        var ts = f.Parser ?? throw new InvalidOperationException("Source has not been prepared.");
        var zone = new StampZone(f);
        var headCap = f.IsJson ? 8192 : 128;   // CLEF puts "@l" after the template
        var head = new byte[headCap];
        var flat = new byte[headCap];
        var headLen = 0;
        var lineStart = f.IndexedTo;
        long fileLen;

        using (var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete, FileStreamBufferBytes, FileOptions.SequentialScan))
        {
            fileLen = fs.Length;
            fs.Seek(lineStart, SeekOrigin.Begin);
            var gbase = lineStart;
            var stride = f.Stride;
            var bigEndian = IsBigEndian(f);

            // Sized to what is left to read: following a file re-runs this every second
            // for a few new bytes, and a fresh 8 MB buffer each time is pure garbage. The
            // file may still grow while it is read; a smaller buffer only means more reads.
            // Even, so that in UTF-16 no two-byte character is split between two reads.
            var size = (int)Math.Clamp(fileLen - lineStart, 4096, ReadBufferBytes);
            var buf = new byte[size + (size & 1)];

            byte prev1 = 0, prev2 = 0;
            while (true)
            {
                var n = ReadFull(fs, buf);
                if (n <= 0)
                {
                    break;
                }
                var p = 0;
                while (p < n)
                {
                    int q;
                    if (stride == 1)
                    {
                        q = Array.IndexOf(buf, (byte)10, p, n - p);
                    }
                    else
                    {
                        q = NextLineFeed16(buf, p, n, bigEndian);
                    }
                    if (q < 0)
                    {
                        q = n;
                    }
                    if (headLen < head.Length)
                    {
                        var take = Math.Min(head.Length - headLen, q - p);
                        if (take > 0) { Buffer.BlockCopy(buf, p, head, headLen, take); headLen += take; }
                    }
                    if (q < n)
                    {
                        var lineEnd = gbase + q;                 // offset of the line feed
                        bool cr;
                        if (stride == 1)
                        {
                            cr = (q > 0 ? buf[q - 1] : prev1) == 13;
                        }
                        else
                        {
                            // The character before the line feed, which may have been the
                            // last one of the previous read.
                            var c0 = q >= 2 ? buf[q - 2] : prev2;
                            var c1 = q >= 2 ? buf[q - 1] : prev1;
                            cr = bigEndian ? c0 == 0 && c1 == 13 : c0 == 13 && c1 == 0;
                        }
                        Emit(doc, si, ts, zone, lineStart, lineEnd, head, headLen, flat, stride, bigEndian ? 1 : 0, cr);
                        lineStart = lineEnd + stride;
                        headLen = 0;
                        p = q + stride;
                    }
                    else
                    {
                        p = n;
                    }
                }
                prev1 = buf[n - 1];
                prev2 = n >= 2 ? buf[n - 2] : prev1;
                gbase += n;
                if (progress != null)
                {
                    progress(gbase, fileLen);
                }
                if (cancelled != null && cancelled()) { f.IndexedTo = lineStart; return; }
            }

            // trailing line with no newline
            if (gbase > lineStart)
            {
                Emit(doc, si, ts, zone, lineStart, gbase, head, headLen, flat, stride, bigEndian ? 1 : 0, false);
                f.LastUnterminated = true;
            }
            else
            {
                f.LastUnterminated = false;
            }
            f.IndexedTo = lineStart;
            f.Size = fileLen;
        }
    }

    private static bool IsBigEndian(LogSource f) => f.Stride == 2 && f.Encoding.CodePage == 1201;

    /// <summary>Fills the buffer unless the file ends first. A short read would leave
    /// the next read starting halfway through a UTF-16 character.</summary>
    private static int ReadFull(FileStream fs, byte[] buf)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var r = fs.Read(buf, got, buf.Length - got);
            if (r <= 0)
            {
                break;
            }
            got += r;
        }
        return got;
    }

    /// <summary>
    /// Index of the next UTF-16 line feed at or after <paramref name="from"/>, or -1.
    /// <paramref name="from"/> must sit on a character boundary; only characters are
    /// looked at, so a 0x0A byte that is half of some other character (U+410A is
    /// 0A 41 in little-endian) never ends a line. Returns the index of the line
    /// feed's first byte.
    /// </summary>
    internal static int NextLineFeed16(byte[] buf, int from, int n, bool bigEndian)
    {
        // In big-endian the 0x0A byte is the second byte of the character.
        var lfByte = bigEndian ? 1 : 0;
        var i = from;
        while (i + 1 < n)
        {
            var k = Array.IndexOf(buf, (byte)10, i + lfByte, n - i - lfByte);
            if (k < 0)
            {
                return -1;
            }
            var unit = k - lfByte;
            var aligned = ((unit - from) & 1) == 0;
            if (aligned && unit + 1 < n && buf[unit + 1 - lfByte] == 0)
            {
                return unit;
            }
            // Past this character, or onto the boundary this byte was not on.
            i = aligned ? unit + 2 : unit + 1;
        }
        return -1;
    }

    [SuppressMessage("Major Code Smell", "S107", Justification = "Signature carries all emit state to avoid temporary allocations.")]
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Emit consolidates parse/write decisions in one hot path.")]
    private static void Emit(LogDocument doc, int si, ITimestampParser ts, StampZone zone, long start, long end, byte[] head, int headLen,
                     byte[] flat, int stride, int lowByte, bool hadCr)
    {
        var len = (int)Math.Min(end - start, int.MaxValue);
        if (hadCr && len >= stride)
        {
            len -= stride;
        }   // works no matter how long the line is
        if (len < 0)
        {
            len = 0;
        }

        var hn = Math.Min(headLen, len);
        var src = head; var so = 0;
        if (stride == 2)
        {
            // The low byte of each character: first in little-endian, second in big-endian.
            var m = 0;
            for (var i = 0; i + 1 < hn && m < flat.Length; i += 2)
            {
                flat[m++] = head[i + lowByte];
            }
            src = flat; hn = m; so = 0;
        }

        var t = double.NaN;
        var own = false;
        byte sev = 0;
        var skip = 0;
        byte flag = 0;

        if (ts.IsJson)
        {
            // Anchor to the real keys instead of scanning loose text, so a GUID
            // or a path inside the payload can never be mistaken for the stamp.
            var p = -1;
            for (var k = 0; k < JsonLog.TimeKeys.Length && p < 0; k++)

            {
                p = JsonLog.Find(src, so, hn, JsonLog.TimeKeys[k]);
            }
            if (p >= 0 && ts.Kind != TsKind.None)

            {
                own = ts.Parse(src, p, hn - (p - so), out t);
            }

            var lp = -1;
            for (var k = 0; k < JsonLog.LevelKeys.Length && lp < 0; k++)

            {
                lp = JsonLog.Find(src, so, hn, JsonLog.LevelKeys[k]);
            }
            if (lp >= 0)
            {
                sev = JsonLog.Level(JsonLog.Scalar(src, lp, so + hn, 24));
            }
            // CLEF omits the level entirely for Information.
            if (sev == 0 && own)
            {
                sev = 3;
            }
            flag = 0;                       // every JSON line is its own entry
        }
        else
        {
            ts.LastEnd = so;
            if (ts.Kind != TsKind.None && hn > 0)
            {
                own = ts.Parse(src, so, hn, out t);
            }
            int lvA = -1, lvB = -1;
            if (hn > 0)
            {
                sev = LevelScan.Scan(src, so, hn, out lvA, out lvB);
            }

            if (own)
            {
                skip = ts.LastEnd - so;
                if (sev != 0 && lvA >= 0 && lvA - so <= skip + 6)
                {
                    skip = lvB - so;
                }
                while (skip < hn && (src[so + skip] == 32 || src[so + skip] == 9))
                {
                    skip++;
                }
                if (skip < 0 || skip >= hn)
                {
                    skip = 0;
                }
            }

            if (ts.Kind != TsKind.None)
            {
                flag = own ? (byte)0 : (byte)1;
            }
            else if (hn > 0 && (src[so] == 32 || src[so] == 9))
{
    flag = 1;
}
        }

        doc.Ensure(doc.N + 1);
        var ix = doc.N;
        doc.Off[ix] = start;
        doc.Len[ix] = len;
        doc.Sev[ix] = sev;
        if (own)
        {
            doc.Ts[ix] = zone.Store(t, ts.LastOffset);
        }
        else if (ix > 0 && doc.Src[ix - 1] == si)
{
    doc.Ts[ix] = doc.Ts[ix - 1];
}
        else
        {
            doc.Ts[ix] = double.NaN;
        }
        doc.Flag[ix] = flag;
        doc.Skip[ix] = (byte)Math.Min(255, skip);
        doc.Src[ix] = (byte)si;
        doc.N = ix + 1;
        doc.Srcs[si].Lines++;
    }

    // Reads a sample of complete lines for format detection.
    [SuppressMessage("Major Code Smell", "S3776", Justification = "Sampling logic works with byte streams and stride-aware decoding.")]
    internal static List<byte[]> Sample(LogSource f, int max)
    {
        var outp = new List<byte[]>();
        using (var fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Seek(f.DataStart, SeekOrigin.Begin);
            var buf = new byte[SampleBufferBytes];
            var n = ReadFull(fs, buf);
            var stride = f.Stride;
            var bigEndian = IsBigEndian(f);
            var lowByte = bigEndian ? 1 : 0;
            var p = 0;
            while (p < n && outp.Count < max)
            {
                // UTF-16 is split on whole line-feed characters, the same as Run does;
                // splitting on the byte left every sample line after the first a byte
                // out of step, so the timestamp format of a UTF-16 log was never found.
                var q = stride == 2 ? NextLineFeed16(buf, p, n, bigEndian) : Array.IndexOf(buf, (byte)10, p, n - p);
                if (q < 0)
                {
                    q = n;
                }
                var len = q - p;
                if (stride == 2)
                {
                    if (len >= 2 && buf[p + len - 2 + lowByte] == 13 && buf[p + len - 1 - lowByte] == 0)
                    {
                        len -= 2;
                    }
                }
                else if (len > 0 && buf[p + len - 1] == 13)
                {
                    len--;
                }
                if (len > 0)
                {
                    var take = Math.Min(len, 96);
                    byte[] line;
                    if (stride == 2)
                    {
                        var m = 0; line = new byte[take / 2 + 1];
                        for (var i = p; i < p + take - 1; i += 2)
                        {
                            line[m++] = buf[i + lowByte];
                        }
                        Array.Resize(ref line, m);
                    }
                    else
                    {
                        line = new byte[take];
                        Buffer.BlockCopy(buf, p, line, 0, take);
                    }
                    if (line.Length > 0)
                    {
                        outp.Add(line);
                    }
                }
                if (q >= n)
                {
                    break;
                }
                p = q + stride;
            }
        }
        return outp;
    }

    // Interleaves several single-file documents into one timeline.
    // Each log is already in time order, so this is an entry-level k-way merge:
    // a stack trace always travels with the line above it.
    [SuppressMessage("Major Code Smell", "S3776", Justification = "K-way merge logic preserves entry grouping and time ordering.")]
    public LogDocument Merge(IReadOnlyList<LogDocument> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);
        if (docs.Count == 0)
        {
            throw new ArgumentException("At least one document is required.", nameof(docs));
        }

        var m = new LogDocument();
        m.Merged = true;
        var baseIx = new int[docs.Count];
        for (var d = 0; d < docs.Count; d++)
        {
            baseIx[d] = m.Srcs.Count;
            foreach (var s in docs[d].Srcs) { s.Lines = 0; m.Srcs.Add(s); }
        }
        for (var i = 0; i < m.Srcs.Count; i++)
        {
            m.Srcs[i].TintIndex = i;
        }

        var total = 0;
        foreach (var d in docs)
        {
            total += d.N;
        }
        m.Ensure(total + 16);

        var pos = new int[docs.Count];
        while (true)
        {
            var best = -1;
            double bestT = 0;
            for (var d = 0; d < docs.Count; d++)
            {
                if (pos[d] >= docs[d].N)
                {
                    continue;
                }
                var t = docs[d].Ts[pos[d]];
                if (double.IsNaN(t))
                {
                    t = double.NegativeInfinity;
                }
                if (best < 0 || t < bestT) { best = d; bestT = t; }
            }
            if (best < 0)
            {
                break;
            }

            var src = docs[best];
            var i = pos[best];
            do
            {
                var ix = m.N;
                m.Off[ix] = src.Off[i];
                m.Len[ix] = src.Len[i];
                m.Sev[ix] = src.Sev[i];
                m.Ts[ix] = src.Ts[i];
                m.Flag[ix] = src.Flag[i];
                m.Skip[ix] = src.Skip[i];
                m.Src[ix] = (byte)(baseIx[best] + src.Src[i]);
                m.Srcs[m.Src[ix]].Lines++;
                m.N = ix + 1;
                i++;
            } while (i < src.N && src.Flag[i] == 1);
            pos[best] = i;
        }

        m.Name = "Merged (" + m.Srcs.Count + " files)";
        SourceLabels.Apply(m);
        return m;
    }

    // Re-sorts a merged document in place after one file's times have moved, the
    // same entry-level k-way merge as Merge, reading each file's entries back out in
    // the order they already had. Marks follow their lines.
    [SuppressMessage("Major Code Smell", "S3776", Justification = "K-way merge logic preserves entry grouping and time ordering.")]
    public void Reorder(LogDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var n = doc.N;
        if (n == 0 || doc.Srcs.Count < 2)
        {
            return;
        }

        var starts = new List<int>[doc.Srcs.Count];
        for (var s = 0; s < starts.Length; s++)
        {
            starts[s] = new List<int>();
        }
        for (var i = 0; i < n; i++)
        {
            if (i == 0 || doc.Flag[i] != 1 || doc.Src[i] != doc.Src[i - 1])
            {
                starts[doc.Src[i]].Add(i);
            }
        }

        var off = new long[n];
        var len = new int[n];
        var sev = new byte[n];
        var ts = new double[n];
        var flag = new byte[n];
        var skip = new byte[n];
        var src = new byte[n];
        var moved = new int[n];
        var pos = new int[starts.Length];
        var o = 0;
        while (true)
        {
            var best = -1;
            double bestT = 0;
            for (var s = 0; s < starts.Length; s++)
            {
                if (pos[s] >= starts[s].Count)
                {
                    continue;
                }
                var t = doc.Ts[starts[s][pos[s]]];
                if (double.IsNaN(t))
                {
                    t = double.NegativeInfinity;
                }
                if (best < 0 || t < bestT) { best = s; bestT = t; }
            }
            if (best < 0)
            {
                break;
            }
            var i = starts[best][pos[best]++];
            do
            {
                off[o] = doc.Off[i]; len[o] = doc.Len[i]; sev[o] = doc.Sev[i]; ts[o] = doc.Ts[i];
                flag[o] = doc.Flag[i]; skip[o] = doc.Skip[i]; src[o] = doc.Src[i];
                moved[i] = o;
                o++; i++;
            } while (i < n && doc.Flag[i] == 1 && doc.Src[i] == best);
        }

        Array.Copy(off, doc.Off, n); Array.Copy(len, doc.Len, n); Array.Copy(sev, doc.Sev, n);
        Array.Copy(ts, doc.Ts, n); Array.Copy(flag, doc.Flag, n); Array.Copy(skip, doc.Skip, n);
        Array.Copy(src, doc.Src, n);
        var marks = new List<int>(doc.Marks);
        doc.Marks.Clear();
        foreach (var m in marks)
        {
            if (m < n)
            {
                doc.Marks.Add(moved[m]);
            }
        }
    }
}
