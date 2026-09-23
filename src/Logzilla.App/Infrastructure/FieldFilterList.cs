using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Logzilla.Core.Query;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// The rules for building up a set of field filters one click at a time, kept out of
/// the form so they can be tested.
///
/// Filters are never changed in place: a query built from the list may be running on
/// the filter thread, so every change replaces the filter it touches.
/// </summary>
internal static class FieldFilterList
{
    private static bool SameKey(FieldFilter f, string key) => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase);

    private static FieldOp Opposite(FieldOp op) => op == FieldOp.IsNot ? FieldOp.Is : FieldOp.IsNot;

    /// <summary>
    /// Adds values to the filter for this key and operator, creating it if needed, so
    /// "Feature is DeviceAction" then "Feature is Sync" reads as one filter,
    /// "Feature is DeviceAction or Sync". Showing and hiding the same value contradict
    /// each other, so a value added with "is" leaves the key's "is not" filter and the
    /// other way round. Returns the index of the filter that now holds the values.
    /// </summary>
    public static int Add(List<FieldFilter> list, string key, FieldOp op, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(values);
        key = key.Trim();
        var added = values.Where(v => !string.IsNullOrEmpty(v)).ToList();
        if (added.Count == 0)
        {
            return -1;
        }

        if (op != FieldOp.Contains)
        {
            var other = list.FindIndex(f => SameKey(f, key) && f.Op == Opposite(op));
            if (other >= 0)
            {
                var rest = list[other].Values
                    .Where(v => !added.Exists(a => string.Equals(a, v, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (rest.Count == 0)
                {
                    list.RemoveAt(other);
                }
                else
                {
                    list[other] = new FieldFilter(list[other].Key, list[other].Op, rest);
                }
            }
        }

        var at = list.FindIndex(f => SameKey(f, key) && f.Op == op);
        if (at >= 0)
        {
            list[at] = new FieldFilter(list[at].Key, op, list[at].Values.Concat(added));
            return at;
        }
        list.Add(new FieldFilter(key, op, added));
        return list.Count - 1;
    }

    /// <summary>Puts an edited filter in place of the one at <paramref name="index"/>,
    /// merging it with any other filter that has the same key and operator.</summary>
    public static void Replace(List<FieldFilter> list, int index, string key, FieldOp op, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(list);
        var vals = values.Where(v => !string.IsNullOrEmpty(v)).ToList();
        if (index < 0 || index >= list.Count)
        {
            Add(list, key, op, vals);
            return;
        }
        list.RemoveAt(index);
        if (vals.Count == 0)
        {
            return;
        }
        var existed = list.Exists(f => SameKey(f, key) && f.Op == op);
        var at = Add(list, key, op, vals);
        if (!existed && at >= 0)
        {
            // A filter of its own again: keep its place in the row rather than jumping
            // to the end, so editing a chip does not move it.
            var f = list[at];
            list.RemoveAt(at);
            list.Insert(Math.Min(index, list.Count), f);
        }
    }

    /// <summary>"is" becomes "is not" and back. "contains" has no opposite here.</summary>
    public static bool CanInvert(FieldFilter f) => f != null && f.Op != FieldOp.Contains;

    public static void Invert(List<FieldFilter> list, int index)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (index < 0 || index >= list.Count || !CanInvert(list[index]))
        {
            return;
        }
        var f = list[index];
        list.RemoveAt(index);
        Add(list, f.Key, Opposite(f.Op), f.Values);
    }

    // ── setup files ────────────────────────────────────────────────────
    // One line per filter: op|key|value␟value… The unit separator cannot be typed
    // into a text box, so it is safe between values.

    private const char ValueSeparator = '\u001f';

    /// <summary>The filter as one setup-file line, or null when none of its values can
    /// be written on one line (a value picked from a multi-line JSON string).</summary>
    public static string Serialize(FieldFilter f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var values = f.Values.Where(v => v.IndexOfAny(['\r', '\n', ValueSeparator]) < 0).ToList();
        if (values.Count == 0)
        {
            return null;
        }
        var sb = new StringBuilder();
        sb.Append(((int)f.Op).ToString(CultureInfo.InvariantCulture)).Append('|').Append(f.Key.Replace("|", "", StringComparison.Ordinal)).Append('|');
        sb.Append(string.Join(ValueSeparator, values));
        return sb.ToString();
    }

    public static FieldFilter Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var parts = text.Split('|', 3);
        if (parts.Length < 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var op) ||
            !Enum.IsDefined(typeof(FieldOp), op) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }
        var values = parts[2].Split(ValueSeparator).Where(v => v.Length > 0).ToList();
        return values.Count == 0 ? null : new FieldFilter(parts[1], (FieldOp)op, values);
    }
}
