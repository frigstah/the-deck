namespace Sirs.Core.Control;

/// <summary>What a control command did, in words the caller can print without interpretation.</summary>
public readonly record struct ControlResult(bool Ok, string Message)
{
    public static ControlResult Done(string message) => new(true, message);

    public static ControlResult Refused(string message) => new(false, message);
}

/// <summary>
/// A snapshot of what SIRS is doing, for the remote endpoint and the command line (I10).
/// <para>
/// A flat record rather than a live view of the engine on purpose. It is read on a socket thread
/// while the audio thread is running, and copying a dozen fields once is cheaper and far safer than
/// handing a network caller a reference into the pipeline.
/// </para>
/// </summary>
public sealed record ControlStatus
{
    public string State { get; init; } = "Off air";

    public bool IsLive { get; init; }

    public string? Station { get; init; }

    /// <summary>Where the show is going, one entry per destination (C12).</summary>
    public IReadOnlyList<string> Destinations { get; init; } = [];

    public string? NowPlaying { get; init; }

    public TimeSpan Uptime { get; init; }

    public int? Listeners { get; init; }

    /// <summary>Peak level of the mix in dBFS. Negative; 0 is the top of the scale.</summary>
    public double PeakDb { get; init; }

    /// <summary>Integrated loudness so far, or null before there is enough audio to say (B8).</summary>
    public double? Lufs { get; init; }

    public bool IsRecording { get; init; }

    /// <summary>Read by the MIDI mute button (I11), which must not keep its own idea of this.</summary>
    public bool IsMuted { get; init; }

    public string? RecordingFile { get; init; }

    public bool IsAudioRunning { get; init; }

    /// <summary>Whatever is currently wrong, or null when nothing is.</summary>
    public string? Problem { get; init; }
}

/// <summary>
/// The actions the control endpoint and the command line can take (I10), and the MIDI surface (I11).
/// <para>
/// An interface implemented by the view model rather than calls straight into
/// <see cref="BroadcastEngine"/>, because "go live" is not an engine operation - it depends on which
/// servers are ticked, whether the settings are valid and what the user last chose. Routing remote
/// control through the same path as the button means the two cannot drift apart, and it lets the
/// checks drive every command without a window.
/// </para>
/// </summary>
public interface IControlSurface
{
    ControlStatus Status();

    Task<ControlResult> GoLiveAsync();

    Task<ControlResult> GoOffAsync();

    ControlResult SetTitle(string title);

    ControlResult StartRecording();

    ControlResult StopRecording();

    ControlResult SetMuted(bool muted);

    /// <summary>Input trim in dB. Clamped by the implementation to whatever the fader allows.</summary>
    ControlResult SetGainDb(double db);
}
