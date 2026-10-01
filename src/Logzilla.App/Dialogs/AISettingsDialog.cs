using System;
using System.Drawing;
using System.Windows.Forms;
using Logzilla.AI.Providers;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Dialogs;

/// <summary>The choices the AI settings dialog edits. Keys are plain text here; the caller encrypts them.</summary>
internal sealed class AISettings
{
    public bool Enabled;
    public bool Redact = true;
    public bool Record;
    public string CopilotToken = "";

    /// <summary>Set when the token came from Sign in with GitHub rather than being pasted.</summary>
    public string CopilotLogin = "";
    public string CopilotRefresh = "";
    public DateTimeOffset? CopilotExpires;
    public string CopilotCli = "";
    public string AnthropicKey = "";
    public string AnthropicEndpoint = "";
    public string OpenAIKey = "";
    public string OpenAIEndpoint = "";
}

/// <summary>
/// Where the Copilot CLI is (null for the runtime built into Logzilla) and, once known, who it is signed in as. <see cref="CheckAccount"/>
/// is null when checking would start Copilot before the user has turned the assistant on.
/// </summary>
internal sealed record CopilotSetup(bool Found, string Where, Func<System.Threading.Tasks.Task<ProviderStatus>> CheckAccount);

/// <summary>
/// Turning the assistant on, and connecting the providers. It says plainly what
/// leaves the machine before anyone switches it on, and shows where an organisation
/// has decided for the user rather than letting them change it.
/// </summary>
internal sealed class AISettingsDialog : Form
{
    private readonly AIPolicy _policy;
    private readonly CheckBox _enabled;
    private readonly CheckBox _redact;
    private readonly CheckBox _record;
    private readonly TextBox _copilotToken;
    private readonly Label _copilotAccount;
    private readonly Button _copilotSignIn;
    private readonly string _clientId;
    private string _login;
    private string _refresh;
    private DateTimeOffset? _expires;
    private string _signedInToken;

    /// <summary>The account the Copilot CLI is signed in to, once the check has said so.
    /// Signed in there, Copilot already works, and offering Sign in with GitHub as well
    /// only ran the whole flow a second time.</summary>
    private string _cliLogin;

    /// <summary>True while the account check is running, so the button cannot start a
    /// sign-in in the moment before the dialog knows one is not needed.</summary>
    private bool _checkingAccount;
    private readonly TextBox _copilotCli;
    private readonly TextBox _anthropicKey;
    private readonly TextBox _anthropicEndpoint;
    private readonly TextBox _openaiKey;
    private readonly TextBox _openaiEndpoint;
    private readonly FlowLayoutPanel _body;

    public AISettingsDialog(AISettings current, AIPolicy policy, CopilotSetup copilot, string gitHubClientId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        _clientId = gitHubClientId;
        _login = current.CopilotLogin;
        _refresh = current.CopilotRefresh;
        _expires = current.CopilotExpires;
        _signedInToken = string.IsNullOrEmpty(current.CopilotLogin) ? "" : current.CopilotToken;
        Text = "AI settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.BgChrome;
        ForeColor = Theme.Text;
        Font = Theme.UiBody;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Dpi.S(560), Dpi.S(640));

        _body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(Dpi.S(20), Dpi.S(16), Dpi.S(20), Dpi.S(8)),
            BackColor = Theme.BgChrome,
        };

        if (policy.Managed)
        {
            Note(policy.Disabled
                ? "Your organisation has turned the AI assistant off."
                : "Some of these settings are set by your organisation and cannot be changed here.", Theme.SevColor[4]);
        }

        _enabled = Check("Turn on the AI assistant", current.Enabled && !policy.Disabled, !policy.Disabled);
        Note("When you ask a question, the parts of your log needed to answer it are sent to the " +
             "AI provider you choose — never the whole file at once. Nothing is sent until you ask. " +
             "Each provider's own terms decide how long it keeps what it receives.", Theme.Text3);

        Heading("GitHub Copilot");
        if (policy.Allows("copilot"))
        {
            CopilotHelp(copilot);
            _copilotAccount = new Label { AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(4)) };
            _body.Controls.Add(_copilotAccount);
            _copilotSignIn = new Button
            {
                Width = Dpi.S(200), Height = Dpi.S(32), FlatStyle = FlatStyle.Flat, BackColor = Theme.BgControl, ForeColor = Theme.Text,
                FlatAppearance = { BorderColor = Theme.AccentEdge }, Margin = new Padding(0, 0, 0, Dpi.S(8)),
            };
            _copilotSignIn.Click += delegate { SignInOrOut(); };
            _body.Controls.Add(_copilotSignIn);
            _copilotToken = Field("Or paste a GitHub token (fine-grained, github_pat_…)", string.IsNullOrEmpty(current.CopilotLogin) ? current.CopilotToken : "", secret: true);
            SyncAccount();
            _copilotCli = Field("Copilot CLI location (optional)", current.CopilotCli, secret: false);
        }
        else
        {
            Note("Not allowed by your organisation.", Theme.TextDim);
        }

        Heading("Claude (Anthropic)");
        if (policy.Allows("anthropic"))
        {
            _anthropicKey = Field("API key", current.AnthropicKey, secret: true);
            _anthropicEndpoint = Field("Endpoint (optional, for your organisation's gateway)",
                policy.AnthropicEndpoint ?? current.AnthropicEndpoint, secret: false, locked: policy.AnthropicEndpoint != null);
        }
        else
        {
            Note("Not allowed by your organisation.", Theme.TextDim);
        }

        Heading("OpenAI");
        if (policy.Allows("openai"))
        {
            _openaiKey = Field("API key", current.OpenAIKey, secret: true);
            _openaiEndpoint = Field("Endpoint (optional, e.g. Azure OpenAI)",
                policy.OpenAIEndpoint ?? current.OpenAIEndpoint, secret: false, locked: policy.OpenAIEndpoint != null);
        }
        else
        {
            Note("Not allowed by your organisation.", Theme.TextDim);
        }

        Heading("Privacy");
        _redact = Check("Hide personal data before sending (recommended)", current.Redact || policy.RequireRedaction, !policy.RequireRedaction);
        Note("Email and IP addresses, IDs, passwords, keys and tokens are replaced with placeholders such " +
             "as «IP_1». You still see the real values; the AI never does.", Theme.Text3);
        _record = Check("Keep a record of everything sent", current.Record || policy.RequireRecord, !policy.RequireRecord);
        Note("Saved on this computer in " + AIRecord.Folder + ".", Theme.Text3);
        Note("API keys and tokens are encrypted for your Windows account and never saved in setup files.", Theme.TextDim);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = Dpi.S(52),
            Padding = new Padding(Dpi.S(16), Dpi.S(10), Dpi.S(16), Dpi.S(10)),
            BackColor = Theme.BgChrome,
        };
        var ok = Button("Save", DialogResult.OK);
        var cancel = Button("Cancel", DialogResult.Cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_body);
        Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Line });
        Controls.Add(buttons);
        Icon = null;
        ShowIcon = false;
    }

    /// <summary>What was chosen. Only meaningful after the dialog returns OK.</summary>
    public AISettings Result => new()
    {
        Enabled = _enabled.Checked,
        Redact = _redact.Checked,
        Record = _record.Checked,
        CopilotToken = !string.IsNullOrEmpty(_signedInToken) ? _signedInToken : _copilotToken?.Text.Trim() ?? "",
        CopilotLogin = !string.IsNullOrEmpty(_signedInToken) ? _login ?? "" : "",
        CopilotRefresh = !string.IsNullOrEmpty(_signedInToken) ? _refresh ?? "" : "",
        CopilotExpires = !string.IsNullOrEmpty(_signedInToken) ? _expires : null,
        CopilotCli = _copilotCli?.Text.Trim() ?? "",
        AnthropicKey = _anthropicKey?.Text.Trim() ?? "",
        AnthropicEndpoint = _policy.AnthropicEndpoint == null ? _anthropicEndpoint?.Text.Trim() ?? "" : "",
        OpenAIKey = _openaiKey?.Text.Trim() ?? "",
        OpenAIEndpoint = _policy.OpenAIEndpoint == null ? _openaiEndpoint?.Text.Trim() ?? "" : "",
    };

    /// <summary>
    /// How to get Copilot working, in the order it has to happen. Commands sit in
    /// boxes that can be selected and copied; once the CLI is found, the steps give
    /// way to where it is and which account it uses.
    /// </summary>
    private void CopilotHelp(CopilotSetup copilot)
    {
        if (copilot == null || !copilot.Found)
        {
            Note("Copilot uses your own GitHub Copilot subscription through the GitHub Copilot CLI. To set it up:", Theme.Text2);
            Note("1.  Install the CLI, from a terminal:", Theme.Text2);
            Command("winget install GitHub.Copilot");
            Note("     or, with Node.js:  npm install -g @github/copilot", Theme.Text3);
            Note("2.  Sign in once: use Sign in with GitHub below, or run  copilot  in a terminal and type  /login.", Theme.Text2);
            Note("3.  Restart Logzilla. It finds the CLI by itself, and GitHub Copilot appears in the Ask tab's model list.", Theme.Text2);
            Link("GitHub's guide to installing the Copilot CLI",
                "https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli");
            return;
        }
        Note(copilot.Where == null ? "Using the Copilot runtime built into Logzilla." : "Using the GitHub Copilot CLI at:", Theme.Text2);
        if (copilot.Where != null)
        {
            Command(copilot.Where);
        }
        Note("Unless you sign in with GitHub below, Copilot uses the account the CLI is signed in to. To switch that account, run  copilot  in a terminal and type  /login.", Theme.Text3);
        var account = new Label
        {
            AutoSize = false, Width = Inner, Height = Dpi.S(22), ForeColor = Theme.Text3, Font = Theme.UiTag,
            Margin = new Padding(0, 0, 0, Dpi.S(6)),
            Text = copilot.CheckAccount == null ? "Turn on the assistant and save to check the account." : "Checking which account Copilot uses…",
        };
        _body.Controls.Add(account);
        if (copilot.CheckAccount != null)
        {
            _checkingAccount = true;
            Shown += async delegate
            {
                ProviderStatus result;
                try
                {
                    result = await copilot.CheckAccount().ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    result = ProviderStatus.NotReady("Could not check: " + ex.Message);
                }
                if (!account.IsDisposed)
                {
                    account.Text = result.Message;
                    account.ForeColor = result.Ready ? Theme.Ok : Theme.SevColor[4];
                    _checkingAccount = false;
                    _cliLogin = result.Ready ? result.Account : null;
                    SyncAccount();
                }
            };
        }
    }

    /// <summary>A command to type, in a box it can be copied from.</summary>
    private void Command(string text)
    {
        _body.Controls.Add(new TextBox
        {
            Text = text, ReadOnly = true, BorderStyle = BorderStyle.None, Font = Theme.MonoSmall,
            BackColor = Theme.BgData, ForeColor = Theme.TextBright, Width = Inner, Margin = new Padding(Dpi.S(18), Dpi.S(2), 0, Dpi.S(6)),
            AccessibleName = "Command: " + text,
        });
    }

    private void Link(string text, string url)
    {
        var link = new LinkLabel
        {
            Text = text, AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Accent,
            Font = Theme.UiTag, Margin = new Padding(0, 0, 0, Dpi.S(8)),
        };
        link.LinkClicked += delegate
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Open link: " + ex.Message);
            }
        };
        _body.Controls.Add(link);
    }

    /// <summary>Shows who is signed in, and offers the one action that makes sense now.</summary>
    private void SyncAccount()
    {
        if (_copilotAccount == null)
        {
            return;
        }
        var signedIn = !string.IsNullOrEmpty(_signedInToken);
        // Signed in through the CLI, Copilot already has an account: there is nothing to
        // sign in to here, so the button goes rather than offering the flow again.
        var viaCli = !signedIn && !string.IsNullOrEmpty(_cliLogin);
        var canSignIn = !string.IsNullOrWhiteSpace(_clientId);
        _copilotAccount.Text = signedIn
            ? "Signed in to GitHub as " + (string.IsNullOrEmpty(_login) ? "your account" : _login)
            : viaCli
                ? "Signed in to GitHub as " + _cliLogin + " through the Copilot CLI."
                : canSignIn
                    ? "Not signed in with GitHub."
                    : "Sign in with GitHub is not set up in this build of Logzilla.";
        _copilotAccount.ForeColor = signedIn || viaCli ? Theme.Text : Theme.Text2;
        _copilotAccount.Visible = signedIn || viaCli || canSignIn;
        _copilotSignIn.Visible = signedIn || (!viaCli && canSignIn);
        _copilotSignIn.Text = signedIn ? "Sign out" : "Sign in with GitHub";
        _copilotSignIn.Enabled = signedIn || (canSignIn && !_checkingAccount);
        _copilotToken.Enabled = !signedIn;
    }

    private void SignInOrOut()
    {
        if (string.IsNullOrEmpty(_signedInToken) && (!string.IsNullOrEmpty(_cliLogin) || _checkingAccount))
        {
            return;
        }
        if (!string.IsNullOrEmpty(_signedInToken))
        {
            _signedInToken = "";
            _login = "";
            _refresh = "";
            _expires = null;
            SyncAccount();
            return;
        }
        using var dialog = new GitHubSignInDialog(_clientId);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Token != null)
        {
            _signedInToken = dialog.Token.AccessToken;
            _refresh = dialog.Token.RefreshToken ?? "";
            _expires = dialog.Token.ExpiresAt;
            _login = dialog.Login ?? "";
            _copilotToken.Text = "";
            _enabled.Checked = true;   // signing in is a clear sign they want to use it
        }
        SyncAccount();
    }

    private int Inner =>ClientSize.Width - _body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;

    private void Heading(string text)
    {
        _body.Controls.Add(new Label
        {
            Text = text, AutoSize = false, Width = Inner, Height = Dpi.S(34),
            Font = Theme.UiBodyBold, ForeColor = Theme.Text, TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(0, Dpi.S(8), 0, Dpi.S(4)),
        });
    }

    private void Note(string text, Color color)
    {
        var label = new Label { Text = text, ForeColor = color, Font = Theme.UiTag, AutoSize = false, Width = Inner, Margin = new Padding(0, 0, 0, Dpi.S(6)) };
        label.Height = TextRenderer.MeasureText(text, label.Font, new Size(Inner, int.MaxValue), TextFormatFlags.WordBreak).Height + Dpi.S(2);
        _body.Controls.Add(label);
    }

    private CheckBox Check(string text, bool value, bool enabled)
    {
        var c = new CheckBox
        {
            Text = text, Checked = value, Enabled = enabled, AutoSize = true, ForeColor = Theme.Text,
            Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(2)), FlatStyle = FlatStyle.System,
        };
        _body.Controls.Add(c);
        return c;
    }

    private TextBox Field(string label, string value, bool secret, bool locked = false)
    {
        _body.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Text2, Margin = new Padding(0, Dpi.S(4), 0, Dpi.S(2)) });
        var box = new TextBox
        {
            Text = value ?? "", Width = Inner, UseSystemPasswordChar = secret, ReadOnly = locked,
            BackColor = Theme.BgData, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, Dpi.S(6)), AccessibleName = label,
        };
        _body.Controls.Add(box);
        return box;
    }

    private static Button Button(string text, DialogResult result) => new()
    {
        Text = text, DialogResult = result, Width = Dpi.S(88), Height = Dpi.S(30),
        FlatStyle = FlatStyle.Flat, BackColor = Theme.BgControl, ForeColor = Theme.Text,
        FlatAppearance = { BorderColor = Theme.LineControl }, Margin = new Padding(Dpi.S(8), 0, 0, 0),
    };
}
