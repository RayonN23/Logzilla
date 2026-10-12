using System;
using System.Net;

namespace Logzilla.AI.Providers;

/// <summary>
/// Whether an endpoint typed into AI settings is safe to send an API key and log
/// excerpts to. Every request carries the key, so it must travel encrypted: https, or
/// plain http only to this machine, where a local gateway or proxy usually listens.
/// </summary>
public static class EndpointCheck
{
    /// <summary>Why <paramref name="endpoint"/> cannot be used, or null when it can (or is empty).</summary>
    public static string Problem(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return "\"" + endpoint.Trim() + "\" is not a web address. Use one like https://gateway.example.com/v1.";
        }
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return null;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && IsThisMachine(uri))
        {
            return null;
        }
        return "The endpoint must start with https:// — your API key and parts of the log are sent to it, " +
               "and over " + uri.Scheme + ":// anyone on the network could read them. (http:// is allowed only for localhost.)";
    }

    private static bool IsThisMachine(Uri uri) =>
        uri.IsLoopback ||
        (IPAddress.TryParse(uri.DnsSafeHost, out var ip) && IPAddress.IsLoopback(ip));
}
