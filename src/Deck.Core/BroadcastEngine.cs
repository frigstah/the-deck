using Deck.Core.Audio;
using Deck.Core.Codecs;
using Deck.Core.Diagnostics;
using Deck.Core.Metadata;
using Deck.Core.Recording;
using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.Core;

/// <summary>
/// Wires the pieces together and owns the one audio callback everything hangs off. Capture pushes a
/// block; that block goes to the sound check, the monitor, the recorder and the encoder, in that
/// order, on the capture thread.
/// <para>
/// The encoder is the only stage that must never be skipped while live, so it runs last and its
/// output goes straight into the network buffer rather than the socket.
/// </para>
/// </summary>
public sealed class DeviceRecoveredEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class BroadcastEngine : IDisposable
{
    private AudioDeviceKind _inputKind = AudioDeviceKind.Input;
    private string? _inputDeviceId;
    private AudioDeviceKind _secondaryKind = AudioDeviceKind.Loopback;
    private string? _secondaryDeviceId;
    private bool _secondaryWanted;

    private readonly System.Timers.Timer _deviceWatchdog;
    private readonly System.Timers.Timer _listenerPoll;
    private readonly object _recoveryLock = new();
    private AudioFormat? _captureFormat;
    private bool _primaryWanted;

    public BroadcastEngine()
    {
        Capture.BlockCaptured += OnBlockCaptured;
        NowPlaying.TitleChanged += (_, e) => Broadcast.SetMetadata(e.Title);

        _deviceWatchdog = new System.Timers.Timer(2000) { AutoReset = true };
        _deviceWatchdog.Elapsed += (_, _) => CheckDevices();
        _deviceWatchdog.Start();

        // Listener counts change slowly and the query costs a round trip, so this is unhurried.
        _listenerPoll = new System.Timers.Timer(15000) { AutoReset = true };
        _listenerPoll.Elapsed += (_, _) => _ = PollListenersAsync();
        _listenerPoll.Start();

        WireLogging();
    }

    /// <summary>
    /// Feeds the things worth remembering into the session log. Kept in one place so every event
    /// that reaches the user also reaches the log, in the same words.
    /// </summary>
    private void WireLogging()
    {
        Broadcast.TargetStateChanged += (_, e) =>
        {
            // With a backup running, "Reconnecting" on its own is ambiguous, so the log always says
            // which destination it is about once there is more than one.
            var prefix = Broadcast.IsMultiTarget ? $"{e.Target.Name}: " : string.Empty;

            var description = e.Message is { Length: > 0 } message
                ? $"{prefix}{e.State.Headline()} — {message}"
                : $"{prefix}{e.State.Headline()}";

            switch (e.State)
            {
                case StreamState.Failed:
                    Log.Error(description);
                    break;
                case StreamState.Reconnecting:
                    Log.Warn(description);
                    break;
                default:
                    Log.Info(description);
                    break;
            }
        };

        Capture.CaptureFailed += (_, e) => Log.Error(e.Message);
        DeviceRecovered += (_, e) => Log.Info(e.Message);
        Recorder.Failed += (_, e) => Log.Error(e.Message);
        NowPlaying.TitleChanged += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Title)) Log.Info($"Now playing: {e.Title}");
        };
    }

    public CaptureEngine Capture { get; } = new();

    /// <summary>Every destination the current show is going to (C12).</summary>
    public BroadcastSet Broadcast { get; } = new();

    public StreamState StreamState => Broadcast.State;

    public Recorder Recorder { get; } = new();

    public SoundCheck SoundCheck { get; } = new();

    public MonitorPlayer Monitor { get; } = new();

    public NowPlayingService NowPlaying { get; } = new();

    public SessionLog Log { get; } = new();

    /// <summary>Goes on air when sound appears and off when it stops (G6). Off by default.</summary>
    public AutoAirSwitch AutoAir { get; } = new();

    /// <summary>Listeners the servers last reported, or null when none of them say (H4).</summary>
    public int? ListenerCount { get; private set; }

    /// <summary>
    /// How the count was arrived at, or why there is not one. Not a headline - the deck says the
    /// number and this says the rest, on hover and in the log.
    /// </summary>
    public string? ListenerDetail { get; private set; }

    public event EventHandler? ListenerCountChanged;

    /// <summary>
    /// Servers already explained once. A poll runs every few seconds for the length of a show, and a
    /// server that publishes nothing will say so every single time; the log is for the morning after,
    /// not for the same sentence four hundred times.
    /// </summary>
    private readonly HashSet<Guid> _listenersExplained = [];

    /// <summary>
    /// Only while something is actually on air. Deck deliberately does not ask a server about its
    /// audience while idle: the count is not shown off air, so polling would be traffic to somebody's
    /// server every fifteen seconds in exchange for nothing.
    /// </summary>
    private async Task PollListenersAsync()
    {
        var live = Broadcast.Targets.Where(t => t.State == StreamState.Live).ToList();

        if (live.Count == 0)
        {
            _listenersExplained.Clear();

            if (ListenerCount is null && ListenerDetail is null) return;

            ListenerCount = null;
            ListenerDetail = null;
            ListenerCountChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var reports = await Task.WhenAll(live.Select(t => ListenerCounter.QueryAsync(t.Profile)))
            .ConfigureAwait(false);

        // Say once, in the log, why a count is missing. Without this the answer to "why does it never
        // show listeners?" lived nowhere at all - which is exactly how it went unnoticed that one
        // ordinary host serves 404 for the endpoint every Icecast is supposed to have.
        for (var i = 0; i < reports.Length; i++)
        {
            if (reports[i].Known || !_listenersExplained.Add(live[i].Profile.Id)) continue;
            Log.Info(reports[i].Detail);
        }

        var combined = ListenerTally.Combine(reports);

        if (combined.Value == ListenerCount && combined.Detail == ListenerDetail) return;

        ListenerCount = combined.Value;
        ListenerDetail = combined.Detail;
        ListenerCountChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The format audio flows in once capture is running.</summary>
    public AudioFormat StreamFormat => Capture.StreamFormat;

    public bool IsAudioRunning => Capture.IsRunning;

    public AudioDeviceKind InputKind => _inputKind;

    public string? MonitorFeedbackWarning(string? monitorDeviceId) =>
        MonitorPlayer.FeedbackWarning(_inputKind, _inputDeviceId, monitorDeviceId);

    /// <summary>
    /// Starts capture at the format the given encoder settings need. Called whenever the input or
    /// the quality settings change; restarting is cheap and keeps the pipeline in one known state.
    /// </summary>
    public void StartAudio(string? inputDeviceId, AudioDeviceKind kind, EncoderSettings encoderSettings) =>
        StartAudio(inputDeviceId, kind, encoderSettings.Normalised().Format);

    public void StartAudio(string? inputDeviceId, AudioDeviceKind kind, AudioFormat captureFormat)
    {
        _inputDeviceId = inputDeviceId;
        _inputKind = kind;

        _captureFormat = captureFormat;
        _primaryWanted = true;

        Capture.Start(inputDeviceId, kind, captureFormat);

        // Restarting the main input tears the whole mix down, so bring the second source back.
        if (_secondaryWanted) TryStartSecondary();
    }

    /// <summary>Adds a second input to the mix (A5), for example music under a live microphone.</summary>
    public void StartSecondaryInput(string? deviceId, AudioDeviceKind kind)
    {
        _secondaryDeviceId = deviceId;
        _secondaryKind = kind;
        _secondaryWanted = true;

        TryStartSecondary();
    }

    public void StopSecondaryInput()
    {
        _secondaryWanted = false;
        Capture.StopSecondary();
    }

    public bool IsMixing => Capture.IsMixing;

    private void TryStartSecondary()
    {
        if (!Capture.IsRunning) return;
        Capture.StartSecondary(_secondaryDeviceId, _secondaryKind);
    }

    /// <summary>Whether Deck is waiting for a dropped-out device to come back.</summary>
    public bool IsWaitingForDevice => _primaryWanted && !Capture.Primary.IsRunning;

    public void StopAudio()
    {
        _primaryWanted = false;
        Capture.Stop();
        Monitor.Stop();
    }

    /// <summary>
    /// Raised when a device that had gone away comes back and Deck has picked it up again (A6).
    /// </summary>
    public event EventHandler<DeviceRecoveredEventArgs>? DeviceRecovered;

    /// <summary>
    /// Watches for an input that dropped out - an unplugged interface, a driver reset, another
    /// program grabbing exclusive access - and takes it back the moment it returns.
    /// <para>
    /// A poll rather than a WASAPI notification callback: notifications arrive on a COM thread and
    /// do not fire at all for some failure modes, and two seconds of dead air is already the worst
    /// case here. Recovery insists on the exact device coming back rather than falling through to
    /// the system default, because quietly switching what is going out is worse than staying off.
    /// </para>
    /// </summary>
    private void CheckDevices()
    {
        if (!Monitor.IsRunning && !_primaryWanted) return;

        lock (_recoveryLock)
        {
            if (_primaryWanted && !Capture.Primary.IsRunning && _captureFormat is { } format)
            {
                if (TryRecover(_inputDeviceId, () => Capture.Start(_inputDeviceId, _inputKind, format)))
                {
                    DeviceRecovered?.Invoke(this, new DeviceRecoveredEventArgs(
                        "The audio input is back and Deck is listening to it again."));

                    if (_secondaryWanted) TryStartSecondary();
                    return;
                }
            }

            if (_secondaryWanted && Capture.Primary.IsRunning && !Capture.Secondary.IsRunning)
            {
                if (TryRecover(_secondaryDeviceId, TryStartSecondary))
                {
                    DeviceRecovered?.Invoke(this, new DeviceRecoveredEventArgs(
                        "The second sound source is back in the mix."));
                }
            }
        }
    }

    private static bool TryRecover(string? deviceId, Action restart)
    {
        // A null id means "whatever Windows calls the default", which is always available.
        if (AsioCapture.IsAsioId(deviceId))
        {
            // An ASIO driver is not a Windows endpoint and cannot be resolved as one, so it is
            // looked for in the driver list instead. Without this the watchdog would decide a
            // perfectly healthy interface had vanished and never try to reopen it (A8).
            var driver = AsioCapture.DriverNameFrom(deviceId!);
            if (!AsioCapture.DriverNames().Contains(driver)) return false;
        }
        else if (!string.IsNullOrEmpty(deviceId))
        {
            using var device = AudioDevices.Resolve(deviceId);
            if (device is null) return false;
        }

        try
        {
            restart();
            return true;
        }
        catch (Exception)
        {
            // Device is enumerable but not yet ready to open. Another tick will come round.
            return false;
        }
    }

    /// <summary>Restarts capture on the current device - used after a device is unplugged and returns.</summary>
    public void RestartAudio(EncoderSettings encoderSettings) =>
        StartAudio(_inputDeviceId, _inputKind, encoderSettings);

    public void StartMonitoring(string? outputDeviceId)
    {
        if (!Capture.IsRunning) return;
        Monitor.Start(outputDeviceId, Capture.StreamFormat);
    }

    public void StopMonitoring() => Monitor.Stop();

    public void GoLive(ServerProfile profile) => GoLive([profile]);

    /// <summary>
    /// Starts the show to every destination at once (C12). The first profile is treated as the
    /// primary; the rest are backups or alternate bitrates.
    /// </summary>
    public void GoLive(IReadOnlyList<ServerProfile> profiles)
    {
        if (profiles.Count == 0) throw new ArgumentException("Nothing to broadcast to.", nameof(profiles));

        // One capture format serves them all, at the highest rate any of them asked for; each
        // target resamples down from there.
        var captureFormat = BroadcastSet.CaptureFormatFor(profiles);

        if (!Capture.IsRunning || Capture.StreamFormat != captureFormat)
        {
            StartAudio(_inputDeviceId, _inputKind, captureFormat);
        }

        Broadcast.Start(profiles, captureFormat);

        // Push whatever is showing as now playing so listeners see it immediately rather than at
        // the next track change.
        if (!string.IsNullOrWhiteSpace(NowPlaying.Title)) Broadcast.SetMetadata(NowPlaying.Title);
    }

    public Task StopBroadcastAsync() => Broadcast.StopAsync();

    public void StartRecording(RecordingSettings settings, EncoderSettings encoderSettings, string stationName)
    {
        if (!Capture.IsRunning) StartAudio(_inputDeviceId, _inputKind, encoderSettings);

        Recorder.Start(settings, encoderSettings.Normalised(), Capture.StreamFormat, stationName, NowPlaying.Title);
    }

    public string? StopRecording() => Recorder.Stop();

    public void StartSoundCheck()
    {
        if (!Capture.IsRunning)
        {
            throw new InvalidOperationException("Audio is not running.");
        }

        SoundCheck.StartRecording(Capture.StreamFormat);
    }

    private void OnBlockCaptured(ReadOnlySpan<float> interleaved, AudioFormat format)
    {
        SoundCheck.Write(interleaved);
        Monitor.Write(interleaved);
        Recorder.Write(interleaved);
        Broadcast.Write(interleaved);
    }

    public void Dispose()
    {
        _deviceWatchdog.Stop();
        _deviceWatchdog.Dispose();
        _listenerPoll.Stop();
        _listenerPoll.Dispose();

        Capture.BlockCaptured -= OnBlockCaptured;

        StopBroadcastAsync().GetAwaiter().GetResult();

        Capture.Dispose();
        Monitor.Dispose();
        Recorder.Dispose();
        SoundCheck.Dispose();
        NowPlaying.Dispose();
        Broadcast.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
