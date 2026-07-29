using NAudio.Lame;
using NAudio.Wave;

namespace Sirs.Core.Codecs;

/// <summary>
/// MP3 via LAME (D1). MP3 patents expired in 2017, so this ships freely - unlike AAC, which is why
/// the spec defers it. LAME is fed 16-bit PCM, which is transparent well beyond the bitrates any
/// internet station uses.
/// </summary>
public sealed class Mp3Encoder : IAudioEncoder
{
    private readonly LameMP3FileWriter _lame;
    private readonly SinkStream _sink = new();
    private readonly GrowableBuffer _output = new();
    private byte[] _pcmBytes = new byte[8192];
    private bool _finished;

    public Mp3Encoder(EncoderSettings settings)
    {
        Settings = settings.Normalised();

        var waveFormat = new WaveFormat(Settings.SampleRate, 16, Settings.Channels);
        _lame = new LameMP3FileWriter(_sink, waveFormat, Settings.BitrateKbps);
    }

    public StreamCodec Codec => StreamCodec.Mp3;

    public EncoderSettings Settings { get; }

    public string ContentType => StreamCodec.Mp3.ContentType();

    /// <summary>MP3 is a bare frame stream - nothing to send up front.</summary>
    public byte[] StreamHeader { get; } = [];

    public ReadOnlySpan<byte> Encode(ReadOnlySpan<float> interleaved)
    {
        if (_finished || interleaved.IsEmpty) return ReadOnlySpan<byte>.Empty;

        var byteCount = interleaved.Length * 2;
        if (_pcmBytes.Length < byteCount) _pcmBytes = new byte[byteCount * 2];

        for (var i = 0; i < interleaved.Length; i++)
        {
            // Clamp before scaling: the safety limiter should have handled this, but a stray
            // out-of-range sample must never wrap around into a loud click.
            var clamped = AudioClamp(interleaved[i]);
            var value = (short)(clamped * 32767f);
            _pcmBytes[i * 2] = (byte)(value & 0xFF);
            _pcmBytes[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        _output.Clear();
        _sink.Target = _output;
        _lame.Write(_pcmBytes, 0, byteCount);
        _sink.Target = null;

        return _output.AsSpan();
    }

    public ReadOnlySpan<byte> Finish()
    {
        if (_finished) return ReadOnlySpan<byte>.Empty;
        _finished = true;

        _output.Clear();
        _sink.Target = _output;
        _lame.Flush();
        _sink.Target = null;

        return _output.AsSpan();
    }

    private static float AudioClamp(float value) =>
        value > 1f ? 1f : value < -1f ? -1f : value;

    public void Dispose()
    {
        // Dispose emits LAME's final frames; route them nowhere since Finish() already ran, or
        // discard them if the caller never called Finish.
        _sink.Target = null;
        _lame.Dispose();
    }

    /// <summary>
    /// A write-only Stream that forwards LAME's output into whichever buffer is currently active.
    /// LameMP3FileWriter insists on a Stream; this avoids a MemoryStream that would grow forever.
    /// </summary>
    private sealed class SinkStream : Stream
    {
        public GrowableBuffer? Target { get; set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get => 0; set { } }

        public override void Write(byte[] buffer, int offset, int count) =>
            Target?.Append(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => Target?.Append(buffer);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
