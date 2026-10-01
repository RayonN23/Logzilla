using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Logzilla.AI.Auth;
using Logzilla.App.Infrastructure;

namespace Logzilla.App.Dialogs;

/// <summary>
/// Sign in with GitHub. Shows a short code, sends the user to github.com in their own
/// browser to approve it, and waits. The user's password never passes through
/// Logzilla, and the dialog can be cancelled at any point.
/// </summary>
internal sealed class GitHubSignInDialog : Form
{
    /// <summary>Just enough to show who signed in; Copilot's access comes from the user's own plan.</summary>
    private const string Scope = "read:user";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly GitHubSignIn _signIn;
    private readonly CancellationTokenSource _cts = new();
    private readonly Label _code;
    private readonly Label _status;
    private readonly Button _open;
    private DeviceCode _device;

    public GitHubSignInDialog(string clientId)
    {
        _signIn = new GitHubSignIn(Http, clientId);
        Text = "Sign in with GitHub";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        BackColor = Theme.BgChrome;
        ForeColor = Theme.Text;
        Font = Theme.UiBody;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Dpi.S(440), Dpi.S(300));

        var pad = Dpi.S(24);
        var width = ClientSize.Width - pad * 2;
        var intro = new Label
        {
            Text = "Copy this code, then paste it on GitHub to let Logzilla use your Copilot subscription.",
            Location = new Point(pad, pad), Size = new Size(width, Dpi.S(40)), ForeColor = Theme.Text2,
        };
        _code = new Label
        {
            Text = "…", Location = new Point(pad, intro.Bottom + Dpi.S(8)), Size = new Size(width, Dpi.S(56)),
            Font = new Font(Theme.MonoBody.FontFamily, Theme.MonoBody.Size * 2f, FontStyle.Bold),
            ForeColor = Theme.Accent, TextAlign = ContentAlignment.MiddleCenter, BackColor = Theme.BgData,
            AccessibleName = "Your sign-in code",
        };
        _open = new Button
        {
            Text = "Copy code and open GitHub", Enabled = false,
            Location = new Point(pad, _code.Bottom + Dpi.S(16)), Size = new Size(width, Dpi.S(34)),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.BgControl, ForeColor = Theme.Text,
            FlatAppearance = { BorderColor = Theme.AccentEdge },
        };
        _open.Click += delegate { CopyAndOpen(); };
        _status = new Label
        {
            Text = "Asking GitHub for a code…", Location = new Point(pad, _open.Bottom + Dpi.S(12)),
            Size = new Size(width, Dpi.S(40)), ForeColor = Theme.Text3, Font = Theme.UiTag,
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Size = new Size(Dpi.S(88), Dpi.S(30)), Location = new Point(ClientSize.Width - pad - Dpi.S(88), ClientSize.Height - Dpi.S(46)),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.BgControl, ForeColor = Theme.Text,
            FlatAppearance = { BorderColor = Theme.LineControl },
        };
        CancelButton = cancel;
        Controls.AddRange([intro, _code, _open, _status, cancel]);
        Shown += async delegate { await RunAsync().ConfigureAwait(true); };
        FormClosed += delegate { _cts.Cancel(); };
    }

    /// <summary>The token, once the user has approved. Null otherwise.</summary>
    public GitHubToken Token { get; private set; }

    /// <summary>The GitHub account that signed in.</summary>
    public string Login { get; private set; }

    private async Task RunAsync()
    {
        try
        {
            _device = await _signIn.StartAsync(Scope, _cts.Token).ConfigureAwait(true);
            _code.Text = _device.UserCode;
            _open.Enabled = true;
            _open.Focus();
            _status.Text = "Waiting for you to approve Logzilla on GitHub…";
            var token = await _signIn.WaitAsync(_device, () => DateTimeOffset.UtcNow, _cts.Token).ConfigureAwait(true);
            _status.Text = "Approved. Checking your account…";
            Login = await _signIn.LoginAsync(token.AccessToken, _cts.Token).ConfigureAwait(true);
            Token = token;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            // Closed or cancelled; nothing to report.
        }
        catch (Exception ex) when (ex is GitHubSignInException or HttpRequestException)
        {
            if (!IsDisposed)
            {
                _status.Text = ex is HttpRequestException ? "Could not reach GitHub: " + ex.Message : ex.Message;
                _status.ForeColor = Theme.SevColor[5];
                _open.Enabled = false;
            }
        }
    }

    private void CopyAndOpen()
    {
        if (_device == null)
        {
            return;
        }
        Clipboard.SetText(_device.UserCode);
        try
        {
            Process.Start(new ProcessStartInfo(_device.VerificationUri.ToString()) { UseShellExecute = true });
            _status.Text = "Code copied. Paste it on the GitHub page that just opened, then approve.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _status.Text = "Code copied. Open " + _device.VerificationUri + " and paste it there.";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
            _code.Font.Dispose();
        }
        base.Dispose(disposing);
    }
}
