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
            "Deck does not know what kind of server this is. Open the server settings and pick the type your host told you to use."),
    };
}

/// <summary>
/// Turns a profile into a connected sink, working out the server type first if nobody has yet.
/// </summary>
/// <remarks>
/// <para>
/// The type is stored on a profile as <see cref="ServerType.Unknown"/> until something resolves it,
/// and the picker offers that state as "Detect automatically" - so the honest reading of Unknown is
/// "detect it now", not "give up". It used to mean the latter: <see cref="ServerProbe"/> was called
/// from the Test button and nowhere else, so a server that was filled in and saved without pressing
/// Test failed at Go live with a message asking the user to go and press Test. Deck already knew how
/// to find the answer and was asking someone else to fetch it.
/// </para>
/// <para>
/// Resolving here rather than in the editor covers every way a broadcast can start - the Go live
/// button, connect-on-start, the remote control endpoint, a reconnect after the type was cleared -
/// with one piece of code, and the cost lands where the user is already waiting for a connection.
/// </para>
/// </remarks>
public static class SinkResolver
{
    /// <summary>
    /// Creates the sink for <paramref name="profile"/>, probing the server first if its type is not
    /// known. A successful probe is written back to the profile, so this happens once and not on
    /// every reconnect. Returns true in <c>Detected</c> when the caller has something new to save.
    /// </summary>
    public static async Task<(IStreamSink Sink, bool Detected)> CreateAsync(
        ServerProfile profile,
        EncoderSettings encoder,
        CancellationToken cancellationToken)
    {
        if (profile.ServerType != ServerType.Unknown)
        {
            return (SinkFactory.Create(profile, encoder), false);
        }

        var probe = await ServerProbe.DetectAsync(profile.Host, profile.Port, profile.UseTls, cancellationToken)
            .ConfigureAwait(false);

        if (probe.DetectedType == ServerType.Unknown)
        {
            // Two different failures, and the difference matters: one is "your address or port is
            // wrong", the other is "the address is right, I just need to be told the type". The probe
            // already phrases both, so pass its message through rather than inventing a third.
            throw new StreamException(StreamFailure.Protocol, probe.Reachable
                ? probe.Message + " Open the server settings and choose it there."
                : probe.Message);
        }

        profile.ServerType = probe.DetectedType;
        return (SinkFactory.Create(profile, encoder), true);
    }
}
