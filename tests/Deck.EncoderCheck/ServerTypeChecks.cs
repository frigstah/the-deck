using System.Net;
using System.Net.Sockets;
using System.Text;
using Deck.Core.Codecs;
using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.EncoderCheck;

/// <summary>
/// Checks that a server whose type nobody has filled in gets worked out on the way to air (C3).
/// <para>
/// This exists because of a real failure, not a hypothetical one. <see cref="ServerProbe"/> was
/// called from the Test button and nowhere else, so a profile saved without pressing Test kept
/// <see cref="ServerType.Unknown"/> for ever and Go live refused with a note asking the user to go
/// and press Test. The picker offered that state as "Detect automatically" the whole time.
/// </para>
/// <para>
/// Run against real sockets on the loopback interface rather than against a mocked probe: the thing
/// worth pinning down is the classification of what servers actually put on the wire, including
/// SHOUTcast v1's "ICY 200 OK", which is not a valid HTTP status line and which a mock would happily
/// pretend was fine.
/// </para>
/// </summary>
internal static class ServerTypeChecks
{
    private static readonly EncoderSettings Encoder = QualityPreset.Default.Settings;

    public static int Run()
    {
        var failures = 0;

        failures += Case("an Icecast banner is enough, on the first request", () =>
        {
            using var server = new FakeServer(IcecastFrontPage);
            var profile = Profile(server.Port);

            var (sink, detected) = Resolve(profile);
            using var _ = sink as IDisposable;

            if (profile.ServerType != ServerType.Icecast) return $"type is {profile.ServerType}";
            if (!detected) return "did not report that there was something new to save";
            return sink is IcecastSink ? null : $"built a {sink.GetType().Name}";
        });

        // The shape of the user's own server: it identifies itself in the Server header, but its
        // status-json.xsl is missing and answers 404. Detection must not depend on the second request.
        failures += Case("Icecast with no status-json.xsl still detected", () =>
        {
            using var server = new FakeServer(IcecastFrontPage, statusJson: NotFound);
            var profile = Profile(server.Port);

            Resolve(profile);
            return profile.ServerType == ServerType.Icecast ? null : $"type is {profile.ServerType}";
        });

        // The other way round: a station that replaced the front page with its own template. The
        // banner is gone, so only the status document can answer.
        failures += Case("Icecast behind a custom front page, found via status-json", () =>
        {
            const string plain = "HTTP/1.0 200 OK\r\nServer: nginx\r\nContent-Type: text/html\r\n\r\n<h1>My Station</h1>";
            const string stats = "HTTP/1.0 200 OK\r\nContent-Type: application/json\r\n\r\n{\"icestats\":{\"listeners\":0}}";

            using var server = new FakeServer(plain, statusJson: stats);
            var profile = Profile(server.Port);

            Resolve(profile);
            return profile.ServerType == ServerType.Icecast ? null : $"type is {profile.ServerType}";
        });

        failures += Case("a bare ICY line is SHOUTcast v1", () =>
        {
            using var server = new FakeServer("ICY 200 OK\r\nicy-name:Test\r\n\r\n");
            var profile = Profile(server.Port);

            var (sink, _) = Resolve(profile);
            using var _2 = sink as IDisposable;

            if (profile.ServerType != ServerType.ShoutcastV1) return $"type is {profile.ServerType}";
            return sink is ShoutcastSink ? null : $"built a {sink.GetType().Name}";
        });

        failures += Case("DNAS 2 is told apart from v1", () =>
        {
            using var server = new FakeServer("HTTP/1.1 200 OK\r\nServer: DNAS/2.6\r\n\r\n<html></html>");
            var profile = Profile(server.Port);

            Resolve(profile);
            return profile.ServerType == ServerType.ShoutcastV2 ? null : $"type is {profile.ServerType}";
        });

        failures += Case("detection happens once, not on every reconnect", () =>
        {
            using var server = new FakeServer(IcecastFrontPage);
            var profile = Profile(server.Port);

            Resolve(profile);
            var afterFirst = server.Requests;

            var (_, detected) = Resolve(profile);

            if (detected) return "reported a second detection for an already-known server";
            return server.Requests == afterFirst
                ? null
                : $"went back to the server: {afterFirst} requests, then {server.Requests}";
        });

        failures += Case("a server that answers but says nothing is refused, kindly", () =>
        {
            using var server = new FakeServer("HTTP/1.1 200 OK\r\nServer: nginx\r\n\r\n<h1>hello</h1>");
            var profile = Profile(server.Port);

            var problem = ExpectRefusal(profile);
            if (problem is not null) return problem;

            // Left alone rather than guessed at: a wrong type produces a failed handshake with a
            // misleading reason, which is worse than being asked the question.
            if (profile.ServerType != ServerType.Unknown) return $"guessed {profile.ServerType} anyway";

            var message = LastMessage!;
            return message.Contains("could not tell", StringComparison.OrdinalIgnoreCase) &&
                   message.Contains("server settings", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"unhelpful message: {message}";
        });

        failures += Case("nothing listening reads as a connection problem, not a type problem", () =>
        {
            var profile = Profile(ClosedPort());

            var problem = ExpectRefusal(profile);
            if (problem is not null) return problem;
            if (profile.ServerType != ServerType.Unknown) return $"guessed {profile.ServerType} anyway";

            // The distinction the user needs: check the address, versus tell me the type.
            var message = LastMessage!;
            return message.Contains("server settings", StringComparison.OrdinalIgnoreCase)
                ? $"blamed the type when the address was the problem: {message}"
                : null;
        });

        failures += Case("a type that is already known is never probed", () =>
        {
            using var server = new FakeServer(IcecastFrontPage);
            var profile = Profile(server.Port);
            profile.ServerType = ServerType.Icecast;

            var (_, detected) = Resolve(profile);

            if (detected) return "claimed to have detected something";
            return server.Requests == 0 ? null : $"made {server.Requests} requests it did not need";
        });

        return failures;
    }

    private const string IcecastFrontPage =
        "HTTP/1.0 200 OK\r\nServer: Icecast\r\nContent-Type: text/html\r\n\r\n<html><head><title>Icecast Streaming Media Server</title></head></html>";

    private const string NotFound =
        "HTTP/1.0 404 File Not Found\r\nContent-Type: text/html\r\n\r\nCould not provide XSLT file";

    private static string? LastMessage;

    private static ServerProfile Profile(int port) => new()
    {
        Name = "check",
        Host = "127.0.0.1",
        Port = port,
        MountPoint = "/stream",
        Password = "secret",
    };

    private static (IStreamSink Sink, bool Detected) Resolve(ServerProfile profile) =>
        SinkResolver.CreateAsync(profile, Encoder, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Runs the resolver expecting it to refuse, and stashes the message it refused with.</summary>
    private static string? ExpectRefusal(ServerProfile profile)
    {
        LastMessage = null;

        try
        {
            Resolve(profile);
            return "connected when it should have refused";
        }
        catch (StreamException ex)
        {
            LastMessage = ex.Message;
            return null;
        }
    }

    /// <summary>A port with nothing on it: bound, its number taken, then released.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A server that answers one canned response per path and counts what it was asked for. Speaks
    /// raw TCP because half of what it has to imitate is not valid HTTP.
    /// </summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _root;
        private readonly string? _statusJson;
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public FakeServer(string root, string? statusJson = null)
        {
            _root = root;
            _statusJson = statusJson;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[2048];
                    var read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                    var request = Encoding.ASCII.GetString(buffer, 0, read);

                    Interlocked.Increment(ref _requests);

                    var body = request.Contains("status-json.xsl", StringComparison.OrdinalIgnoreCase)
                        ? _statusJson ?? NotFound
                        : _root;

                    var bytes = Encoding.ASCII.GetBytes(body);
                    await stream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A client that gave up mid-exchange is the probe's business, not the fake's.
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    private static int Case(string name, Func<string?> verify)
    {
        string? problem;
        try
        {
            problem = verify();
        }
        catch (Exception ex)
        {
            problem = $"threw {ex.GetType().Name}: {ex.Message}";
        }

        if (problem is null)
        {
            Console.WriteLine($"  ok    {name}");
            return 0;
        }

        Console.WriteLine($"  FAIL  {name}: {problem}");
        return 1;
    }
}
