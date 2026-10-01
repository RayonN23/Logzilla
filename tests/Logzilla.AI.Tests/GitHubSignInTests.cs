using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Logzilla.AI.Auth;
using NUnit.Framework;

namespace Logzilla.AI.Tests;

/// <summary>
/// Sign in with GitHub, by the device flow, against a scripted GitHub: the code is
/// shown, polling respects GitHub's pace, and every way it can end says why.
/// </summary>
[TestFixture]
public sealed class GitHubSignInTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private const string CodeReply =
        "{\"device_code\":\"dev-123\",\"user_code\":\"WDJB-MJHT\",\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":900,\"interval\":5}";

    [Test]
    public async Task StartingGivesTheCodeToShowAndWhereToEnterIt()
    {
        var github = new ScriptedGitHub(CodeReply);
        var signIn = new GitHubSignIn(new HttpClient(github), "Iv1.test");

        var code = await signIn.StartAsync("read:user", CancellationToken.None);

        Assert.That(code.UserCode, Is.EqualTo("WDJB-MJHT"));
        Assert.That(code.VerificationUri.ToString(), Is.EqualTo("https://github.com/login/device"));
        Assert.That(code.Interval, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(github.Requests.Single(), Does.Contain("client_id=Iv1.test"));
        Assert.That(github.Requests.Single(), Does.Contain("scope=read%3Auser"));
    }

    [Test]
    public async Task WaitingPollsUntilApprovedAndSlowsDownWhenAsked()
    {
        var github = new ScriptedGitHub(
            "{\"error\":\"authorization_pending\"}",
            "{\"error\":\"slow_down\",\"interval\":10}",
            "{\"access_token\":\"gho_abc\",\"token_type\":\"bearer\",\"scope\":\"read:user\"}");
        var waits = new List<TimeSpan>();
        var signIn = new GitHubSignIn(new HttpClient(github), "Iv1.test", (d, _) => { waits.Add(d); return Task.CompletedTask; });

        var token = await signIn.WaitAsync(Code(), () => T0, CancellationToken.None);

        Assert.That(token.AccessToken, Is.EqualTo("gho_abc"));
        Assert.That(token.ExpiresAt, Is.Null, "An OAuth App token does not expire.");
        Assert.That(waits.Select(w => w.TotalSeconds), Is.EqualTo(new double[] { 5, 5, 10 }), "After slow_down, GitHub's longer interval is kept.");
        Assert.That(github.Requests, Has.All.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code"));
    }

    [Test]
    public async Task AGitHubAppTokenCarriesItsExpiryAndRefreshToken()
    {
        var github = new ScriptedGitHub("{\"access_token\":\"ghu_abc\",\"expires_in\":28800,\"refresh_token\":\"ghr_def\"}");
        var signIn = new GitHubSignIn(new HttpClient(github), "Iv1.test", (_, _) => Task.CompletedTask);

        var token = await signIn.WaitAsync(Code(), () => T0, CancellationToken.None);

        Assert.That(token.RefreshToken, Is.EqualTo("ghr_def"));
        Assert.That(token.ExpiresAt, Is.EqualTo(T0.AddHours(8)));
        Assert.That(token.Expired(T0.AddHours(7)), Is.False);
        Assert.That(token.Expired(T0.AddHours(8).AddMinutes(-1)), Is.True, "Renewed a little early rather than a little late.");
    }

    [Test]
    public void DecliningOnGitHubSaysSo()
    {
        var signIn = new GitHubSignIn(new HttpClient(new ScriptedGitHub("{\"error\":\"access_denied\"}")), "Iv1.test", (_, _) => Task.CompletedTask);

        var ex = Assert.ThrowsAsync<GitHubSignInException>(() => signIn.WaitAsync(Code(), () => T0, CancellationToken.None));
        Assert.That(ex?.Message, Is.EqualTo("Sign-in was cancelled on GitHub."));
    }

    [Test]
    public void ACodeThatRunsOutSaysSo()
    {
        var now = T0;
        var github = new ScriptedGitHub(Enumerable.Repeat("{\"error\":\"authorization_pending\"}", 400).ToArray());
        var signIn = new GitHubSignIn(new HttpClient(github), "Iv1.test", (d, _) => { now += d; return Task.CompletedTask; });

        var ex = Assert.ThrowsAsync<GitHubSignInException>(() => signIn.WaitAsync(Code(), () => now, CancellationToken.None));
        Assert.That(ex?.Message, Does.Contain("expired"));
    }

    [Test]
    public void DeviceFlowSwitchedOffOnTheAppIsExplained()
    {
        var signIn = new GitHubSignIn(new HttpClient(new ScriptedGitHub("{\"error\":\"device_flow_disabled\"}")), "Iv1.test");

        var ex = Assert.ThrowsAsync<GitHubSignInException>(() => signIn.StartAsync("", CancellationToken.None));
        Assert.That(ex?.Message, Does.Contain("Device Flow"));
    }

    [Test]
    public void AnUnknownAppIdIsExplained()
    {
        // What github.com/login/device/code really answers for a Client ID it does not know.
        var signIn = new GitHubSignIn(new HttpClient(new ScriptedGitHub("{\"error\":\"Not Found\"}")), "Ov23liUnknown");

        var ex = Assert.ThrowsAsync<GitHubSignInException>(() => signIn.StartAsync("read:user", CancellationToken.None));
        Assert.That(ex?.Message, Is.EqualTo("Logzilla's GitHub app ID is not recognised by GitHub."));
    }

    [Test]
    public async Task RefreshRenewsAnExpiringToken()
    {
        var github = new ScriptedGitHub("{\"access_token\":\"ghu_new\",\"expires_in\":28800,\"refresh_token\":\"ghr_new\"}");
        var signIn = new GitHubSignIn(new HttpClient(github), "Iv1.test");

        var token = await signIn.RefreshAsync("ghr_old", () => T0, CancellationToken.None);

        Assert.That(token.AccessToken, Is.EqualTo("ghu_new"));
        Assert.That(github.Requests.Single(), Does.Contain("grant_type=refresh_token"));
        Assert.That(github.Requests.Single(), Does.Contain("refresh_token=ghr_old"));
        Assert.That(github.Requests.Single(), Does.Not.Contain("client_secret"), "A desktop app has no secret to send.");
    }

    [Test]
    public async Task ARefusedRefreshReturnsNothing()
    {
        var signIn = new GitHubSignIn(new HttpClient(new ScriptedGitHub("{\"error\":\"bad_refresh_token\"}")), "Iv1.test");

        Assert.That(await signIn.RefreshAsync("ghr_old", () => T0, CancellationToken.None), Is.Null);
        Assert.That(await signIn.RefreshAsync("", () => T0, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task TheSignedInAccountIsLookedUp()
    {
        var github = new ScriptedGitHub("{\"login\":\"octocat\",\"id\":1}");
        var signIn = new GitHubSignIn(new HttpClient(github), "Iv1.test");

        Assert.That(await signIn.LoginAsync("gho_abc", CancellationToken.None), Is.EqualTo("octocat"));
        Assert.That(github.Authorizations.Single(), Is.EqualTo("Bearer gho_abc"));
    }

    [Test]
    public void AnAppIdIsRequired()
    {
        Assert.Throws<ArgumentException>(() => _ = new GitHubSignIn(new HttpClient(), " "));
    }

    private static DeviceCode Code() =>
        new("dev-123", "WDJB-MJHT", new Uri("https://github.com/login/device"), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

    /// <summary>Answers each request with the next scripted reply, and remembers what was asked.</summary>
    private sealed class ScriptedGitHub(params string[] replies) : HttpMessageHandler
    {
        private int _next;

        public List<string> Requests { get; } = new();

        public List<string> Authorizations { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Content == null ? request.RequestUri?.ToString() : await request.Content.ReadAsStringAsync(cancellationToken));
            if (request.Headers.Authorization != null)
            {
                Authorizations.Add(request.Headers.Authorization.ToString());
            }
            var body = replies[Math.Min(_next++, replies.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
