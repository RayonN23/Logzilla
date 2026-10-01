namespace Logzilla.AI.Providers;

/// <summary>Whether a provider can be used right now, and if not, what to do about it.</summary>
public sealed record ProviderStatus(bool Ready, string Message)
{
    /// <summary>The account the provider is signed in as, when it has one to report.</summary>
    public string Account { get; init; }

    public static ProviderStatus Ok(string message) => new(true, message);

    public static ProviderStatus NotReady(string message) => new(false, message);
}
