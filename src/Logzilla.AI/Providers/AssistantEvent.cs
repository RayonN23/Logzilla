namespace Logzilla.AI.Providers;

/// <summary>Something that happened while a model answered: text arrived, it finished, or it failed.</summary>
public abstract record AssistantEvent;

/// <summary>The next piece of the answer, as it streams in.</summary>
public sealed record TextDelta(string Text) : AssistantEvent;

/// <summary>The answer is complete.</summary>
public sealed record Finished : AssistantEvent;

/// <summary>The request failed; <paramref name="Message"/> is fit to show the user.</summary>
public sealed record Failed(string Message) : AssistantEvent;

/// <summary>
/// What one call to the model cost. A question can make several calls — one per round
/// of tool use — and each reports its own. Any figure the provider did not give is null.
/// </summary>
/// <param name="InputTokens">Tokens sent to the model, including tool results.</param>
/// <param name="OutputTokens">Tokens the model wrote.</param>
/// <param name="CachedTokens">Input tokens the provider served from its cache, usually cheaper.</param>
/// <param name="Multiplier">GitHub Copilot's premium-request multiplier for the model.</param>
/// <param name="ContextTokens">How much of the model's context window the conversation now fills.</param>
/// <param name="ContextLimit">The size of that window.</param>
public sealed record UsageReport(long? InputTokens = null, long? OutputTokens = null, long? CachedTokens = null,
    double? Multiplier = null, long? ContextTokens = null, long? ContextLimit = null) : AssistantEvent;
