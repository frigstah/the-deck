using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Deck.Core.Servers;

public sealed record ProbeResult(
    bool Reachable,
    ServerType DetectedType,
    string? Banner,
    string Message);

/// <summary>
/// Works out what kind of server is on the other end (C3), so the user never has to answer the
/// "Icecast or SHOUTcast?" question that trips people up in every other encoder.
/// <para>
/// Deliberately speaks raw TCP rather than using HttpClient: SHOUTcast v1 answers with "ICY 200 OK"
/// instead of a valid HTTP status line, which a strict HTTP client rejects outright.
/// </para>
/// </summary>
public static class ServerProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(6);

    public static async Task<ProbeResult> DetectAsync(
        string host,
        int port,
        bool useTls,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return new ProbeResult(false, ServerType.Unknown, null, "Enter a server address first.");
        }

        string response;
        try
        {
            response = await RequestAsync(host, port, useTls, "/", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, ServerType.Unknown, null, DescribeConnectionFailure(ex, host, port, useTls));
        }

        var detected = Classify(response);
        var banner = ExtractHeader(response, "Server");

        if (detected != ServerType.Unknown)
        {
            return new ProbeResult(true, detected, banner, $"Found {detected.DisplayName()}.");
        }

        // Icecast always serves its JSON status document; a positive answer is conclusive even when
        // the front page has been replaced by a custom template.
        try
        {
            var status = await RequestAsync(host, port, useTls, "/status-json.xsl", cancellationToken).ConfigureAwait(false);
            if (status.Contains("icestats", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeResult(true, ServerType.Icecast, banner, "Found Icecast.");
            }
        }
        catch (Exception)
        {
            // Fall through: failing this second request tells us nothing new.
        }

        return new ProbeResult(true, ServerType.Unknown, banner,
            "Deck reached the server but could not tell what type it is. Pick the type your host told you to use.");
    }

    private static ServerType Classify(string response)
    {
        if (response.Length == 0) return ServerType.Unknown;

        var banner = ExtractHeader(response, "Server") ?? string.Empty;
        var haystack = (banner + "\n" + response[..Math.Min(response.Length, 2048)]).ToLowerInvariant();

        if (haystack.Contains("icecast")) return ServerType.Icecast;

        // DNAS 2 identifies itself in the Server header; v1 does not have one worth trusting.
        if (haystack.Contains("dnas/2") || haystack.Contains("shoutcast server v2") || haystack.Contains("sc_serv2"))
        {
            return ServerType.ShoutcastV2;
        }

        if (haystack.Contains("shoutcast") || haystack.Contains("ultravox"))
        {
            return response.StartsWith("ICY", StringComparison.OrdinalIgnoreCase)
                ? ServerType.ShoutcastV1
                : ServerType.ShoutcastV2;
        }

        // A bare "ICY 200 OK" with nothing else is the classic v1 signature.
        return response.StartsWith("ICY", StringComparison.OrdinalIgnoreCase)
            ? ServerType.ShoutcastV1
            : ServerType.Unknown;
    }

    private static async Task<string> RequestAsync(
        string host,
        int port,
        bool useTls,
        string path,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(ConnectTimeout);

        await client.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);

        Stream stream = client.GetStream();
        if (useTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, connectCts.Token)
                .ConfigureAwait(false);
            stream = ssl;
        }

        await using (stream.ConfigureAwait(false))
        {
            var request = $"GET {path} HTTP/1.0\r\nHost: {host}:{port}\r\nUser-Agent: Deck/1.0\r\nConnection: close\r\n\r\n";
            var bytes = System.Text.Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(ReadTimeout);

            var builder = new StringBuilder();
            var buffer = new byte[4096];

            try
            {
                while (builder.Length < 16384)
                {
                    var read = await stream.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    builder.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Server held the connection open. Whatever arrived is enough to classify.
            }

            return builder.ToString();
        }
    }

    private static string? ExtractHeader(string response, string name)
    {
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) break; // end of headers
            if (!trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)) continue;
            return trimmed[(name.Length + 1)..].Trim();
        }

        return null;
    }

    /// <summary>Turns socket exceptions into something a broadcaster can act on (design principle 3).</summary>
    internal static string DescribeConnectionFailure(Exception ex, string host, int port, bool useTls) => ex switch
    {
        OperationCanceledException or TimeoutException =>
            $"{host} did not answer on port {port}. Check the address and port, and that nothing is blocking the connection.",

        SocketException { SocketErrorCode: SocketError.HostNotFound } =>
            $"There is no server called \"{host}\". Check the address for a typo.",

        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            $"{host} refused a connection on port {port}. The port is probably wrong, or the server is not running.",

        SocketException { SocketErrorCode: SocketError.TimedOut } =>
            $"{host} did not answer on port {port}. Check the address and port, and that nothing is blocking the connection.",

        SocketException { SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable } =>
            "Your computer could not reach the network. Check your internet connection.",

        AuthenticationException =>
            $"The secure connection to {host} failed. The server's certificate may be invalid, or it may not support secure connections on port {port} - try turning the secure option off.",

        _ when useTls =>
            $"Could not connect securely to {host}:{port}. {ex.Message}",

        _ => $"Could not connect to {host}:{port}. {ex.Message}",
    };
}
