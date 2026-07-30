using Deck.Core.Audio;
using Deck.Core.Codecs;
using Deck.Core.Servers;

namespace Deck.Core.Streaming;

/// <summary>
/// A destination that is not on air, and the whole of why. Carried as a value rather than a string
/// so the deck can say what kind of problem it is before it says the words, and can tell "still
/// trying" apart from "stopped".
/// </summary>
/// <param name="Server">Which destination, needed as soon as there is a backup running.</param>
/// <param name="Detail">The full explanation, never trimmed. What the user came to read.</param>
public sealed record BroadcastProblem(
    string Server,
    StreamFailure Failure,
    string Detail,
    bool StillTrying)
{
    public string Headline => Failure.Headline();
}

public sealed class TargetStateChangedEventArgs(BroadcastTarget target, StreamStateChangedEventArgs change) : EventArgs
{
    public BroadcastTarget Target { get; } = target;

    public StreamState State { get; } = change.State;

    public string? Message { get; } = change.Message;
}

/// <summary>
/// Every destination the current show is going to (C12) - normally one, sometimes a main plus a
/// backup relay, occasionally the same show at two bitrates.
/// <para>
/// The aggregate state deliberately reports <see cref="StreamState.Live"/> while <em>any</em> target
/// is connected. A backup exists precisely so that one server failing does not take the show off
/// air; showing "Reconnecting" over a perfectly good main stream would be a lie. What went wrong
/// elsewhere is said in <see cref="StatusDetail"/> instead.
/// </para>
/// </summary>
public sealed class BroadcastSet : IAsyncDisposable
{
    private readonly object _lifecycleLock = new();

    // Read on the audio thread, replaced wholesale on the UI thread: an immutable snapshot means
    // fanning a block out to every target costs no lock at all.
    private volatile BroadcastTarget[] _targets = [];

    public IReadOnlyList<BroadcastTarget> Targets => _targets;

    public int Count => _targets.Length;

    public bool IsMultiTarget => _targets.Length > 1;

    public event EventHandler<TargetStateChangedEventArgs>? TargetStateChanged;

    /// <summary>
    /// Raised when connecting worked out a server's type, so the server list can be written back to
    /// disk with the answer in it.
    /// </summary>
    public event EventHandler? ServerTypeDetected;

    /// <summary>The single capture format that feeds every target, before their own conversion.</summary>
    public static AudioFormat CaptureFormatFor(IEnumerable<ServerProfile> profiles)
    {
        var settings = profiles.Select(p => p.Encoder.Normalised()).ToList();
        if (settings.Count == 0) return QualityPreset.Default.Settings.Format;

        // Capture at the highest rate anyone wants and let the rest resample down. Going the other
        // way would upsample audio that was never there, which is worse than pointless.
        return new AudioFormat(settings.Max(s => s.SampleRate), settings.Max(s => s.Channels));
    }

    public void Start(IReadOnlyList<ServerProfile> profiles, AudioFormat captureFormat)
    {
        lock (_lifecycleLock)
        {
            if (_targets.Length > 0) throw new InvalidOperationException("Already broadcasting.");

            var targets = new BroadcastTarget[profiles.Count];

            for (var i = 0; i < profiles.Count; i++)
            {
                var target = new BroadcastTarget(profiles[i], captureFormat, isPrimary: i == 0);
                target.Connection.StateChanged += (_, e) =>
                    TargetStateChanged?.Invoke(this, new TargetStateChangedEventArgs(target, e));

                target.Connection.ServerTypeDetected += (_, _) => ServerTypeDetected?.Invoke(this, EventArgs.Empty);

                targets[i] = target;
            }

            _targets = targets;

            // Started only after the array is published, so the first state change already has
            // somewhere to be counted against.
            foreach (var target in targets) target.Start();
        }
    }

    /// <summary>Called on the audio thread. One block, every destination.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        var targets = _targets;
        for (var i = 0; i < targets.Length; i++) targets[i].Write(interleaved);
    }

    public void SetMetadata(string title)
    {
        var targets = _targets;
        for (var i = 0; i < targets.Length; i++) targets[i].SetMetadata(title);
    }

    public async Task StopAsync()
    {
        BroadcastTarget[] targets;

        lock (_lifecycleLock)
        {
            targets = _targets;
            _targets = [];
        }

        // Stopped together rather than one after another: a server that is slow to close should not
        // hold the others on air.
        await Task.WhenAll(targets.Select(t => t.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- aggregate view

    public StreamState State
    {
        get
        {
            var targets = _targets;
            if (targets.Length == 0) return StreamState.Idle;

            if (Any(targets, StreamState.Live)) return StreamState.Live;
            if (Any(targets, StreamState.Connecting)) return StreamState.Connecting;
            if (Any(targets, StreamState.Reconnecting)) return StreamState.Reconnecting;
            if (Any(targets, StreamState.Failed)) return StreamState.Failed;

            return StreamState.Idle;
        }
    }

    /// <summary>
    /// How the destinations are doing, when there is more than one and they disagree. Null when
    /// there is nothing worth saying, so the status line stays quiet in the ordinary single-server
    /// case.
    /// </summary>
    public string? StatusDetail
    {
        get
        {
            var targets = _targets;
            if (targets.Length <= 1) return null;

            var live = targets.Count(t => t.State == StreamState.Live);
            if (live == targets.Length) return $"On air to all {live} servers.";

            var struggling = targets.Where(t => t.State != StreamState.Live).Select(t => t.Name);
            var list = string.Join(", ", struggling);

            return live == 0
                ? $"No server connected yet. Trying: {list}."
                : $"On air to {live} of {targets.Length} servers. Still trying: {list}.";
        }
    }

    /// <summary>The longest any destination has been connected - what the on-air clock shows.</summary>
    public TimeSpan Uptime
    {
        get
        {
            var longest = TimeSpan.Zero;
            foreach (var target in _targets)
            {
                var uptime = target.Connection.Uptime;
                if (uptime > longest) longest = uptime;
            }

            return longest;
        }
    }

    public long BytesSent => _targets.Sum(t => t.Connection.BytesSent);

    public int DroppedBlocks => _targets.Sum(t => t.Connection.DroppedBlocks);

    /// <summary>Everything leaving the machine, across all destinations, in kbps (H7).</summary>
    public double ThroughputKbps => _targets.Sum(t => t.Connection.ThroughputKbps);

    /// <summary>The fullest send buffer of any destination - the first sign of a struggling link.</summary>
    public double BufferFill
    {
        get
        {
            var worst = 0.0;
            foreach (var target in _targets)
            {
                var fill = target.Connection.BufferFill;
                if (fill > worst) worst = fill;
            }

            return worst;
        }
    }

    public int ReconnectAttempts => _targets.Sum(t => t.Connection.ReconnectAttempts);

    /// <summary>The most recent failure from any destination, for the status line.</summary>
    public string? LastError => Problem?.Detail;

    /// <summary>
    /// The destination that is not working and everything known about why - taken from one target
    /// rather than assembled from several, so the name, the verdict and the detail always describe
    /// the same server.
    /// </summary>
    public BroadcastProblem? Problem
    {
        get
        {
            foreach (var target in _targets)
            {
                if (target.State is not (StreamState.Failed or StreamState.Reconnecting)) continue;
                if (target.Connection.LastError is not { Length: > 0 } detail) continue;

                var failure = target.Connection.LastFailure ?? StreamFailure.Protocol;

                return new BroadcastProblem(
                    target.Name,
                    failure,
                    detail,
                    StillTrying: target.State == StreamState.Reconnecting);
            }

            return null;
        }
    }

    private static bool Any(BroadcastTarget[] targets, StreamState state)
    {
        foreach (var target in targets)
        {
            if (target.State == state) return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
