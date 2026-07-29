namespace Sirs.Core.Servers;

public enum ServerType
{
    /// <summary>Not chosen yet - SIRS will work it out by probing the server (C3).</summary>
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
        ServerType.Icecast => "The part after the port, for example /live or /stream. Your host will have given you this.",
        ServerType.ShoutcastV2 => "Usually 1, unless your host told you otherwise.",
        ServerType.ShoutcastV1 => "This kind of server only carries one stream, so there is nothing to fill in.",
        _ => string.Empty,
    };

    public static bool UsesMountPoint(this ServerType type) => type is ServerType.Icecast;

    public static bool UsesStreamId(this ServerType type) => type is ServerType.ShoutcastV2;

    /// <summary>Icecast authenticates a username too; SHOUTcast only ever wants a password.</summary>
    public static bool UsesUsername(this ServerType type) => type is ServerType.Icecast;
}
