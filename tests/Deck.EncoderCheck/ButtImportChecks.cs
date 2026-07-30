using Deck.Core.Servers;

namespace Deck.EncoderCheck;

/// <summary>
/// Reading a BUTT configuration (C13). Everything here is synthetic: a real BUTT file is a list of
/// live broadcast passwords in the clear, and the one place it must never end up is a public
/// repository. The shapes are real, the credentials are not.
/// </summary>
internal static class ButtImportChecks
{
    private const string Sample = """
        #This is a configuration file for butt (broadcast using this tool)

        [main]
        server = Second Station
        srv_ent = First Station;Second Station;Never Filled In;Missing Section
        num_of_srv = 4

        [audio]
        device = 13
        dev_name = IN 1-2 (SOME INTERFACE) [Windows WASAPI]

        [First Station]
        address = radio.example.org
        port = 8000
        password = pa=ssw0rd!
        type = 1
        tls = 1
        mount = live
        usr = broadcaster
        protocol = 0

        [Second Station]
        address = 198.51.100.7
        port = 7942
        password = another-one
        type = 0
        tls = 0
        custom_listener_url =
        mount = (none)
        usr = (none)

        [Never Filled In]
        address =
        port = 8000
        password =
        type = 0

        [Stream Info Preset]
        expand_variables = 0
        genre =

        [midi_cmd_0]
        enabled = 0
        """;

    public static int Run()
    {
        var failures = 0;

        failures += Check("a BUTT config is recognised without being asked", () =>
        {
            Expect(ButtImport.Looks(Sample), "a real BUTT config was not recognised");

            // One button takes both formats, so the sniff has to be certain in both directions.
            Expect(!ButtImport.Looks("""[{"Name":"Exported from Deck","Host":"radio.example.org"}]"""),
                "Deck's own export was mistaken for a BUTT config");
            Expect(!ButtImport.Looks("[main]\nwindow_height = 393"),
                "an unrelated INI file with a [main] section was mistaken for a BUTT config");
            Expect(!ButtImport.Looks(""), "empty text was mistaken for a BUTT config");
        });

        failures += Check("an Icecast entry keeps everything it needs to connect", () =>
        {
            var first = Read().Servers.Single(s => s.Name == "First Station");

            Expect(first.Host == "radio.example.org", $"host came through as {first.Host}");
            Expect(first.Port == 8000, $"port came through as {first.Port}");
            Expect(first.ServerType == ServerType.Icecast, $"type came through as {first.ServerType}");
            Expect(first.UseTls, "TLS was on in the file and off after the import");
            Expect(first.Username == "broadcaster", $"username came through as {first.Username}");

            // BUTT writes the mount without one; Deck stores it with one. A missing slash is a
            // connection that fails on a detail the user cannot see in either program.
            Expect(first.MountPoint == "/live", $"mount came through as {first.MountPoint}");
        });

        failures += Check("a password containing an equals sign survives", () =>
        {
            // The whole value after the first separator, not the part before the second one. A
            // truncated password looks right on screen and is refused by the server.
            var first = Read().Servers.Single(s => s.Name == "First Station");
            Expect(first.Password == "pa=ssw0rd!", $"password came through as {first.Password}");
        });

        failures += Check("a SHOUTcast entry is left undecided rather than guessed at", () =>
        {
            var second = Read().Servers.Single(s => s.Name == "Second Station");

            // BUTT records "SHOUTcast" without saying which, and v1 and v2 are different handshakes.
            Expect(second.ServerType == ServerType.Unknown,
                $"a SHOUTcast entry arrived as {second.ServerType} rather than waiting to be probed");

            Expect(second.Password == "another-one", "the password did not come through");
            Expect(!second.UseTls, "TLS was off in the file and on after the import");
        });

        failures += Check("the (none) placeholders do not become real values", () =>
        {
            // BUTT stores "(none)" for the fields SHOUTcast has no use for. Reading the file
            // literally gives every SHOUTcast server a mount point called "(none)".
            var second = Read().Servers.Single(s => s.Name == "Second Station");

            Expect(!second.MountPoint.Contains("none", StringComparison.OrdinalIgnoreCase),
                $"the mount placeholder was imported as {second.MountPoint}");
            Expect(!(second.Username ?? string.Empty).Contains("none", StringComparison.OrdinalIgnoreCase),
                $"the username placeholder was imported as {second.Username}");
        });

        failures += Check("an entry with no address is reported rather than imported", () =>
        {
            var result = Read();

            Expect(result.Servers.All(s => s.Name != "Never Filled In"),
                "a server with no address was imported as a broken row");
            Expect(result.Skipped.Contains("Never Filled In"), "the empty entry was dropped silently");
            Expect(result.Skipped.Contains("Missing Section"),
                "a name listed with no section behind it was dropped silently");
        });

        failures += Check("only the servers cross", () =>
        {
            // A BUTT config is mostly not servers: audio devices, codec blocks, MIDI bindings,
            // window positions and the stream-info presets are all sections too.
            var names = Read().Servers.Select(s => s.Name).ToList();

            foreach (var stray in new[] { "audio", "midi_cmd_0", "Stream Info Preset", "main" })
            {
                Expect(!names.Contains(stray, StringComparer.OrdinalIgnoreCase),
                    $"{stray} was imported as if it were a server");
            }

            Expect(names.Count == 2, $"{names.Count} servers came out of a file with two usable ones");
        });

        failures += Check("a server the list forgot is still found", () =>
        {
            // Hand-edited files exist, and a section with an address in it is a server whatever
            // srv_ent claims. Losing one silently is worse than importing one row too many.
            var patched = Sample.Replace(
                "srv_ent = First Station;Second Station;Never Filled In;Missing Section",
                "srv_ent = First Station");

            var names = ButtImport.Read(patched).Servers.Select(s => s.Name).ToList();

            Expect(names.Contains("Second Station"),
                "a section with an address was skipped because the list did not mention it");
            Expect(names.Count(n => n == "First Station") == 1,
                "a server named in the list and present as a section was imported twice");
        });

        failures += Check("two servers whose names differ only in capitals stay two servers", () =>
        {
            // From a real fifty-four server file: "Belly button bay" and "Belly Button Bay" were two
            // different stations on two different hosts. Matching section names without regard to
            // case landed the second on top of the first, and an Icecast server silently became a
            // copy of a SHOUTcast one - the kind of fault nobody finds until a broadcast goes to the
            // wrong place.
            var cased = """
                [main]
                srv_ent = Same Name;same name
                num_of_srv = 2

                [Same Name]
                address = first.example.org
                port = 8000
                password = one
                type = 1
                mount = live
                usr = source

                [same name]
                address = second.example.org
                port = 9000
                password = two
                type = 0
                mount = (none)
                usr = (none)
                """;

            var imported = ButtImport.Read(cased).Servers;

            Expect(imported.Count == 2, $"{imported.Count} servers came out of two distinct sections");

            var upper = imported.Single(s => s.Name == "Same Name");
            var lower = imported.Single(s => s.Name == "same name");

            Expect(upper.Host == "first.example.org", $"the first server points at {upper.Host}");
            Expect(lower.Host == "second.example.org", $"the second server points at {lower.Host}");
            Expect(upper.ServerType == ServerType.Icecast, "the Icecast server lost its type to its neighbour");
            Expect(upper.Password == "one" && lower.Password == "two", "the passwords were crossed over");
        });

        failures += Check("imported servers merge without replacing what is already there", () =>
        {
            // The end of the road for an import: it has to land in a list that already has things in
            // it. Same path Deck's own share file takes, so a name collision is handled once.
            var existing = new List<ServerProfile>
            {
                new() { Name = "First Station", Host = "already.example.net" },
            };

            var added = ProfileStore.MergeInto(existing, Read().Servers);

            Expect(added == 2, $"{added} servers were added, expected 2");
            Expect(existing.Count == 3, $"the list holds {existing.Count} servers, expected 3");
            Expect(existing[0].Host == "already.example.net", "an existing server was overwritten");
            Expect(existing.Select(s => s.Id).Distinct().Count() == 3, "two servers share an id");
        });

        return failures;
    }

    private static ButtImportResult Read() => ButtImport.Read(Sample);

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  ok   {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }
}
