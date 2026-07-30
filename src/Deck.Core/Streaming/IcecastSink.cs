using System.Net.Http.Headers;
using System.Text;
using Deck.Core.Codecs;
using Deck.Core.Servers;

namespace Deck.Core.Streaming;

/// <summary>
/// Icecast source client (C4). Uses the HTTP PUT method introduced in Icecast 2.4, and falls back
/// to the legacy SOURCE method automatically when the server answers with nothing at all - which is
/// exactly how Icecast versions older than 2.4 react to PUT. The user never has to know which of
/// the two their host runs.
/// </summary>
public sealed class IcecastSink(ServerProfile profile, EncoderSettings encoder) : IStreamSink
{
    private readonly ServerProfile _profile = profile;
    private readonly EncoderSettings _encoder = encoder;
    private SourceConnection? _connection;

    public string? ConnectionNote { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var response = await HandshakeAsync(useLegacySourceMethod: false, cancellationToken).ConfigureAwait(false);

        // An empty reply is Icecast < 2.4 rejecting PUT without so much as a status line.
        if (string.IsNullOrWhiteSpace(response))
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
            response = await HandshakeAsync(useLegacySourceMethod: true, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(response))
            {
                await DisposeConnectionAsync().ConfigureAwait(false);
                throw new StreamException(
                    StreamFailure.Protocol,
                    $"{_profile.Host} accepted the connection but did not answer. It may not be an Icecast server — open the server settings and press Test to let Deck check.");
            }

            ConnectionNote = "Connected using the older Icecast method.";
        }

        Interpret(response);

        // Icecast rarely volunteers anything mid-stream, but when it does - a mount taken over, a limit
        // reached - it is the reason the broadcast is about to end, and there is nowhere else to read it.
        _connection?.ListenWhileSending();
    }

    private async Task<string> HandshakeAsync(bool useLegacySourceMethod, CancellationToken cancellationToken)
    {
        var connection = new SourceConnection();
        _connection = connection;

        await connection.OpenAsync(_profile.Host, _profile.Port, _profile.UseTls, cancellationToken).ConfigureAwait(false);
        await connection.WriteAsciiAsync(BuildRequest(useLegacySourceMethod), cancellationToken).ConfigureAwait(false);
        await connection.FlushAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ReadHandshakeReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    private string BuildRequest(bool useLegacySourceMethod)
    {
        var builder = new StringBuilder();

        // SOURCE is the pre-2.4 method and has no 100-continue handshake.
        builder.Append(useLegacySourceMethod
            ? $"SOURCE {_profile.NormalisedMount} HTTP/1.0\r\n"
            : $"PUT {_profile.NormalisedMount} HTTP/1.1\r\n");

        builder.Append($"Host: {_profile.Host}:{_profile.Port}\r\n");
        builder.Append($"Authorization: {SourceConnection.BasicAuth(_profile.Username, _profile.Password)}\r\n");
        builder.Append("User-Agent: Deck/1.0\r\n");
        builder.Append($"Content-Type: {_encoder.Codec.ContentType()}\r\n");
        builder.Append($"Ice-Public: {(_profile.ListInDirectory ? 1 : 0)}\r\n");

        AppendIfPresent(builder, "Ice-Name", _profile.StationName);
        AppendIfPresent(builder, "Ice-Description", _profile.Description);
        AppendIfPresent(builder, "Ice-Genre", _profile.Genre);
        AppendIfPresent(builder, "Ice-URL", _profile.Website);

        builder.Append($"Ice-Bitrate: {_encoder.BitrateKbps}\r\n");
        builder.Append(
            $"Ice-Audio-Info: ice-samplerate={_encoder.SampleRate};ice-bitrate={_encoder.BitrateKbps};ice-channels={_encoder.Channels}\r\n");

        if (!useLegacySourceMethod) builder.Append("Expect: 100-continue\r\n");

        builder.Append("\r\n");
        return builder.ToString();
    }

    /// <summary>
    /// Station names come from user input and end up in HTTP headers, so anything that could start
    /// a new header line is stripped rather than escaped.
    /// </summary>
    private static void AppendIfPresent(StringBuilder builder, string header, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var clean = value.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        if (clean.Length == 0) return;

        builder.Append($"{header}: {clean}\r\n");
    }

    private void Interpret(string response)
    {
        var status = SourceConnection.ParseStatusCode(response);

        switch (status)
        {
            case 100 or 200:
                return;

            case 401:
                throw new StreamException(
                    StreamFailure.Authentication,
                    $"The server rejected the username or password for \"{_profile.Name}\". Check the password, and that the username is right — it is usually \"source\".");

            case 403 when response.Contains("Mountpoint in use", StringComparison.OrdinalIgnoreCase):
                throw new StreamException(
                    StreamFailure.MountInUse,
                    $"Something is already broadcasting to {_profile.NormalisedMount}. Stop the other encoder, or use a different stream address.");

            case 403 when response.Contains("content-type", StringComparison.OrdinalIgnoreCase):
                throw new StreamException(
                    StreamFailure.FormatRejected,
                    $"The server will not accept {_encoder.Codec.DisplayName()} on this stream address. Try a different format in the quality settings.");

            case 403:
                throw new StreamException(
                    StreamFailure.Protocol,
                    $"The server refused the connection: {FirstLine(response)}");

            case null:
                throw new StreamException(
                    StreamFailure.Protocol,
                    $"{_profile.Host} sent an answer Deck did not understand. It may not be an Icecast server.");

            default:
                throw new StreamException(
                    StreamFailure.Protocol,
                    $"The server refused the connection: {FirstLine(response)}");
        }
    }

    private static string FirstLine(string response) =>
        response.Split('\n')[0].Trim().TrimEnd('\r');

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_connection is null) throw new StreamException(StreamFailure.Network, "Not connected to the server.");
        await _connection.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateMetadataAsync(string title, CancellationToken cancellationToken)
    {
        // Ogg carries its own tags, so an out-of-band update would be ignored anyway.
        if (_encoder.Codec == StreamCodec.OggOpus) return;

        var scheme = _profile.UseTls ? "https" : "http";
        var url = $"{scheme}://{_profile.Host}:{_profile.Port}/admin/metadata" +
                  $"?mount={Uri.EscapeDataString(_profile.NormalisedMount)}" +
                  $"&mode=updinfo&charset=UTF-8&song={SourceConnection.EncodeTitle(title)}";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(
            SourceConnection.BasicAuth(_profile.Username, _profile.Password));
        request.Headers.UserAgent.ParseAdd("Deck/1.0");

        // A failed title update must never take the broadcast down with it.
        try
        {
            await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
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
