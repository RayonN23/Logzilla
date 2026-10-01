using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Logzilla.App.Infrastructure;

/// <summary>
/// Everything the assistant sent to a model in this session: the user's questions and
/// every tool result, exactly as they left the machine (after redaction). Kept in memory
/// for "What was sent", and — when the user or their organisation asks for it — appended
/// to a daily file under <c>%APPDATA%\Logzilla\ai-record</c>. Nothing is sent anywhere else.
/// </summary>
internal sealed class AIRecord
{
    private const int MaxInMemory = 500;

    private readonly List<string> _entries = new();
    private readonly object _gate = new();

    public bool WriteToDisk { get; set; }

    public static string Folder => Path.Combine(Paths.Dir, "ai-record");

    public void Add(string provider, string model, string kind, string text)
    {
        var now = DateTimeOffset.Now;
        var shown = new StringBuilder()
            .Append(now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("  ").Append(kind)
            .Append("  ->  ").Append(provider).Append(" / ").Append(model).AppendLine()
            .AppendLine(text)
            .ToString();
        lock (_gate)
        {
            _entries.Add(shown);
            if (_entries.Count > MaxInMemory)
            {
                _entries.RemoveAt(0);
            }
            if (WriteToDisk)
            {
                Append(now, provider, model, kind, text);
            }
        }
    }

    /// <summary>Everything recorded this session, oldest first.</summary>
    public string Text()
    {
        lock (_gate)
        {
            return _entries.Count == 0
                ? "Nothing has been sent yet in this session."
                : string.Join(Environment.NewLine, _entries);
        }
    }

    private static void Append(DateTimeOffset now, string provider, string model, string kind, string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var line = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["time"] = now.ToString("o", CultureInfo.InvariantCulture),
                ["provider"] = provider,
                ["model"] = model,
                ["kind"] = kind,
                ["text"] = text,
            });
            File.AppendAllText(Path.Combine(Folder, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl"),
                line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("Write AI record: " + ex.Message);
        }
    }
}
