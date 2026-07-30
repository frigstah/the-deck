namespace Deck.Core.Servers;

/// <summary>What came out of a BUTT configuration file, and what did not.</summary>
public sealed class ButtImportResult
{
    public List<ServerProfile> Servers { get; } = [];

    /// <summary>Names listed as servers whose section was missing or had no address.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>
    /// Entries that describe the same host, port and mount as an earlier one under a different name.
    /// Reported rather than removed - a duplicate in the source is the user's own list, and quietly
    /// dropping half of it would be the import deciding something it was not asked to decide.
    /// </summary>
    public List<string> Duplicates { get; } = [];
}

/// <summary>
/// Reads the server list out of a BUTT (broadcast using this tool) configuration file.
/// <para>
/// BUTT is the free encoder most people arrive from, and the thing that keeps them there is not the
/// software - it is that their servers are already in it. Somebody with fifty stations saved is not
/// going to retype fifty addresses, ports and passwords to try something else, and no amount of
/// design work on the rest of Deck answers that. So Deck reads their file.
/// </para>
/// <para>
/// Only the servers. A BUTT config also carries audio devices, DSP settings, window positions and
/// MIDI bindings, and none of that should cross: the device indices mean nothing outside BUTT, and
/// silently importing somebody's compressor settings into a different compressor would be worse than
/// not importing them at all.
/// </para>
/// </summary>
public static class ButtImport
{
    /// <summary>
    /// Whether this text looks like a BUTT configuration, so one Import button can take either
    /// format and the user never has to know which kind of file they were handed.
    /// </summary>
    public static bool Looks(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Both markers, not either: [main] alone is far too common a heading in an INI file, and a
        // stray "srv_ent" in something else is not a config. Together they are BUTT and nothing else.
        var sections = Parse(text);
        return sections.TryGetValue("main", out var main) &&
               (main.ContainsKey("srv_ent") || main.ContainsKey("num_of_srv"));
    }

    public static ButtImportResult Read(string text)
    {
        var result = new ButtImportResult();
        var sections = Parse(text);

        var seen = new List<(string Host, int Port, string Path)>();

        foreach (var name in ServerNames(sections))
        {
            if (Section(sections, name) is not { } entry)
            {
                result.Skipped.Add(name);
                continue;
            }

            var host = Value(entry, "address");
            if (host is null)
            {
                // Listed as a server but never filled in. BUTT keeps these; there is nothing to
                // connect to, so carrying one across would only be a broken row to delete later.
                result.Skipped.Add(name);
                continue;
            }

            var profile = Build(name, host, entry);
            result.Servers.Add(profile);

            var key = (profile.Host.ToLowerInvariant(), profile.Port, profile.MountPoint.ToLowerInvariant());
            if (seen.Contains(key)) result.Duplicates.Add(name);
            else seen.Add(key);
        }

        return result;
    }

    private static ServerProfile Build(string name, string host, Dictionary<string, string> entry)
    {
        var profile = new ServerProfile
        {
            Name = name,
            Host = host,
            UseTls = Value(entry, "tls") == "1",
        };

        if (int.TryParse(Value(entry, "port"), out var port) && port is > 0 and <= 65535)
        {
            profile.Port = port;
        }

        // BUTT stores its passwords in the clear. Deck does not: this one is handed straight to the
        // property that protects it, so the copy Deck keeps is DPAPI-bound to this user even though
        // the file it came out of is readable by anything on the machine.
        if (Value(entry, "password") is { } password) profile.Password = password;

        // type 1 is Icecast, type 0 is SHOUTcast without saying which SHOUTcast. Both are recorded
        // for exactly what they say (C3): the family is a fact the file gives, and throwing it away
        // to arrive at Unknown would mean asking the user a question their own config had answered.
        // The version is not needed to connect and the server states it in the handshake reply.
        //
        // Anything else - a type BUTT does not write, a section somebody hand-edited - stays
        // undecided rather than being read as SHOUTcast by default.
        profile.ServerType = Value(entry, "type") switch
        {
            "1" => ServerType.Icecast,
            "0" => ServerType.Shoutcast,
            _ => ServerType.Unknown,
        };

        if (Value(entry, "mount") is { } mount)
        {
            profile.MountPoint = mount.StartsWith('/') ? mount : "/" + mount;
        }

        if (Value(entry, "usr") is { } user) profile.Username = user;

        return profile;
    }

    /// <summary>
    /// The names BUTT considers servers, in the order it lists them.
    /// <para>
    /// <c>srv_ent</c> is the authority, because a section is not a server just because it has a
    /// heading - the stream-info presets, the codec blocks and the MIDI bindings are all sections
    /// too. Falling back to "anything with an address" covers a file that has been hand-edited or
    /// truncated, where the list and the sections no longer agree.
    /// </para>
    /// </summary>
    private static IEnumerable<string> ServerNames(Dictionary<string, Dictionary<string, string>> sections)
    {
        var listed = new List<string>();

        if (sections.TryGetValue("main", out var main) && Value(main, "srv_ent") is { } list)
        {
            listed.AddRange(list
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0));
        }

        foreach (var name in listed) yield return name;

        // Anything with an address that the list forgot. Matched the same way the lookup above
        // resolves a listed name - exactly first, then ignoring case - so a section is only treated
        // as forgotten when nothing in the list would have reached it.
        foreach (var (name, entry) in sections)
        {
            if (listed.Contains(name, StringComparer.Ordinal)) continue;
            if (listed.Any(l => !sections.ContainsKey(l) && string.Equals(l, name, StringComparison.OrdinalIgnoreCase))) continue;
            if (!entry.ContainsKey("address")) continue;
            if (string.Equals(name, "main", StringComparison.OrdinalIgnoreCase)) continue;

            yield return name;
        }
    }

    /// <summary>
    /// The section for a listed name: the exact one if it exists, otherwise one that differs only by
    /// case.
    /// <para>
    /// Exactly first, and that ordering is the whole point. BUTT lets two servers have names that
    /// differ only in capitals - "Belly button bay" and "Belly Button Bay" as two different stations
    /// on two different hosts - so a case-insensitive lookup finds the wrong one, and a
    /// case-insensitive section table is worse still: the second section lands on top of the first
    /// and one of the two servers is quietly replaced by a copy of the other. Found in a real
    /// fifty-four server file, where it turned an Icecast station into a duplicate of a SHOUTcast one.
    /// </para>
    /// <para>
    /// The tolerant second pass stays for hand-edited files, where the list and the headings have
    /// drifted apart in case and there is only one candidate anyway.
    /// </para>
    /// </summary>
    private static Dictionary<string, string>? Section(
        Dictionary<string, Dictionary<string, string>> sections, string name)
    {
        if (sections.TryGetValue(name, out var exact)) return exact;

        foreach (var (key, entry) in sections)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return entry;
        }

        return null;
    }

    /// <summary>
    /// A value, or null when BUTT wrote a placeholder. It stores <c>(none)</c> for the fields a
    /// SHOUTcast server does not have, so reading the file literally would give every SHOUTcast
    /// entry a mount point called "(none)" and a username to match.
    /// </summary>
    private static string? Value(Dictionary<string, string> entry, string key)
    {
        if (!entry.TryGetValue(key, out var value)) return null;

        value = value.Trim();

        return value.Length == 0 || value == "(none)" || value == "-" ? null : value;
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        // Section names compare exactly: two BUTT servers are allowed to differ only in capitals, and
        // folding them together loses one of them. Keys inside a section are the opposite - BUTT
        // writes those itself and their case carries no meaning.
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        sections[string.Empty] = current;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current!))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[name] = current;
                }

                continue;
            }

            // Split on the first separator only. Passwords are values here, and a password is
            // allowed to contain an equals sign - splitting on all of them would quietly truncate
            // one and leave the user with a server that refuses a password they can see is right.
            var split = line.IndexOf('=');
            if (split <= 0) continue;

            current[line[..split].Trim()] = line[(split + 1)..].Trim();
        }

        return sections;
    }
}
