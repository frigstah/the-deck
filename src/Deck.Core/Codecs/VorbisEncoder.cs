using OggVorbisEncoder;

namespace Deck.Core.Codecs;

/// <summary>
/// Ogg Vorbis (D3). Royalty-free and still accepted by every Icecast server, which is why it earns
/// a place even though Opus beats it at the same bitrate. Some older listener software and a few
/// hosts only speak Vorbis.
/// <para>
/// Vorbis here is variable bitrate, so the chosen figure is a target the encoder averages around
/// rather than a hard rate. That is how Vorbis is meant to be used and it sounds better for it.
/// </para>
/// </summary>
public sealed class VorbisEncoder : IAudioEncoder
{
    private readonly OggStream _oggStream;
    private readonly ProcessingState _processingState;
    private readonly GrowableBuffer _output = new();

    private float[][] _planar;
    private int _planarCapacity;
    private bool _finished;

    public VorbisEncoder(EncoderSettings settings)
    {
        Settings = settings.Normalised();

        var info = VorbisInfo.InitVariableBitRate(Settings.Channels, Settings.SampleRate, QualityFor(Settings.BitrateKbps));

        _oggStream = new OggStream(Random.Shared.Next(1, int.MaxValue));
        _processingState = ProcessingState.Create(info);

        _planarCapacity = 4096;
        _planar = CreatePlanar(Settings.Channels, _planarCapacity);

        StreamHeader = BuildHeaders(info);
    }

    public StreamCodec Codec => StreamCodec.OggVorbis;

    public EncoderSettings Settings { get; }

    public string ContentType => StreamCodec.OggVorbis.ContentType();

    /// <summary>The three Vorbis header pages. Re-sent verbatim whenever a connection restarts.</summary>
    public byte[] StreamHeader { get; }

    public ReadOnlySpan<byte> Encode(ReadOnlySpan<float> interleaved)
    {
        if (_finished || interleaved.IsEmpty) return ReadOnlySpan<byte>.Empty;

        _output.Clear();

        var channels = Settings.Channels;
        var frames = interleaved.Length / channels;
        EnsurePlanar(frames);

        // Vorbis wants one array per channel; the pipeline carries interleaved samples.
        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * channels;
            for (var ch = 0; ch < channels; ch++) _planar[ch][frame] = interleaved[baseIndex + ch];
        }

        _processingState.WriteData(_planar, frames);
        DrainPackets();

        return _output.AsSpan();
    }

    public ReadOnlySpan<byte> Finish()
    {
        if (_finished) return ReadOnlySpan<byte>.Empty;
        _finished = true;

        _output.Clear();

        try
        {
            _processingState.WriteEndOfStream();
            DrainPackets(flush: true);
        }
        catch (Exception)
        {
            // The stream is being torn down; whatever has already been written is still playable.
        }

        return _output.AsSpan();
    }

    private void DrainPackets(bool flush = false)
    {
        while (_processingState.PacketOut(out var packet))
        {
            _oggStream.PacketIn(packet);
        }

        WritePages(flush);
    }

    /// <summary>
    /// Deliberately not guarded on <c>OggStream.Finished</c>. That flag is raised as soon as the
    /// end-of-stream packet goes in, so testing it before PageOut swallows the final page and
    /// leaves the stream without its end-of-stream marker.
    /// </summary>
    private void WritePages(bool flush)
    {
        while (_oggStream.PageOut(out var page, flush))
        {
            _output.Append(page.Header);
            _output.Append(page.Body);
        }
    }

    private byte[] BuildHeaders(VorbisInfo info)
    {
        var comments = new Comments();
        comments.AddTag("ENCODER", "The Deck");

        var infoPacket = HeaderPacketBuilder.BuildInfoPacket(info);
        var commentsPacket = HeaderPacketBuilder.BuildCommentsPacket(comments);
        var booksPacket = HeaderPacketBuilder.BuildBooksPacket(info);

        _oggStream.PacketIn(infoPacket);
        _oggStream.PacketIn(commentsPacket);
        _oggStream.PacketIn(booksPacket);

        // Headers must occupy their own pages before any audio, so force them out now.
        var header = new GrowableBuffer(4096);
        while (_oggStream.PageOut(out var page, true))
        {
            header.Append(page.Header);
            header.Append(page.Body);
        }

        return header.ToArray();
    }

    /// <summary>
    /// Maps a target bitrate onto a Vorbis quality setting. Vorbis is quality-driven rather than
    /// rate-driven; these are the usual stereo equivalences.
    /// </summary>
    private static float QualityFor(int bitrateKbps) => bitrateKbps switch
    {
        <= 48 => 0.0f,
        <= 64 => 0.1f,
        <= 80 => 0.2f,
        <= 96 => 0.3f,
        <= 128 => 0.4f,
        <= 160 => 0.5f,
        <= 192 => 0.6f,
        <= 224 => 0.7f,
        <= 256 => 0.8f,
        <= 320 => 0.9f,
        _ => 1.0f,
    };

    private void EnsurePlanar(int frames)
    {
        if (frames <= _planarCapacity) return;

        _planarCapacity = frames * 2;
        _planar = CreatePlanar(Settings.Channels, _planarCapacity);
    }

    private static float[][] CreatePlanar(int channels, int capacity)
    {
        var planar = new float[channels][];
        for (var ch = 0; ch < channels; ch++) planar[ch] = new float[capacity];
        return planar;
    }

    public void Dispose()
    {
        // Nothing unmanaged: the encoder is a fully managed port.
    }
}
