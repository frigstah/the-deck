using System.Net;
using System.Net.Sockets;
using System.Text;
using Deck.Core.Codecs;
using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.EncoderCheck;

/// <summary>
/// What Deck actually puts on the wire when it signs in, read off a real socket.
/// <para>
/// This exists because of a failure that cost an evening and could not have been found any other way.
/// A station's SHOUTcast server accepted the password, answered "OK2", and closed the connection forty
/// milliseconds later - before a byte of audio - and all Deck could say was "the connection to the
/// server was lost", four times in a row. Every guess was wrong: not the bitrate, not the sample rate,
/// not a burst of audio, not the port. A proxy between Deck and the server finally showed it, and the
/// difference was one header.
/// </para>
/// <para>
/// The profile had no station name, so <c>icy-name</c> was left out - and SHOUTcast refuses a nameless
/// source exactly that way. Proved by sending the same handshake twice against the real server, with
/// the header and without: nine seconds of audio, against gone in forty milliseconds.
/// </para>
/// <para>
/// So these checks read the handshake bytes rather than trusting the code that writes them. A test that
/// only asked "did it connect?" would have passed throughout, because connecting was never the problem.
/// </para>
/// </summary>
internal static class HandshakeChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("SHOUTcast always gets a station name, even when the profile has none", () =>
        {
            using var server = new HandshakeRecorder("OK2\r\nicy-caps:11\r\n\r\n");

            var profile = Shoutcast(server.Port);
            profile.Name = "Elixir";
            profile.StationName = string.Empty;

            Connect(profile);

            var lines = server.Handshake();
            var name = lines.FirstOrDefault(l => l.StartsWith("icy-name:", StringComparison.OrdinalIgnoreCase));

            Expect(name is not null,
                "no icy-name in the handshake - a SHOUTcast server will accept this and then hang up without a word");

            // The server's own label stands in, so there is always something rather than nothing.
            Expect(name!.Contains("Elixir"), $"the name sent was \"{name}\"");
        });

        failures += Check("a station name that was set is the one that is sent", () =>
        {
            using var server = new HandshakeRecorder("OK2\r\nicy-caps:11\r\n\r\n");

            var profile = Shoutcast(server.Port);
            profile.Name = "the private label";
            profile.StationName = "BenCast live";

            Connect(profile);

            var name = server.Handshake()
                .FirstOrDefault(l => l.StartsWith("icy-name:", StringComparison.OrdinalIgnoreCase));

            Expect(name == "icy-name:BenCast live", $"sent \"{name}\" instead of the station name");
        });

        failures += Check("a station name is never allowed to break out of its header", () =>
        {
            using var server = new HandshakeRecorder("OK2\r\nicy-caps:11\r\n\r\n");

            var profile = Shoutcast(server.Port);
            profile.StationName = "Bad\r\nicy-br:8\r\nicy-name:hijacked";

            Connect(profile);

            var lines = server.Handshake();
            var names = lines.Count(l => l.StartsWith("icy-name:", StringComparison.OrdinalIgnoreCase));

            Expect(names == 1, $"a station name with newlines in it produced {names} icy-name headers");
            Expect(!lines.Any(l => l == "icy-br:8"), "a station name managed to set the bitrate");
        });

        failures += Check("the handshake carries what the server needs to accept the audio", () =>
        {
            using var server = new HandshakeRecorder("OK2\r\nicy-caps:11\r\n\r\n");

            Connect(Shoutcast(server.Port));

            var lines = server.Handshake();

            foreach (var required in new[] { "icy-name:", "icy-br:", "content-type:" })
            {
                Expect(lines.Any(l => l.StartsWith(required, StringComparison.OrdinalIgnoreCase)),
                    $"no {required} in the handshake - sent: {string.Join(" | ", lines.Skip(1))}");
            }
        });

        failures += Check("a server that signs you in and then hangs up says something useful", () =>
        {
            // Exactly what the real one did: OK2, then the door closes. The old message sent the user to
            // look at their internet connection, which was working perfectly.
            using var server = new HandshakeRecorder("OK2\r\nicy-caps:11\r\n\r\n", closeAfterHandshake: true);

            var profile = Shoutcast(server.Port);
            var sink = new ShoutcastSink(profile, profile.Encoder);

            try
            {
                sink.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();

                // Writing is where a closed connection is noticed. Twice, because the first write after a
                // remote close often succeeds into the local buffer.
                for (var i = 0; i < 20; i++)
                {
                    sink.SendAsync(new byte[4096], CancellationToken.None).GetAwaiter().GetResult();
                    Thread.Sleep(20);
                }

                throw new Exception("sending into a closed connection was reported as success");
            }
            catch (StreamException ex)
            {
                Expect(ex.Failure == StreamFailure.Network, $"reported as {ex.Failure}");
            }
            finally
            {
                sink.DisposeAsync().GetAwaiter().GetResult();
            }
        });

        // ------------------------------------------------------------------ the rule, not the wire

        failures += Check("a nameless SHOUTcast station is told, not blocked", () =>
        {
            // Requiring the name looked like the tidy answer and was the wrong one: the fallback makes
            // the broadcast work, so refusing it would stop a station that is now perfectly able to go
            // on air. What the user needs is to know that listeners are about to see the wrong name.
            var profile = Shoutcast(8000);
            profile.Name = "Elixir";
            profile.StationName = string.Empty;

            Expect(profile.Validate().Count == 0,
                $"a nameless SHOUTcast station was refused: {string.Join(" | ", profile.Validate())}");

            using var server = new HandshakeRecorder("OK2\r\nicy-caps:11\r\n\r\n");
            profile.Port = server.Port;

            var sink = new ShoutcastSink(profile, profile.Encoder);
            try
            {
                sink.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();

                Expect(sink.ConnectionNote is not null, "went on air under an invented name without saying so");
                Expect(sink.ConnectionNote!.Contains("Elixir"),
                    $"the note does not say what listeners will see: \"{sink.ConnectionNote}\"");
            }
            finally
            {
                sink.DisposeAsync().GetAwaiter().GetResult();
            }
        });

        failures += Check("Icecast is not made to answer a question it does not ask", () =>
        {
            // Icecast does not care about the name, and inventing a requirement would be a rule the user
            // has to satisfy for no reason.
            var profile = new ServerProfile
            {
                Name = "frig2",
                ServerType = ServerType.Icecast,
                Host = "radio.example.com",
                Port = 8000,
                MountPoint = "/stream",
                Username = "source",
                Password = "secret",
                StationName = string.Empty,
            };

            Expect(profile.Validate().Count == 0,
                $"an Icecast server was refused for having no station name: {string.Join(" | ", profile.Validate())}");

            Expect(!ServerType.Icecast.NeedsStationName(), "Icecast should not need a station name");
            Expect(ServerType.ShoutcastV1.NeedsStationName(), "SHOUTcast v1 does need one");
            Expect(ServerType.ShoutcastV2.NeedsStationName(), "SHOUTcast v2 does need one");
        });

        return failures;
    }

    private static void Connect(ServerProfile profile)
    {
        var sink = new ShoutcastSink(profile, profile.Encoder);

        try
        {
            sink.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (StreamException)
        {
            // The recorder answers whatever it was told to; the handshake it wrote down is the point.
        }
        finally
        {
            sink.DisposeAsync().GetAwaiter().GetResult();
        }
    }

    private static ServerProfile Shoutcast(int port) => new()
    {
        Name = "test station",
        ServerType = ServerType.ShoutcastV1,
        Host = "127.0.0.1",
        Port = port,
        Password = "secret",
        StationName = "test station",
        Encoder = new EncoderSettings { Codec = StreamCodec.Mp3, BitrateKbps = 128, SampleRate = 44100, Channels = 2 },
    };

    /// <summary>
    /// A source port that writes down the handshake it is sent, and can hang up straight afterwards the
    /// way the real server did.
    /// </summary>
    private sealed class HandshakeRecorder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly ManualResetEventSlim _received = new(false);
        private readonly string _reply;
        private readonly bool _closeAfterHandshake;
        private string _handshake = string.Empty;

        public HandshakeRecorder(string reply, bool closeAfterHandshake = false)
        {
            _reply = reply;
            _closeAfterHandshake = closeAfterHandshake;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        /// <summary>The handshake as lines, the first of which is the password.</summary>
        public IReadOnlyList<string> Handshake()
        {
            _received.Wait(TimeSpan.FromSeconds(3));

            return _handshake
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();
        }

        private async Task AcceptAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                var stream = client.GetStream();

                var header = new StringBuilder();
                var one = new byte[1];

                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && header.Length < 4096)
                {
                    var read = await stream.ReadAsync(one, _stop.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    header.Append((char)one[0]);
                }

                _handshake = header.ToString();
                _received.Set();

                await stream.WriteAsync(Encoding.ASCII.GetBytes(_reply), _stop.Token).ConfigureAwait(false);
                await stream.FlushAsync(_stop.Token).ConfigureAwait(false);

                if (_closeAfterHandshake)
                {
                    client.Client.Shutdown(SocketShutdown.Both);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _received.Set();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
            _received.Dispose();
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
