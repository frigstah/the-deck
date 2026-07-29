using Sirs.Core.Codecs;
using Sirs.Core.Servers;

namespace Sirs.EncoderCheck;

/// <summary>
/// Checks for sharing server settings between DJs (C10). The parts worth pinning down are that
/// passwords never leave the machine, that the technical fields survive the trip intact, and that
/// importing a file exported from your own list does not quietly replace what you are using.
/// </summary>
internal static class ProfileSharingChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Case("technical fields survive a round trip", () =>
        {
            var original = Sample();
            var imported = ProfileStore.Import(ProfileStore.Export([original]));

            if (imported.Count != 1) return $"got {imported.Count} servers back, expected 1";

            var copy = imported[0];
            if (copy.Name != original.Name) return $"name was \"{copy.Name}\"";
            if (copy.Host != original.Host) return $"host was \"{copy.Host}\"";
            if (copy.Port != original.Port) return $"port was {copy.Port}";
            if (copy.ServerType != original.ServerType) return $"server type was {copy.ServerType}";
            if (copy.NormalisedMount != original.NormalisedMount) return $"mount was \"{copy.NormalisedMount}\"";
            if (copy.Username != original.Username) return $"username was \"{copy.Username}\"";
            if (copy.UseTls != original.UseTls) return $"TLS was {copy.UseTls}";
            if (copy.StationName != original.StationName) return $"station name was \"{copy.StationName}\"";
            if (copy.Encoder != original.Encoder) return $"encoder settings were {copy.Encoder.Summary}";

            return null;
        });

        failures += Case("passwords are never exported", () =>
        {
            var original = Sample();
            original.Password = "hunter2";

            if (string.IsNullOrEmpty(original.ProtectedPassword))
            {
                return "the test could not store a password to begin with";
            }

            var json = ProfileStore.Export([original]);
            if (json.Contains("hunter2", StringComparison.Ordinal)) return "the plain password appeared in the export";

            var copy = ProfileStore.Import(json)[0];
            if (!string.IsNullOrEmpty(copy.ProtectedPassword)) return "an encrypted password came through the export";
            if (!string.IsNullOrEmpty(copy.Password)) return "the imported profile still has a password";

            // The original must be untouched: exporting is not a destructive act.
            return original.Password == "hunter2" ? null : "exporting cleared the original's password";
        });

        failures += Case("importing your own file adds rather than replaces", () =>
        {
            var mine = new List<ServerProfile> { Sample() };
            var json = ProfileStore.Export(mine);

            var added = ProfileStore.MergeInto(mine, ProfileStore.Import(json));

            if (added != 1) return $"reported {added} added, expected 1";
            if (mine.Count != 2) return $"list holds {mine.Count} servers, expected 2";
            if (mine[0].Id == mine[1].Id) return "the imported server kept a colliding id";
            if (mine[0].Name == mine[1].Name) return "the imported server kept an identical name";
            if (!mine[1].Name.Contains("imported")) return $"the copy was named \"{mine[1].Name}\"";

            return null;
        });

        failures += Case("distinct servers import unchanged", () =>
        {
            var mine = new List<ServerProfile> { Sample() };

            var theirs = Sample();
            theirs.Id = Guid.NewGuid();
            theirs.Name = "Their Station";

            var added = ProfileStore.MergeInto(mine, [theirs]);

            if (added != 1) return $"reported {added} added, expected 1";
            if (mine[1].Name != "Their Station") return $"name became \"{mine[1].Name}\"";

            return null;
        });

        failures += Case("an empty export imports cleanly", () =>
        {
            var imported = ProfileStore.Import(ProfileStore.Export([]));
            return imported.Count == 0 ? null : $"got {imported.Count} servers from an empty export";
        });

        return failures;
    }

    private static ServerProfile Sample() => new()
    {
        Name = "My Station",
        ServerType = ServerType.Icecast,
        Host = "stream.example.com",
        Port = 8010,
        UseTls = true,
        MountPoint = "/live",
        Username = "source",
        StationName = "Example FM",
        Genre = "Jazz",
        Encoder = new EncoderSettings { Codec = StreamCodec.OggOpus, BitrateKbps = 96, SampleRate = 48000, Channels = 2 },
    };

    private static int Case(string name, Func<string?> verify)
    {
        string? problem;
        try
        {
            problem = verify();
        }
        catch (Exception ex)
        {
            problem = $"threw {ex.GetType().Name}: {ex.Message}";
        }

        if (problem is null)
        {
            Console.WriteLine($"  ok    {name}");
            return 0;
        }

        Console.WriteLine($"  FAIL  {name}: {problem}");
        return 1;
    }
}
