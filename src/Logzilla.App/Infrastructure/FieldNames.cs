using System;
using Logzilla.Core.Query;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// How field keys read on screen. CLEF files its trace, span and event ids under terse
/// @-keys; those get a plain name wherever a key is shown, while filters keep the
/// real key.
/// </summary>
internal static class FieldNames
{
    public static string Display(string key) => key switch
    {
        "@tr" => "trace id",
        "@sp" => "span id",
        "@i" => "event id",
        _ => key,
    };

    /// <summary>The key's plain name, with the key itself after it when they differ:
    /// "trace id (@tr)". For lists where the real key should stay findable.</summary>
    public static string DisplayWithKey(string key)
    {
        var name = Display(key);
        return name == key ? key : name + "  (" + key + ")";
    }

    public static string Describe(FieldFilter f)
    {
        ArgumentNullException.ThrowIfNull(f);
        return Display(f.Key) + " " + FieldFilter.OpText(f.Op) + " " + string.Join(" or ", f.Values);
    }

    /// <summary>
    /// Which id to follow a request by, lower is better; -1 when the key is not one.
    /// Named ids (CorrelationId, RequestId, TraceId …) come before CLEF's @tr: a
    /// service that runs inside one long trace stamps nearly every line with the same
    /// @tr, so it narrows far less than the id the application chose to log.
    /// </summary>
    public static int CorrelationRank(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return -1;
        }
        if (key == "@tr")
        {
            return 3;
        }
        var k = key.Replace("_", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (k.Contains("correlation", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (k.Equals("requestid", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("operationid", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("activityid", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        return k.Equals("traceid", StringComparison.OrdinalIgnoreCase) ? 2 : -1;
    }

    /// <summary>
    /// True for the field that says which thread wrote the line: ThreadId, thread,
    /// thread_id, tid, ThreadName and the like. Following one thread shows what it did
    /// in order, which interleaved threads otherwise hide.
    /// </summary>
    public static bool IsThread(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }
        var k = key.Replace("_", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return k.Equals("thread", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("threadid", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("threadname", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("tid", StringComparison.OrdinalIgnoreCase) ||
               k.Equals("managedthreadid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where a field goes in a list of an entry's fields: the ids a request is
    /// followed by, then the thread, then everything else in its own order.</summary>
    public static int FollowRank(string key)
    {
        var r = CorrelationRank(key);
        if (r >= 0)
        {
            return r;
        }
        return IsThread(key) ? 4 : 9;
    }
}
