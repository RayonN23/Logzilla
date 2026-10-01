using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Logzilla.AI.Tools;
using Microsoft.Extensions.AI;

namespace Logzilla.AI.Providers;

/// <summary>
/// A provider reached through its own API with the user's key. Anthropic and OpenAI
/// both plug into Microsoft.Extensions.AI, so the conversation loop — streaming,
/// calling Logzilla's tools, keeping history — is written once, here.
/// </summary>
public abstract class ChatClientProvider : IAssistantProvider
{
    /// <summary>Tool calls one question may make before the model must answer.</summary>
    public const int MaxToolRounds = 12;

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken);

    public abstract Task<IReadOnlyList<AssistantModel>> ListModelsAsync(CancellationToken cancellationToken);

    /// <summary>The vendor's chat client for one model.</summary>
    protected abstract IChatClient CreateClient(string model);

    public Task<IAssistantChat> StartChatAsync(string model, string instructions, IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(tools);
        var client = new ChatClientBuilder(CreateClient(model))
            .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = MaxToolRounds)
            .Build();
        var functions = tools
            .Select(t => (AITool)AIFunctionFactory.Create(t.Method, t.Name, t.Description))
            .ToList();
        IAssistantChat chat = new ChatClientChat(client, model, instructions, functions);
        return Task.FromResult(chat);
    }

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private sealed class ChatClientChat : IAssistantChat
    {
        private readonly IChatClient _client;
        private readonly ChatOptions _options;
        private readonly List<ChatMessage> _history = new();
        private readonly SemaphoreSlim _turn = new(1, 1);

        public ChatClientChat(IChatClient client, string model, string instructions, IList<AITool> tools)
        {
            _client = client;
            _options = new ChatOptions { ModelId = model, Tools = tools, AllowMultipleToolCalls = true };
            if (!string.IsNullOrEmpty(instructions))
            {
                _history.Add(new ChatMessage(ChatRole.System, instructions));
            }
        }

        public async Task SendAsync(string prompt, Action<AssistantEvent> onEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(onEvent);
            await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
            var mark = _history.Count;
            try
            {
                _history.Add(new ChatMessage(ChatRole.User, prompt));
                var updates = new List<ChatResponseUpdate>();
                await foreach (var update in _client.GetStreamingResponseAsync(_history, _options, cancellationToken).ConfigureAwait(false))
                {
                    updates.Add(update);
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        onEvent(new TextDelta(update.Text));
                    }
                    // Each round of tool use is its own call to the model, with its own usage.
                    foreach (var usage in update.Contents.OfType<UsageContent>())
                    {
                        onEvent(new UsageReport(usage.Details.InputTokenCount, usage.Details.OutputTokenCount,
                            usage.Details.CachedInputTokenCount));
                    }
                }
                _history.AddMessages(updates);
                onEvent(new Finished());
            }
            catch (OperationCanceledException)
            {
                // A cancelled question leaves no half-answer behind to confuse the next one.
                _history.RemoveRange(mark, _history.Count - mark);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _history.RemoveRange(mark, _history.Count - mark);
                onEvent(new Failed(ex.Message));
            }
            finally
            {
                _turn.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            _turn.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
