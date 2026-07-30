using System.Net;
using System.Net.Sockets;
using System.Text;
using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.EncoderCheck;

/// <summary>
/// The listener count, end to end over a real socket (H4).
/// <para>
/// Parsing was never the broken part. What was broken is that Deck asked one endpoint,
/// <c>status-json.xsl</c>, and a perfectly ordinary shared Icecast host answers 404 for it - so a
/// station saw an empty space where the count should be, for months, with nothing anywhere to say
/// why. These checks are about the order things are asked in and what happens when each one refuses,
/// which is exactly the part a parsing test cannot see.
/// </para>
/// <para>
/// Real loopback servers rather than a mocked HTTP client: the fallback only means anything if it is
/// driven by actual status codes, and a mock would have been written to match whatever the code
/// already did.
/// </para>
/// </summary>
internal static class ListenerChainChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("the modern endpoint is asked first and is enough", () =>
        {
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = Json("""{"icestats":{"source":{"listenurl":"http://x/live","listeners":7}}}"""),
            };

            var report = Ask(server.Port);

            Expect(report.Value == 7, $"got {Describe(report)}, expected 7");
            Expect(server.Paths.Count == 1, $"asked {server.Paths.Count} endpoints when the first one answered");
        });

        failures += Check("a host with no status-json.xsl still gets counted", () =>
        {
            // The real case. The server is healthy Icecast; the host has removed the status templates.
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = NotFound(),
                ["/status2.xsl"] = Text(
                    "\nGlobal,Clients:1536,Sources:59,,0,,\n" +
                    "MountPoint,Connections,Stream Name,Current Listeners,Description,Currently Playing,Stream URL\n" +
                    "/live,12,Example FM,8,desc,song,http://x/live\n"),
            };

            var report = Ask(server.Port);

            Expect(report.Value == 8, $"got {Describe(report)}, expected 8 from status2.xsl");
            Expect(report.Detail.Contains("status2"), $"did not say where the number came from: \"{report.Detail}\"");
        });

        failures += Check("when nothing is public, the mount's own admin stats are asked with the broadcast password", () =>
        {
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = NotFound(),
                ["/status2.xsl"] = NotFound(),
                ["/admin/stats"] = Authenticated(
                    "source", "hunter2",
                    Xml("""<icestats><source mount="/live"><listeners>5</listeners></source></icestats>""")),
            };

            var report = Ask(server.Port, password: "hunter2");

            Expect(report.Value == 5, $"got {Describe(report)}, expected 5 from the admin stats");
            Expect(server.SawAuthorizationOn("/admin/stats"), "asked the admin endpoint without credentials");
            Expect(!server.SawAuthorizationOn("/status-json.xsl") && !server.SawAuthorizationOn("/status2.xsl"),
                "sent the broadcast password to a public status page that never asked for one");
        });

        failures += Check("counting people never means downloading who they are", () =>
        {
            // /admin/listclients returns the same number along with every listener's IP address.
            // Deck asks for the mount's stats instead, and this is what keeps it that way.
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = NotFound(),
                ["/status2.xsl"] = NotFound(),
                ["/admin/stats"] = Unauthorised(),
                ["/admin/listclients"] = Xml(
                    """<icestats><source mount="/live"><listeners>3</listeners><listener><IP>203.0.113.9</IP></listener></source></icestats>"""),
            };

            Ask(server.Port, password: "hunter2");

            Expect(!server.Paths.Contains("/admin/listclients"),
                "asked for the listener list, which is a list of people's addresses");
        });

        failures += Check("a wrong password ends in an explanation, not a number", () =>
        {
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = NotFound(),
                ["/status2.xsl"] = NotFound(),
                ["/admin/stats"] = Unauthorised(),
            };

            var report = Ask(server.Port, password: "wrong");

            Expect(report.Status == ListenerStatus.NotPublished, $"got {report.Status}");
            Expect(report.Value is null, "invented a number when every endpoint refused");

            // The whole point: the user can find out why the space is empty.
            foreach (var mentioned in new[] { "status-json.xsl", "status2.xsl", "password" })
            {
                Expect(report.Detail.Contains(mentioned),
                    $"the explanation does not mention {mentioned}: \"{report.Detail}\"");
            }
        });

        failures += Check("all three tried before giving up", () =>
        {
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = NotFound(),
                ["/status2.xsl"] = NotFound(),
                ["/admin/stats"] = Unauthorised(),
            };

            Ask(server.Port, password: "hunter2");

            foreach (var path in new[] { "/status-json.xsl", "/status2.xsl", "/admin/stats" })
            {
                Expect(server.Paths.Contains(path), $"never asked {path} - asked {string.Join(", ", server.Paths)}");
            }
        });

        failures += Check("a status page that answers about somebody else is not our count", () =>
        {
            // A shared host serving a document that lists other stations and not ours. Reporting their
            // audience as this station's would be worse than reporting none.
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = NotFound(),
                ["/status2.xsl"] = Text("/theirs,44,Someone Else,31,desc,song,url\n"),
                ["/admin/stats"] = Unauthorised(),
            };

            var report = Ask(server.Port, password: "hunter2");

            Expect(report.Value is null, $"reported {report.Value} listeners from another station's mount");
        });

        failures += Check("nothing listening at all says so, and says it differently", () =>
        {
            // A closed port is worth acting on - the address or the port is wrong - and must not read
            // the same as a server that simply does not publish the figure.
            int port;
            using (var closed = new RoutedServer()) port = closed.Port;

            var report = Ask(port, password: "hunter2");

            Expect(report.Value is null, "counted listeners on a port with nothing behind it");
            Expect(report.Status is ListenerStatus.NotPublished or ListenerStatus.Unreachable,
                $"got {report.Status}");
        });

        failures += Check("the shared server's own client total is never read as this station's audience", () =>
        {
            // This is the real document that host returns for an idle mount: server-wide counters and
            // no source element at all. It carries a four-figure client count for the whole machine -
            // fifty-nine other stations' listeners - and reporting that as one station's audience would
            // be the most confidently wrong thing Deck could put on screen.
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = NotFound(),
                ["/status2.xsl"] = NotFound(),
                ["/admin/stats"] = Xml("""
                    <?xml version="1.0"?>
                    <icestats><admin>icemaster@localhost</admin><clients>1552</clients>
                    <client_connections>1773</client_connections><listeners>1552</listeners></icestats>
                    """),
            };

            var report = Ask(server.Port, password: "hunter2");

            Expect(report.Value is null, $"reported {report.Value} listeners from the whole shared server");
        });

        failures += Check("the endpoint that worked is asked first next time", () =>
        {
            // Fifteen seconds apart for the length of a show. On a host that publishes nothing publicly
            // that would otherwise be two futile requests every poll, several hundred in an evening,
            // from a client that has already been told no.
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = NotFound(),
                ["/status2.xsl"] = Text("/live,12,Example FM,4,desc,song,url\n"),
            };

            var profile = Profile(server.Port);

            var first = ListenerCounter.QueryAsync(profile).GetAwaiter().GetResult();
            var asked = server.Paths.Count;
            var second = ListenerCounter.QueryAsync(profile).GetAwaiter().GetResult();

            Expect(first.Value == 4 && second.Value == 4, $"got {Describe(first)} then {Describe(second)}");
            Expect(asked == 2, $"the first poll took {asked} requests, expected 2");
            Expect(server.Paths.Count == 3, $"the second poll took {server.Paths.Count - asked} requests, expected 1");
            Expect(server.Paths[2] == "/status2.xsl", $"asked {server.Paths[2]} first rather than what worked");
        });

        failures += Check("SHOUTcast v1 stats found on the port the user actually saved", () =>
        {
            // A real one: the user's v1 host takes broadcasts on the port after the one listeners use,
            // so Deck connects on 8439 while the saved port is 8438 - and 7.html lives on 8438. The old
            // code only ever looked one below the saved port, found nothing on 8437, and reported no
            // count for ever. Both are worth asking, because hosts differ about which they quoted.
            using var server = new RoutedServer
            {
                ["/7.html"] = Text("<HTML><body>2,0,8,100,2,128, - </body></html>"),
            };

            var profile = Profile(server.Port);
            profile.ServerType = ServerType.ShoutcastV1;

            var report = ListenerCounter.QueryAsync(profile).GetAwaiter().GetResult();

            Expect(report.Value == 2, $"got {Describe(report)}, expected 2");
            Expect(report.Detail.Contains(server.Port.ToString()),
                $"did not say which port answered: \"{report.Detail}\"");
        });

        failures += Check("a server whose type is unknown is not guessed at", () =>
        {
            using var server = new RoutedServer
            {
                ["/status-json.xsl"] = Json("""{"icestats":{"source":{"listeners":7}}}"""),
            };

            var profile = Profile(server.Port);
            profile.ServerType = ServerType.Unknown;

            var report = ListenerCounter.QueryAsync(profile).GetAwaiter().GetResult();

            Expect(report.Status == ListenerStatus.Unsupported, $"got {report.Status}");
            Expect(server.Paths.Count == 0, "went asking endpoints before knowing what kind of server it is");
        });

        return failures;
    }

    private static ListenerReport Ask(int port, string? password = null)
    {
        var profile = Profile(port);
        if (password is not null) profile.Password = password;

        return ListenerCounter.QueryAsync(profile).GetAwaiter().GetResult();
    }

    private static ServerProfile Profile(int port) => new()
    {
        Name = "test server",
        ServerType = ServerType.Icecast,
        Host = "127.0.0.1",
        Port = port,
        MountPoint = "/live",
        Username = "source",
    };

    private static string Describe(ListenerReport report) =>
        report.Value?.ToString() ?? $"{report.Status} ({report.Detail})";

    // ------------------------------------------------------------------ canned responses

    private const string NotFoundBody =
        "HTTP/1.1 404 File Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private const string UnauthorisedBody =
        "HTTP/1.1 401 Authentication Required\r\nWWW-Authenticate: Basic realm=\"Icecast\"\r\n" +
        "Content-Length: 0\r\nConnection: close\r\n\r\n";

    /// <summary>A route is a function of the request, so the ones that check credentials can.</summary>
    private static Func<string, string> NotFound() => _ => NotFoundBody;

    private static Func<string, string> Unauthorised() => _ => UnauthorisedBody;

    private static Func<string, string> Json(string body) => Response("application/json", body);

    private static Func<string, string> Xml(string body) => Response("text/xml", body);

    private static Func<string, string> Text(string body) => Response("text/plain", body);

    private static Func<string, string> Response(string type, string body)
    {
        var response =
            $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
            $"Connection: close\r\n\r\n{body}";

        return _ => response;
    }

    /// <summary>A response only given to a request carrying these credentials; 401 otherwise.</summary>
    private static Func<string, string> Authenticated(string user, string password, Func<string, string> response)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

        return request => request.Contains(expected, StringComparison.Ordinal)
            ? response(request)
            : UnauthorisedBody;
    }

    /// <summary>
    /// A loopback HTTP server with a route table, recording what was asked for and whether the request
    /// carried credentials. Hand-rolled over TcpListener because HttpListener needs a URL reservation
    /// the test runner has no business asking for.
    /// </summary>
    private sealed class RoutedServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Dictionary<string, Func<string, string>> _routes = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<string> _paths = [];
        private readonly HashSet<string> _authenticated = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public RoutedServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public Func<string, string> this[string path]
        {
            set => _routes[path] = value;
        }

        public IReadOnlyList<string> Paths
        {
            get { lock (_lock) return _paths.ToList(); }
        }

        public bool SawAuthorizationOn(string path)
        {
            lock (_lock) return _authenticated.Contains(path);
        }

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
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                    var request = Encoding.ASCII.GetString(buffer, 0, read);

                    var path = PathOf(request);

                    lock (_lock)
                    {
                        _paths.Add(path);
                        if (request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase))
                        {
                            _authenticated.Add(path);
                        }
                    }

                    var body = _routes.TryGetValue(path, out var handler) ? handler(request) : NotFoundBody;

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(body), _stop.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A test server going quiet is a test result, not something to report here.
                }
            }
        }

        /// <summary>The path without its query, which is where the mount is passed.</summary>
        private static string PathOf(string request)
        {
            var line = request.Split('\r')[0];
            var parts = line.Split(' ');
            if (parts.Length < 2) return "/";

            var target = parts[1];
            var query = target.IndexOf('?');
            return query < 0 ? target : target[..query];
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
