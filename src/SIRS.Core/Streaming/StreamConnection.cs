using System.Diagnostics;
using Sirs.Core.Codecs;
using Sirs.Core.Servers;

namespace Sirs.Core.Streaming;

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
/// before reconnecting, which turns a momentary blip into dead air; SIRS has no reason to do that.
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

    public void Start(ServerProfile profile, EncoderSettings encoder, byte[] streamHeader)
    {
        if (State.IsBroadcasting()) throw new InvalidOperationException("Already broadcasting.");

        _profile = profile;
        _encoder = encoder;
        _streamHeader = streamHeader;
        _maxQueuedBytes = Math.Max(16 * 1024, (int)(encoder.BitrateKbps * 1000 / 8 * BufferSeconds));

        LastError = null;
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

                sink = SinkFactory.Create(_profile!, _encoder!);
                await sink.ConnectAsync(cancellationToken).ConfigureAwait(false);

                ConnectionNote = sink.ConnectionNote;
                attempt = 0;
                _liveSinceTicks = Stopwatch.GetTimestamp();
                LastError = null;
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
                LastError = ex.Message;

                // A wrong password will never come right on its own, so stop rather than hammering
                // the server and burying the real reason under retry messages.
                if (ex.Failure == StreamFailure.Authentication)
                {
                    Transition(StreamState.Failed, ex.Message);
                    break;
                }

                Transition(StreamState.Reconnecting, ex.Message);
            }
            catch (Exception ex)
            {
                LastError = $"Unexpected problem with the connection: {ex.Message}";
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
