using System;
using System.Globalization;
using System.Text;

namespace Logzilla.AI.Providers;

/// <summary>
/// Adds up <see cref="UsageReport"/>s: one tally per answer, and one for the whole
/// conversation, so the user can see what a question cost before the bill does.
/// </summary>
public sealed class UsageTally
{
    public int Calls { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CachedTokens { get; private set; }

    /// <summary>The highest Copilot premium-request multiplier seen, or null for none.</summary>
    public double? Multiplier { get; private set; }

    public long? ContextTokens { get; private set; }
    public long? ContextLimit { get; private set; }

    public bool Any => Calls > 0 || ContextTokens.HasValue;

    public long TotalTokens => InputTokens + OutputTokens;

    public void Add(UsageReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.InputTokens.HasValue || r.OutputTokens.HasValue)
        {
            Calls++;
            InputTokens += r.InputTokens ?? 0;
            OutputTokens += r.OutputTokens ?? 0;
            CachedTokens += r.CachedTokens ?? 0;
        }
        if (r.Multiplier.HasValue)
        {
            Multiplier = Math.Max(Multiplier ?? 0, r.Multiplier.Value);
        }
        if (r.ContextTokens.HasValue)
        {
            ContextTokens = r.ContextTokens;
            ContextLimit = r.ContextLimit ?? ContextLimit;
        }
    }

    public void Add(UsageTally other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Calls += other.Calls;
        InputTokens += other.InputTokens;
        OutputTokens += other.OutputTokens;
        CachedTokens += other.CachedTokens;
        if (other.Multiplier.HasValue)
        {
            Multiplier = Math.Max(Multiplier ?? 0, other.Multiplier.Value);
        }
        if (other.ContextTokens.HasValue)
        {
            ContextTokens = other.ContextTokens;
            ContextLimit = other.ContextLimit ?? ContextLimit;
        }
    }

    /// <summary>One line under an answer: "12,480 in · 612 out tokens · 3 calls".</summary>
    public string Line()
    {
        if (Calls == 0)
        {
            return "";
        }
        var sb = new StringBuilder()
            .Append(Number(InputTokens)).Append(" in · ").Append(Number(OutputTokens)).Append(" out tokens");
        if (Calls > 1)
        {
            sb.Append(" · ").Append(Calls).Append(" calls");
        }
        if (Multiplier is > 0)
        {
            sb.Append(" · ").Append(Multiplier.Value.ToString("0.##", CultureInfo.InvariantCulture)).Append("× premium");
        }
        return sb.ToString();
    }

    /// <summary>The conversation total in a few characters: "12.5k tokens".</summary>
    public string Brief() => Calls == 0 ? "" : Compact(TotalTokens) + " tokens";

    /// <summary>Everything known, for the Usage window.</summary>
    public string Details()
    {
        var sb = new StringBuilder();
        sb.Append("Model calls        ").AppendLine(Number(Calls));
        sb.Append("Tokens sent        ").AppendLine(Number(InputTokens));
        if (CachedTokens > 0)
        {
            sb.Append("  of which cached  ").AppendLine(Number(CachedTokens));
        }
        sb.Append("Tokens received    ").AppendLine(Number(OutputTokens));
        sb.Append("Total              ").AppendLine(Number(TotalTokens));
        if (Multiplier is > 0)
        {
            sb.Append("Premium requests   ").Append(Multiplier.Value.ToString("0.##", CultureInfo.InvariantCulture))
              .AppendLine("× per question (GitHub Copilot)");
        }
        if (ContextTokens.HasValue && ContextLimit is > 0)
        {
            sb.Append("Context window     ").Append(Number(ContextTokens.Value)).Append(" of ").Append(Number(ContextLimit.Value))
              .Append(" (").Append((ContextTokens.Value * 100.0 / ContextLimit.Value).ToString("0", CultureInfo.InvariantCulture)).AppendLine("% full)");
        }
        return sb.ToString();
    }

    private static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Compact(long n) => n switch
    {
        < 1000 => n.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (n / 1000.0).ToString(n < 10_000 ? "0.0" : "0", CultureInfo.InvariantCulture) + "k",
        _ => (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M",
    };
}
