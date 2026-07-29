using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sirs.Core.Servers;

/// <summary>
/// Loads and saves the server list (C1). Writes go through a temporary file and a replace, so a
/// crash mid-save cannot leave a station with an unreadable config.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ProfileStore(string? path = null) => _path = path ?? AppPaths.ServersFile;

    public List<ServerProfile> Load()
    {
        if (!File.Exists(_path)) return [];

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<ServerProfile>>(json, Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable list must not stop the app from starting. Keep the bad file
            // so it can be recovered by hand rather than silently destroying the user's servers.
            TryQuarantine();
            return [];
        }
    }

    public void Save(IEnumerable<ServerProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles.ToList(), Options);
        var temporary = _path + ".tmp";

        File.WriteAllText(temporary, json);

        if (File.Exists(_path))
        {
            File.Replace(temporary, _path, null);
        }
        else
        {
            File.Move(temporary, _path);
        }
    }

    /// <summary>Export for sharing a station's settings between DJs (C10 groundwork).</summary>
    public static string Export(IEnumerable<ServerProfile> profiles)
    {
        // Passwords are DPAPI-bound to this user, so they cannot travel. Strip them rather than
        // exporting a value that would silently fail to decrypt on the other machine.
        var portable = profiles.Select(p =>
        {
            var copy = p.Clone();
            copy.Id = p.Id;
            copy.ProtectedPassword = null;
            return copy;
        });

        return JsonSerializer.Serialize(portable.ToList(), Options);
    }

    public static List<ServerProfile> Import(string json) =>
        JsonSerializer.Deserialize<List<ServerProfile>>(json, Options) ?? [];

    /// <summary>
    /// Adds imported servers to an existing list, returning how many were added.
    /// <para>
    /// Ids are regenerated on collision and names are suffixed, so importing a file exported from a
    /// copy of your own list adds servers rather than silently replacing the ones you are using.
    /// </para>
    /// </summary>
    public static int MergeInto(IList<ServerProfile> existing, IEnumerable<ServerProfile> imported)
    {
        var added = 0;

        foreach (var profile in imported)
        {
            if (existing.Any(s => s.Id == profile.Id)) profile.Id = Guid.NewGuid();

            if (existing.Any(s => string.Equals(s.Name, profile.Name, StringComparison.CurrentCultureIgnoreCase)))
            {
                profile.Name = $"{profile.Name} (imported)";
            }

            existing.Add(profile);
            added++;
        }

        return added;
    }

    private void TryQuarantine()
    {
        try
        {
            var backup = _path + ".broken";
            File.Copy(_path, backup, overwrite: true);
        }
        catch (Exception)
        {
            // Nothing more we can do; the caller still gets an empty list and a working app.
        }
    }
}
