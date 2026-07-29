using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sirs.Core.Control;

namespace Sirs.EncoderCheck;

/// <summary>
/// The remote control endpoint and the command line (I10).
/// <para>
/// These go over a real socket with hand-written HTTP rather than through the server's own parser,
/// because the thing worth proving is what a stranger's script gets when it connects - not that the
/// code agrees with itself. The one that matters most is the last: an endpoint that can put a
/// station on air must not be reachable from the network unless someone deliberately made it so.
/// </para>
/// </summary>
internal static class ControlChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("arguments become the right command", () =>
        {
            ExpectPath(CommandLine.Parse(["--live"]), "/live");
            ExpectPath(CommandLine.Parse(["--connect"]), "/live");
            ExpectPath(CommandLine.Parse(["--off"]), "/off");
            ExpectPath(CommandLine.Parse(["--status"]), "/status");
            ExpectPath(CommandLine.Parse(["--record"]), "/record");
            ExpectPath(CommandLine.Parse(["--stop-recording"]), "/record/stop");
            ExpectPath(CommandLine.Parse(["--mute"]), "/mute?on=true");
            ExpectPath(CommandLine.Parse(["--unmute"]), "/mute?on=false");
        });

        failures += Check("no arguments means open the window", () =>
        {
            Expect(CommandLine.Parse([]) is null, "an empty command line asked for something");

            // What a shortcut with a working directory or an unrelated switch looks like.
            Expect(CommandLine.Parse(["--squelch"]) is null, "an unknown switch was treated as a command");
        });

        failures += Check("a title survives spaces and punctuation", () =>
        {
            var separate = CommandLine.Parse(["--title", "Sigur Rós — Hoppípolla"]);
            var inline = CommandLine.Parse(["--title=Sigur Rós — Hoppípolla"]);

            Expect(separate?.Path == inline?.Path, "--title x and --title=x parsed differently");

            // Round-tripped through the same unescaping the server does, so this proves the whole
            // path rather than just that something was escaped.
            var value = ValueFrom(separate!.Path, "text");
            Expect(value == "Sigur Rós — Hoppípolla", $"the title came back as \"{value}\"");
        });

        failures += Check("a switch is never swallowed as a title", () =>
        {
            // Without a guard this sets the title to "--live" and never goes on air, which would be
            // a maddening thing to debug from a log that says "title set".
            var request = CommandLine.Parse(["--title", "--live"]);
            Expect(request?.Error is not null, "--title --live was accepted as a title");
        });

        failures += Check("a negative level is a number, not a switch", () =>
        {
            var request = CommandLine.Parse(["--gain", "-3.5"]);
            Expect(request?.Error is null, $"--gain -3.5 was refused: {request?.Error}");
            Expect(ValueFrom(request!.Path, "db") == "-3.5", $"the level came back as {ValueFrom(request.Path, "db")}");

            Expect(CommandLine.Parse(["--gain", "loud"])?.Error is not null, "--gain loud was accepted");
        });

        failures += Check("--json only when it was asked for", () =>
        {
            ExpectPath(CommandLine.Parse(["--status", "--json"]), "/status?format=json");
            ExpectPath(CommandLine.Parse(["--json", "--status"]), "/status?format=json");
            ExpectPath(CommandLine.Parse(["--status", "--format=json"]), "/status?format=json");
            ExpectPath(CommandLine.Parse(["--status", "--format=xml"]), "/status");
        });

        // ---------------------------------------------------------------- over a socket

        failures += Check("commands reach the app and come back as sentences", () =>
        {
            var surface = new FakeSurface();
            using var server = new ControlServer(surface);

            Expect(server.Start(0, allowOtherComputers: false, token: null), $"the endpoint did not start: {server.Problem}");

            var (status, body) = Get(server.Port, "/live");
            Expect(status == 200, $"/live answered {status}: {body}");
            Expect(surface.IsLive, "/live did not put the fake station on air");

            // Asking twice is a 409, not a 400 - the request was fine, the state was not.
            (status, body) = Get(server.Port, "/live");
            Expect(status == 409, $"a second /live answered {status}, expected 409");
            Expect(body.Contains("Already", StringComparison.OrdinalIgnoreCase), $"the refusal read \"{body}\"");

            (status, _) = Get(server.Port, "/off");
            Expect(status == 200 && !surface.IsLive, "/off did not take it off air");

            (status, _) = Get(server.Port, "/title?text=Artist%20-%20Song");
            Expect(status == 200 && surface.Title == "Artist - Song", $"the title came through as \"{surface.Title}\"");

            (status, _) = Get(server.Port, "/gain?db=-6");
            Expect(status == 200 && Math.Abs(surface.GainDb + 6) < 0.001, $"the level came through as {surface.GainDb}");

            (status, body) = Get(server.Port, "/nonsense");
            Expect(status == 404, $"an unknown command answered {status}: {body}");
        });

        failures += Check("the status reads as text and as JSON", () =>
        {
            var surface = new FakeSurface();
            using var server = new ControlServer(surface);
            server.Start(0, allowOtherComputers: false, token: null);

            Get(server.Port, "/live");
            Get(server.Port, "/title?text=Something%20Good");

            var (_, text) = Get(server.Port, "/status");
            Expect(text.Contains("Something Good"), $"the text status left out the title:\n{text}");

            var (_, json) = Get(server.Port, "/status?format=json");
            using var document = JsonDocument.Parse(json);

            Expect(document.RootElement.GetProperty("IsLive").GetBoolean(), "the JSON status did not say it was live");
            Expect(document.RootElement.GetProperty("NowPlaying").GetString() == "Something Good",
                "the JSON status had the wrong title");
        });

        failures += Check("a password is required once one is set", () =>
        {
            var surface = new FakeSurface();
            using var server = new ControlServer(surface);
            server.Start(0, allowOtherComputers: false, token: "hunter2");

            var (status, _) = Get(server.Port, "/live");
            Expect(status == 401, $"an unauthenticated /live answered {status}");
            Expect(!surface.IsLive, "an unauthenticated command went on air anyway");

            (status, _) = Get(server.Port, "/live?token=wrong");
            Expect(status == 401, $"a wrong password answered {status}");
            Expect(!surface.IsLive, "a wrong password went on air anyway");

            (status, _) = Get(server.Port, "/live?token=hunter2");
            Expect(status == 200 && surface.IsLive, "the right password was refused");

            // A Bearer header is the shape most HTTP clients reach for.
            Get(server.Port, "/off?token=hunter2");
            (status, _) = Get(server.Port, "/live", bearer: "hunter2");
            Expect(status == 200 && surface.IsLive, "a Bearer token was refused");
        });

        failures += Check("a command that throws does not take SIRS down", () =>
        {
            var surface = new FakeSurface { ThrowOnLive = true };
            using var server = new ControlServer(surface);
            server.Start(0, allowOtherComputers: false, token: null);

            var (status, body) = Get(server.Port, "/live");
            Expect(status == 500, $"a failing command answered {status}");
            Expect(!body.Contains("   at ", StringComparison.Ordinal), "a stack trace was sent to the caller");

            // Still answering afterwards is the point.
            (status, _) = Get(server.Port, "/status");
            Expect(status == 200, $"the endpoint stopped working after a failure: {status}");
        });

        failures += Check("it refuses to open to the network without a password", () =>
        {
            var surface = new FakeSurface();
            using var server = new ControlServer(surface);

            var started = server.Start(0, allowOtherComputers: true, token: null);

            Expect(!started, "SIRS opened a control endpoint to the whole network with no password");
            Expect(!server.IsRunning, "it reported failure but was listening anyway");
            Expect(server.Problem is not null, "it failed without saying why");
        });

        failures += Check("loopback really means loopback", () =>
        {
            var surface = new FakeSurface();
            using var server = new ControlServer(surface);
            server.Start(0, allowOtherComputers: false, token: null);

            if (LanAddress() is not { } address)
            {
                Console.WriteLine("       (skipped: this machine has no non-loopback address)");
                return;
            }

            // The whole security claim in one assertion: another machine cannot even connect.
            using var client = new TcpClient();

            try
            {
                client.Connect(address, server.Port);
                throw new Exception($"the control endpoint accepted a connection on {address}");
            }
            catch (SocketException)
            {
                // Refused, which is what should happen.
            }
        });

        return failures;
    }

    /// <summary>A stand-in for the app, so the endpoint can be driven without a window.</summary>
    private sealed class FakeSurface : IControlSurface
    {
        public bool IsLive { get; private set; }

        public string? Title { get; private set; }

        public double GainDb { get; private set; }

        public bool Muted { get; private set; }

        public bool Recording { get; private set; }

        public bool ThrowOnLive { get; init; }

        public ControlStatus Status() => new()
        {
            State = IsLive ? "Live" : "Off air",
            IsLive = IsLive,
            Station = "Test Station",
            NowPlaying = Title,
            PeakDb = -12.5,
            IsRecording = Recording,
            IsAudioRunning = true,
        };

        public Task<ControlResult> GoLiveAsync()
        {
            if (ThrowOnLive) throw new InvalidOperationException("the encoder fell over");
            if (IsLive) return Task.FromResult(ControlResult.Refused("Already live."));

            IsLive = true;
            return Task.FromResult(ControlResult.Done("Going on air."));
        }

        public Task<ControlResult> GoOffAsync()
        {
            if (!IsLive) return Task.FromResult(ControlResult.Refused("Not on air."));

            IsLive = false;
            return Task.FromResult(ControlResult.Done("Coming off air."));
        }

        public ControlResult SetTitle(string title)
        {
            Title = title;
            return ControlResult.Done($"Now playing: {title}");
        }

        public ControlResult StartRecording()
        {
            if (Recording) return ControlResult.Refused("Already recording.");

            Recording = true;
            return ControlResult.Done("Recording.");
        }

        public ControlResult StopRecording()
        {
            if (!Recording) return ControlResult.Refused("Not recording.");

            Recording = false;
            return ControlResult.Done("Stopped.");
        }

        public ControlResult SetMuted(bool muted)
        {
            Muted = muted;
            return ControlResult.Done(muted ? "Muted." : "Unmuted.");
        }

        public ControlResult SetGainDb(double db)
        {
            GainDb = db;
            return ControlResult.Done($"{db:0.0} dB");
        }
    }

    /// <summary>A hand-written GET, so nothing of the server's own parsing is assumed.</summary>
    private static (int Status, string Body) Get(int port, string target, string? bearer = null)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);

        using var stream = client.GetStream();

        var authorisation = bearer is null ? string.Empty : $"Authorization: Bearer {bearer}\r\n";
        var request = Encoding.UTF8.GetBytes(
            $"GET {target} HTTP/1.1\r\nHost: 127.0.0.1\r\n{authorisation}Connection: close\r\n\r\n");

        stream.Write(request);
        stream.Flush();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = reader.ReadToEnd();

        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? response : response[..split];
        var body = split < 0 ? string.Empty : response[(split + 4)..];

        var statusLine = head.Split("\r\n")[0].Split(' ');
        var status = statusLine.Length > 1 && int.TryParse(statusLine[1], out var code) ? code : 0;

        return (status, body);
    }

    private static IPAddress? LanAddress() =>
        Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    private static string? ValueFrom(string path, string name)
    {
        var query = path.IndexOf('?');
        if (query < 0) return null;

        foreach (var pair in path[(query + 1)..].Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0 && pair[..equals] == name) return Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return null;
    }

    private static void ExpectPath(CommandLineRequest? request, string expected)
    {
        if (request is null) throw new Exception($"expected {expected}, got no command at all");
        if (request.Path != expected) throw new Exception($"expected {expected}, got {request.Path}");
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
