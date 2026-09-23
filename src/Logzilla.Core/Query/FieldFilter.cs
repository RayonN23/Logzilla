using System;
using System.Collections.Generic;

namespace Logzilla.Core.Query;

/// <summary>How a <see cref="FieldFilter"/> compares a field's value.</summary>
public enum FieldOp
{
    /// <summary>The value equals one of the filter's values.</summary>
    Is,

    /// <summary>The value contains one of the filter's values.</summary>
    Contains,

    /// <summary>The field is missing, or holds none of the filter's values.</summary>
    IsNot,
}

/// <summary>
/// path contains /orders. The field is a JSON key in a structured log or a key=value
/// pair in a plain-text one. Keys and values compare case-insensitively.
///
/// Several values on one filter are alternatives (any of them will do); separate
/// filters must all hold. That is how a filter row reads in every log tool people
/// already know, and it needs no query syntax to learn.
/// </summary>
public sealed class FieldFilter
{
    public FieldFilter(string key, FieldOp op, IEnumerable<string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(values);
        Key = key.Trim();
        Op = op;
        foreach (var v in values)
        {
            if (!string.IsNullOrEmpty(v) && !Values.Exists(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
            {
                Values.Add(v);
            }
        }
    }

    public string Key { get; }
    public FieldOp Op { get; }
    public List<string> Values { get; } = [];

    /// <summary>"is", "contains" or "is not", as the filter row shows it.</summary>
    public static string OpText(FieldOp op) => op switch
    {
        FieldOp.Contains => "contains",
        FieldOp.IsNot => "is not",
        _ => "is",
    };

    public override string ToString() => Key + " " + OpText(Op) + " " + string.Join(" or ", Values);
}
