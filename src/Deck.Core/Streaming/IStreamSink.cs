using Deck.Core.Codecs;
using Deck.Core.Servers;

namespace Deck.Core.Streaming;

/// <summary>
/// One connected server. Implementations own a socket and speak whichever source protocol the
/// server expects; everything above this interface is protocol-agnostic.
/// </summary>
public interface IStreamSink : IAsyncDisposable
{
    /// <summary>
    /// Anything worth telling the user about how the connection was made - for example that Deck
    /// had to move to the source port. Null when there is nothing surprising to report.
    /// </summary>
    string? ConnectionNote { get; }

    /// <summary>Opens the socket and completes the source handshake, or throws a <see cref="StreamException"/>.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Pushes a now-playing title. MP3 and AAC carry no metadata of their own, so this goes out of
    /// band over the server's admin endpoint; container formats like Ogg ignore it.
    /// </summary>
    Task UpdateMetadataAsync(string title, CancellationToken cancellationToken);
}

public static class SinkFactory
{
    public static IStreamSink Create(ServerProfile profile, EncoderSettings encoder) => profile.ServerType switch
    {
        ServerType.Icecast => new IcecastSink(profile, encoder),
        ServerType.ShoutcastV1 or ServerType.ShoutcastV2 => new ShoutcastSink(profile, encoder),
        _ => throw new StreamException(
            StreamFailure.Protocol,
            "Deck does not know what kind of server this is yet. Open the server settings and press Test, and it will work it out."),
    };
}
