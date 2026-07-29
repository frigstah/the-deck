using System.Globalization;
using System.Text;
using System.Text.Json;
using Deck.Core.Net;

namespace Deck.Core.Control;

/// <summary>
/// The remote control endpoint (I10): go live, go off, set a title, start recording, read the status.
/// <para>
/// This one is a bigger deal than the now-playing endpoint next door. That one can change what
/// listeners see; this one can put a station on air. So it is off by default, loopback by default,
/// and refuses outright to open to the network without a password - the same fail-closed rule, held
/// harder.
/// </para>
/// <para>
/// Every command is a plain GET. Not because that is correct HTTP - it is not, these are anything
/// but safe - but because the automation systems this exists for can all fetch a URL and most cannot
/// easily POST one. A rule that makes Deck unusable from the software it is meant to serve is the
/// wrong rule; the token is what actually guards these, not the verb.
/// </para>
/// </summary>
public sealed class ControlServer : IDisposable
{
    public const int DefaultPort = 8999;

    private readonly IControlSurface _surface;
    private readonly TinyHttpServer _server;

    public ControlServer(IControlSurface surface)
    {
        _surface = surface;

        _server = new TinyHttpServer(Handle)
        {
            OpenWithoutTokenProblem =
                "Letting other computers control Deck needs a password. Without one, anything on " +
                "your network could put your station on air or take it off.",
        };
    }

    public bool IsRunning => _server.IsRunning;

    public int Port => _server.IsRunning ? _server.Port : DefaultPort;

    public bool AllowOtherComputers => _server.AllowOtherComputers;

    public string? Token => _server.Token;

    public string? Problem => _server.Problem;

    public int CommandsHandled { get; private set; }

    /// <summary>The last command that arrived, so the UI can show the endpoint is being used.</summary>
    public string? LastCommand { get; private set; }

    public event EventHandler? CommandHandled;

    public string ExampleUrl
    {
        get
        {
            var host = AllowOtherComputers ? "this-pc" : "127.0.0.1";
            var token = string.IsNullOrEmpty(Token) ? string.Empty : $"?token={Uri.EscapeDataString(Token)}";
            return $"http://{host}:{Port}/status{token}";
        }
    }

    public bool Start(int port, bool allowOtherComputers, string? token)
    {
        CommandsHandled = 0;
        LastCommand = null;

        if (!_server.Start(port, allowOtherComputers, token)) return false;

        ControlHandshake.Write(_server.Port, _server.Token);
        return true;
    }

    public void Stop()
    {
        _server.Stop();
        ControlHandshake.Clear();
    }

    private (int Status, string Body) Handle(HttpRequest request)
    {
        if (request.Path is "/" or "/help") return (200, HelpText());

        if (!_server.Authorised(request)) return (401, "Wrong password.");

        // Read before dispatch, so an unknown path is a 404 rather than a silent success.
        var result = request.Path switch
        {
            "/status" => Status(request),
            "/live" or "/connect" or "/on" => Run("live", () => _surface.GoLiveAsync().GetAwaiter().GetResult()),
            "/off" or "/disconnect" or "/stop" => Run("off", () => _surface.GoOffAsync().GetAwaiter().GetResult()),
            "/title" => Title(request),
            "/record" => Run("record", _surface.StartRecording),
            "/record/stop" => Run("record/stop", _surface.StopRecording),
            "/mute" => Mute(request),
            "/gain" => Gain(request),
            _ => (404, "Not a command. Ask /help for the list."),
        };

        return result;
    }

    private (int Status, string Body) Status(HttpRequest request)
    {
        Record("status");

        var status = _surface.Status();

        var wantsJson = request.Value("format") is { } format &&
                        format.Equals("json", StringComparison.OrdinalIgnoreCase);

        return (200, wantsJson ? Json(status) : Text(status));
    }

    private (int Status, string Body) Title(HttpRequest request)
    {
        var text = request.Value("text") ?? request.Value("song") ?? request.Value("title");

        if (string.IsNullOrWhiteSpace(text))
        {
            return (400, "Nothing to set. Send /title?text=Artist - Title");
        }

        return Run("title", () => _surface.SetTitle(text));
    }

    private (int Status, string Body) Mute(HttpRequest request)
    {
        var value = request.Value("on") ?? request.Value("muted") ?? "true";
        if (!TryReadBool(value, out var muted))
        {
            return (400, $"\"{value}\" is not yes or no. Send /mute?on=true or /mute?on=false.");
        }

        return Run("mute", () => _surface.SetMuted(muted));
    }

    private (int Status, string Body) Gain(HttpRequest request)
    {
        var value = request.Value("db");

        if (value is null || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
        {
            return (400, "Send a number of decibels, for example /gain?db=-3.5");
        }

        return Run("gain", () => _surface.SetGainDb(db));
    }

    private (int Status, string Body) Run(string name, Func<ControlResult> action)
    {
        Record(name);

        ControlResult result;

        try
        {
            result = action();
        }
        catch (Exception ex)
        {
            // The caller is a script. It gets a sentence it can log, not a stack trace, and Deck
            // stays up either way - a bad remote command must never take the show off air.
            return (500, $"Deck could not do that: {ex.Message}");
        }

        // 409 rather than 400: the request was well formed, Deck is simply not in a state where it
        // makes sense - already live, no server chosen, nothing recording.
        return result.Ok ? (200, result.Message) : (409, result.Message);
    }

    private void Record(string command)
    {
        CommandsHandled++;
        LastCommand = command;
        CommandHandled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Readable down a terminal, and what <c>Deck.exe --status</c> prints.</summary>
    public static string Text(ControlStatus status)
    {
        var text = new StringBuilder();

        text.AppendLine(status.State.ToUpperInvariant());

        if (status.Station is { Length: > 0 }) text.AppendLine($"Station:    {status.Station}");

        foreach (var destination in status.Destinations)
        {
            text.AppendLine($"Sending to: {destination}");
        }

        if (status.IsLive) text.AppendLine($"On air for: {Duration(status.Uptime)}");
        if (status.Listeners is { } listeners) text.AppendLine($"Listeners:  {listeners}");

        if (status.IsAudioRunning)
        {
            text.AppendLine($"Level:      {status.PeakDb:0.0} dB peak");

            text.AppendLine(status.Lufs is { } lufs
                ? $"Loudness:   {lufs:0.0} LUFS"
                : "Loudness:   not enough audio yet");
        }
        else
        {
            text.AppendLine("Level:      no audio running");
        }

        if (status.NowPlaying is { Length: > 0 }) text.AppendLine($"Playing:    {status.NowPlaying}");

        text.AppendLine(status.IsRecording
            ? $"Recording:  {status.RecordingFile ?? "yes"}"
            : "Recording:  no");

        if (status.Problem is { Length: > 0 }) text.AppendLine($"Problem:    {status.Problem}");

        return text.ToString();
    }

    private static string Json(ControlStatus status) =>
        JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });

    private static string Duration(TimeSpan uptime) =>
        uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s"
            : $"{uptime.Minutes}m {uptime.Seconds}s";

    private static bool TryReadBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "yes" or "on":
                result = true;
                return true;

            case "0" or "false" or "no" or "off":
                result = false;
                return true;

            default:
                result = false;
                return false;
        }
    }

    private string HelpText() =>
        $"""
         Deck remote control.

           GET /status              what Deck is doing, as text
           GET /status?format=json  the same, for a program to read
           GET /live                go on air
           GET /off                 go off air
           GET /title?text=...      set what listeners see as now playing
           GET /record              start recording
           GET /record/stop         stop recording
           GET /mute?on=true        mute or unmute the input
           GET /gain?db=-3          set the input level

         {(Token is null ? "No password is set." : "Add ?token=... to every command, or send it as a Bearer token.")}
         Listening on {(AllowOtherComputers ? "all network interfaces" : "this computer only")}, port {Port}.

         The same commands are on the command line: Deck.exe --status, --live, --off, --title "...".
         """;

    public void Dispose() => Stop();
}
