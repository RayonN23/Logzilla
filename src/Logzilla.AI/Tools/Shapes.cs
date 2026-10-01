using System.Text.RegularExpressions;

namespace Logzilla.AI.Tools;

/// <summary>
/// Reduces a message to its shape by replacing the parts that vary — numbers, ids,
/// addresses, quoted values — so "latency=812ms" and "latency=41ms" count as one
/// thing. The same idea as the Patterns panel, kept here so the assistant does not
/// depend on the UI.
/// </summary>
public static partial class Shapes
{
    public const int MaxLength = 160;

    [GeneratedRegex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b")]
    private static partial Regex Guid { get; }

    [GeneratedRegex(@"«[A-Z]+_\d+»")]
    private static partial Regex Placeholder { get; }

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex Email { get; }

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?\b")]
    private static partial Regex Ip { get; }

    [GeneratedRegex(@"(?i)\b(?:0x)?[0-9a-f]{12,}\b")]
    private static partial Regex Hex { get; }

    [GeneratedRegex(@"""[^""]{0,200}""|'[^']{0,200}'")]
    private static partial Regex Quoted { get; }

    [GeneratedRegex(@"\d+(?:[.,:]\d+)*")]
    private static partial Regex Number { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space { get; }

    public static string Of(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "";
        }
        var s = message.Length > 1000 ? message[..1000] : message;
        s = Placeholder.Replace(s, "<*>");
        s = Guid.Replace(s, "<id>");
        s = Email.Replace(s, "<email>");
        s = Ip.Replace(s, "<ip>");
        s = Hex.Replace(s, "<hex>");
        s = Quoted.Replace(s, "<str>");
        s = Number.Replace(s, "<n>");
        s = Space.Replace(s, " ").Trim();
        return s.Length > MaxLength ? s[..MaxLength] + "…" : s;
    }
}
