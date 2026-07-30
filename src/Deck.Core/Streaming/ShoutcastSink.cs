using System.Text;
using Deck.Core.Codecs;
using Deck.Core.Servers;

namespace Deck.Core.Streaming;

/// <summary>
/// SHOUTcast source client covering both v1 and v2 (C5). Both are reached with the same legacy
/// source handshake; v2 selects a stream by appending <c>:#sid</c> to the password.
/// <para>
/// Which is why a profile is allowed to arrive as <see cref="ServerType.Shoutcast"/> with no version
/// on it at all. Nothing above the handshake needs to know, and the reply says which it was, so a
/// profile that only knows its family gets on air and comes out of the connection knowing more than
/// it went in with.
/// </para>
/// <para>
/// SHOUTcast listens for sources on the port after the one listeners use, and hosts are split on
/// which of the two they quote. Rather than making the user find out, Deck tries the port they
/// entered and then the one after it, and says which worked.
/// </para>
/// </summary>
public sealed class ShoutcastSink(ServerProfile profile, EncoderSettings encoder) : IStreamSink
{
    private readonly ServerProfile _profile = profile;
    private readonly EncoderSettings _encoder = encoder;

    private SourceConnection? _connection;
    private int _sourcePort;
    private string? _nameNote;

    /// <summary>
    /// Anything worth telling the user about how this connection was made: the port Deck had to move to,
    /// and a station name it had to invent. Both are things that worked but were not what was asked for,
    /// which is the category that belongs in the log rather than in an error.
    /// </summary>
    public string? ConnectionNote { get; private set; }

    /// <summary>Admin commands go to the listener port, which is always one below the source port.</summary>
    private int ListenPort => Math.Max(1, _sourcePort - 1);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        StreamException? lastFailure = null;

        // The entered port first: if the host quoted the source port directly, this succeeds and
        // nothing surprising is reported.
        foreach (var port in new[] { _profile.Port, _profile.Port + 1 })
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await TryConnectAsync(port, cancellationToken).ConfigureAwait(false);
                _sourcePort = port;

                var notes = new List<string>();

                if (port != _profile.Port)
                {
                    notes.Add(
                        $"Connected on port {port}. SHOUTcast takes broadcasts on the port after the one listeners use, so Deck moved up from {_profile.Port} for you.");
                }

                if (_nameNote is not null) notes.Add(_nameNote);

                ConnectionNote = notes.Count == 0 ? null : string.Join(" ", notes);

                return;
            }
            catch (StreamException ex) when (ex.Failure is not StreamFailure.Authentication)
            {
                // Authentication failures are conclusive - a different port will not fix a wrong
                // password, and retrying would only muddle the error the user sees.
                //
                // Everything else keeps whichever answer says more. The second port is a guess, and
                // when it is wrong it fails with "nothing is listening" - which was then reported as
                // the reason, burying an actual reply from the port the user entered. Somebody whose
                // stream was already taken got told their server could not be reached, and went
                // looking at their connection instead of at the encoder still running upstairs.
                lastFailure = Better(lastFailure, ex);
                await DisposeConnectionAsync().ConfigureAwait(false);
            }
        }

        throw lastFailure ?? new StreamException(
            StreamFailure.Network,
            $"Could not reach {_profile.Host} on port {_profile.Port} or {_profile.Port + 1}.");
    }

    /// <summary>
    /// The more informative of two failures. A server that answered - even to refuse - knows more
    /// about what is wrong than a port that nothing was listening on, so anything beats
    /// <see cref="StreamFailure.Network"/>; between two of the same kind the earlier one wins,
    /// because that is the port the user actually entered.
    /// </summary>
    private static StreamException Better(StreamException? existing, StreamException candidate)
    {
        if (existing is null) return candidate;
        if (existing.Failure != StreamFailure.Network) return existing;

        return candidate.Failure == StreamFailure.Network ? existing : candidate;
    }

    private async Task TryConnectAsync(int port, CancellationToken cancellationToken)
    {
        var connection = new SourceConnection();
        _connection = connection;

        await connection.OpenAsync(_profile.Host, port, _profile.UseTls, cancellationToken).ConfigureAwait(false);
        await connection.WriteAsciiAsync(BuildHandshake(), cancellationToken).ConfigureAwait(false);
        await connection.FlushAsync(cancellationToken).ConfigureAwait(false);

        var response = await connection.ReadHandshakeReplyAsync(cancellationToken).ConfigureAwait(false);
        Interpret(response, port);

        // Keep an ear on the socket for the rest of the broadcast. A DNAS that accepts a source and
        // then objects to it says so and hangs up, and that sentence is the only explanation there is.
        connection.ListenWhileSending();
    }

    private string BuildHandshake()
    {
        var builder = new StringBuilder();

        // v2 routes a legacy source to a particular stream through the password field.
        var password = _profile.ServerType == ServerType.ShoutcastV2 && _profile.StreamId != 1
            ? $"{_profile.Password}:#{_profile.StreamId}"
            : _profile.Password;

        builder.Append($"{Sanitise(password)}\r\n");

        // Never omitted, whatever the profile says. A SHOUTcast server that gets no icy-name accepts the
        // password, answers OK2, and closes the connection - no error, no reason, and Deck reporting
        // "the connection to the server was lost" four times over. The editor now asks for a station
        // name, but a profile saved before it did must not fall into that hole either, so the server's
        // own label stands in. Verified against a real DNAS: with the header, nine seconds of audio and
        // counting; without it, gone forty milliseconds after sign-in.
        var stationName = Sanitise(_profile.StationName);

        if (stationName.Length == 0)
        {
            stationName = Sanitise(_profile.Name);
            if (stationName.Length == 0) stationName = "Deck";

            // Said out loud rather than done quietly: the server's own label is a private note to
            // yourself - "Backup relay" - and listeners are about to see it as the station's name.
            // Better to be on air with the wrong name and told about it than refused, which is what
            // requiring a name would have done to a profile that now works.
            _nameNote = $"This server has no station name, so listeners will see \"{stationName}\". " +
                        "Set one under \"What listeners see\".";
        }

        builder.Append($"icy-name:{stationName}\r\n");

        AppendIfPresent(builder, "icy-genre", _profile.Genre);
        AppendIfPresent(builder, "icy-url", _profile.Website);

        builder.Append($"icy-pub:{(_profile.ListInDirectory ? 1 : 0)}\r\n");
        builder.Append($"icy-br:{_encoder.BitrateKbps}\r\n");
        builder.Append($"content-type:{_encoder.Codec.ShoutcastFormatName()}\r\n");
        builder.Append("icy-audio-info:");
        builder.Append($"ice-samplerate={_encoder.SampleRate};ice-bitrate={_encoder.BitrateKbps};ice-channels={_encoder.Channels}\r\n");
        builder.Append("\r\n");

        return builder.ToString();
    }

    private static void AppendIfPresent(StringBuilder builder, string header, string? value)
    {
        var clean = Sanitise(value);
        if (clean.Length == 0) return;
        builder.Append($"{header}:{clean}\r\n");
    }

    /// <summary>Strips anything that would break out of the current header line.</summary>
    private static string Sanitise(string? value) =>
        (value ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();

    private void Interpret(string response, int port)
    {
        var trimmed = response.TrimStart();

        if (trimmed.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            // "OK" from v1, "OK2" from v2 in legacy mode - which is the version question answered by
            // the server itself, on a connection that was being made anyway. Better evidence than a
            // banner on the listener port, and free.
            //
            // Narrowed one way only. "OK2" is a positive claim to being v2 and is acted on; a plain
            // "OK" is only the absence of that claim, so it leaves the profile saying SHOUTcast
            // rather than asserting v1. Nothing is lost by staying undecided - the family connects
            // perfectly well - whereas writing v1 onto a v2 server would drop the stream id from
            // every metadata update afterwards.
            if (_profile.ServerType == ServerType.Shoutcast &&
                trimmed.StartsWith("OK2", StringComparison.OrdinalIgnoreCase))
            {
                _profile.ServerType = ServerType.ShoutcastV2;
            }

            return;
        }

        if (trimmed.Contains("invalid password", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("bad password", StringComparison.OrdinalIgnoreCase))
        {
            throw new StreamException(
                StreamFailure.Authentication,
                _profile.ServerType == ServerType.ShoutcastV2 && _profile.StreamId != 1
                    ? $"The server rejected the password for \"{_profile.Name}\". Check the password, and that stream number {_profile.StreamId} is the one your host gave you."
                    : $"The server rejected the password for \"{_profile.Name}\". Check the broadcast password.");
        }

        if (trimmed.Contains("Too many sources", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("already in use", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("stream is currently up", StringComparison.OrdinalIgnoreCase))
        {
            throw new StreamException(
                StreamFailure.MountInUse,
                "Something is already broadcasting to this server. Stop the other encoder first.");
        }

        // Hitting the listener port produces an ordinary HTTP reply. Let the caller move on to the
        // next candidate port rather than reporting this as a real error.
        if (trimmed.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ICY 4", StringComparison.OrdinalIgnoreCase))
        {
            throw new StreamException(
                StreamFailure.Protocol,
                $"Port {port} on {_profile.Host} is for listeners, not for broadcasting.");
        }

        throw new StreamException(
            StreamFailure.Protocol,
            trimmed.Length == 0
                ? $"{_profile.Host} did not answer on port {port}."
                : $"The server refused the connection: {trimmed.Split('\n')[0].Trim()}");
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_connection is null) throw new StreamException(StreamFailure.Network, "Not connected to the server.");
        await _connection.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateMetadataAsync(string title, CancellationToken cancellationToken)
    {
        if (_encoder.Codec == StreamCodec.OggOpus) return;

        var scheme = _profile.UseTls ? "https" : "http";
        var url = $"{scheme}://{_profile.Host}:{ListenPort}/admin.cgi" +
                  $"?mode=updinfo&pass={Uri.EscapeDataString(_profile.Password ?? string.Empty)}" +
                  $"&song={SourceConnection.EncodeTitle(title)}";

        if (_profile.ServerType == ServerType.ShoutcastV2) url += $"&sid={_profile.StreamId}";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // DNAS refuses admin requests that do not look like they came from a source client.
        request.Headers.UserAgent.ParseAdd("Deck/1.0 (Mozilla Compatible)");

        try
        {
            await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A title that fails to update is not worth dropping a broadcast over.
        }
    }

    private async Task DisposeConnectionAsync()
    {
        if (_connection is null) return;
        await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;
    }

    public ValueTask DisposeAsync() => new(DisposeConnectionAsync());
}
