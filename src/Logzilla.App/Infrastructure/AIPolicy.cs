using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// What an organisation allows the AI assistant to do, set by Group Policy or Intune
/// under <c>SOFTWARE\Policies\Logzilla</c>. The machine-wide key wins over the user's.
/// Logzilla installs per user and never writes here, so a user cannot loosen what an
/// administrator set.
///
/// <list type="bullet">
/// <item><c>AIDisabled</c> (DWORD 1): no assistant at all; the Ask tab says so.</item>
/// <item><c>AIAllowedProviders</c> (string, e.g. "copilot,anthropic"): only these.</item>
/// <item><c>AIRequireRedaction</c> (DWORD 1): personal data is always hidden.</item>
/// <item><c>AIRequireRecord</c> (DWORD 1): everything sent is always recorded.</item>
/// <item><c>AIAnthropicEndpoint</c>, <c>AIOpenAIEndpoint</c> (string): the only endpoint to use.</item>
/// <item><c>AIGitHubClientId</c> (string): the organisation's own GitHub app for "Sign in with GitHub".</item>
/// </list>
/// </summary>
internal sealed class AIPolicy
{
    public const string KeyPath = @"SOFTWARE\Policies\Logzilla";

    public static readonly AIPolicy None = new();

    private static readonly string[] ValueNames =
    [
        "AIDisabled", "AIAllowedProviders", "AIRequireRedaction", "AIRequireRecord",
        "AIAnthropicEndpoint", "AIOpenAIEndpoint", "AIGitHubClientId",
    ];

    public bool Disabled { get; private init; }

    /// <summary>Null allows every provider.</summary>
    public IReadOnlySet<string> AllowedProviders { get; private init; }

    public bool RequireRedaction { get; private init; }

    public bool RequireRecord { get; private init; }

    public string AnthropicEndpoint { get; private init; }

    public string OpenAIEndpoint { get; private init; }

    public string GitHubClientId { get; private init; }

    /// <summary>True when anything at all is set, so the settings can say who decided.</summary>
    public bool Managed => Disabled || AllowedProviders != null || RequireRedaction || RequireRecord ||
                           AnthropicEndpoint != null || OpenAIEndpoint != null || GitHubClientId != null;

    public bool Allows(string providerId) => !Disabled && (AllowedProviders == null || AllowedProviders.Contains(providerId));

    /// <summary>Reads the policy from the registry. Any failure reads as no policy.</summary>
    public static AIPolicy Load()
    {
        try
        {
            // Read everything while the keys are open; the policy never looks at them again.
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            using (var machine = Registry.LocalMachine.OpenSubKey(KeyPath))
            using (var user = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                foreach (var name in ValueNames)
                {
                    var v = machine?.GetValue(name) ?? user?.GetValue(name);
                    if (v != null)
                    {
                        values[name] = v;
                    }
                }
            }
            return From(name => values.TryGetValue(name, out var v) ? v : null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            System.Diagnostics.Debug.WriteLine("Read AI policy: " + ex.Message);
            return None;
        }
    }

    /// <summary>Builds a policy from named values, so it can be tested without a registry.</summary>
    public static AIPolicy From(Func<string, object> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var allowed = Text(read("AIAllowedProviders"));
        return new AIPolicy
        {
            Disabled = Flag(read("AIDisabled")),
            AllowedProviders = allowed == null
                ? null
                : allowed.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Select(p => p.ToLowerInvariant())
                         .ToHashSet(StringComparer.Ordinal),
            RequireRedaction = Flag(read("AIRequireRedaction")),
            RequireRecord = Flag(read("AIRequireRecord")),
            AnthropicEndpoint = Text(read("AIAnthropicEndpoint")),
            OpenAIEndpoint = Text(read("AIOpenAIEndpoint")),
            GitHubClientId = Text(read("AIGitHubClientId")),
        };
    }

    private static bool Flag(object v) => v switch
    {
        int i => i != 0,
        long l => l != 0,
        string s => s.Trim() is "1" or "true" or "True" or "TRUE",
        _ => false,
    };

    private static string Text(object v) => v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
