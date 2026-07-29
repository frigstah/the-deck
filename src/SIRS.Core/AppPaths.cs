namespace Sirs.Core;

/// <summary>
/// Where SIRS keeps its files. Portable mode (I7) drops a marker file next to the executable and
/// everything moves alongside it, which is what BUTT users expect from a Windows encoder.
/// </summary>
public static class AppPaths
{
    private const string PortableMarker = "sirs-portable.txt";

    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string ServersFile => Path.Combine(DataDirectory, "servers.json");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => EnsureDirectory(Path.Combine(DataDirectory, "logs"));

    /// <summary>Community translations, one JSON file per language (I8).</summary>
    public static string LanguageDirectory => EnsureDirectory(Path.Combine(DataDirectory, "languages"));

    /// <summary>
    /// Where a running SIRS leaves the port and password its control endpoint is on, so that
    /// <c>SIRS.exe --live</c> can find the copy already running (I10). Written only while the
    /// endpoint is up, and deleted when it stops.
    /// </summary>
    public static string ControlFile => Path.Combine(DataDirectory, "control.json");

    /// <summary>Default folder for recordings: the user's Music folder, where they will find it.</summary>
    public static string DefaultRecordingDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "SIRS Recordings");

    private static string ResolveDataDirectory()
    {
        var directory = IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SIRS");

        return EnsureDirectory(directory);
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
