// A shared Icecast host, imitated closely enough to broadcast to.
//
// The point is to see the whole loop on screen without touching the user's station: Deck connects,
// goes live, polls for listeners, and the deck reports what came back. Built to behave like the host
// the bug was found on - status-json.xsl answers 404, status2.xsl lists nothing, and only the mount's
// own admin stats will say - so the fallback is exercised rather than assumed.
//
// Also writes the portable data folder the app will read, so nothing in %APPDATA% is touched.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Deck.Core.Codecs;
using Deck.Core.Servers;

Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8765;

// listeners=-1 means "the admin stats will not say", so the deck should show that it does not know.
var listeners = args.Length > 1 && int.TryParse(args[1], out var l) ? l : 3;

var dataDirectory = args.Length > 2 && args[2] != "-" ? args[2] : null;

// alwaysUp: report listeners even with no source connected, which is what a server with a fallback
// mount or an AutoDJ looks like while its next presenter is still off air.
var alwaysUp = args.Length > 3 && args[3] == "alwaysup";

if (dataDirectory is not null)
{
    Directory.CreateDirectory(dataDirectory);

    var profile = new ServerProfile
    {
        Name = "loopback",
        ServerType = ServerType.Icecast,
        Host = "127.0.0.1",
        Port = port,
        MountPoint = "/live",
        Username = "source",
        Password = "letmein",
        StationName = "Loopback Test",
        Encoder = QualityPreset.Default.Settings,
    };

    new ProfileStore(Path.Combine(dataDirectory, "servers.json")).Save([profile]);
    Console.WriteLine($"wrote {Path.Combine(dataDirectory, "servers.json")}");
}

var live = 0;
var sourceConnections = 0;
var statusRequests = new List<string>();

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"fake Icecast on 127.0.0.1:{port}, mount /live, listeners={(listeners < 0 ? "not published" : listeners.ToString())}");
Console.WriteLine("press Ctrl+C to stop\n");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => ServeAsync(client));
}

async Task ServeAsync(TcpClient client)
{
    using (client)
    {
        try
        {
            var stream = client.GetStream();
            var header = new StringBuilder();
            var buffer = new byte[1];

            // Read to the end of the headers, one byte at a time: whatever follows is either audio or
            // nothing, and it must not be swallowed with the header.
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) return;
                header.Append((char)buffer[0]);
                if (header.Length > 8192) return;
            }

            var request = header.ToString();
            var line = request.Split("\r\n")[0];
            var parts = line.Split(' ');
            var method = parts.Length > 0 ? parts[0] : string.Empty;
            var target = parts.Length > 1 ? parts[1] : "/";
            var path = target.Split('?')[0];

            if (method is "SOURCE" or "PUT")
            {
                Interlocked.Increment(ref sourceConnections);
                Interlocked.Exchange(ref live, 1);
                Console.WriteLine($"[source] {method} {target} — on air");

                await Write(stream, method == "PUT"
                    ? "HTTP/1.1 100 Continue\r\n\r\n"
                    : "HTTP/1.0 200 OK\r\n\r\n");

                // Swallow the stream. Counting the bytes is the only proof audio is really flowing.
                var audio = new byte[16384];
                long total = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(audio);
                    if (read == 0) break;
                    total += read;
                }

                Interlocked.Exchange(ref live, 0);
                Console.WriteLine($"[source] closed after {total / 1024} KB — off air");
                return;
            }

            lock (statusRequests) statusRequests.Add(path);
            Console.WriteLine($"[stats ] {method} {target}");

            switch (path)
            {
                // Exactly what the real host does: the endpoint every Icecast is supposed to have is
                // simply not there.
                case "/status-json.xsl":
                    await Write(stream, "HTTP/1.1 404 File Not Found\r\nServer: Icecast\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    break;

                // Answers, but lists no mounts - also what the real host does.
                case "/status2.xsl":
                    await Body(stream, "text/plain",
                        "\nGlobal,Clients:1552,Sources:59,,0,,\n" +
                        "MountPoint,Connections,Stream Name,Current Listeners,Description,Currently Playing,Stream URL\n");
                    break;

                case "/admin/stats":
                    if (!request.Contains("Authorization: Basic", StringComparison.OrdinalIgnoreCase))
                    {
                        await Write(stream, "HTTP/1.1 401 Authentication Required\r\nWWW-Authenticate: Basic realm=\"Icecast\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        break;
                    }

                    var mountLive = alwaysUp || Volatile.Read(ref live) == 1;
                    var source = mountLive && listeners >= 0
                        ? $"<source mount=\"/live\"><listeners>{listeners}</listeners><listener_peak>{listeners + 2}</listener_peak></source>"
                        : string.Empty;

                    await Body(stream, "text/xml",
                        "<?xml version=\"1.0\"?>\n<icestats><admin>icemaster@localhost</admin>" +
                        "<clients>1552</clients><client_connections>1773</client_connections>" +
                        $"{source}</icestats>");
                    break;

                case "/admin/metadata":
                    await Body(stream, "text/xml", "<?xml version=\"1.0\"?>\n<iceresponse><message>Metadata update successful</message><return>1</return></iceresponse>");
                    break;

                default:
                    await Body(stream, "text/html", "<html><body>Icecast</body></html>");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[error ] {ex.GetType().Name}: {ex.Message}");
        }
    }
}

async Task Write(NetworkStream stream, string text)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    await stream.WriteAsync(bytes);
    await stream.FlushAsync();
}

async Task Body(NetworkStream stream, string type, string body)
{
    await Write(stream,
        $"HTTP/1.1 200 OK\r\nServer: Icecast 2.4.0-kh22\r\nContent-Type: {type}\r\n" +
        $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
}
