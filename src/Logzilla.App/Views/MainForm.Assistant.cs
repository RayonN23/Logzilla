using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Logzilla.AI;
using Logzilla.AI.Auth;
using Logzilla.AI.Privacy;
using Logzilla.AI.Providers;
using Logzilla.AI.Tools;
using Logzilla.App.Controls;
using Logzilla.App.Dialogs;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Common;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.App.Views;

// The Ask tab: a conversation with a model about the open log.
//
// Off until the user turns it on, and never on where an organisation's policy says
// no. A model sees the log only through LogTools — read-only, capped, redacted — and
// each question is answered against a snapshot of what was on screen when it was
// asked, so a tool call running on a background thread never touches the UI.

internal sealed partial class MainForm
{
    private AskPanel _ask;
    private AIPolicy _aiPolicy = AIPolicy.None;
    private readonly Redactor _redactor = new();
    private readonly AIRecord _aiRecord = new();
    private readonly SwitchableContext _askContext = new();
    private readonly Dictionary<string, IAssistantProvider> _providers = new();
    private readonly Dictionary<string, IReadOnlyList<AssistantModel>> _modelCache = new();
    private LogTools _tools;
    private IAssistantChat _chat;
    private LogDocument _chatDocument;
    private CancellationTokenSource _askCts;
    private AskBlock _answer;
    private UsageTally _chatUsage = new();

    private static readonly string[] ProviderIds = ["copilot", "anthropic", "openai"];

    private AskPanel BuildAskPanel()
    {
        _aiPolicy = AIPolicy.Load();
        _redactor.Enabled = _cfg.GetBool("ai.redact", true) || _aiPolicy.RequireRedaction;
        _aiRecord.WriteToDisk = _cfg.GetBool("ai.record", false) || _aiPolicy.RequireRecord;

        _ask = new AskPanel { Name = "AskPanel", Dock = DockStyle.Fill, Visible = false };
        _ask.Transcript.Restore = _redactor.Restore;
        _ask.SendRequested += (_, text) => Ask(text);
        _ask.StopRequested += delegate { _askCts?.Cancel(); };
        _ask.NewChatRequested += delegate { NewChat(null); };
        _ask.ModelMenuRequested += delegate { ShowModelMenu(); };
        _ask.MoreMenuRequested += delegate { ShowAskMenu(); };
        _ask.ScopeToggled += delegate
        {
            _cfg.Set("ai.scope", ScopeToView ? "all" : "view");
            RefreshAsk();
        };
        _ask.Transcript.CitationClicked += (_, line) => JumpToCitation(line);
        _ask.Transcript.ApplyClicked += (_, search) => ApplySearch(search);
        _ask.Transcript.PromptPicked += (_, prompt) => Ask(prompt);
        _ask.Transcript.ActionClicked += delegate { ShowAISettings(); };

        _tools = new LogTools(_askContext, _jsonReader, _redactor);
        _tools.Activity += a => BeginInvokeSafe(() => ShowActivity(a));
        _tools.Sending += (tool, text) => _aiRecord.Add(ProviderName(CurrentProviderId), CurrentModel, "tool result: " + tool, text);
        return _ask;
    }

    private bool ScopeToView => _cfg.Get("ai.scope", "all") == "view";

    private bool AIEnabled => !_aiPolicy.Disabled && _cfg.GetBool("ai.enabled", false);

    private string CurrentProviderId
    {
        get
        {
            var id = _cfg.Get("ai.provider", "");
            return AvailableProviders().Contains(id) ? id : AvailableProviders().FirstOrDefault();
        }
    }

    private string CurrentModel => CurrentProviderId == null ? null : _cfg.Get("ai.model." + CurrentProviderId, "");

    /// <summary>Providers the policy allows and the user has connected.</summary>
    private List<string> AvailableProviders()
    {
        var list = new List<string>();
        foreach (var id in ProviderIds)
        {
            if (!_aiPolicy.Allows(id))
            {
                continue;
            }
            var connected = id switch
            {
                "copilot" => CopilotReady(out _),
                "anthropic" => !string.IsNullOrEmpty(Secret("anthropic")),
                _ => !string.IsNullOrEmpty(Secret("openai")),
            };
            if (connected)
            {
                list.Add(id);
            }
        }
        return list;
    }

    private bool CopilotReady(out string cli)
    {
        cli = CopilotCli.Resolve(_cfg.Get("ai.copilot.cli", ""), out var found);
        return found;
    }

    private static string ProviderName(string id) => id switch
    {
        "copilot" => "GitHub Copilot",
        "anthropic" => "Claude",
        "openai" => "OpenAI",
        _ => "No provider",
    };

    private string Secret(string id) => SecretStore.Unprotect(_cfg.Get("ai.key." + id, ""));

    private IAssistantProvider Provider(string id)
    {
        if (id == null)
        {
            return null;
        }
        if (_providers.TryGetValue(id, out var p))
        {
            return p;
        }
        p = id switch
        {
            "copilot" => CopilotReady(out var cli) ? new CopilotProvider(Secret("copilot"), cli) : null,
            "anthropic" => new AnthropicProvider(Secret("anthropic"), _aiPolicy.AnthropicEndpoint ?? UserEndpoint("anthropic")),
            _ => new OpenAIProvider(Secret("openai"), _aiPolicy.OpenAIEndpoint ?? UserEndpoint("openai")),
        };
        if (p != null)
        {
            _providers[id] = p;
        }
        return p;
    }

    private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>The endpoint the user set for a provider, refused if the key would not
    /// travel encrypted. Checked here as well as in the settings dialog, because the
    /// settings file can be edited by hand, and earlier versions let a crafted log
    /// write lines into it.</summary>
    private string UserEndpoint(string providerId)
    {
        var endpoint = NullIfEmpty(_cfg.Get("ai.endpoint." + providerId, ""));
        var problem = EndpointCheck.Problem(endpoint);
        return problem == null ? endpoint?.Trim() : throw new InvalidOperationException(problem + " Change it in Tools › AI settings.");
    }

    // ── Sign in with GitHub ──────────────────────────────────────────

    private static readonly System.Net.Http.HttpClient GitHubHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The GitHub app "Sign in with GitHub" uses: the organisation's, else Logzilla's own.</summary>
    private string GitHubClientId()
    {
        if (!string.IsNullOrWhiteSpace(_aiPolicy.GitHubClientId))
        {
            return _aiPolicy.GitHubClientId;
        }
        foreach (var a in typeof(MainForm).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
        {
            if (a is System.Reflection.AssemblyMetadataAttribute m && m.Key == "GitHubClientId" && !string.IsNullOrWhiteSpace(m.Value))
            {
                return m.Value;
            }
        }
        return null;
    }

    private DateTimeOffset? CopilotExpiry() =>
        DateTimeOffset.TryParse(_cfg.Get("ai.copilot.expires", ""), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null;

    private void SaveCopilotSignIn(string login, string refresh, DateTimeOffset? expires)
    {
        _cfg.Set("ai.copilot.login", login ?? "");
        _cfg.Set("ai.key.copilot-refresh", SecretStore.Protect(refresh));
        _cfg.Set("ai.copilot.expires", expires?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    /// <summary>
    /// A GitHub App's user token lasts hours, not forever. Renew it before Copilot needs
    /// it; if GitHub will not, sign the user out and say so rather than fail later.
    /// </summary>
    private async Task FreshCopilotTokenAsync()
    {
        var expires = CopilotExpiry();
        if (expires == null || DateTimeOffset.UtcNow < expires.Value - TimeSpan.FromMinutes(5))
        {
            return;
        }
        GitHubToken renewed = null;
        var clientId = GitHubClientId();
        if (clientId != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                renewed = await new GitHubSignIn(GitHubHttp, clientId)
                    .RefreshAsync(Secret("copilot-refresh"), () => DateTimeOffset.UtcNow, cts.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or GitHubSignInException)
            {
                System.Diagnostics.Debug.WriteLine("Renew GitHub sign-in: " + ex.Message);
            }
        }
        if (renewed != null)
        {
            _cfg.Set("ai.key.copilot", SecretStore.Protect(renewed.AccessToken));
            SaveCopilotSignIn(_cfg.Get("ai.copilot.login", ""), renewed.RefreshToken, renewed.ExpiresAt);
        }
        else
        {
            _cfg.Set("ai.key.copilot", "");
            SaveCopilotSignIn("", "", null);
            _ask.Transcript.Add(AskBlockKind.Notice, "Your GitHub sign-in has expired. Sign in again in AI settings.");
        }
        _cfg.Save();
        // The running Copilot session holds the old token; start the next one afresh.
        if (_providers.Remove("copilot", out var old))
        {
            _ = DisposeAllAsync([old], _chat);
            _chat = null;
        }
    }

    // ── the tab ──────────────────────────────────────────────────────

    /// <summary>Brings the tab's header, scope and empty state up to date.</summary>
    private void RefreshAsk()
    {
        if (_ask == null)
        {
            return;
        }
        var ready = AIEnabled && CurrentProviderId != null;
        _ask.SetCanAsk(ready);
        if (_aiPolicy.Disabled)
        {
            _ask.Transcript.SetEmptyState("The AI assistant is turned off",
                "Your organisation has turned it off on this computer.", null, []);
        }
        else if (!AIEnabled)
        {
            _ask.Transcript.SetEmptyState("Ask about this log",
                "Ask questions in plain words and get answers drawn from the log itself, with the lines " +
                "they rely on. It is off until you turn it on, and nothing is sent anywhere until you ask.",
                "Turn on the AI assistant…");
        }
        else if (CurrentProviderId == null)
        {
            _ask.Transcript.SetEmptyState("Connect a provider",
                "For GitHub Copilot, install the Copilot CLI and sign in to it once; AI settings shows the steps. " +
                "For Claude or OpenAI, add an API key there.", "Open AI settings…");
        }
        else
        {
            _ask.Transcript.SetEmptyState("Ask about this log",
                "Answers come from the log itself, with the lines they rely on. Click a line number to jump to it.",
                null,
                [
                    "What went wrong in this log?",
                    "What are the most common errors, and when did they start?",
                    "Summarise what happened around the first error.",
                ]);
        }
        var model = CurrentModel;
        _ask.SetModel(!AIEnabled ? "AI is off" : CurrentProviderId == null ? "No provider connected" :
            ProviderName(CurrentProviderId) + " · " + (string.IsNullOrEmpty(model) ? "choose a model" : ModelName(CurrentProviderId, model)));
        var f = F;
        var count = f == null ? "" : ScopeToView && _res != null
            ? " · " + DisplayFormatter.Number(_res.View.Count) + " lines"
            : " · " + DisplayFormatter.Number(f.N) + " lines";
        _ask.SetScope((ScopeToView ? "Looking at: what your filters show" : "Looking at: the whole log") + count);
        UpdateAskStatus();
    }

    private string ModelName(string provider, string id)
    {
        if (_modelCache.TryGetValue(provider, out var list))
        {
            var m = list.FirstOrDefault(x => x.Id == id);
            if (m != null)
            {
                return m.Name;
            }
        }
        return id;
    }

    private void ShowModelMenu()
    {
        var menu = DarkMenu();
        var available = AvailableProviders();
        foreach (var id in available)
        {
            var providerId = id;
            var item = new ToolStripMenuItem(ProviderName(id)) { BackColor = Theme.BgChrome, ForeColor = Theme.Text };
            item.DropDown.BackColor = Theme.BgChrome;
            FillModels(item, providerId);
            if (!_modelCache.ContainsKey(providerId))
            {
                _ = LoadModelsAsync(providerId, item);
            }
            menu.Items.Add(item);
        }
        if (available.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(new ToolStripMenuItem("AI settings…", null, delegate { ShowAISettings(); }) { BackColor = Theme.BgChrome, ForeColor = Theme.Text });
        menu.Show(_ask.ModelButton, new Point(0, _ask.ModelButton.Height));
    }

    private void FillModels(ToolStripMenuItem item, string providerId)
    {
        item.DropDownItems.Clear();
        if (!_modelCache.TryGetValue(providerId, out var models))
        {
            item.DropDownItems.Add(new ToolStripMenuItem("Loading models…") { Enabled = false, BackColor = Theme.BgChrome });
            return;
        }
        if (models.Count == 0)
        {
            item.DropDownItems.Add(new ToolStripMenuItem("No models available") { Enabled = false, BackColor = Theme.BgChrome });
            return;
        }
        foreach (var m in models)
        {
            var model = m;
            var label = m.Note.Length > 0 ? m.Name + "   " + m.Note : m.Name;
            var mi = new ToolStripMenuItem(label)
            {
                Checked = CurrentProviderId == providerId && CurrentModel == m.Id,
                BackColor = Theme.BgChrome,
                ForeColor = Theme.Text,
            };
            mi.Click += delegate { ChooseModel(providerId, model.Id); };
            item.DropDownItems.Add(mi);
        }
    }

    private async Task LoadModelsAsync(string providerId, ToolStripMenuItem item)
    {
        IReadOnlyList<AssistantModel> models;
        try
        {
            if (providerId == "copilot")
            {
                await FreshCopilotTokenAsync().ConfigureAwait(true);
            }
            var provider = Provider(providerId);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            models = provider == null ? [] : await provider.ListModelsAsync(cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            item.DropDownItems.Clear();
            item.DropDownItems.Add(new ToolStripMenuItem("Could not list models: " + OneLine(ex.Message)) { Enabled = false, BackColor = Theme.BgChrome });
            return;
        }
        _modelCache[providerId] = models;
        if (!item.IsDisposed)
        {
            FillModels(item, providerId);
        }
        RefreshAsk();
    }

    private void ChooseModel(string providerId, string model)
    {
        var changed = providerId != CurrentProviderId || model != CurrentModel;
        _cfg.Set("ai.provider", providerId);
        _cfg.Set("ai.model." + providerId, model);
        if (changed)
        {
            NewChat("Now answering with " + ProviderName(providerId) + " · " + ModelName(providerId, model) + ".");
        }
        RefreshAsk();
    }

    private void ShowAskMenu()
    {
        var menu = DarkMenu();
        menu.Items.Add(new ToolStripMenuItem("Usage in this conversation…", null, delegate
        {
            Prompt.Info(this, "Usage in this conversation", _chatUsage.Any
                ? _chatUsage.Details() + Environment.NewLine + "Counts come from " + ProviderName(CurrentProviderId) + " as it reports them."
                : "Nothing has been asked in this conversation yet.");
        }) { BackColor = Theme.BgChrome, ForeColor = Theme.Text });
        menu.Items.Add(new ToolStripMenuItem("What was sent…", null, delegate
        {
            Prompt.Info(this, "What was sent to the AI", _aiRecord.Text());
        }) { BackColor = Theme.BgChrome, ForeColor = Theme.Text });
        menu.Items.Add(new ToolStripMenuItem("Copy the whole conversation", null, delegate
        {
            var text = _ask.Transcript.AllText();
            if (text.Length > 0)
            {
                Clipboard.SetText(text);
            }
        }) { BackColor = Theme.BgChrome, ForeColor = Theme.Text });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("AI settings…", null, delegate { ShowAISettings(); }) { BackColor = Theme.BgChrome, ForeColor = Theme.Text });
        menu.Show(_ask.MoreButton, new Point(0, _ask.MoreButton.Height));
    }

    private static ContextMenuStrip DarkMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()),
            BackColor = Theme.BgChrome,
            ForeColor = Theme.Text,
            Font = Theme.UiBody,
        };
        return menu;
    }

    private void ShowAISettings()
    {
        _aiPolicy = AIPolicy.Load();
        var current = new AISettings
        {
            Enabled = _cfg.GetBool("ai.enabled", false),
            Redact = _cfg.GetBool("ai.redact", true),
            Record = _cfg.GetBool("ai.record", false),
            CopilotToken = Secret("copilot") ?? "",
            CopilotLogin = _cfg.Get("ai.copilot.login", ""),
            CopilotRefresh = Secret("copilot-refresh") ?? "",
            CopilotExpires = CopilotExpiry(),
            CopilotCli = _cfg.Get("ai.copilot.cli", ""),
            AnthropicKey = Secret("anthropic") ?? "",
            AnthropicEndpoint = _cfg.Get("ai.endpoint.anthropic", ""),
            OpenAIKey = Secret("openai") ?? "",
            OpenAIEndpoint = _cfg.Get("ai.endpoint.openai", ""),
        };
        var found = CopilotReady(out var cli);
        // Checking the account starts the Copilot runtime, so only once the user has opted in.
        var copilot = new CopilotSetup(found,
            cli,
            found && AIEnabled ? CheckCopilotAccountAsync : null);
        using var dialog = new AISettingsDialog(current, _aiPolicy, copilot, GitHubClientId());
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var r = dialog.Result;
        _cfg.Set("ai.enabled", r.Enabled);
        _cfg.Set("ai.redact", r.Redact);
        _cfg.Set("ai.record", r.Record);
        _cfg.Set("ai.copilot.cli", r.CopilotCli);
        _cfg.Set("ai.endpoint.anthropic", r.AnthropicEndpoint);
        _cfg.Set("ai.endpoint.openai", r.OpenAIEndpoint);
        _cfg.Set("ai.key.copilot", SecretStore.Protect(r.CopilotToken));
        SaveCopilotSignIn(r.CopilotLogin, r.CopilotRefresh, r.CopilotExpires);
        _cfg.Set("ai.key.anthropic", SecretStore.Protect(r.AnthropicKey));
        _cfg.Set("ai.key.openai", SecretStore.Protect(r.OpenAIKey));
        _cfg.Save();
        _redactor.Enabled = r.Redact || _aiPolicy.RequireRedaction;
        _aiRecord.WriteToDisk = r.Record || _aiPolicy.RequireRecord;

        // Keys or endpoints may have changed; nothing old may keep running on them.
        ResetProviders();
        NewChat(null);
        RefreshAsk();
    }

    /// <summary>Which account Copilot is signed in as, in a sentence for the settings.</summary>
    private async Task<ProviderStatus> CheckCopilotAccountAsync()
    {
        var provider = Provider("copilot");
        if (provider == null)
        {
            return ProviderStatus.NotReady("The Copilot CLI could not be found.");
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await provider.CheckAsync(cts.Token).ConfigureAwait(true);
    }

    private void ResetProviders()
    {
        _askCts?.Cancel();
        var old = _providers.Values.ToList();
        _providers.Clear();
        _modelCache.Clear();
        _ = DisposeAllAsync(old, _chat);
        _chat = null;
    }

    private static async Task DisposeAllAsync(IEnumerable<IAsyncDisposable> items, IAsyncDisposable chat)
    {
        try
        {
            if (chat != null)
            {
                await chat.DisposeAsync().ConfigureAwait(false);
            }
            foreach (var p in items)
            {
                await p.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Close AI provider: " + ex.Message);
        }
    }

    /// <summary>Called as the window closes: stop anything running, end every session.</summary>
    private void CloseAssistant()
    {
        _askCts?.Cancel();
        var pending = DisposeAllAsync(_providers.Values.ToList(), _chat);
        _providers.Clear();
        _chat = null;
        // Give the Copilot runtime a moment to shut down cleanly; never hold the exit.
        pending.Wait(TimeSpan.FromSeconds(2));
    }

    private void NewChat(string notice)
    {
        _askCts?.Cancel();
        var old = _chat;
        _chat = null;
        _chatDocument = null;
        _answer = null;
        if (old != null)
        {
            _ = DisposeAllAsync([], old);
        }
        _redactor.Clear();
        _ask.Transcript.Clear();
        _ask.SetBusy(false);
        _chatUsage = new UsageTally();
        UpdateAskStatus();
        if (notice != null)
        {
            _ask.Transcript.Add(AskBlockKind.Notice, notice);
        }
    }

    // ── asking ───────────────────────────────────────────────────────

    /// <summary>Right-click › Ask about this entry.</summary>
    private void AskAboutEntry(int line)
    {
        _sideTab = "ask";
        SyncSideTabs();
        FillSide();
        Ask("Explain entry [#" + (line + 1) + "]: what does it mean, and what led up to it?");
    }

    private async void Ask(string question)
    {
        if (string.IsNullOrWhiteSpace(question) || _askCts != null)
        {
            return;
        }
        if (!AIEnabled || CurrentProviderId == null)
        {
            RefreshAsk();
            return;
        }
        var doc = F;
        if (doc == null || doc.N == 0)
        {
            _ask.Transcript.Add(AskBlockKind.Notice, "Open a log first, then ask about it.");
            return;
        }
        var providerId = CurrentProviderId;
        var model = CurrentModel;
        if (string.IsNullOrEmpty(model))
        {
            _ask.Transcript.Add(AskBlockKind.Notice, "Choose a model first: click the model name at the top of this tab.");
            ShowModelMenu();
            return;
        }
        if (_chat != null && !ReferenceEquals(_chatDocument, doc))
        {
            NewChat("New conversation about " + doc.Name + ".");
        }

        _askContext.Current = TakeSnapshot(doc);
        _ask.Transcript.Add(AskBlockKind.User, question);
        _answer = _ask.Transcript.Add(AskBlockKind.Assistant, "");
        _answer.Streaming = true;
        _answer.Usage = new UsageTally();
        if (ModelMultiplier(providerId, model) is { } multiplier)
        {
            _answer.Usage.Add(new UsageReport(Multiplier: multiplier));
            _chatUsage.Add(new UsageReport(Multiplier: multiplier));
        }
        _ask.SetBusy(true);
        var answer = _answer;
        using var cts = new CancellationTokenSource();
        _askCts = cts;
        try
        {
            if (_chat == null)
            {
                if (providerId == "copilot")
                {
                    await FreshCopilotTokenAsync().ConfigureAwait(true);
                }
                var provider = Provider(providerId) ?? throw new InvalidOperationException(ProviderName(providerId) + " is not connected.");
                _chat = await provider.StartChatAsync(model, AssistantInstructions.For(_askContext.Current.ClockName),
                    _tools.Definitions, cts.Token).ConfigureAwait(true);
                _chatDocument = doc;
            }
            var sent = _redactor.Redact(question);
            _aiRecord.Add(ProviderName(providerId), model, "question", sent);
            await _chat.SendAsync(sent, e => BeginInvokeSafe(() => OnAssistantEvent(answer, e)), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            BeginInvokeSafe(() => Finish(answer, "Stopped."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BeginInvokeSafe(() => Fail(answer, ex.Message));
        }
        finally
        {
            _askCts = null;
            BeginInvokeSafe(() => _ask.SetBusy(false));
        }
    }

    private void OnAssistantEvent(AskBlock answer, AssistantEvent e)
    {
        switch (e)
        {
            case TextDelta d:
                _ask.Transcript.Append(answer, d.Text);
                break;
            case Finished:
                Finish(answer, null);
                break;
            case UsageReport u:
                answer.Usage?.Add(u);
                _chatUsage.Add(u);
                _ask.Transcript.Changed(answer);
                UpdateAskStatus();
                break;
            case Failed f:
                Fail(answer, f.Message);
                break;
        }
    }

    private void Finish(AskBlock answer, string notice)
    {
        answer.Streaming = false;
        if (answer.Usage is { Calls: > 0 } used)
        {
            _aiRecord.Add(ProviderName(CurrentProviderId), CurrentModel, "usage", used.Line());
        }
        if (answer.Text.Length == 0)
        {
            _ask.Transcript.Remove(answer);
        }
        else
        {
            _ask.Transcript.Changed(answer);
        }
        if (notice != null)
        {
            _ask.Transcript.Add(AskBlockKind.Notice, notice);
        }
    }

    /// <summary>The right end of the scope line: whether personal data is hidden, and what this conversation has used.</summary>
    private void UpdateAskStatus()
    {
        var text = _redactor.Enabled ? "Personal data hidden" : "Personal data not hidden";
        if (_chatUsage.Calls > 0)
        {
            text += "  ·  " + _chatUsage.Brief();
        }
        _ask.SetStatus(text, !_redactor.Enabled);
    }

    /// <summary>GitHub Copilot's premium-request multiplier for a model, when it gave one.</summary>
    private double? ModelMultiplier(string providerId, string model) =>
        _modelCache.TryGetValue(providerId, out var list) ? list.FirstOrDefault(m => m.Id == model)?.Multiplier : null;

    private void Fail(AskBlock answer, string message)
    {
        Finish(answer, null);
        _ask.Transcript.Add(AskBlockKind.Error, "The request failed: " + OneLine(message));
    }

    private void ShowActivity(ToolActivity a)
    {
        _ask.Transcript.AddBefore(_answer != null && _answer.Streaming ? _answer : null, AskBlockKind.Tool, a.Summary, a.Search);
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException ex)
        {
            System.Diagnostics.Debug.WriteLine("Assistant update after close: " + ex.Message);
        }
    }

    private void JumpToCitation(int line)
    {
        var f = F;
        if (f == null || line < 1 || line > f.N)
        {
            _st3.Text = "Line " + line + " is not in this log.";
            return;
        }
        _view.Select(line - 1, true);
        _st3.Text = _view.Selected == line - 1
            ? "Line " + DisplayFormatter.Number(line)
            : "Line " + DisplayFormatter.Number(line) + " is hidden by your filters. Reset (Ctrl+R) shows it.";
    }

    /// <summary>Turns a search the assistant ran into the user's own filters.</summary>
    private void ApplySearch(SearchRequest s)
    {
        _q.Text = s.Query;
        _regex = s.Regex;
        _case = s.MatchCase;
        var level = string.IsNullOrEmpty(s.MinLevel) ? 0 : LogTools.LevelIndex(s.MinLevel);
        if (level > 0)
        {
            _levelMask = 0;
            for (var l = level; l < Levels.Count; l++)
            {
                _levelMask |= 1 << l;
            }
        }
        else
        {
            _levelMask = 0x7F;
        }
        if (!string.IsNullOrEmpty(s.From) && !string.IsNullOrEmpty(s.To))
        {
            _updatingTimeRange = true;
            _tRange.Text = s.From + " - " + s.To;
            _updatingTimeRange = false;
            ApplyTimeRangeText();
            UpdateTimeTrigger();
        }
        SetToggleStates();
        SyncLevels();
        if (!string.IsNullOrEmpty(s.Field) && !string.IsNullOrEmpty(s.FieldValue))
        {
            AddFieldFilter(s.Field, FieldOp.Is, s.FieldValue);
        }
        StartFilter();
        _st3.Text = "Applied the assistant's search";
    }

    private static string OneLine(string s)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    // ── what the tools see ───────────────────────────────────────────

    /// <summary>Freezes what is on screen now, for the tools to read while a model answers.</summary>
    private AskSnapshot TakeSnapshot(LogDocument doc)
    {
        var reference = TimeZones.Reference(doc);
        var lo = _res != null && !double.IsNaN(_res.Lo) ? _res.Lo : double.NaN;
        var baseDay = double.IsNaN(lo) ? 0 : Math.Floor(TimeZones.Display(lo, reference, _timeMode, _localZone) / 86400000.0) * 86400000.0;
        return new AskSnapshot(doc, _res, FilterSummary(), ScopeToView, _filterEngine, _indexer, _jsonReader,
            _timeMode, _localZone, ClockName(), (long)baseDay);
    }

    private string ClockName() => _timeMode switch
    {
        TimeMode.Local => "local time (" + LocalOffsetText() + ")",
        TimeMode.Utc => "UTC",
        _ => "the time each log wrote",
    };

    private string FilterSummary()
    {
        var parts = new List<string>();
        if (_q.Text.Length > 0)
        {
            parts.Add((_regex ? "regex " : "text ") + "\"" + _q.Text + "\"");
        }
        if ((_levelMask | 1) != 0x7F)
        {
            var shown = Enumerable.Range(1, Levels.Count - 1).Where(l => ((_levelMask >> l) & 1) == 1).Select(l => Levels.Name[l]);
            parts.Add("levels " + string.Join(", ", shown));
        }
        if (!double.IsNaN(_t0))
        {
            parts.Add("time " + FormatShownRange(_t0, _t1));
        }
        foreach (var f in _fieldFilters)
        {
            parts.Add(FieldNames.Describe(f));
        }
        if (_srcMask != -1)
        {
            parts.Add("some files hidden");
        }
        return string.Join("; ", parts);
    }

    /// <summary>
    /// The context the tools hold for the whole conversation; each question swaps in a
    /// fresh <see cref="AskSnapshot"/> behind it.
    /// </summary>
    private sealed class SwitchableContext : ILogContext
    {
        public volatile AskSnapshot Current;

        private AskSnapshot S => Current ?? throw new InvalidOperationException("No log is open in Logzilla.");

        public LogDocument Document => Current?.Document;
        public FilterResult View => Current?.View;
        public string FilterSummary => S.FilterSummary;
        public bool ScopeToView => S.ScopeToView;
        public IFilterEngine Engine => S.Engine;
        public string ClockName => S.ClockName;
        public ILineReader OpenReader() => S.OpenReader();
        public string FormatTime(LogSource source, double storedMs) => S.FormatTime(source, storedMs);
        public bool TryParseTime(string text, out double storedMs) => S.TryParseTime(text, out storedMs);
        public IReadOnlyList<(string Key, int Entries)> Fields() => S.Fields();
        public IReadOnlyList<(string Value, int Count)> FieldValues(string key) => S.FieldValues(key);
    }

    /// <summary>What was on screen when a question was asked. Immutable, so any thread may read it.</summary>
    private sealed class AskSnapshot(LogDocument document, FilterResult view, string filterSummary, bool scopeToView,
        IFilterEngine engine, ILogIndexer indexer, Core.Formats.IJsonLogReader json, TimeMode mode, TimeZoneInfo local,
        string clockName, long baseDay)
    {
        private readonly object _gate = new();
        private FieldCatalog _catalog;

        public LogDocument Document { get; } = document;
        public FilterResult View { get; } = view;
        public string FilterSummary { get; } = filterSummary;
        public bool ScopeToView { get; } = scopeToView;
        public IFilterEngine Engine { get; } = engine;
        public string ClockName { get; } = clockName;

        public ILineReader OpenReader() => indexer.OpenReader(Document, 1 << 20);

        public string FormatTime(LogSource source, double storedMs) =>
            DisplayFormatter.Time(TimeZones.Display(storedMs, source, mode, local));

        public bool TryParseTime(string text, out double storedMs)
        {
            storedMs = double.NaN;
            text = (text ?? "").Trim();
            double shown;
            if (TimeRangeParser.TryParseDateTimeMs(text, out var full))
            {
                shown = full;
            }
            else if (TimeRangeParser.TryParseTimeOfDay(text, out var h, out var m, out var s))
            {
                shown = baseDay + h * 3600000.0 + m * 60000.0 + s * 1000.0;
            }
            else
            {
                return false;
            }
            storedMs = TimeZones.Stored(shown, TimeZones.Reference(Document), mode, local);
            return true;
        }

        public List<(string Key, int Entries)> Fields() => Catalog().Keys();

        public List<(string Value, int Count)> FieldValues(string key) => Catalog().ValuesOf(key);

        private FieldCatalog Catalog()
        {
            lock (_gate)
            {
                if (_catalog == null)
                {
                    using var reader = OpenReader();
                    _catalog = FieldCatalog.Build(Document, reader, json);
                }
                return _catalog;
            }
        }
    }
}
