namespace Sirs.Core.Metadata;

public enum MetadataSource
{
    /// <summary>The user types the title themselves (F1).</summary>
    Manual,

    /// <summary>A text file that playout software rewrites on every track change (F2).</summary>
    TextFile,

    /// <summary>Whatever Windows itself reports as playing - Spotify, a browser, a player (F3).</summary>
    MediaSession,

    /// <summary>Pushed in over the local endpoint by an automation system (F4).</summary>
    Remote,
}

public sealed class NowPlayingChangedEventArgs(string title) : EventArgs
{
    public string Title { get; } = title;
}

/// <summary>
/// Tracks what is playing and tells the broadcast when it changes.
/// <para>
/// The text file source is the universal interface to automation software - every playout system
/// worth using can write the current track to a file, which is why it earns a place in the first
/// release alongside typing the title by hand.
/// </para>
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    private readonly System.Timers.Timer _pollTimer;
    private string _title = string.Empty;
    private string? _lastFileContent;

    private readonly MediaSessionWatcher _mediaSession = new();
    private bool _suspended;

    public NowPlayingService()
    {
        _pollTimer = new System.Timers.Timer(2000) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) => PollFile();

        _mediaSession.TitleChanged += (_, title) =>
        {
            if (Source == MetadataSource.MediaSession) SetTitle(title);
        };

        Server.TrackReceived += (_, e) =>
        {
            if (Source != MetadataSource.Remote) return;

            SetTitle(e.Song is { Length: > 0 }
                ? e.Song
                : TitleTemplate.Build(Template, e.Artist, e.Title, e.Album));
        };
    }

    /// <summary>The local endpoint automation systems push titles to (F4). Off until started.</summary>
    public MetadataServer Server { get; } = new();

    /// <summary>How the pieces of a track are put together into one line (F5).</summary>
    public string Template { get; set; } = TitleTemplate.Default;

    /// <summary>
    /// Holds the last title on air while it is on (F5). Stations run adverts and jingles between
    /// tracks, and neither belongs in a listener's now-playing display.
    /// </summary>
    public bool SuspendUpdates
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;

            _suspended = value;

            // Coming off hold, whatever arrived meanwhile goes out immediately rather than waiting
            // for the next track change.
            if (!value && _title.Length > 0) TitleChanged?.Invoke(this, new NowPlayingChangedEventArgs(_title));
        }
    }

    /// <summary>Which app the media session title is coming from, when that source is in use.</summary>
    public string? MediaSessionApp => _mediaSession.SourceApp;

    public MetadataSource Source { get; private set; } = MetadataSource.Manual;

    /// <summary>Path watched when <see cref="Source"/> is <see cref="MetadataSource.TextFile"/>.</summary>
    public string? FilePath { get; private set; }

    public string Title => _title;

    /// <summary>Set when the watched file cannot be read, so the UI can say why nothing updates.</summary>
    public string? SourceProblem { get; private set; }

    public event EventHandler<NowPlayingChangedEventArgs>? TitleChanged;

    public void UseManual()
    {
        Source = MetadataSource.Manual;
        FilePath = null;
        SourceProblem = null;
        _pollTimer.Stop();
        _mediaSession.Stop();
        Server.Stop();
    }

    public void UseTextFile(string path)
    {
        _mediaSession.Stop();
        Server.Stop();

        Source = MetadataSource.TextFile;
        FilePath = path;
        SourceProblem = null;
        _lastFileContent = null;

        PollFile();
        _pollTimer.Start();
    }

    /// <summary>Follows whatever Windows reports as playing (F3).</summary>
    public async Task UseMediaSessionAsync()
    {
        _pollTimer.Stop();
        Server.Stop();

        Source = MetadataSource.MediaSession;
        FilePath = null;
        SourceProblem = null;

        await _mediaSession.StartAsync().ConfigureAwait(false);

        SourceProblem = _mediaSession.Problem;
        if (SourceProblem is null && _mediaSession.Title.Length > 0) SetTitle(_mediaSession.Title);
    }

    /// <summary>Sets the title directly. Used by the manual box, and by the wizard for a first title.</summary>
    public void SetTitle(string title)
    {
        var clean = Clean(title);
        if (clean == _title) return;

        _title = clean;

        // The title is still tracked while updates are held, so the UI can show what is queued up
        // and the right thing goes out the moment the hold is lifted.
        if (!_suspended) TitleChanged?.Invoke(this, new NowPlayingChangedEventArgs(clean));
    }

    /// <summary>Sets a title from its parts, applying the template (F5).</summary>
    public void SetTrack(string? artist, string? title, string? album = null) =>
        SetTitle(TitleTemplate.Build(Template, artist, title, album));

    /// <summary>Takes titles from the local endpoint (F4).</summary>
    public bool UseRemote(int port, bool allowOtherComputers, string? token)
    {
        _pollTimer.Stop();
        _mediaSession.Stop();

        Source = MetadataSource.Remote;
        FilePath = null;

        var started = Server.Start(port, allowOtherComputers, token);
        SourceProblem = Server.Problem;

        return started;
    }

    private void PollFile()
    {
        if (Source != MetadataSource.TextFile || string.IsNullOrEmpty(FilePath)) return;

        try
        {
            if (!File.Exists(FilePath))
            {
                SourceProblem = $"SIRS cannot find {Path.GetFileName(FilePath)}. Check the file is still there.";
                return;
            }

            // Share the file: playout software usually holds it open while writing.
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            SourceProblem = null;

            if (content == _lastFileContent) return;
            _lastFileContent = content;

            // Automation systems write either a single line or the title on the first line.
            var firstLine = content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim() ?? string.Empty;

            SetTitle(firstLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SourceProblem = $"SIRS could not read {Path.GetFileName(FilePath)}: {ex.Message}";
        }
    }

    /// <summary>Titles end up in HTTP query strings and stream headers, so control characters go.</summary>
    private static string Clean(string title)
    {
        var trimmed = (title ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length > 255 ? trimmed[..255] : trimmed;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _mediaSession.Dispose();
        Server.Dispose();
    }
}
