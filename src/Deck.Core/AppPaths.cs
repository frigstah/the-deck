namespace Deck.Core;

/// <summary>
/// Where Deck keeps its files. Portable mode (I7) drops a marker file next to the executable and
/// everything moves alongside it, which is what BUTT users expect from a Windows encoder.
/// </summary>
public static class AppPaths
{
    private const string PortableMarker = "deck-portable.txt";

    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string ServersFile => Path.Combine(DataDirectory, "servers.json");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => EnsureDirectory(Path.Combine(DataDirectory, "logs"));

    /// <summary>Community translations, one JSON file per language (I8).</summary>
    public static string LanguageDirectory => EnsureDirectory(Path.Combine(DataDirectory, "languages"));

    /// <summary>
    /// Where a running Deck leaves the port and password its control endpoint is on, so that
    /// <c>Deck.exe --live</c> can find the copy already running (I10). Written only while the
    /// endpoint is up, and deleted when it stops.
    /// </summary>
    public static string ControlFile => Path.Combine(DataDirectory, "control.json");

    /// <summary>Default folder for recordings: the user's Music folder, where they will find it.</summary>
    public static string DefaultRecordingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Deck Recordings");

    /// <summary>
    /// "Deck" rather than "The Deck": no space, so a path in a script or a support answer needs no
    /// quoting, and it matches <c>Deck.exe</c> and <c>deck-portable.txt</c>. The article belongs in
    /// the window title, not in a folder name.
    /// <para>
    /// Its own folder rather than SIRS's, which matters more than it looks. The Deck is a fork, so
    /// both are installable side by side — and sharing one settings file would have them overwrite
    /// each other's servers every time the other one closed.
    /// </para>
    /// </summary>
    private static string ResolveDataDirectory()
    {
        var directory = IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Deck");

        return EnsureDirectory(directory);
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
