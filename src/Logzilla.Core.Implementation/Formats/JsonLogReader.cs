using System;
using System.Collections.Generic;
using System.Linq;
using Logzilla.Core.Formats;

namespace Logzilla.Core.Implementation.Formats;

internal sealed class JsonLogReader : IJsonLogReader
{
    public IReadOnlyList<string> MessageKeys => JsonLog.MsgKeys;

    public IReadOnlyList<KeyValuePair<string, string>> Parse(string record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonLog.Parse(record);
    }

    public string FirstValue(IReadOnlyList<KeyValuePair<string, string>> properties, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(keys);
        return JsonLog.Get(properties as List<KeyValuePair<string, string>> ?? properties.ToList(), keys as string[] ?? keys.ToArray());
    }

    public string RenderLine(string record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonLog.Line(record);
    }

    public string RenderFull(string record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonLog.Full(record);
    }

    public string Describe(string record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonLog.Pretty(record);
    }

    public bool IsMetadataKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return JsonLog.IsMeta(key);
    }
}
