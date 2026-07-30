using System.Diagnostics;
using Deck.Core.Codecs;
using Deck.Core.Servers;

namespace Deck.Core.Streaming;

public sealed class StreamStateChangedEventArgs(StreamState state, string? message) : EventArgs
{
    public StreamState State { get; } = state;

    /// <summary>Plain-language detail for the status line, or null when the state speaks for itself.</summary>
    public string? Message { get; } = message;
}

/// <summary>
/// Owns the network side of a broadcast (H1-H3): a send buffer between the audio thread and the
/// socket, the connection state machine, and reconnection with a fast backoff.
/// <para>
/// The backoff starts at one second by design. Rocket Broadcaster's free tier waits twenty seconds
/// before reconnecting, which turns a momentary blip into dead air; Deck has no reason to do that.
/// </para>
/// </summary>
public sealed class StreamConnection : IAsyncDisposable
{
    private static readonly int[] BackoffSeconds = [1, 2, 5, 10, 20, 30];

    private readonly object _queueLock = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly SemaphoreSlim _dataSignal = new(0);

    private CancellationTokenSource? _cancellation;
    private Task? _pump;
    private ServerProfile? _profile;
    private EncoderSettings? _encoder;
    private byte[] _streamHeader = [];

    private int _queuedBytes;
    private int _maxQueuedBytes = 64 * 1024;
    private long _liveSinceTicks;
    private string? _pendingMetadata;

    private long _throughputTicks = Stopwatch.GetTimestamp();
    private long _throughputBytes;
    private double _throughputKbps;

    public StreamState State { get; private set; } = StreamState.Idle;

    /// <summary>The most recent failure, phrased for the user. Survives into the Failed state.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// What kind of failure <see cref="LastError"/> describes, kept alongside the words rather than
    /// thrown away with the exception. The sinks work out the difference between a refused password,
    /// a taken stream and a silent server and then used to flatten all three into one string, which
    /// left the deck unable to say anything about a problem beyond quoting it.
    /// </summary>
    public StreamFailure? LastFailure { get; private set; }

    /// <summary>Anything unusual about how the connection was made, e.g. an adjusted port.</summary>
    public string? ConnectionNote { get; private set; }

    public long BytesSent { get; private set; }

    public int ReconnectAttempts { get; private set; }

    /// <summary>Blocks discarded because the network could not keep up with the encoder.</summary>
    public int DroppedBlocks { get; private set; }

    /// <summary>
    /// What is actually going down the wire, in kbps (H7). Recomputed at most once a second, on
    /// read: the UI polls this at twenty frames a second and does not need it any fresher.
    /// <para>
    /// Worth showing because it answers a question the configured bitrate cannot: a number well
    /// below what was asked for means the connection, not the encoder, is the problem.
    /// </para>
    /// </summary>
    public double ThroughputKbps
    {
        get
        {
            var elapsed = Stopwatch.GetElapsedTime(_throughputTicks).TotalSeconds;
            if (elapsed < 1.0) return _throughputKbps;

            var sent = BytesSent;
            _throughputKbps = (sent - _throughputBytes) * 8 / 1000.0 / elapsed;
            _throughputBytes = sent;
            _throughputTicks = Stopwatch.GetTimestamp();

            return _throughputKbps;
        }
    }

    /// <summary>
    /// How full the send buffer is, 0 to 1 (H7). Steady near zero is healthy; anything climbing
    /// means the network is not keeping up and audio is about to be dropped.
    /// </summary>
    public double BufferFill
    {
        get
        {
            lock (_queueLock)
            {
                return _maxQueuedBytes <= 0 ? 0 : Math.Clamp((double)_queuedBytes / _maxQueuedBytes, 0, 1);
            }
        }
    }

    public TimeSpan Uptime => State == StreamState.Live && _liveSinceTicks != 0
        ? Stopwatch.GetElapsedTime(_liveSinceTicks)
        : TimeSpan.Zero;

    /// <summary>How much encoded audio to hold when the network stalls, in seconds.</summary>
    public double BufferSeconds { get; set; } = 4;

    public event EventHandler<StreamStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised when connecting worked out the server's type and wrote it onto the profile, so whoever
    /// owns the server list can save it. Without this the detection would be redone on every run.
    /// </summary>
    public event EventHandler? ServerTypeDetected;

    public void Start(ServerProfile profile, EncoderSettings encoder, byte[] streamHeader)
    {
        if (State.IsBroadcasting()) throw new InvalidOperationException("Already broadcasting.");

        _profile = profile;
        _encoder = encoder;
        _streamHeader = streamHeader;
        _maxQueuedBytes = Math.Max(16 * 1024, (int)(encoder.BitrateKbps * 1000 / 8 * BufferSeconds));

        LastError = null;
        LastFailure = null;
        ConnectionNote = null;
        BytesSent = 0;
        ReconnectAttempts = 0;
        DroppedBlocks = 0;
        _throughputBytes = 0;
        _throughputKbps = 0;
        _throughputTicks = Stopwatch.GetTimestamp();
        ClearQueue();

        _cancellation = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cancellation.Token));
    }

    /// <summary>Called from the audio thread. Never blocks and never throws.</summary>
    public void Enqueue(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty || !State.IsBroadcasting()) return;

        var block = encoded.ToArray();

        lock (_queueLock)
        {
            _queue.Enqueue(block);
            _queuedBytes += block.Length;

            // Drop the oldest audio rather than the newest: after a stall, listeners would rather
            // rejoin at the present moment than work through a backlog.
            while (_queuedBytes > _maxQueuedBytes && _queue.Count > 1)
            {
                var dropped = _queue.Dequeue();
                _queuedBytes -= dropped.Length;
                DroppedBlocks++;
            }
        }

        _dataSignal.Release();
    }

    /// <summary>Queues a now-playing title; it is sent once the connection is live.</summary>
    public void SetMetadata(string title) => _pendingMetadata = title;

    public async Task StopAsync()
    {
        if (_cancellation is null) return;

        await _cancellation.CancelAsync().ConfigureAwait(false);

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation.Dispose();
        _cancellation = null;
        _pump = null;

        ClearQueue();
        Transition(StreamState.Idle, null);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            IStreamSink? sink = null;

            try
            {
                Transition(attempt == 0 ? StreamState.Connecting : StreamState.Reconnecting, null);

                bool detected;
                (sink, detected) = await SinkResolver
                    .CreateAsync(_profile!, _encoder!, cancellationToken)
                    .ConfigureAwait(false);

                if (detected) ServerTypeDetected?.Invoke(this, EventArgs.Empty);

                // The handshake can settle what the probe could not - a SHOUTcast answering "OK2" is
                // telling us it is v2 - so the type is worth reading again once the server has spoken.
                var typeBeforeHandshake = _profile!.ServerType;

                await sink.ConnectAsync(cancellationToken).ConfigureAwait(false);

                if (_profile.ServerType != typeBeforeHandshake) ServerTypeDetected?.Invoke(this, EventArgs.Empty);

                ConnectionNote = sink.ConnectionNote;
                attempt = 0;
                _liveSinceTicks = Stopwatch.GetTimestamp();
                LastError = null;
                LastFailure = null;
                Transition(StreamState.Live, sink.ConnectionNote);

                // Ogg needs its identification pages at the head of every connection, so they are
                // re-sent verbatim after a reconnect.
                if (_streamHeader.Length > 0)
                {
                    await sink.SendAsync(_streamHeader, cancellationToken).ConfigureAwait(false);
                }

                await SendLoopAsync(sink, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (StreamException ex)
            {
                LastError = Explain(ex);
                LastFailure = ex.Failure;

                // A wrong password will never come right on its own, so stop rather than hammering
                // the server and burying the real reason under retry messages.
                if (!ex.Failure.WorthRetrying())
                {
                    Transition(StreamState.Failed, LastError);
                    break;
                }

                Transition(StreamState.Reconnecting, LastError);
            }
            catch (Exception ex)
            {
                LastError = $"Unexpected problem with the connection: {ex.Message}";
                LastFailure = StreamFailure.Protocol;
                Transition(StreamState.Reconnecting, LastError);
            }
            finally
            {
                if (sink is not null) await sink.DisposeAsync().ConfigureAwait(false);
                _liveSinceTicks = 0;
            }

            if (cancellationToken.IsCancellationRequested) break;

            var delay = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
            attempt++;
            ReconnectAttempts++;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// How long the connection lasted before it broke. Zero if it never got on air at all.
    /// </summary>
    private TimeSpan TimeOnAir => _liveSinceTicks == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_liveSinceTicks);

    /// <summary>
    /// Adds what the shape of a failure says about it.
    /// <para>
    /// A server that accepts a broadcast, says OK, and then drops it a second or two later is not a
    /// network problem, and "the connection to the server was lost" sends the user looking at their
    /// internet. It is nearly always the server disagreeing with what is being sent - a bitrate above
    /// what the stream allows, a format it is not configured for, or another encoder already on. Deck
    /// knows the shape even when the server will not say, so it says the shape.
    /// </para>
    /// </summary>
    private string Explain(StreamException ex)
    {
        if (ex.Failure != StreamFailure.Network) return ex.Message;

        var onAir = TimeOnAir;
        if (onAir == TimeSpan.Zero || onAir > TimeSpan.FromSeconds(20)) return ex.Message;

        var howLong = onAir.TotalSeconds < 10
            ? $"{onAir.TotalSeconds:0.0} seconds"
            : $"{onAir.TotalSeconds:0} seconds";

        // Nothing at all got through. The sign-in was accepted and the server then closed the door,
        // which is a different fault from a stream that ran and stopped - and worth saying apart,
        // because it means the server objected to the broadcast rather than to the network.
        if (BytesSent == 0)
        {
            return $"{_profile?.Host} accepted the sign-in and then closed the connection before any audio " +
                   $"reached it, after {howLong}. The server is refusing the broadcast rather than the " +
                   "connection: check that the station has a name, that the quality is one this server " +
                   "allows, and that nothing else is already broadcasting to it.";
        }

        var sent = BytesSent < 1024 ? $"{BytesSent} bytes" : $"{BytesSent / 1024} KB";

        // The family, not the two versions by name. Listing them meant a server that knows only that
        // it is SHOUTcast - which is most of a list imported from another encoder - fell through to
        // the generic advice and lost the bitrate line, which is the usual answer.
        var whatToCheck = _profile?.ServerType switch
        {
            { } type when type.IsShoutcast() =>
                $"A SHOUTcast server that drops a broadcast this quickly is usually refusing the audio " +
                $"rather than the connection. Check that the quality Deck is sending ({_encoder?.BitrateKbps} kbps " +
                $"{_encoder?.Codec.DisplayName()}) is what this stream is set up for - a bitrate above what the " +
                $"server allows is the most common cause - and that nothing else is already broadcasting to it.",

            _ =>
                "A server that drops a broadcast this quickly is usually refusing the audio rather than the " +
                "connection. Check that the format and bitrate are ones this server accepts, and that nothing " +
                "else is already broadcasting to the same stream address.",
        };

        return $"{ex.Message} It had been on air for {howLong} and had sent {sent}. {whatToCheck}";
    }

    private async Task SendLoopAsync(IStreamSink sink, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var metadata = Interlocked.Exchange(ref _pendingMetadata, null);
            if (metadata is not null)
            {
                await sink.UpdateMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
            }

            var block = TryDequeue();
            if (block is null)
            {
                await _dataSignal.WaitAsync(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await sink.SendAsync(block, cancellationToken).ConfigureAwait(false);
            BytesSent += block.Length;
        }
    }

    private byte[]? TryDequeue()
    {
        lock (_queueLock)
        {
            if (_queue.Count == 0) return null;
            var block = _queue.Dequeue();
            _queuedBytes -= block.Length;
            return block;
        }
    }

    private void ClearQueue()
    {
        lock (_queueLock)
        {
            _queue.Clear();
            _queuedBytes = 0;
        }
    }

    private void Transition(StreamState state, string? message)
    {
        if (State == state && message is null) return;

        State = state;
        StateChanged?.Invoke(this, new StreamStateChangedEventArgs(state, message));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _dataSignal.Dispose();
    }
}
