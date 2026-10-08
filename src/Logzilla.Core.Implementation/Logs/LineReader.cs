using System;
using System.IO;
using Logzilla.Core.Logs;

namespace Logzilla.Core.Implementation.Logs;

/// <summary>
/// Random access to the raw bytes of a document's lines, read through a window per
/// source file. A merged document interleaves its files line by line; with one shared
/// window every change of file threw the other file's window away and re-read up to
/// the whole buffer for a single line, which made searching two merged 50 MB logs take
/// minutes instead of milliseconds.
/// </summary>
internal sealed class LineReader : ILineReader
{
    /// <summary>Smallest window worth keeping for one file, however many share the budget.</summary>
    private const int MinWindow = 1 << 18;

    private readonly LogDocument _f;
    private readonly Window[] _windows;
    private FileStream[] _fs;

    private sealed class Window
    {
        public byte[] Buf;
        public long Base = -1;
        public int Len;
    }

    public LineReader(LogDocument f, int bufSize)
    {
        _f = f;
        var count = Math.Max(1, f.Srcs.Count);
        _fs = new FileStream[count];
        _windows = new Window[count];
        // The budget is shared between the files, and no file gets more than it could
        // fill: every filter pass and every re-attached grid opens a reader, and most
        // logs are far smaller than the megabytes asked for. Locate still grows a
        // window for a line that needs it.
        var share = Math.Max(MinWindow, bufSize / count);
        for (var si = 0; si < count; si++)
        {
            var size = si < f.Srcs.Count ? f.Srcs[si].Size : f.Size;
            _windows[si] = new Window { Buf = new byte[(int)Math.Clamp(size + 16, 1024, Math.Max(1024, share))] };
        }
    }

    private FileStream Stream(int si)
    {
        ObjectDisposedException.ThrowIf(_fs == null, this);
        return _fs[si] ??= new FileStream(_f.Srcs[si].Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
    }

    // Places line i into its file's window and returns its offset there.
    public int Locate(int i, out byte[] buf)
    {
        var start = _f.Off[i];
        var len = _f.Len[i];
        var si = _f.Src[i];
        var w = _windows[si];
        if (w.Base >= 0 && start >= w.Base && start + len <= w.Base + w.Len)
        {
            buf = w.Buf;
            return (int)(start - w.Base);
        }
        if (len + 16 > w.Buf.Length)
        {
            w.Buf = new byte[len + 1024];
        }
        var fs = Stream(si);
        fs.Seek(start, SeekOrigin.Begin);
        var got = 0;
        while (got < w.Buf.Length)
        {
            var r = fs.Read(w.Buf, got, w.Buf.Length - got);
            if (r <= 0)
            {
                break;
            }
            got += r;
        }
        w.Base = start;
        w.Len = got;
        buf = w.Buf;
        return 0;
    }

    public string Text(int i)
    {
        var o = Locate(i, out var b);
        var w = _windows[_f.Src[i]];
        // Bounded by what was actually read, not the window's size: a file cut short
        // since it was indexed must not hand back the stale bytes of an earlier read.
        var n = Math.Max(0, Math.Min(_f.Len[i], w.Len - o));
        return _f.Srcs[_f.Src[i]].Encoding.GetString(b, o, n);
    }

    public void Dispose()
    {
        if (_fs == null)
        {
            return;
        }
        foreach (var fs in _fs)
        {
            fs?.Dispose();
        }
        _fs = null;
    }
}
