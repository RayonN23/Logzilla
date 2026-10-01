using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Logzilla.AI.Privacy;

/// <summary>
/// Hides personal and secret values before text leaves the machine, and puts them
/// back in what comes home.
///
/// Each distinct value gets one placeholder for the whole conversation — the same
/// IP address is always «IP_1» — so the model can still see that two lines concern
/// the same host or device without ever learning which. <see cref="Restore"/> swaps
/// the real values back in for display, on this machine only.
/// </summary>
public sealed partial class Redactor
{
    private readonly Dictionary<string, string> _toPlaceholder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>False passes everything through untouched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many distinct values have been hidden so far.</summary>
    public int Hidden
    {
        get
        {
            lock (_gate)
            {
                return _toValue.Count;
            }
        }
    }

    // Order matters: a secret's value is hidden before the patterns below can carve
    // an email or IP address out of the middle of it.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt { get; }

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+([A-Za-z0-9\-._~+/]{12,}=*)")]
    private static partial Regex AuthHeader { get; }

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|client_?secret|api_?key|apikey|access_?key|access_?token|refresh_?token|auth_?token|token|sharedaccesskey|accountkey)(""?\s*[=:]\s*""?)([^\s""',;&}\]]{3,})")]
    private static partial Regex KeyedSecret { get; }

    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey { get; }

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9_-]{20,}|xox[abpr]-[A-Za-z0-9-]{10,})\b")]
    private static partial Regex VendorToken { get; }

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex Email { get; }

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex Ipv4 { get; }

    [GeneratedRegex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b")]
    private static partial Regex Guid { get; }

    [GeneratedRegex(@"«[A-Z]+_\d+»")]
    private static partial Regex Placeholder { get; }

    /// <summary>The text with every recognised personal or secret value replaced.</summary>
    public string Redact(string text)
    {
        if (!Enabled || string.IsNullOrEmpty(text))
        {
            return text;
        }
        lock (_gate)
        {
            text = Jwt.Replace(text, m => Hide("TOKEN", m.Value));
            text = AuthHeader.Replace(text, m => m.Groups[1].Value + " " + Hide("TOKEN", m.Groups[2].Value));
            text = KeyedSecret.Replace(text, m => IsPlaceholder(m.Groups[3].Value)
                ? m.Value
                : m.Groups[1].Value + m.Groups[2].Value + Hide("SECRET", m.Groups[3].Value));
            text = AwsKey.Replace(text, m => Hide("SECRET", m.Value));
            text = VendorToken.Replace(text, m => Hide("SECRET", m.Value));
            text = Email.Replace(text, m => Hide("EMAIL", m.Value));
            text = Ipv4.Replace(text, m => IsLoopbackOrUnspecified(m.Value) ? m.Value : Hide("IP", m.Value));
            text = Guid.Replace(text, m => Hide("ID", m.Value));
            return text;
        }
    }

    /// <summary>Puts the real values back. Used only for what is shown on this machine.</summary>
    public string Restore(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('«', StringComparison.Ordinal) < 0)
        {
            return text;
        }
        lock (_gate)
        {
            return Placeholder.Replace(text, m => _toValue.TryGetValue(m.Value, out var v) ? v : m.Value);
        }
    }

    /// <summary>Forgets every value, for a new conversation.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _toPlaceholder.Clear();
            _toValue.Clear();
            _counts.Clear();
        }
    }

    private string Hide(string kind, string value)
    {
        if (_toPlaceholder.TryGetValue(value, out var existing))
        {
            return existing;
        }
        _counts.TryGetValue(kind, out var n);
        n++;
        _counts[kind] = n;
        var placeholder = new StringBuilder().Append('«').Append(kind).Append('_').Append(n).Append('»').ToString();
        _toPlaceholder[value] = placeholder;
        _toValue[placeholder] = value;
        return placeholder;
    }

    private static bool IsPlaceholder(string s) => s.StartsWith('«');

    private static bool IsLoopbackOrUnspecified(string ip) =>
        ip == "127.0.0.1" || ip == "0.0.0.0" || ip == "255.255.255.255";
}
