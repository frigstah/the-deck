using System.Net;
using System.Net.Sockets;
using System.Text;
using Deck.Core.Codecs;
using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.EncoderCheck;

/// <summary>
/// Why a broadcast is not going out, told apart correctly and said in full (H3).
/// <para>
/// Three questions a broadcaster asks when Go live does not work, and they want different answers:
/// is the password wrong, is somebody else already on the stream, or is the server simply not there.
/// Deck's sinks could tell all three apart and each wrote a careful sentence about it - and then the
/// kind was dropped along with the exception, so the deck had nothing but a string it could not
/// categorise, and showed it truncated in the same grey as the byte counter.
/// </para>
/// <para>
/// Every case runs against both server families over a real socket, answering the way the real
/// software answers. Icecast refuses with HTTP status codes; SHOUTcast refuses with a line of text
/// and no status line at all. A check that only covered one of them would have left half the
/// destinations in an imported server list unaccounted for.
/// </para>
/// </summary>
internal static class ConnectionFailureChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("every kind of failure has words of its own", () =>
        {
            // No fallback arm is allowed to carry a real failure. A kind added later without a
            // sentence written for it would otherwise reach the deck as "the connection failed",
            // which is the one thing the block exists not to say.
            var seen = new List<string>();

            foreach (StreamFailure failure in Enum.GetValues<StreamFailure>())
            {
                var headline = failure.Headline();

                Expect(headline.Length > 0, $"{failure} has no headline");
                Expect(headline != "The connection failed",
                    $"{failure} fell through to the catch-all headline");
                Expect(!seen.Contains(headline), $"{failure} shares its headline with another kind");

                seen.Add(headline);
            }
        });

        // ------------------------------------------------------------------ the password is wrong

        failures += Check("Icecast: a refused password is a password problem, not a network one", () =>
        {
            using var server = new FakeSource("HTTP/1.0 401 Unauthorized\r\nWWW-Authenticate: Basic\r\n\r\n");

            var problem = Fails(Icecast(server.Port));

            Expect(problem.Failure == StreamFailure.Authentication, $"read as {problem.Failure}");
            Expect(problem.Headline == "The password was refused", $"said \"{problem.Headline}\"");

            // Sending them back to their router when the password is wrong is the failure that
            // wastes the most of somebody's evening.
            Expect(!problem.Detail.Contains("firewall", StringComparison.OrdinalIgnoreCase),
                $"blamed the network for a password: {problem.Detail}");
        });

        failures += Check("SHOUTcast: a refused password is a password problem too", () =>
        {
            // No status line anywhere - this is what the real thing sends.
            using var server = new FakeSource("invalid password\r\n");

            var problem = Fails(Shoutcast(server.Port));

            Expect(problem.Failure == StreamFailure.Authentication, $"read as {problem.Failure}");
            Expect(problem.Headline == "The password was refused", $"said \"{problem.Headline}\"");
        });

        failures += Check("a refused password stops rather than retrying for ever", () =>
        {
            // The one failure waiting cannot fix. Retrying would hammer the server and bury the real
            // reason under a scroll of reconnect messages.
            using var server = new FakeSource("HTTP/1.0 401 Unauthorized\r\n\r\n");

            var problem = Fails(Icecast(server.Port));

            Expect(!problem.StillTrying, "kept trying a password that will never be accepted");
            Expect(!problem.Failure.WorthRetrying(), "authentication was marked as worth retrying");
            Expect(problem.Detail.Contains("password", StringComparison.OrdinalIgnoreCase),
                $"never mentioned the password: {problem.Detail}");
        });

        // ---------------------------------------------------------------- somebody else is already on

        failures += Check("Icecast: a stream already in use says so", () =>
        {
            using var server = new FakeSource(
                "HTTP/1.0 403 Forbidden\r\nContent-Type: text/html\r\n\r\nMountpoint in use");

            var problem = Fails(Icecast(server.Port));

            Expect(problem.Failure == StreamFailure.MountInUse, $"read as {problem.Failure}");
            Expect(problem.Headline == "Something else is already broadcasting", $"said \"{problem.Headline}\"");
            Expect(problem.StillTrying, "gave up on a stream the other encoder may be about to release");
        });

        failures += Check("SHOUTcast: a stream already in use says so", () =>
        {
            using var server = new FakeSource("Server is already in use\r\n");

            var problem = Fails(Shoutcast(server.Port));

            Expect(problem.Failure == StreamFailure.MountInUse, $"read as {problem.Failure}");
            Expect(problem.Detail.Contains("already broadcasting", StringComparison.OrdinalIgnoreCase),
                $"did not say what was wrong: {problem.Detail}");
        });

        failures += Check("what the server said beats what the second port did not say", () =>
        {
            // SHOUTcast takes broadcasts on the port after the listener port, and hosts differ about
            // which they quote, so Deck tries both. The second try is a guess - and when the guess
            // was wrong it failed with "nothing is listening", which then got reported as the reason
            // and buried a real answer from the port the user actually entered.
            //
            // A reply Deck cannot classify is used here on purpose: servers say things this code has
            // never heard of, and those are exactly the times the sentence matters most.
            using var server = new FakeSource("ERROR - stream limit reached for this account\r\n");

            var problem = Fails(Shoutcast(server.Port));

            Expect(problem.Failure != StreamFailure.Network,
                $"reported the fallback port's silence instead of the server's answer: {problem.Detail}");
            Expect(problem.Detail.Contains("stream limit", StringComparison.OrdinalIgnoreCase),
                $"lost what the server actually said: {problem.Detail}");
        });

        // ------------------------------------------------------------------ nothing is answering

        failures += Check("a server that is not there reads as a server that is not there", () =>
        {
            foreach (var profile in new[] { Icecast(ClosedPort()), Shoutcast(ClosedPort()) })
            {
                var problem = Fails(profile);

                Expect(problem.Failure == StreamFailure.Network,
                    $"{profile.ServerType}: read as {problem.Failure}");
                Expect(problem.Headline == "The server is not answering",
                    $"{profile.ServerType}: said \"{problem.Headline}\"");

                // Kept trying, because a server that is down at nine o'clock is often up at ten.
                Expect(problem.StillTrying, $"{profile.ServerType}: gave up on a server that may come back");
            }
        });

        failures += Check("a server that accepts the socket and then says nothing is not silent about it", () =>
        {
            // The nastiest shape: the port is open, so nothing looks wrong, and the handshake never
            // comes back. Deck has to time out and account for it rather than sitting on Connecting.
            using var server = new FakeSource(reply: null);

            var problem = Fails(Icecast(server.Port), TimeSpan.FromSeconds(40));

            Expect(problem.Detail.Length > 0, "timed out with nothing to say");
            Expect(problem.Headline.Length > 0, "timed out with no verdict");
        });

        // -------------------------------------------------------------------- and it has to be readable

        failures += Check("the whole explanation reaches the deck, not the first few words of it", () =>
        {
            // What this is all for. The sinks write a diagnosis and a remedy; the remedy is the half
            // worth reading and it was the half being cut off.
            using var server = new FakeSource("HTTP/1.0 401 Unauthorized\r\n\r\n");

            var problem = Fails(Icecast(server.Port));

            Expect(!problem.Detail.EndsWith('…') && !problem.Detail.EndsWith("..."),
                $"arrived already truncated: {problem.Detail}");
            Expect(problem.Detail.Length > 60,
                $"only {problem.Detail.Length} characters survived: {problem.Detail}");

            // Diagnosis and remedy are different sentences, and both have to be there.
            Expect(problem.Detail.Contains("Check", StringComparison.OrdinalIgnoreCase),
                $"said what was wrong but not what to do: {problem.Detail}");
        });

        failures += Check("a problem names its server once there is more than one", () =>
        {
            using var good = new FakeSource("HTTP/1.0 401 Unauthorized\r\n\r\n");

            var profile = Icecast(good.Port);
            profile.Name = "Backup relay";

            var problem = Fails(profile);

            Expect(problem.Server == "Backup relay", $"attributed it to \"{problem.Server}\"");
        });

        failures += Check("a connection that is working reports no problem at all", () =>
        {
            // The other half of the contract: the block must not linger after the fault clears, or it
            // becomes something people learn to ignore.
            var server = new FakeSource("HTTP/1.0 200 OK\r\n\r\n");
            var profile = Icecast(server.Port);
            var set = new BroadcastSet();

            try
            {
                set.Start([profile], BroadcastSet.CaptureFormatFor([profile]));

                Expect(WaitFor(() => set.State == StreamState.Live, TimeSpan.FromSeconds(15)),
                    $"never got on air: {set.Problem?.Detail}");
                Expect(set.Problem is null, $"reported a problem while live: {set.Problem?.Detail}");
            }
            finally
            {
                set.StopAsync().GetAwaiter().GetResult();
                server.Dispose();
            }
        });

        return failures;
    }

    /// <summary>Runs a profile until it reports a problem, and hands back the whole of it.</summary>
    private static BroadcastProblem Fails(ServerProfile profile, TimeSpan? patience = null)
    {
        var set = new BroadcastSet();

        try
        {
            set.Start([profile], BroadcastSet.CaptureFormatFor([profile]));

            if (!WaitFor(() => set.Problem is not null, patience ?? TimeSpan.FromSeconds(25)))
            {
                throw new Exception($"no problem was ever reported; state is {set.State}");
            }

            return set.Problem!;
        }
        finally
        {
            set.StopAsync().GetAwaiter().GetResult();
        }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }

        return condition();
    }

    private static ServerProfile Icecast(int port) => new()
    {
        Name = "check",
        ServerType = ServerType.Icecast,
        Host = "127.0.0.1",
        Port = port,
        MountPoint = "/live",
        Username = "source",
        Password = "wrong-one",
        StationName = "Check",
    };

    private static ServerProfile Shoutcast(int port) => new()
    {
        Name = "check",
        ServerType = ServerType.Shoutcast,
        Host = "127.0.0.1",
        Port = port,
        Password = "wrong-one",
        StationName = "Check",
    };

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// A server that answers every source connection the same way and then hangs up. Raw TCP,
    /// because half of what it has to imitate - SHOUTcast's replies - is not valid HTTP.
    /// A null reply is a server that accepts the socket and never says anything.
    /// </summary>
    private sealed class FakeSource : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly string? _reply;

        public FakeSource(string? reply)
        {
            _reply = reply;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        private async Task AcceptAsync()
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
                    var buffer = new byte[4096];
                    await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);

                    if (_reply is null)
                    {
                        // Hold the socket open saying nothing, which is what a wedged server does.
                        await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
                        return;
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_reply), _stop.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A client that gave up mid-exchange is the sink's business, not the fake's.
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

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  ok   {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }
}
