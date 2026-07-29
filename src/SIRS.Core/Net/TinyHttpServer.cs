using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sirs.Core.Net;

/// <summary>
/// The small HTTP server behind both the now-playing endpoint (F4) and the remote control endpoint
/// (I10).
/// <para>
/// A raw <see cref="TcpListener"/> rather than <c>HttpListener</c>: the latter needs a URL
/// reservation or administrator rights on Windows even for loopback, and asking a broadcaster to run
/// <c>netsh</c> to get their track names across is not a reasonable trade.
/// </para>
/// <para>
/// Shared rather than written twice. Two hand-rolled HTTP parsers in one program means two chances
/// to get the security posture wrong, and it is the posture - loopback unless asked otherwise, a
/// token required before it can be asked otherwise, a hard cap on request size - that matters here
/// more than the parsing.
/// </para>
/// </summary>
public sealed class TinyHttpServer : IDisposable
{
    /// <summary>Generous for a query string, small enough that nothing can be filled up with one.</summary>
    private const int MaxRequestBytes = 8 * 1024;

    private readonly object _lock = new();
    private readonly Func<HttpRequest, (int Status, string Body)> _handler;

    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;

    public TinyHttpServer(Func<HttpRequest, (int Status, string Body)> handler) => _handler = handler;

    public bool IsRunning { get; private set; }

    public int Port { get; private set; }

    /// <summary>False means loopback only - nothing outside this computer can reach it.</summary>
    public bool AllowOtherComputers { get; private set; }

    public string? Token { get; private set; }

    /// <summary>Why the endpoint is not listening, phrased for the user.</summary>
    public string? Problem { get; private set; }

    /// <summary>How many requests were accepted, so the UI can show it is actually being used.</summary>
    public int RequestsHandled { get; private set; }

    /// <summary>
    /// The message shown when someone asks to open the endpoint to the network without setting a
    /// password. Worded per endpoint, because the consequence differs.
    /// </summary>
    public required string OpenWithoutTokenProblem { get; init; }

    public bool Start(int port, bool allowOtherComputers, string? token)
    {
        lock (_lock)
        {
            Stop();

            Port = port;
            AllowOtherComputers = allowOtherComputers;
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            Problem = null;

            // Fails closed. Opening a listening socket to the network with no password is the one
            // combination that can hurt someone, so it is refused rather than warned about.
            if (allowOtherComputers && Token is null)
            {
                Problem = OpenWithoutTokenProblem;
                return false;
            }

            try
            {
                var address = allowOtherComputers ? IPAddress.Any : IPAddress.Loopback;
                _listener = new TcpListener(address, port);
                _listener.Start();

                // Read the port back rather than trusting the request: port 0 means "any free one",
                // and the address shown to the user has to be the one that actually opened.
                if (_listener.LocalEndpoint is IPEndPoint endPoint) Port = endPoint.Port;
            }
            catch (SocketException ex)
            {
                Problem = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Port {port} is already in use by another program. Try a different number."
                    : $"SIRS could not listen on port {port}: {ex.Message}";

                _listener = null;
                return false;
            }

            _cancellation = new CancellationTokenSource();
            IsRunning = true;
            RequestsHandled = 0;

            _ = AcceptLoopAsync(_listener, _cancellation.Token);
            return true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;

            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
                // Already down; nothing to do.
            }

            _listener = null;
            IsRunning = false;
        }
    }

    /// <summary>True when the request carries the right token, or no token is required.</summary>
    public bool Authorised(HttpRequest request) =>
        Token is null || request.Value("token") == Token || request.BearerToken == Token;

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            // Each request is handled on its own so one slow or malformed caller cannot block the
            // next; a stalled connection times out rather than holding the endpoint.
            _ = HandleAsync(client, cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);

                if (request is null)
                {
                    await RespondAsync(stream, 400, "Bad request.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                RequestsHandled++;

                var (status, body) = _handler(request);
                await RespondAsync(stream, status, body, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // A caller that hangs up mid-request is not worth reporting.
            }
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var filled = 0;
        var headerEnd = -1;

        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            filled += read;
            headerEnd = FindHeaderEnd(buffer, filled);
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return null;

        var headerText = Encoding.UTF8.GetString(buffer, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var request = new HttpRequest { Method = parts[0], Target = parts[1] };

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                request.BearerToken = value["Bearer ".Length..].Trim();
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(value, out var length))
            {
                request.ContentLength = length;
            }
        }

        // Body, when there is one and it already arrived with the headers. Waiting for more would
        // mean holding a connection open for a form post SIRS can already answer from the query.
        var bodyStart = headerEnd + 4;
        if (request.ContentLength > 0 && filled > bodyStart)
        {
            var available = Math.Min(request.ContentLength, filled - bodyStart);
            request.Body = Encoding.UTF8.GetString(buffer, bodyStart, available);
        }

        return request;
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string body, CancellationToken cancellationToken)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            409 => "Conflict",
            _ => "Error",
        };

        var payload = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}

/// <summary>Just enough of a request to answer one.</summary>
public sealed class HttpRequest
{
    public string Method { get; init; } = "GET";

    public string Target { get; init; } = "/";

    public string? BearerToken { get; set; }

    public int ContentLength { get; set; }

    public string? Body { get; set; }

    public string Path
    {
        get
        {
            var query = Target.IndexOf('?');
            return query < 0 ? Target : Target[..query];
        }
    }

    /// <summary>Looks in the query string first, then a form-encoded body.</summary>
    public string? Value(string name)
    {
        var query = Target.IndexOf('?');
        if (query >= 0 && Find(Target[(query + 1)..], name) is { } fromQuery) return fromQuery;

        return Body is null ? null : Find(Body, name);
    }

    private static string? Find(string encoded, string name)
    {
        foreach (var pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;

            if (!Uri.UnescapeDataString(pair[..equals]).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Form encoding writes spaces as '+', which UnescapeDataString leaves alone.
            return Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }

        return null;
    }
}
