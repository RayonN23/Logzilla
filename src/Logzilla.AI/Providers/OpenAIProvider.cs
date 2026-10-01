using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Logzilla.AI.Providers;

/// <summary>OpenAI's models, through its API with the user's own key.</summary>
public sealed class OpenAIProvider : ChatClientProvider
{
    // The models endpoint lists everything the key can reach, including embeddings,
    // speech and image models that cannot hold a conversation.
    private static readonly string[] NotChat =
        ["embedding", "tts", "whisper", "dall-e", "image", "audio", "realtime", "moderation", "transcribe", "search", "davinci", "babbage", "sora"];

    private readonly OpenAIClient _client;

    /// <param name="apiKey">The user's OpenAI API key.</param>
    /// <param name="endpoint">An organisation's own OpenAI-compatible endpoint, or null for OpenAI's.</param>
    public OpenAIProvider(string apiKey, string endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = new Uri(endpoint);
        }
        _client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }

    public override string Id => "openai";

    public override string Name => "OpenAI";

    public override async Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.GetOpenAIModelClient().GetModelsAsync(cancellationToken).ConfigureAwait(false);
            return ProviderStatus.Ok("Connected with your API key.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return ProviderStatus.NotReady("Could not connect: " + ex.Message);
        }
    }

    public override async Task<IReadOnlyList<AssistantModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var result = await _client.GetOpenAIModelClient().GetModelsAsync(cancellationToken).ConfigureAwait(false);
        return result.Value
            .Select(m => m.Id)
            .Where(id => !NotChat.Any(n => id.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(id => id, StringComparer.Ordinal)
            .Select(id => new AssistantModel(id, id))
            .ToList();
    }

    protected override IChatClient CreateClient(string model) => _client.GetChatClient(model).AsIChatClient();
}
