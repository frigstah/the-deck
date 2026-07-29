namespace Deck.Core.Servers;

/// <summary>
/// A starting point for a known hosting company (C11).
/// <para>
/// Presets deliberately carry only what is genuinely standard - the server family, the usual source
/// username, whether the connection is secure - plus guidance on what that host calls each field
/// and where to find it. They do not invent addresses, ports or mount points: those differ per
/// account, and a confidently wrong prefill is worse than an empty box. Pressing Test confirms
/// whatever the user fills in.
/// </para>
/// </summary>
public sealed record HostPreset(
    string Name,
    ServerType ServerType,
    string WhereToFind,
    int? DefaultPort = null,
    bool UseTls = false,
    string Username = "source",
    string? FieldNaming = null)
{
    /// <summary>Applies the preset without overwriting anything the user already filled in.</summary>
    public void ApplyTo(ServerProfile profile)
    {
        profile.ServerType = ServerType;
        profile.UseTls = UseTls;

        if (DefaultPort is { } port && profile.Port is 0 or 8000) profile.Port = port;
        if (!string.IsNullOrWhiteSpace(Username) && profile.Username is "source" or "") profile.Username = Username;
    }

    public static HostPreset Generic { get; } = new(
        "I'm not sure — let Deck work it out",
        ServerType.Unknown,
        "Fill in the address, port and password your host gave you, then press Test. Deck will identify the server itself.");

    public static IReadOnlyList<HostPreset> All { get; } =
    [
        Generic,

        new("Icecast (any host)",
            ServerType.Icecast,
            "Your host will have given you a server address, a port, a mount point and a password.",
            DefaultPort: 8000,
            FieldNaming: "Icecast calls the stream address a \"mount point\". It starts with a slash, like /live or /stream."),

        new("Icecast over a secure connection",
            ServerType.Icecast,
            "Use this when your host told you to connect securely, or gave you an address starting with https.",
            DefaultPort: 443,
            UseTls: true,
            FieldNaming: "The port for secure connections is usually 443 or 8443. Your host will say which."),

        new("SHOUTcast v2",
            ServerType.ShoutcastV2,
            "Your host will have given you a server address, a port, a stream number and a password.",
            DefaultPort: 8000,
            FieldNaming: "SHOUTcast calls the stream a \"stream ID\" or \"SID\". It is usually 1. If your host quoted the listener port, Deck will move to the broadcast port for you."),

        new("SHOUTcast v1 (older servers)",
            ServerType.ShoutcastV1,
            "Only for older SHOUTcast servers. If you are not sure, try SHOUTcast v2 first.",
            DefaultPort: 8000,
            FieldNaming: "This kind of server carries one stream, so there is no mount point or stream number to fill in."),

        new("Radio Mast",
            ServerType.Icecast,
            "Sign in to Radio Mast and open your station's encoder or source settings.",
            FieldNaming: "Radio Mast runs Icecast, so you will need the mount point along with the address, port and password."),

        new("RadioKing",
            ServerType.Icecast,
            "In RadioKing, open your radio's settings and look for the live broadcasting or source connection details.",
            FieldNaming: "RadioKing runs Icecast. The mount point is often your stream name."),

        new("Airtime Pro / LibreTime",
            ServerType.Icecast,
            "In Airtime or LibreTime, open Settings then Streams and read the connection details for the input you want.",
            FieldNaming: "These run Icecast. The mount point is shown next to the stream you are connecting to."),

        new("Live365",
            ServerType.Icecast,
            "In the Live365 broadcaster dashboard, open your station's live streaming or encoder settings.",
            FieldNaming: "Live365 will give you a mount point along with the address, port and password."),

        new("Shoutcast.com",
            ServerType.ShoutcastV2,
            "In the Shoutcast for Business dashboard, open your stream's source or encoder details.",
            FieldNaming: "You will need the stream ID, which is usually 1."),
    ];
}
