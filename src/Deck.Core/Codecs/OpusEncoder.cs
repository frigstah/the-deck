using Concentus;
using Concentus.Enums;

namespace Deck.Core.Codecs;

/// <summary>
/// Ogg Opus (D2). Opus is royalty-free and clearly better than MP3 below about 96 kbps, which is
/// why the spec leans on it instead of licensing AAC. Concentus is a managed port, so there is no
/// native dependency to ship.
/// </summary>
public sealed class OpusEncoder : IAudioEncoder
{
    /// <summary>20 ms frames: the Opus default, and a good balance of overhead against latency.</summary>
    private const int FrameMilliseconds = 20;

    private const int MaxPacketBytes = 4000;

    private readonly IOpusEncoder _encoder;
    private readonly OggStreamWriter _ogg;
    private readonly GrowableBuffer _output = new();
    private readonly short[] _frame;
    private readonly byte[] _packet = new byte[MaxPacketBytes];
    private readonly int _framesPerPacket;
    private readonly int _granulePerFrame;

    private int _frameFill;
    private long _granulePosition;
    private bool _finished;

    public OpusEncoder(EncoderSettings settings)
    {
        Settings = settings.Normalised();

        _encoder = OpusCodecFactory.CreateEncoder(
            Settings.SampleRate,
            Settings.Channels,
            OpusApplication.OPUS_APPLICATION_AUDIO,
            null);

        _encoder.Bitrate = Settings.BitrateKbps * 1000;
        _encoder.UseVBR = true;
        _encoder.UseConstrainedVBR = true;
        _encoder.Complexity = 8;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_AUTO;

        _framesPerPacket = Settings.SampleRate * FrameMilliseconds / 1000;
        _frame = new short[_framesPerPacket * Settings.Channels];

        // Granule positions are always counted at 48 kHz, whatever the encoder input rate is.
        _granulePerFrame = 48000 * FrameMilliseconds / 1000;

        _ogg = new OggStreamWriter(Random.Shared.Next(1, int.MaxValue));
        StreamHeader = BuildHeaderPages();
    }

    public StreamCodec Codec => StreamCodec.OggOpus;

    public EncoderSettings Settings { get; }

    public string ContentType => StreamCodec.OggOpus.ContentType();

    /// <summary>The OpusHead and OpusTags pages. Re-sent verbatim whenever a connection restarts.</summary>
    public byte[] StreamHeader { get; }

    public ReadOnlySpan<byte> Encode(ReadOnlySpan<float> interleaved)
    {
        if (_finished || interleaved.IsEmpty) return ReadOnlySpan<byte>.Empty;

        _output.Clear();

        var position = 0;
        while (position < interleaved.Length)
        {
            var take = Math.Min(_frame.Length - _frameFill, interleaved.Length - position);
            for (var i = 0; i < take; i++)
            {
                var sample = interleaved[position + i];
                var clamped = sample > 1f ? 1f : sample < -1f ? -1f : sample;
                _frame[_frameFill + i] = (short)(clamped * 32767f);
            }

            _frameFill += take;
            position += take;

            if (_frameFill == _frame.Length)
            {
                EncodeFrame();
                _frameFill = 0;
            }
        }

        return _output.AsSpan();
    }

    private void EncodeFrame()
    {
        var length = _encoder.Encode(_frame, _framesPerPacket, _packet, MaxPacketBytes);
        if (length <= 0) return;

        _granulePosition += _granulePerFrame;
        _ogg.AddPacket(_packet.AsSpan(0, length), _granulePosition, _output);
    }

    public ReadOnlySpan<byte> Finish()
    {
        if (_finished) return ReadOnlySpan<byte>.Empty;
        _finished = true;

        _output.Clear();

        // Pad the last partial frame with silence so the final packet is a legal frame size.
        if (_frameFill > 0)
        {
            Array.Clear(_frame, _frameFill, _frame.Length - _frameFill);
            EncodeFrame();
            _frameFill = 0;
        }

        _ogg.Flush(_output, endOfStream: true);
        return _output.AsSpan();
    }

    private byte[] BuildHeaderPages()
    {
        var buffer = new GrowableBuffer(512);

        // Lookahead is reported at the encoder's input rate; the header wants 48 kHz samples.
        var preSkip = (ushort)((long)_encoder.Lookahead * 48000 / Settings.SampleRate);

        Span<byte> head = stackalloc byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1; // version
        head[9] = (byte)Settings.Channels;
        BitConverter.TryWriteBytes(head[10..], preSkip);
        BitConverter.TryWriteBytes(head[12..], Settings.SampleRate);
        BitConverter.TryWriteBytes(head[16..], (short)0); // output gain
        head[18] = 0; // channel mapping family 0: mono or plain stereo
        _ogg.AddPacket(head, 0, buffer, forceFlush: true);

        var vendor = System.Text.Encoding.UTF8.GetBytes("The Deck");
        var tags = new GrowableBuffer(64);
        tags.Append("OpusTags"u8);
        BitConverter.TryWriteBytes(tags.Reserve(4), vendor.Length);
        tags.Append(vendor);
        BitConverter.TryWriteBytes(tags.Reserve(4), 0); // no user comments
        _ogg.AddPacket(tags.AsSpan(), 0, buffer, forceFlush: true);

        return buffer.ToArray();
    }

    public void Dispose() => (_encoder as IDisposable)?.Dispose();
}
