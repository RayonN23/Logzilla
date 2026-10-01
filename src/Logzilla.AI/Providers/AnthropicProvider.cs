using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Models;
using Microsoft.Extensions.AI;

namespace Logzilla.AI.Providers;

/// <summary>Claude, through Anthropic's API with the user's own key.</summary>
public sealed class AnthropicProvider : ChatClientProvider
{
    /// <summary>Room for an answer; the tools, not the answer, carry the log.</summary>
    public const int MaxOutputTokens = 4096;

    private readonly AnthropicClient _client;

    /// <param name="apiKey">The user's Anthropic API key.</param>
    /// <param name="baseUrl">An organisation's own gateway, or null for Anthropic's.</param>
    public AnthropicProvider(string apiKey, string baseUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var options = new ClientOptions { ApiKey = apiKey };
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options = options with { BaseUrl = baseUrl };
        }
        _client = new AnthropicClient(options);
    }

    public override string Id => "anthropic";

    public override string Name => "Claude";

    public override async Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.Models.List(new ModelListParams { Limit = 1 }, cancellationToken).ConfigureAwait(false);
            return ProviderStatus.Ok("Connected with your API key.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return ProviderStatus.NotReady("Could not connect: " + ex.Message);
        }
    }

    public override async Task<IReadOnlyList<AssistantModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var models = new List<AssistantModel>();
        var page = await _client.Models.List(new ModelListParams { Limit = 100 }, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            foreach (var m in page.Items)
            {
                models.Add(new AssistantModel(m.ID, string.IsNullOrEmpty(m.DisplayName) ? m.ID : m.DisplayName));
            }
            if (!page.HasNext())
            {
                break;
            }
            page = await page.Next(cancellationToken).ConfigureAwait(false);
        }
        return models;
    }

    protected override IChatClient CreateClient(string model) => _client.AsIChatClient(model, MaxOutputTokens);

    public override ValueTask DisposeAsync()
    {
        _client.Dispose();
        return base.DisposeAsync();
    }
}
