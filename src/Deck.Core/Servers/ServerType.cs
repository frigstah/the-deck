namespace Deck.Core.Servers;

public enum ServerType
{
    /// <summary>Not chosen yet - Deck will work it out by probing the server (C3).</summary>
    Unknown,

    Icecast,

    /// <summary>
    /// SHOUTcast, version not settled yet - and it does not have to be to connect (C3).
    /// <para>
    /// The two versions share one source handshake, so the only things the version decides are the
    /// <c>:#sid</c> suffix on the password for a v2 stream other than the first, and the <c>sid</c>
    /// parameter on metadata updates. Neither is needed to get on air, and the server's own reply to
    /// the handshake - "OK" from v1, "OK2" from v2 - settles it without anyone being asked.
    /// </para>
    /// <para>
    /// Which makes this the honest answer to a fact Deck often has: a BUTT config records "SHOUTcast"
    /// without saying which, and a probe can find the word in a banner with no version beside it.
    /// Before this existed both became <see cref="Unknown"/>, so a known family was thrown away and
    /// the user was asked a question the file had already answered.
    /// </para>
    /// </summary>
    Shoutcast,

    ShoutcastV1,
    ShoutcastV2,
}

public static class ServerTypeInfo
{
    public static string DisplayName(this ServerType type) => type switch
    {
        ServerType.Icecast => "Icecast",
        ServerType.Shoutcast => "SHOUTcast",
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
        ServerType.Shoutcast => "Deck will connect as SHOUTcast, and work out which version while it signs in.",
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
        ServerType.ShoutcastV1 or ServerType.Shoutcast => "Not needed for this server",
        _ => "Stream address",
    };

    public static string StreamPathHint(this ServerType type) => type switch
    {
        ServerType.ShoutcastV2 => "Usually 1, unless your host told you otherwise.",
        ServerType.ShoutcastV1 => "This kind of server only carries one stream, so there is nothing to fill in.",

        // The one case where the version does matter, said where it can be acted on: a v2 server
        // carrying more than one stream needs to be told which. Almost nobody is on stream 2, so
        // this is a note rather than a field - the field appears if they pick v2 above.
        ServerType.Shoutcast => "Nothing to fill in. If your host gave you a stream number other than 1, choose SHOUTcast (v2) above.",

        // Undecided included, and deliberately: "detect automatically" is now the normal state of a
        // half-filled server, and it used to be the one state where this field was shown with nothing
        // to explain it. The field is only shown at all for Icecast and undecided, and the mount point
        // is what an undecided server almost always turns out to want.
        _ => "The part after the port, for example /live or /stream. Your host will have given you this.",
    };

    public static bool UsesMountPoint(this ServerType type) => type is ServerType.Icecast;

    public static bool UsesStreamId(this ServerType type) => type is ServerType.ShoutcastV2;

    /// <summary>
    /// Whether the server will refuse a broadcast that does not name its station.
    /// <para>
    /// SHOUTcast will, and it does it silently: the password is accepted, "OK2" comes back, and the
    /// connection closes a moment later with no explanation at all. Icecast does not care. So the name
    /// is required for one family and optional for the other, which is not a rule anyone would guess.
    /// </para>
    /// </summary>
    public static bool NeedsStationName(this ServerType type) => type.IsShoutcast();

    /// <summary>Icecast authenticates a username too; SHOUTcast only ever wants a password.</summary>
    public static bool UsesUsername(this ServerType type) => type is ServerType.Icecast;

    /// <summary>
    /// Whether this is a SHOUTcast of any version. The family is what decides how Deck talks to the
    /// server - one handshake, one sink, one set of rules about station names - so anything that
    /// cares about the protocol should ask this rather than list the versions and forget one.
    /// </summary>
    public static bool IsShoutcast(this ServerType type) =>
        type is ServerType.Shoutcast or ServerType.ShoutcastV1 or ServerType.ShoutcastV2;
}
