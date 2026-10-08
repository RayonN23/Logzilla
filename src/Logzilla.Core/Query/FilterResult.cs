using System.Collections.Generic;
using Logzilla.Core.Common;

namespace Logzilla.Core.Query;

/// <summary>The outcome of one scan: which lines survived, and the shapes drawn from them.</summary>
public sealed class FilterResult
{
    public const int Buckets = 480;
    public const int Slots = 2048;

    public List<int> View { get; } = [];
    public List<int> Matches { get; } = [];
    public int[] Counts { get; } = new int[Levels.Count];
    public int[] SrcCounts { get; set; }

    public double Lo { get; set; } = double.NaN;
    public double Hi { get; set; } = double.NaN;

    public int[] Bucket { get; set; }
    public int[] BucketWarn { get; set; }
    public int[] BucketErr { get; set; }

    /// <summary>Severity density for the whole document, one byte per minimap slot.</summary>
    public byte[] Density { get; set; }

    /// <summary>
    /// Which minimap slots hold a search hit, on the same scale as <see cref="Density"/>.
    /// Null when there is nothing to show: no search, or every row on view is a hit.
    /// </summary>
    public bool[] HitSlots { get; set; }
}
