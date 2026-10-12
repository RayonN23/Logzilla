using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Logzilla.AI.Auth;

/// <summary>The code the user types on github.com, and how long they have to do it.</summary>
public sealed record DeviceCode(string Code, string UserCode, Uri VerificationUri, TimeSpan ExpiresIn, TimeSpan Interval);

/// <summary>
/// What signing in produced. <see cref="ExpiresAt"/> and <see cref="RefreshToken"/> are set
/// only for a GitHub App with expiring tokens; an OAuth App's token does not expire.
/// </summary>
public sealed record GitHubToken(string AccessToken, string RefreshToken, DateTimeOffset? ExpiresAt)
{
    public bool Expired(DateTimeOffset now) => ExpiresAt is { } at && now >= at - TimeSpan.FromMinutes(5);
}

/// <summary>Why signing in stopped without a token, in words fit for the user.</summary>
public sealed class GitHubSignInException(string message) : Exception(message);

/// <summary>
/// "Sign in with GitHub" by the OAuth device flow, the way desktop apps are meant to:
/// Logzilla shows a short code, the user approves it on github.com in their own
/// browser, and Logzilla receives a token. No password and no client secret ever pass
/// through the app. The token is a user token for Logzilla's registered GitHub OAuth
/// App or GitHub App, which the Copilot SDK accepts and bills to the user's own plan.
/// </summary>
public sealed class GitHubSignIn
{
    private const string Host = "https://github.com";
    private const string Api = "https://api.github.com";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="http">The client to send requests with.</param>
    /// <param name="clientId">The Client ID of Logzilla's registered GitHub app; public, not a secret.</param>
    /// <param name="delay">How to wait between polls; replaced in tests.</param>
    public GitHubSignIn(HttpClient http, string clientId, Func<TimeSpan, CancellationToken, Task> delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        _http = http;
        _clientId = clientId.Trim();
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Asks GitHub for a code for the user to approve.</summary>
    public async Task<DeviceCode> StartAsync(string scope, CancellationToken cancellationToken)
    {
        var json = await PostAsync(Host + "/login/device/code", new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = scope ?? "",
        }, cancellationToken).ConfigureAwait(false);
        ThrowIfError(json);
        // The address is handed to the shell to open, so only a web page will do: a
        // file path or another scheme here would run whatever it names.
        if (!Uri.TryCreate(Text(json, "verification_uri"), UriKind.Absolute, out var verify) || verify.Scheme != Uri.UriSchemeHttps)
        {
            throw new GitHubSignInException("GitHub sent an unexpected sign-in address.");
        }
        return new DeviceCode(
            Text(json, "device_code"),
            Text(json, "user_code"),
            verify,
            TimeSpan.FromSeconds(Number(json, "expires_in", 900)),
            TimeSpan.FromSeconds(Math.Max(1, Number(json, "interval", 5))));
    }

    /// <summary>
    /// Waits for the user to approve the code, asking GitHub no faster than it allows.
    /// Throws <see cref="GitHubSignInException"/> if they decline or the code runs out.
    /// </summary>
    public async Task<GitHubToken> WaitAsync(DeviceCode code, Func<DateTimeOffset> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(now);
        var deadline = now() + code.ExpiresIn;
        var interval = code.Interval;
        while (true)
        {
            await _delay(interval, cancellationToken).ConfigureAwait(false);
            if (now() > deadline)
            {
                throw new GitHubSignInException("The code expired before it was approved. Try signing in again.");
            }
            var json = await PostAsync(Host + "/login/oauth/access_token", new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["device_code"] = code.Code,
                ["grant_type"] = DeviceGrant,
            }, cancellationToken).ConfigureAwait(false);
            switch (ErrorOf(json))
            {
                case null:
                    return TokenFrom(json, now());
                case "authorization_pending":
                    continue;
                case "slow_down":
                    // GitHub asks for a longer wait and says how long; never poll faster.
                    interval = TimeSpan.FromSeconds(Number(json, "interval", (int)interval.TotalSeconds + 5));
                    continue;
                default:
                    ThrowIfError(json);
                    break;
            }
        }
    }

    /// <summary>Renews an expiring GitHub App token. Null if GitHub will not renew it.</summary>
    public async Task<GitHubToken> RefreshAsync(string refreshToken, Func<DateTimeOffset> now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(now);
        if (string.IsNullOrEmpty(refreshToken))
        {
            return null;
        }
        var json = await PostAsync(Host + "/login/oauth/access_token", new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, cancellationToken).ConfigureAwait(false);
        return ErrorOf(json) == null ? TokenFrom(json, now()) : null;
    }

    /// <summary>The account a token belongs to, to show who is signed in.</summary>
    public async Task<string> LoginAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Logzilla", "3"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubSignInException("GitHub did not accept the sign-in (" + (int)response.StatusCode + ").");
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
    }

    private async Task<Dictionary<string, JsonElement>> PostAsync(string url, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new FormUrlEncodedContent(form);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Logzilla", "3"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
        }
        catch (JsonException)
        {
            throw new GitHubSignInException("GitHub sent an unexpected reply (" + (int)response.StatusCode + ").");
        }
    }

    private static GitHubToken TokenFrom(Dictionary<string, JsonElement> json, DateTimeOffset now)
    {
        var token = Text(json, "access_token");
        if (string.IsNullOrEmpty(token))
        {
            throw new GitHubSignInException("GitHub did not send a token.");
        }
        var expires = Number(json, "expires_in", 0);
        return new GitHubToken(token, Text(json, "refresh_token"), expires > 0 ? now + TimeSpan.FromSeconds(expires) : null);
    }

    private static string ErrorOf(Dictionary<string, JsonElement> json) => Text(json, "error");

    private static void ThrowIfError(Dictionary<string, JsonElement> json)
    {
        var error = ErrorOf(json);
        if (error == null)
        {
            return;
        }
        throw new GitHubSignInException(error switch
        {
            "access_denied" => "Sign-in was cancelled on GitHub.",
            "expired_token" => "The code expired before it was approved. Try signing in again.",
            "device_flow_disabled" => "Sign in with GitHub is not switched on for Logzilla's GitHub app (enable Device Flow in its settings).",
            // GitHub answers an unknown Client ID on the device endpoint with a bare
            // "Not Found" rather than incorrect_client_credentials.
            "incorrect_client_credentials" or "Not Found" => "Logzilla's GitHub app ID is not recognised by GitHub.",
            _ => "GitHub refused the sign-in: " + (Text(json, "error_description") ?? error),
        });
    }

    private static string Text(Dictionary<string, JsonElement> json, string key) =>
        json.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Number(Dictionary<string, JsonElement> json, string key, int fallback) =>
        json.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
}
