using System.Text;

namespace Deck.Core.Diagnostics;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");

    public override string ToString() =>
        $"{Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} [{Level.ToString().ToUpperInvariant()}] {Message}";
}

/// <summary>
/// A running record of what happened during a broadcast (H5): connections, drops, device trouble,
/// recordings. Kept in memory for the in-app view and appended to a daily file.
/// <para>
/// This exists to answer "it cut out last night, what happened?" the morning after, which is
/// otherwise unanswerable. Messages are the same plain-language ones shown in the UI, so a user can
/// read their own log without help.
/// </para>
/// </summary>
public sealed class SessionLog
{
    private const int MaxEntriesInMemory = 500;

    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = [];
    private readonly string? _directory;

    public SessionLog(string? directory = null)
    {
        try
        {
            _directory = directory ?? AppPaths.LogDirectory;
        }
        catch (Exception)
        {
            // No writable log folder. The in-memory view still works, which is what matters live.
            _directory = null;
        }
    }

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_lock) return _entries.ToList();
        }
    }

    public string? FilePath => _directory is null
        ? null
        : Path.Combine(_directory, $"sirs-{DateTime.Now:yyyy-MM-dd}.log");

    public string? Directory => _directory;

    public event EventHandler<LogEntry>? EntryAdded;

    public void Info(string message) => Add(LogLevel.Info, message);

    public void Warn(string message) => Add(LogLevel.Warning, message);

    public void Error(string message) => Add(LogLevel.Error, message);

    public void Add(LogLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var entry = new LogEntry(DateTimeOffset.Now, level, message.Trim());

        lock (_lock)
        {
            _entries.Add(entry);

            // Trim from the front: a long show should not grow memory without bound, and the recent
            // entries are the ones anyone reads.
            if (_entries.Count > MaxEntriesInMemory)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntriesInMemory);
            }
        }

        AppendToFile(entry);
        EntryAdded?.Invoke(this, entry);
    }

    /// <summary>The whole in-memory log as text, for copying into a support message.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        foreach (var entry in Entries) builder.AppendLine(entry.ToString());
        return builder.ToString();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    private void AppendToFile(LogEntry entry)
    {
        var path = FilePath;
        if (path is null) return;

        try
        {
            File.AppendAllText(path, entry + Environment.NewLine);
        }
        catch (Exception)
        {
            // Disk full or locked. Losing the file copy must never interrupt a broadcast.
        }
    }
}
