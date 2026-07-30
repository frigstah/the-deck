namespace Deck.Core.Servers;

public enum ServerType
{
    /// <summary>Not chosen yet - Deck will work it out by probing the server (C3).</summary>
    Unknown,

    Icecast,
    ShoutcastV1,
    ShoutcastV2,
}

public static class ServerTypeInfo
{
    public static string DisplayName(this ServerType type) => type switch
    {
        ServerType.Icecast => "Icecast",
        ServerType.ShoutcastV1 => "SHOUTcast (older, v1)",
        ServerType.ShoutcastV2 => "SHOUTcast (v2)",
        ServerType.Unknown => "Detect automatically",
        _ => type.ToString(),
    };

    /// <summary>
    /// What Deck is going to do about the server family, in one sentence.
    /// <para>
    /// The editor asks who hosts the stream and keeps the raw type picker behind "Change", so this
    /// line is the only thing on screen that says what was decided - it has to stand on its own, and
    /// it must never leave the impression that nothing has been settled when something has (C3).
    /// </para>
    /// </summary>
    public static string ConnectionSummary(this ServerType type) => type switch
    {
        ServerType.Icecast => "Deck will connect as Icecast.",
        ServerType.ShoutcastV2 => "Deck will connect as SHOUTcast v2.",
        ServerType.ShoutcastV1 => "Deck will connect as SHOUTcast v1, the older kind.",
        _ => "Deck will work out what kind of server this is — when you press Test, or the first time you go live.",
    };

    /// <summary>
    /// Icecast calls it a mount point, SHOUTcast v2 calls it a stream ID, and v1 has neither.
    /// The label follows the server so the user sees the word their host used.
    /// </summary>
    public static string StreamPathLabel(this ServerType type) => type switch
    {
        ServerType.Icecast => "Stream address",
        ServerType.ShoutcastV2 => "Stream number",
        ServerType.ShoutcastV1 => "Not needed for this server",
        _ => "Stream address",
    };

    public static string StreamPathHint(this ServerType type) => type switch
    {
        ServerType.ShoutcastV2 => "Usually 1, unless your host told you otherwise.",
        ServerType.ShoutcastV1 => "This kind of server only carries one stream, so there is nothing to fill in.",

        // Undecided included, and deliberately: "detect automatically" is now the normal state of a
        // half-filled server, and it used to be the one state where this field was shown with nothing
        // to explain it. The field is only shown at all for Icecast and undecided, and the mount point
        // is what an undecided server almost always turns out to want.
        _ => "The part after the port, for example /live or /stream. Your host will have given you this.",
    };

    public static bool UsesMountPoint(this ServerType type) => type is ServerType.Icecast;

    public static bool UsesStreamId(this ServerType type) => type is ServerType.ShoutcastV2;

    /// <summary>Icecast authenticates a username too; SHOUTcast only ever wants a password.</summary>
    public static bool UsesUsername(this ServerType type) => type is ServerType.Icecast;
}
