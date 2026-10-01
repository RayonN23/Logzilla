using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Logzilla.AI.Tools;

namespace Logzilla.AI.Providers;

/// <summary>
/// One way of reaching a model: GitHub Copilot, Anthropic, OpenAI. The Ask tab only
/// ever talks to this; nothing outside a provider knows which vendor it is.
/// </summary>
public interface IAssistantProvider : IAsyncDisposable
{
    /// <summary>Stable key, used in settings and policy: "copilot", "anthropic", "openai".</summary>
    string Id { get; }

    /// <summary>What the picker calls it.</summary>
    string Name { get; }

    Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AssistantModel>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts a conversation that may call <paramref name="tools"/> and nothing else.
    /// </summary>
    Task<IAssistantChat> StartChatAsync(string model, string instructions, IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken);
}

/// <summary>One conversation. Each message is answered in turn; history is kept.</summary>
public interface IAssistantChat : IAsyncDisposable
{
    /// <summary>
    /// Sends <paramref name="prompt"/> and reports the answer through <paramref name="onEvent"/>
    /// until it finishes, fails or is cancelled. Events may arrive on any thread.
    /// </summary>
    Task SendAsync(string prompt, Action<AssistantEvent> onEvent, CancellationToken cancellationToken);
}
