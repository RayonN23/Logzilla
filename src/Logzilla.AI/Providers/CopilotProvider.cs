using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Logzilla.AI.Tools;
using Microsoft.Extensions.AI;

namespace Logzilla.AI.Providers;

/// <summary>
/// GitHub Copilot, through the official SDK and the user's own Copilot subscription.
/// It offers whichever models the user's plan and organisation allow.
///
/// The SDK drives Copilot's agent, which comes with tools of its own — a shell, file
/// editing, web fetches. A log is written partly by strangers, so none of those may
/// run: the session is limited to Logzilla's read-only tools by an allowlist, every
/// other permission request is refused, and it runs in an empty folder with the
/// user's own Copilot instructions, skills and MCP servers left out.
/// </summary>
public sealed class CopilotProvider : IAssistantProvider
{
    private const string Refusal = "Logzilla's assistant can only read the open log.";

    private readonly string _token;
    private readonly string _cliPath;
    private readonly string _workDir;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private CopilotClient _client;

    /// <param name="token">A GitHub token, or null to use the account the user signed in with.</param>
    /// <param name="cliPath">The Copilot CLI to run, or null for the one bundled with Logzilla.</param>
    public CopilotProvider(string token = null, string cliPath = null)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _cliPath = string.IsNullOrWhiteSpace(cliPath) ? null : cliPath;
        _workDir = Path.Combine(Path.GetTempPath(), "Logzilla", "copilot-" + Guid.NewGuid().ToString("N"));
    }

    public string Id => "copilot";

    public string Name => "GitHub Copilot";

    public async Task<ProviderStatus> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
            var auth = await client.GetAuthStatusAsync(cancellationToken).ConfigureAwait(false);
            return auth.IsAuthenticated
                ? ProviderStatus.Ok("Signed in as " + auth.Login + ".") with { Account = auth.Login }
                : ProviderStatus.NotReady("Not signed in. In a terminal, run  copilot  and type  /login, then try again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return ProviderStatus.NotReady("Copilot could not start: " + ex.Message);
        }
    }

    public async Task<IReadOnlyList<AssistantModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models
            .Where(m => m.Policy == null || string.IsNullOrEmpty(m.Policy.State) ||
                        string.Equals(m.Policy.State, "enabled", StringComparison.OrdinalIgnoreCase))
            .Select(m => new AssistantModel(m.Id, string.IsNullOrEmpty(m.Name) ? m.Id : m.Name, Cost(m.Billing?.Multiplier), m.Billing?.Multiplier))
            .ToList();
    }

    public async Task<IAssistantChat> StartChatAsync(string model, string instructions, IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(tools);
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var session = await client.CreateSessionAsync(Lockdown(model, instructions, tools, _workDir), cancellationToken).ConfigureAwait(false);
        return new CopilotChat(session);
    }

    /// <summary>
    /// The session settings that keep Copilot's agent to reading the log. Public so the
    /// tests can check every switch without a Copilot subscription.
    /// </summary>
    public static SessionConfig Lockdown(string model, string instructions, IReadOnlyList<ToolDefinition> tools, string workDir)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return new SessionConfig
        {
            ClientName = "Logzilla",
            Model = model,
            Streaming = true,
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = instructions },
            Tools = tools
                .Select(t => (AIFunctionDeclaration)CopilotTool.DefineTool(t.Method,
                    new CopilotToolOptions { SkipPermission = true },
                    new AIFunctionFactoryOptions { Name = t.Name, Description = t.Description }))
                .ToList(),
            // Only Logzilla's tools exist in this session; the shell, file and web tools do not.
            AvailableTools = tools.Select(t => t.Name).ToList(),
#pragma warning disable GHCP001 // The handler's own signature requires PermissionDecision; refusing is the whole point.
            OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject(Refusal)),
#pragma warning restore GHCP001
            OnUserInputRequest = null,
            WorkingDirectory = workDir,
            McpServers = new Dictionary<string, McpServerConfig>(),
            EnableConfigDiscovery = false,
            SkipCustomInstructions = true,
            EnableOnDemandInstructionDiscovery = false,
            EnableSkills = false,
            EnableFileHooks = false,
            EnableHostGitOperations = false,
            EnableFileChangeTracking = false,
            EnableSessionStore = false,
            EnableSessionTelemetry = false,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            try
            {
                await _client.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine("Stopping Copilot: " + ex.Message);
            }
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
        _startGate.Dispose();
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine("Removing Copilot's folder: " + ex.Message);
        }
    }

    /// <summary>Starts the Copilot runtime the first time anything needs it.</summary>
    private async Task<CopilotClient> ClientAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client == null)
            {
                Directory.CreateDirectory(_workDir);
                var client = new CopilotClient(new CopilotClientOptions
                {
                    Connection = _cliPath == null ? null : RuntimeConnection.ForStdio(_cliPath),
                    WorkingDirectory = _workDir,
                    UseLoggedInUser = _token == null,
                    GitHubToken = _token,
                });
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
                _client = client;
            }
            return _client;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private static string Cost(double? multiplier) =>
        multiplier is null or 1 ? "" :
        multiplier == 0 ? "included" :
        multiplier.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";

    private sealed class CopilotChat : IAssistantChat
    {
        private readonly CopilotSession _session;
        private readonly SemaphoreSlim _turn = new(1, 1);

        public CopilotChat(CopilotSession session) => _session = session;

        public async Task SendAsync(string prompt, Action<AssistantEvent> onEvent, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(onEvent);
            await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamed = false;
            using var subscription = _session.On<SessionEvent>(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        if (!string.IsNullOrEmpty(delta.Data.DeltaContent))
                        {
                            streamed = true;
                            onEvent(new TextDelta(delta.Data.DeltaContent));
                        }
                        break;
                    case AssistantMessageEvent message when !streamed:
                        // Without streaming deltas the whole answer arrives at once.
                        if (!string.IsNullOrEmpty(message.Data.Content))
                        {
                            onEvent(new TextDelta(message.Data.Content));
                        }
                        break;
                    case AssistantUsageEvent usage:
                        onEvent(new UsageReport(usage.Data.InputTokens, usage.Data.OutputTokens, usage.Data.CacheReadTokens));
                        break;
                    case SessionUsageInfoEvent info:
                        onEvent(new UsageReport(ContextTokens: info.Data.CurrentTokens, ContextLimit: info.Data.TokenLimit));
                        break;
                    case SessionErrorEvent error:
                        done.TrySetException(new InvalidOperationException(string.IsNullOrEmpty(error.Data.Message) ? "Copilot reported an error." : error.Data.Message));
                        break;
                    case SessionIdleEvent:
                        done.TrySetResult();
                        break;
                }
            });
            try
            {
                using var abort = cancellationToken.Register(() => _ = _session.AbortAsync(CancellationToken.None));
                await _session.SendAsync(new MessageOptions { Prompt = prompt }, cancellationToken).ConfigureAwait(false);
                await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                onEvent(new Finished());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                onEvent(new Failed(ex.Message));
            }
            finally
            {
                _turn.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _turn.Dispose();
        }
    }
}
