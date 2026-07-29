namespace Sirs.Core.Codecs;

/// <summary>
/// Turns interleaved float audio into a byte stream ready for the network. Implementations are used
/// from a single thread (the capture callback), so they are deliberately not thread-safe.
/// </summary>
public interface IAudioEncoder : IDisposable
{
    StreamCodec Codec { get; }

    EncoderSettings Settings { get; }

    string ContentType { get; }

    /// <summary>
    /// Bytes that must lead every connection - the Ogg identification and comment pages for Opus,
    /// empty for MP3. Kept as an array because it has to be re-sent verbatim after a reconnect.
    /// </summary>
    byte[] StreamHeader { get; }

    /// <summary>
    /// Encodes one block. The returned span points at an internal buffer valid only until the next
    /// call, so callers copy what they need before returning.
    /// </summary>
    ReadOnlySpan<byte> Encode(ReadOnlySpan<float> interleaved);

    /// <summary>Flushes any buffered audio at the end of a broadcast or recording.</summary>
    ReadOnlySpan<byte> Finish();
}
