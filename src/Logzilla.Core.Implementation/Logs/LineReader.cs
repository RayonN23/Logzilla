using System;
using System.IO;
using Logzilla.Core.Logs;

namespace Logzilla.Core.Implementation.Logs;

internal sealed class LineReader : ILineReader
{
    private readonly LogDocument _f;
    private FileStream[] _fs;
    private byte[] _buf;
    private long _base = -1;
    private int _len, _bufSrc = -1;

    public LineReader(LogDocument f, int bufSize)
    {
        _f = f;
        // No point holding more than the files could fill: every filter pass and every
        // re-attached grid opens a reader, and most logs are far smaller than the
        // megabytes asked for. Locate still grows the buffer for a line that needs it.
        _buf = new byte[(int)Math.Clamp(f.Size + 16, 1024, Math.Max(1024, bufSize))];
        _fs = new FileStream[f.Srcs.Count];
    }

    private FileStream Stream(int si)
    {
        if (_fs[si] == null)
        {
            _fs[si] = new FileStream(_f.Srcs[si].Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        }
        return _fs[si];
    }

    // Places line i into the buffer and returns its offset there.
    public int Locate(int i, out byte[] buf)
    {
        var start = _f.Off[i];
        var len = _f.Len[i];
        var si = _f.Src[i];
        if (si == _bufSrc && _base >= 0 && start >= _base && start + len <= _base + _len)
        {
            buf = _buf;
            return (int)(start - _base);
        }
        if (len + 16 > _buf.Length)
        {
            _buf = new byte[len + 1024];
        }
        var fs = Stream(si);
        fs.Seek(start, SeekOrigin.Begin);
        var got = 0;
        while (got < _buf.Length)
        {
            var r = fs.Read(_buf, got, _buf.Length - got);
            if (r <= 0)
            {
                break;
            }
            got += r;
        }
        _base = start;
        _len = got;
        _bufSrc = si;
        buf = _buf;
        return 0;
    }

    public string Text(int i)
    {
        byte[] b;
        var o = Locate(i, out b);
        return _f.Srcs[_f.Src[i]].Encoding.GetString(b, o, Math.Min(_f.Len[i], b.Length - o));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (_fs == null)
        {
            return;
        }
        for (var i = 0; i < _fs.Length; i++)
        {
            if (_fs[i] != null)
            {
                _fs[i].Dispose();
                _fs[i] = null;
            }
        }
        _fs = null;
    }
}
