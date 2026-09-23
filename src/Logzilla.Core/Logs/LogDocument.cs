using System;
using System.Collections.Generic;

namespace Logzilla.Core.Logs;


public class LogDocument
{
    private const int InitialCapacity = 1 << 16;

    private long[] _off = new long[InitialCapacity];
    private int[] _len = new int[InitialCapacity];
    private byte[] _sev = new byte[InitialCapacity];
    private double[] _ts = new double[InitialCapacity];
    private byte[] _flag = new byte[InitialCapacity];
    private byte[] _skip = new byte[InitialCapacity];
    private byte[] _src = new byte[InitialCapacity];

    public string Name { get; set; }                       // display name for the tab
    public bool Merged { get; set; }
    public List<LogSource> Srcs { get; } = new List<LogSource>();

    public long[] Off { get { return _off; } }
    public int[] Len { get { return _len; } }
    public byte[] Sev { get { return _sev; } }
    public double[] Ts { get { return _ts; } }
    public byte[] Flag { get { return _flag; } }   // 1 = continuation of the entry above
    public byte[] Skip { get { return _skip; } }   // chars of timestamp/level prefix to hide
    public byte[] Src { get { return _src; } }     // which Srcs entry the line came from
    public int N { get; set; }

    public HashSet<int> Marks { get; } = new HashSet<int>();

    public long Size
    {
        get
        {
            long t = 0;
            for (var i = 0; i < Srcs.Count; i++) 
            {
                t += Srcs[i].Size;
            }
            return t;
        }
    }

    public LogSource SrcOf(int line) { return Srcs[Src[line]]; }

    public void Ensure(int need)
    {
        if (need <= Off.Length) 
        {
            return;
        }
        var cap = _off.Length;
        while (cap < need) 
        {
            cap = cap + cap / 2 + 1024;
        }
        Array.Resize(ref _off, cap); Array.Resize(ref _len, cap);
        Array.Resize(ref _sev, cap); Array.Resize(ref _ts, cap); Array.Resize(ref _flag, cap);
        Array.Resize(ref _skip, cap); Array.Resize(ref _src, cap);
    }
}

// Sequential / near-sequential line reader over the raw file.
// Merged files often share a long prefix (app-2026-03-01-web01.log ...).
// Dropping it makes the source column readable without losing what tells them apart.
