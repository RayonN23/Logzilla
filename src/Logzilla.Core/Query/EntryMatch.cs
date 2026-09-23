using System;

namespace Logzilla.Core.Query;

/// <summary>
/// Which of a query's include terms have turned up so far in one log entry, and
/// whether an exclude term has.
///
/// A JSON entry is a single line, so this is filled and read in one step. A
/// plain-text entry is a header line plus every continuation line under it — the
/// stack trace, the wrapped XML payload — and the query is answered for that whole
/// entry. Without it, "error unknown" only ever matched when both words landed on
/// the same physical line, which is why the same search behaved differently on a
/// JSON log and a plain-text one.
/// </summary>
public sealed class EntryMatch
{
    private readonly bool[] _include;

    public EntryMatch(int includeTerms)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(includeTerms);
        _include = new bool[includeTerms];
    }

    /// <summary>An exclude term was seen; nothing later can rescue the entry.</summary>
    public bool Excluded { get; private set; }

    /// <summary>True once every include term has been seen and no exclude term has.</summary>
    public bool Matched
    {
        get
        {
            if (Excluded)
            {
                return false;
            }
            for (var i = 0; i < _include.Length; i++)
            {
                if (!_include[i])
                {
                    return false;
                }
            }
            return true;
        }
    }

    public void Reset()
    {
        Array.Clear(_include);
        Excluded = false;
    }

    public bool Has(int term) => _include[term];

    public void Include(int term) => _include[term] = true;

    public void Exclude() => Excluded = true;
}
