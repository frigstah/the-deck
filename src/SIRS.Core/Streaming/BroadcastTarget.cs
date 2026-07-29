using Sirs.Core.Audio;
using Sirs.Core.Codecs;
using Sirs.Core.Servers;

namespace Sirs.Core.Streaming;

/// <summary>
/// One destination in a broadcast (C12): its own encoder, its own connection, and whatever format
/// conversion gets it from the shared capture format to the one its server was set up for.
/// <para>
/// Every target encodes separately rather than sharing one encoder. That costs CPU, and it is also
/// the entire point: a backup relay can run at a different bitrate, a mobile mount at 64 kbps can
/// sit alongside a 320 kbps main, and one server falling over cannot disturb the others.
/// </para>
/// </summary>
public sealed class BroadcastTarget : IAsyncDisposable
{
    private readonly object _encoderLock = new();
    private readonly AudioFormat _captureFormat;

    private IAudioEncoder? _encoder;
    private FormatConverter? _converter;

    public BroadcastTarget(ServerProfile profile, AudioFormat captureFormat, bool isPrimary)
    {
        Profile = profile;
        Settings = profile.Encoder.Normalised();
        IsPrimary = isPrimary;
        _captureFormat = captureFormat;
    }

    public ServerProfile Profile { get; }

    public EncoderSettings Settings { get; }

    /// <summary>The server the user picked, as opposed to one they added as a backup.</summary>
    public bool IsPrimary { get; }

    public StreamConnection Connection { get; } = new();

    public StreamState State => Connection.State;

    public string Name => Profile.Name;

    /// <summary>Whether this target needs work beyond encoding to match the capture format.</summary>
    public bool NeedsConversion => _captureFormat != Settings.Format;

    public void Start()
    {
        byte[] header;

        lock (_encoderLock)
        {
            _encoder?.Dispose();
            _encoder = EncoderFactory.Create(Settings);
            header = _encoder.StreamHeader;

            _converter = new FormatConverter(_captureFormat, Settings.Format);
        }

        Connection.Start(Profile, Settings, header);
    }

    /// <summary>Called on the audio thread with a block in the shared capture format.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        lock (_encoderLock)
        {
            if (_encoder is null || _converter is null) return;

            var block = _converter.Process(interleaved);
            if (block.IsEmpty) return;

            var encoded = _encoder.Encode(block);
            if (!encoded.IsEmpty) Connection.Enqueue(encoded);
        }
    }

    public void SetMetadata(string title) => Connection.SetMetadata(title);

    public async Task StopAsync()
    {
        await Connection.StopAsync().ConfigureAwait(false);

        lock (_encoderLock)
        {
            _encoder?.Dispose();
            _encoder = null;
            _converter = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Connection.DisposeAsync().ConfigureAwait(false);
    }
}
