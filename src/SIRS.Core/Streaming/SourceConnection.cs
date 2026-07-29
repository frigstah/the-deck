using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Sirs.Core.Servers;

namespace Sirs.Core.Streaming;

/// <summary>
/// Shared socket plumbing for the source protocols: connect, optionally wrap in TLS, and read the
/// handshake reply. Also turns socket-level exceptions into <see cref="StreamException"/> with
/// wording a broadcaster can act on.
/// </summary>
internal sealed class SourceConnection : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private TcpClient? _client;
    private Stream? _stream;

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public async Task OpenAsync(string host, int port, bool useTls, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeout);

            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);

            Stream stream = client.GetStream();
            if (useTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = host },
                    timeoutCts.Token).ConfigureAwait(false);
                stream = ssl;
            }

            _client = client;
            _stream = stream;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw Translate(ex, host, port, useTls);
        }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_stream is null) throw new StreamException(StreamFailure.Network, "The connection to the server was lost.");

        try
        {
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new StreamException(StreamFailure.Network, "The connection to the server was lost.", ex);
        }
    }

    public Task WriteAsciiAsync(string text, CancellationToken cancellationToken) =>
        WriteAsync(System.Text.Encoding.ASCII.GetBytes(text), cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken) =>
        _stream?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// Reads whatever the server says back, stopping at a blank line or when it goes quiet. Source
    /// protocols differ on how much they reply with, so this is intentionally lenient: an empty
    /// answer is a valid outcome that the caller interprets.
    /// </summary>
    public async Task<string> ReadHandshakeReplyAsync(CancellationToken cancellationToken)
    {
        if (_stream is null) return string.Empty;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HandshakeTimeout);

        var builder = new StringBuilder();
        var buffer = new byte[1024];

        try
        {
            while (builder.Length < 8192)
            {
                var read = await _stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                if (read == 0) break;

                builder.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));

                var text = builder.ToString();
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal) ||
                    text.Contains("\n\n", StringComparison.Ordinal))
                {
                    break;
                }

                // Short one-line answers such as "OK2" or "invalid password" arrive without a
                // blank line; once we have a complete line there is nothing more to wait for.
                if (text.Contains('\n') && text.Length < 64) break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Server accepted us silently and is waiting for audio. Treat as an empty reply.
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw new StreamException(StreamFailure.Network, "The server closed the connection during setup.", ex);
        }

        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already torn down by the far end.
            }

            _stream = null;
        }

        _client?.Dispose();
        _client = null;
    }

    internal static StreamException Translate(Exception ex, string host, int port, bool useTls) => ex switch
    {
        StreamException streamException => streamException,

        AuthenticationException =>
            new StreamException(StreamFailure.Tls,
                $"The secure connection to {host} failed. Either the server's certificate is not valid, or it does not accept secure connections on port {port} — try turning off the secure option.",
                ex),

        SocketException { SocketErrorCode: SocketError.HostNotFound } =>
            new StreamException(StreamFailure.Network,
                $"There is no server called \"{host}\". Check the address for a typo.", ex),

        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            new StreamException(StreamFailure.Network,
                $"{host} refused a connection on port {port}. The port is probably wrong, or the server is not running.", ex),

        SocketException { SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable } =>
            new StreamException(StreamFailure.Network,
                "Your computer could not reach the network. Check your internet connection.", ex),

        OperationCanceledException or TimeoutException or SocketException { SocketErrorCode: SocketError.TimedOut } =>
            new StreamException(StreamFailure.Network,
                $"{host} did not answer on port {port}. Check the address and port, and that a firewall is not blocking SIRS.", ex),

        _ when useTls =>
            new StreamException(StreamFailure.Tls, $"Could not connect securely to {host}:{port}. {ex.Message}", ex),

        _ => new StreamException(StreamFailure.Network, $"Could not connect to {host}:{port}. {ex.Message}", ex),
    };

    /// <summary>Basic auth header value for the given credentials.</summary>
    internal static string BasicAuth(string username, string? password)
    {
        var raw = $"{username}:{password ?? string.Empty}";
        return "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>Reads the numeric status from an HTTP or ICY status line, or null if there isn't one.</summary>
    internal static int? ParseStatusCode(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        var firstLine = response.Split('\n')[0].Trim();
        var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        return int.TryParse(parts[1], out var code) ? code : null;
    }

    /// <summary>Percent-encodes a song title for the admin metadata endpoints.</summary>
    internal static string EncodeTitle(string title) => Uri.EscapeDataString(title);

    internal static string DescribeProfile(ServerProfile profile) =>
        $"{profile.Host}:{profile.Port}";
}
