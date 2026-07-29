using Windows.Media.Control;

namespace Deck.Core.Metadata;

/// <summary>
/// Reads the now-playing title from the Windows media session (F3) - the same information that
/// drives the volume-key overlay. Anything that registers with Windows shows up here: Spotify,
/// browsers, foobar2000, VLC.
/// <para>
/// This is the one metadata source BUTT cannot offer on Windows, and it removes the usual chore of
/// pointing an encoder at a text file that playout software has to be configured to write.
/// </para>
/// </summary>
public sealed class MediaSessionWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private CancellationTokenSource? _cancellation;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Task? _loop;

    /// <summary>The formatted title currently being reported, or empty when nothing is playing.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Which app the title came from, so the UI can say where it is reading.</summary>
    public string? SourceApp { get; private set; }

    /// <summary>Set when the media session cannot be used at all, so the UI can explain the silence.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// When true, a paused player stops updating the title rather than clearing it. Paused music
    /// mid-show usually means the presenter is talking over the last track, not that nothing is on.
    /// </summary>
    public bool KeepTitleWhilePaused { get; set; } = true;

    public event EventHandler<string>? TitleChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Stop();

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Problem = $"Deck could not read what Windows is playing: {ex.Message}";
            return;
        }

        Problem = null;

        var cancellation = new CancellationTokenSource();
        lock (_lock) _cancellation = cancellation;

        _loop = Task.Run(() => PollLoopAsync(cancellation.Token), cancellation.Token);
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        lock (_lock)
        {
            cancellation = _cancellation;
            _cancellation = null;
        }

        if (cancellation is null) return;

        cancellation.Cancel();
        cancellation.Dispose();
        _loop = null;
        _manager = null;
    }

    /// <summary>
    /// Polls rather than subscribing to session events. The events arrive on WinRT threads and are
    /// easy to leak across session changes; a two second poll costs nothing and cannot get stuck.
    /// </summary>
    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Problem = $"Deck could not read what Windows is playing: {ex.Message}";
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReadCurrentAsync(CancellationToken cancellationToken)
    {
        var session = _manager?.GetCurrentSession();
        if (session is null)
        {
            SetTitle(string.Empty, null);
            return;
        }

        var playback = session.GetPlaybackInfo();
        var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        if (!isPlaying && !KeepTitleWhilePaused)
        {
            SetTitle(string.Empty, null);
            return;
        }

        if (!isPlaying) return; // hold the last title

        var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (properties is null) return;

        SetTitle(Format(properties.Artist, properties.Title), session.SourceAppUserModelId);
    }

    /// <summary>Builds "Artist - Title", the shape listeners and directory listings expect.</summary>
    private static string Format(string? artist, string? title)
    {
        var cleanTitle = (title ?? string.Empty).Trim();
        var cleanArtist = (artist ?? string.Empty).Trim();

        if (cleanTitle.Length == 0) return string.Empty;
        return cleanArtist.Length == 0 ? cleanTitle : $"{cleanArtist} - {cleanTitle}";
    }

    private void SetTitle(string title, string? sourceApp)
    {
        if (title == Title) return;

        Title = title;
        SourceApp = FriendlyAppName(sourceApp);
        TitleChanged?.Invoke(this, title);
    }

    /// <summary>
    /// Turns an app user model id into something worth showing. These are identifiers rather than
    /// names, so the common ones are mapped by hand and the rest are tidied up as best we can.
    /// </summary>
    private static string? FriendlyAppName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return null;

        var id = appUserModelId.Trim();

        if (id.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (id.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (id.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (id.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (id.Contains("foobar", StringComparison.OrdinalIgnoreCase)) return "foobar2000";
        if (id.Contains("vlc", StringComparison.OrdinalIgnoreCase)) return "VLC";
        if (id.Contains("itunes", StringComparison.OrdinalIgnoreCase)) return "iTunes";
        if (id.Contains("aimp", StringComparison.OrdinalIgnoreCase)) return "AIMP";
        if (id.Contains("winamp", StringComparison.OrdinalIgnoreCase)) return "Winamp";

        // Strip a packaged-app suffix such as "!App" and any .exe tail.
        var bang = id.IndexOf('!');
        if (bang > 0) id = id[..bang];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];

        return id.Length == 0 ? null : id;
    }

    public void Dispose() => Stop();
}
