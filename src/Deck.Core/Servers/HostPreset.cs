namespace Deck.Core.Servers;

/// <summary>
/// The three kinds of answer to "who hosts your stream?", so the one list can hold all of them
/// without reading as a jumble: the company you pay, the software it runs, or "I don't know".
/// </summary>
public static class HostGroups
{
    public const string Unsure = "If you're not sure";
    public const string Companies = "Hosting companies";
    public const string Families = "Kinds of server";
}

/// <summary>
/// A starting point for a known hosting company (C11).
/// <para>
/// Presets deliberately carry only what is genuinely standard - the server family, the usual source
/// username, whether the connection is secure - plus guidance on what that host calls each field
/// and where to find it. They do not invent addresses, ports or mount points: those differ per
/// account, and a confidently wrong prefill is worse than an empty box. Pressing Test confirms
/// whatever the user fills in.
/// </para>
/// <para>
/// This is also the only server-family question the user is asked. The raw <see cref="ServerType"/>
/// picker sits behind "Change" in the editor, because for every host in this list the answer to
/// "who hosts your stream?" already decides it (C3).
/// </para>
/// </summary>
public sealed record HostPreset(
    string Name,
    ServerType ServerType,
    string WhereToFind,
    string Group = HostGroups.Companies,
    int? DefaultPort = null,
    bool? Secure = null,
    string Username = "source",
    string? FieldNaming = null)
{
    /// <summary>Applies the preset without overwriting anything the user already filled in.</summary>
    public void ApplyTo(ServerProfile profile)
    {
        profile.ServerType = ServerType;

        // Only when the preset actually has a view. A hosting company can be reached either way
        // depending on the plan, and silently unticking a box the user ticked themselves is worse
        // than leaving it alone - they would find out on air.
        if (Secure is { } secure) profile.UseTls = secure;

        if (DefaultPort is { } port && profile.Port is 0 or 8000) profile.Port = port;
        if (!string.IsNullOrWhiteSpace(Username) && profile.Username is "source" or "") profile.Username = Username;
    }

    /// <summary>
    /// True when this preset knows which family the server belongs to and the profile says otherwise.
    /// That only happens if someone set the type by hand, or a Test disagreed with the preset - and
    /// either way the picker has to come back on screen, because a setting you cannot see is one you
    /// cannot undo. A preset with no view (<see cref="ServerType.Unknown"/>) never contradicts
    /// anything: a detected type is the preset working, not being overruled.
    /// </summary>
    public bool Contradicts(ServerType type) => ServerType != ServerType.Unknown && type != ServerType;

    /// <summary>
    /// What a screen reader says when it reaches this choice in the list. A record's generated
    /// ToString is every property it has, and that is what a WPF list item announces - the item
    /// template only decides what is drawn (I6). Without this, choosing a host reads out the entire
    /// guidance text and the field-naming note as one sentence.
    /// </summary>
    public override string ToString() => Name;

    public static HostPreset Generic { get; } = new(
        "I'm not sure — let Deck work it out",
        ServerType.Unknown,
        "Fill in the address, port and password your host gave you. Deck will identify the server itself.",
        Group: HostGroups.Unsure);

    // Order matters: this is the list the user reads top to bottom. Not knowing comes first because
    // it is always a safe answer, then the companies people actually have accounts with, and the
    // technical answers last for anyone running their own server.
    public static IReadOnlyList<HostPreset> All { get; } =
    [
        Generic,

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

        new("Icecast (any host)",
            ServerType.Icecast,
            "Your host will have given you a server address, a port, a mount point and a password.",
            Group: HostGroups.Families,
            DefaultPort: 8000,
            Secure: false,
            FieldNaming: "Icecast calls the stream address a \"mount point\". It starts with a slash, like /live or /stream."),

        new("Icecast over a secure connection",
            ServerType.Icecast,
            "Use this when your host told you to connect securely, or gave you an address starting with https.",
            Group: HostGroups.Families,
            DefaultPort: 443,
            Secure: true,
            FieldNaming: "The port for secure connections is usually 443 or 8443. Your host will say which."),

        new("SHOUTcast v2",
            ServerType.ShoutcastV2,
            "Your host will have given you a server address, a port, a stream number and a password.",
            Group: HostGroups.Families,
            DefaultPort: 8000,
            Secure: false,
            FieldNaming: "SHOUTcast calls the stream a \"stream ID\" or \"SID\". It is usually 1. If your host quoted the listener port, Deck will move to the broadcast port for you."),

        new("SHOUTcast v1 (older servers)",
            ServerType.ShoutcastV1,
            "Only for older SHOUTcast servers. If you are not sure, try SHOUTcast v2 first.",
            Group: HostGroups.Families,
            DefaultPort: 8000,
            Secure: false,
            FieldNaming: "This kind of server carries one stream, so there is no mount point or stream number to fill in."),
    ];
}
