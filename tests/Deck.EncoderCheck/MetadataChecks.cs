using System.Net;
using System.Net.Sockets;
using System.Text;
using Deck.Core.Metadata;

namespace Deck.EncoderCheck;

/// <summary>
/// Checks the title template (F5) and the local endpoint automation systems push to (F4). The
/// endpoint cases go over a real socket with a real HTTP client rather than calling the parser
/// directly, because the thing worth proving is that a playout system pointed at Deck gets through.
/// </summary>
internal static class MetadataChecks
{
    public static int Run()
    {
        var failures = 0;

        // ---------------------------------------------------------------- title template (F5)

        failures += Check("the default template joins artist and title", () =>
            ExpectTitle(TitleTemplate.Build(null, "The Clash", "London Calling"), "The Clash - London Calling"));

        failures += Check("a missing artist takes the separator with it", () =>
            ExpectTitle(TitleTemplate.Build("{artist} - {title}", "", "Station Ident"), "Station Ident"));

        failures += Check("a missing title leaves no trailing separator", () =>
            ExpectTitle(TitleTemplate.Build("{artist} - {title}", "The Clash", ""), "The Clash"));

        failures += Check("an empty album leaves no empty brackets", () =>
            ExpectTitle(
                TitleTemplate.Build("{artist} - {title} ({album})", "The Clash", "London Calling", null),
                "The Clash - London Calling"));

        failures += Check("a full template keeps everything", () =>
            ExpectTitle(
                TitleTemplate.Build("{artist} - {title} ({album})", "The Clash", "London Calling", "Combat Rock"),
                "The Clash - London Calling (Combat Rock)"));

        failures += Check("reordered templates work too", () =>
            ExpectTitle(
                TitleTemplate.Build("{title} by {artist}", "The Clash", "London Calling"),
                "London Calling by The Clash"));

        failures += Check("everything empty produces nothing rather than punctuation", () =>
            ExpectTitle(TitleTemplate.Build("{artist} - {title}", null, null), string.Empty));

        // ---------------------------------------------------------------- endpoint (F4)

        failures += Check("a song sent to /metadata arrives", () =>
        {
            using var host = new Host();
            var response = host.Get($"/metadata?song={Uri.EscapeDataString("The Clash - London Calling")}");

            ExpectStatus(response, 200);
            ExpectTitle(host.LastSong, "The Clash - London Calling");
        });

        failures += Check("the Icecast admin form works unchanged", () =>
        {
            // An automation system already configured for Icecast should reach Deck by changing only
            // the host and port, which is the entire reason this path exists.
            using var host = new Host();
            var response = host.Get($"/admin/metadata?mode=updinfo&song={Uri.EscapeDataString("Ident")}");

            ExpectStatus(response, 200);
            ExpectTitle(host.LastSong, "Ident");
        });

        failures += Check("artist and title are formatted by the template", () =>
        {
            using var host = new Host();
            var response = host.Get("/metadata?artist=The+Clash&title=London+Calling");

            ExpectStatus(response, 200);
            ExpectTitle(TitleTemplate.Build(null, host.LastArtist, host.LastTitle), "The Clash - London Calling");
        });

        failures += Check("plus signs and percent escapes both decode", () =>
        {
            using var host = new Host();
            host.Get("/metadata?song=Rock+%26+Roll+%2350");

            ExpectTitle(host.LastSong, "Rock & Roll #50");
        });

        failures += Check("a form post works as well as a query", () =>
        {
            using var host = new Host();
            var response = host.Post("/metadata", "song=Posted+Track");

            ExpectStatus(response, 200);
            ExpectTitle(host.LastSong, "Posted Track");
        });

        failures += Check("an empty update is refused", () =>
        {
            using var host = new Host();
            ExpectStatus(host.Get("/metadata"), 400);

            if (host.Updates != 0) throw new Exception("an empty request still counted as an update");
        });

        failures += Check("an unknown path is refused", () =>
        {
            using var host = new Host();
            ExpectStatus(host.Get("/something-else"), 404);
        });

        failures += Check("the help page explains itself", () =>
        {
            using var host = new Host();
            var response = host.Get("/");

            ExpectStatus(response, 200);
            if (!response.Body.Contains("/metadata")) throw new Exception("the help page does not mention the endpoint");
        });

        failures += Check("a password is enforced", () =>
        {
            using var host = new Host(token: "secret");

            ExpectStatus(host.Get("/metadata?song=No+Token"), 401);
            ExpectStatus(host.Get("/metadata?song=Wrong&token=nope"), 401);

            if (host.Updates != 0) throw new Exception("a rejected request still changed the title");

            ExpectStatus(host.Get("/metadata?song=Right&token=secret"), 200);
            ExpectTitle(host.LastSong, "Right");
        });

        failures += Check("a bearer token is accepted too", () =>
        {
            using var host = new Host(token: "secret");
            var response = host.Get("/metadata?song=Bearer+Track", bearer: "secret");

            ExpectStatus(response, 200);
            ExpectTitle(host.LastSong, "Bearer Track");
        });

        failures += Check("opening up to the network without a password is refused", () =>
        {
            // A listening socket that anything on the network can rewrite is not something to open
            // by accident, so this fails closed and says why.
            using var server = new MetadataServer();
            var started = server.Start(0, allowOtherComputers: true, token: null);

            if (started) throw new Exception("the endpoint opened to the network with no password set");
            if (server.IsRunning) throw new Exception("the endpoint is listening despite refusing to start");
            if (string.IsNullOrWhiteSpace(server.Problem)) throw new Exception("no reason was given");
        });

        failures += Check("loopback only really is loopback only", () =>
        {
            using var host = new Host();

            var address = LocalNetworkAddress();
            if (address is null)
            {
                Console.WriteLine("       (skipped: this machine has no other network address to try)");
                return;
            }

            using var client = new TcpClient();
            try
            {
                if (client.ConnectAsync(address, host.Port).Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new Exception($"the endpoint answered on {address}, not just loopback");
                }
            }
            catch (AggregateException)
            {
                // Refused, which is the whole point.
            }
        });

        // ---------------------------------------------------------------- holding titles (F5)

        failures += Check("holding stops titles going out and releasing sends the latest", () =>
        {
            using var service = new NowPlayingService();
            var sent = new List<string>();
            service.TitleChanged += (_, e) => sent.Add(e.Title);

            service.SetTitle("First track");
            service.SuspendUpdates = true;
            service.SetTitle("Advert one");
            service.SetTitle("Advert two");

            if (sent.Count != 1 || sent[0] != "First track")
            {
                throw new Exception($"listeners were sent [{string.Join(", ", sent)}] while updates were held");
            }

            service.SuspendUpdates = false;

            if (sent.Count != 2 || sent[1] != "Advert two")
            {
                throw new Exception($"releasing the hold sent [{string.Join(", ", sent.Skip(1))}], expected the latest title");
            }
        });

        return failures;
    }

    /// <summary>A running endpoint plus a client, torn down at the end of each case.</summary>
    private sealed class Host : IDisposable
    {
        private readonly MetadataServer _server = new();

        public Host(string? token = null)
        {
            // Port 0: the operating system picks a free one, so the checks never collide with
            // whatever else is listening on this machine.
            if (!_server.Start(0, allowOtherComputers: false, token))
            {
                throw new Exception($"the endpoint would not start: {_server.Problem}");
            }

            _server.TrackReceived += (_, e) =>
            {
                LastSong = e.Song;
                LastArtist = e.Artist;
                LastTitle = e.Title;
            };
        }

        public int Port => _server.Port;

        public int Updates => _server.UpdatesReceived;

        public string? LastSong { get; private set; }

        public string? LastArtist { get; private set; }

        public string? LastTitle { get; private set; }

        public Response Get(string target, string? bearer = null)
        {
            var headers = bearer is null ? string.Empty : $"Authorization: Bearer {bearer}\r\n";
            return Send($"GET {target} HTTP/1.1\r\nHost: 127.0.0.1\r\n{headers}\r\n");
        }

        public Response Post(string target, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            return Send(
                $"POST {target} HTTP/1.1\r\nHost: 127.0.0.1\r\n" +
                $"Content-Type: application/x-www-form-urlencoded\r\n" +
                $"Content-Length: {payload.Length}\r\n\r\n{body}");
        }

        private Response Send(string request)
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, Port);

            using var stream = client.GetStream();
            var bytes = Encoding.UTF8.GetBytes(request);
            stream.Write(bytes);
            stream.Flush();

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = split < 0 ? text : text[..split];
            var body = split < 0 ? string.Empty : text[(split + 4)..];

            var statusLine = head.Split("\r\n")[0].Split(' ');
            var status = statusLine.Length > 1 && int.TryParse(statusLine[1], out var code) ? code : 0;

            return new Response(status, body);
        }

        public void Dispose() => _server.Dispose();
    }

    private record Response(int Status, string Body);

    private static void ExpectStatus(Response response, int expected)
    {
        if (response.Status != expected)
        {
            throw new Exception($"the endpoint answered {response.Status}, expected {expected}: {response.Body}");
        }
    }

    private static void ExpectTitle(string? actual, string expected)
    {
        if (actual != expected) throw new Exception($"got \"{actual}\", expected \"{expected}\"");
    }

    /// <summary>An address on this machine that is not loopback, or null if there is not one.</summary>
    private static IPAddress? LocalNetworkAddress()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        }
        catch (SocketException)
        {
            return null;
        }
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
