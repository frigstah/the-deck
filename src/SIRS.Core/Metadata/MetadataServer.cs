using Sirs.Core.Net;

namespace Sirs.Core.Metadata;

public sealed class RemoteTrackEventArgs(string? artist, string? title, string? album, string? song) : EventArgs
{
    public string? Artist { get; } = artist;

    public string? Title { get; } = title;

    public string? Album { get; } = album;

    /// <summary>A complete line, when the caller sent one instead of the separate pieces.</summary>
    public string? Song { get; } = song;
}

/// <summary>
/// A small HTTP endpoint that playout software can push now-playing to (F4).
/// <para>
/// It speaks the Icecast admin shape - <c>/admin/metadata?mode=updinfo&amp;song=...</c> - as well as
/// its own <c>/metadata</c>, because automation systems already have a box for the Icecast URL and
/// pointing that at SIRS should just work.
/// </para>
/// <para>
/// It listens on loopback only unless the user deliberately opens it up, and opening it up requires
/// a token. This is a listening socket on someone's machine; it should be as small a target as it
/// can be, and off entirely until asked for.
/// </para>
/// </summary>
public sealed class MetadataServer : IDisposable
{
    public const int DefaultPort = 8998;

    private readonly TinyHttpServer _server;

    public MetadataServer()
    {
        _server = new TinyHttpServer(Handle)
        {
            OpenWithoutTokenProblem =
                "Letting other computers send titles needs a password, so that only your " +
                "automation can change what listeners see.",
        };
    }

    public bool IsRunning => _server.IsRunning;

    public int Port => _server.IsRunning ? _server.Port : DefaultPort;

    /// <summary>False means loopback only - nothing outside this computer can reach it.</summary>
    public bool AllowOtherComputers => _server.AllowOtherComputers;

    public string? Token => _server.Token;

    /// <summary>Why the endpoint is not listening, phrased for the user.</summary>
    public string? Problem => _server.Problem;

    /// <summary>How many updates have arrived, so the UI can show it is actually being used.</summary>
    public int UpdatesReceived { get; private set; }

    public event EventHandler<RemoteTrackEventArgs>? TrackReceived;

    /// <summary>The address to paste into the automation system's "Icecast server" box.</summary>
    public string ExampleUrl
    {
        get
        {
            var host = AllowOtherComputers ? "this-pc" : "127.0.0.1";
            var token = string.IsNullOrEmpty(Token) ? string.Empty : $"&token={Uri.EscapeDataString(Token)}";
            return $"http://{host}:{Port}/admin/metadata?mode=updinfo&song=Artist%20-%20Title{token}";
        }
    }

    public bool Start(int port, bool allowOtherComputers, string? token)
    {
        UpdatesReceived = 0;
        return _server.Start(port, allowOtherComputers, token);
    }

    public void Stop() => _server.Stop();

    private (int Status, string Body) Handle(HttpRequest request)
    {
        if (request.Path is "/" or "/help")
        {
            return (200, HelpText());
        }

        if (request.Path is not ("/metadata" or "/admin/metadata"))
        {
            return (404, "Not found. Send titles to /metadata.");
        }

        if (!_server.Authorised(request))
        {
            return (401, "Wrong password.");
        }

        var song = request.Value("song");
        var artist = request.Value("artist");
        var title = request.Value("title");
        var album = request.Value("album");

        if (string.IsNullOrWhiteSpace(song) && string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
        {
            return (400, "Nothing to set. Send song=..., or artist=... and title=...");
        }

        UpdatesReceived++;
        TrackReceived?.Invoke(this, new RemoteTrackEventArgs(artist, title, album, song));

        return (200, "OK");
    }

    private string HelpText() =>
        $"""
         SIRS now-playing endpoint.

         Send a whole line:
           GET /metadata?song=Artist - Title

         Or the pieces, and SIRS will format them with your title template:
           GET /metadata?artist=Artist&title=Title&album=Album

         The Icecast admin form works too, so an existing automation setup can point here unchanged:
           GET /admin/metadata?mode=updinfo&song=Artist - Title

         {(Token is null ? "No password is set." : "Add &token=... , or send it as a Bearer token.")}
         Listening on {(AllowOtherComputers ? "all network interfaces" : "this computer only")}, port {Port}.
         """;

    public void Dispose() => _server.Dispose();
}
