using Deck.Core.Servers;

namespace Deck.EncoderCheck;

/// <summary>
/// The server editor asks who hosts the stream and hides the raw server-type picker behind
/// "Change" (C3, C11). That only works if two things hold: naming a host really does settle the
/// server type, and the line that replaces the picker really does say what was settled. Both are
/// silent when broken - the user sees a tidy form that has quietly decided nothing - so they are
/// checked here rather than trusted.
/// </summary>
internal static class HostQuestionChecks
{
    public static int Run()
    {
        var failures = 0;

        // ------------------------------------------------------- the question can be answered

        failures += Check("every host in the list says who it is and where to look", () =>
        {
            foreach (var preset in HostPreset.All)
            {
                Expect(!string.IsNullOrWhiteSpace(preset.Name), "a preset has no name");
                Expect(!string.IsNullOrWhiteSpace(preset.WhereToFind),
                    $"\"{preset.Name}\" does not say where to find the details");
                Expect(preset.Group is HostGroups.Unsure or HostGroups.Companies or HostGroups.Families,
                    $"\"{preset.Name}\" is in an unknown group \"{preset.Group}\"");
            }
        });

        failures += Check("naming a host settles the server type", () =>
        {
            // The load-bearing claim of hiding the picker. A preset that left the type Unknown while
            // claiming to know the host would hide a question nobody had answered.
            foreach (var preset in HostPreset.All.Where(p => p.Group != HostGroups.Unsure))
            {
                Expect(preset.ServerType != ServerType.Unknown,
                    $"\"{preset.Name}\" claims to know the host but not what kind of server it runs");
            }

            Expect(HostPreset.Generic.ServerType == ServerType.Unknown,
                "\"not sure\" has to mean detect, or Deck would guess on the user's behalf");
            Expect(HostPreset.All.Count(p => p.Group == HostGroups.Unsure) == 1,
                "there should be exactly one way to say you do not know");
        });

        failures += Check("the list reads in the order a person needs it", () =>
        {
            var groups = HostPreset.All.Select(p => p.Group).ToList();

            Expect(groups[0] == HostGroups.Unsure, "the always-safe answer should come first");

            // The technical answers exist for people running their own server; they must not be the
            // first thing someone who pays a hosting company has to read past.
            var firstFamily = groups.IndexOf(HostGroups.Families);
            var lastCompany = groups.LastIndexOf(HostGroups.Companies);
            Expect(firstFamily > lastCompany, "the kinds of server come before the hosting companies");
        });

        // ------------------------------------------------------- applying it does no harm

        failures += Check("a host with no view on security leaves the secure box alone", () =>
        {
            // This was a real trap: every preset used to force UseTls, so choosing your hosting
            // company silently unticked "use a secure connection" and you found out on air.
            var company = HostPreset.All.First(p => p.Group == HostGroups.Companies);
            Expect(company.Secure is null, $"\"{company.Name}\" should have no view on security");

            var profile = new ServerProfile { UseTls = true, Host = "stream.example.com" };
            company.ApplyTo(profile);

            Expect(profile.UseTls, $"\"{company.Name}\" unticked a secure box the user had ticked");
        });

        failures += Check("the secure and plain choices can each undo the other", () =>
        {
            var secure = Find("Icecast over a secure connection");
            var plain = Find("Icecast (any host)");

            var profile = new ServerProfile();
            secure.ApplyTo(profile);
            Expect(profile.UseTls, "choosing the secure option did not turn security on");
            Expect(profile.Port == 443, $"the secure option left the port at {profile.Port}");

            plain.ApplyTo(profile);
            Expect(!profile.UseTls, "there is no way back from the secure option");
        });

        failures += Check("a port the user typed is never overwritten", () =>
        {
            var profile = new ServerProfile { Port = 8443 };
            Find("Icecast (any host)").ApplyTo(profile);

            Expect(profile.Port == 8443, $"a typed port 8443 was replaced with {profile.Port}");
        });

        failures += Check("a username the user typed is never overwritten", () =>
        {
            var profile = new ServerProfile { Username = "benc" };
            Find("Radio Mast").ApplyTo(profile);

            Expect(profile.Username == "benc", $"a typed username was replaced with \"{profile.Username}\"");
        });

        // ------------------------------------------------------- when the picker has to come back

        failures += Check("detection is not treated as an override", () =>
        {
            // "I'm not sure" plus a detected Icecast is the intended path working, not the host
            // question being overruled - so it must not drag the picker back on screen.
            foreach (var detected in new[] { ServerType.Icecast, ServerType.ShoutcastV1, ServerType.ShoutcastV2 })
            {
                Expect(!HostPreset.Generic.Contradicts(detected),
                    $"detecting {detected} counted as contradicting \"not sure\"");
            }
        });

        failures += Check("a type that disagrees with the host brings the picker back", () =>
        {
            var icecast = Find("Icecast (any host)");

            Expect(!icecast.Contradicts(ServerType.Icecast), "an Icecast host agreeing with Icecast counted as a clash");
            Expect(icecast.Contradicts(ServerType.ShoutcastV2), "an Icecast host set to SHOUTcast v2 went unnoticed");
            Expect(icecast.Contradicts(ServerType.Unknown),
                "an Icecast host reset to detect went unnoticed, so the user could not see or undo it");
        });

        // ------------------------------------------------------- the line that replaced the picker

        failures += Check("every server type has a sentence of its own", () =>
        {
            var seen = new List<string>();

            foreach (var type in Enum.GetValues<ServerType>())
            {
                var summary = type.ConnectionSummary();

                Expect(!string.IsNullOrWhiteSpace(summary), $"{type} has nothing to say for itself");
                Expect(summary.EndsWith('.'), $"{type} says \"{summary}\", which is not a sentence");
                Expect(!seen.Contains(summary), $"{type} says the same as another type: \"{summary}\"");
                seen.Add(summary);
            }
        });

        failures += Check("the sentence names the family it settled on", () =>
        {
            Expect(ServerType.Icecast.ConnectionSummary().Contains("Icecast"),
                "the Icecast line does not mention Icecast");
            Expect(ServerType.ShoutcastV2.ConnectionSummary().Contains("v2"),
                "the SHOUTcast v2 line does not say which version");
            Expect(ServerType.ShoutcastV1.ConnectionSummary().Contains("v1"),
                "the SHOUTcast v1 line does not say which version");
        });

        failures += Check("not knowing yet says so, and says when it will know", () =>
        {
            var summary = ServerType.Unknown.ConnectionSummary();

            // The picker is hidden, so this line is the only thing between the user and believing a
            // decision has been made that has not.
            Expect(!summary.Contains("Icecast") && !summary.Contains("SHOUTcast"),
                $"the undecided line names a server family: \"{summary}\"");
            Expect(summary.Contains("Test") || summary.Contains("live"),
                $"the undecided line does not say when it will be settled: \"{summary}\"");
        });

        failures += Check("every field the undecided state shows is explained", () =>
        {
            // "Detect automatically" is the normal state of a half-filled server now that the host
            // question hides the picker, so it must not be the one state with unlabelled fields.
            foreach (var type in new[] { ServerType.Unknown, ServerType.Icecast })
            {
                Expect(!string.IsNullOrWhiteSpace(type.StreamPathLabel()), $"{type} shows a field with no label");
                Expect(!string.IsNullOrWhiteSpace(type.StreamPathHint()), $"{type} shows a field with no explanation");
            }

            Expect(ServerType.Unknown.StreamPathHint() == ServerType.Icecast.StreamPathHint(),
                "an undecided server shows the mount point field, so it should explain it the same way");
        });

        failures += Check("a choice reads out as its name, not as a record", () =>
        {
            // A list item announces the item's ToString, whatever the item template draws (I6).
            foreach (var preset in HostPreset.All)
            {
                Expect(preset.ToString() == preset.Name,
                    $"a screen reader would announce \"{preset}\" instead of \"{preset.Name}\"");
            }
        });

        return failures;
    }

    private static HostPreset Find(string name) =>
        HostPreset.All.FirstOrDefault(p => p.Name == name)
        ?? throw new Exception($"there is no longer a host preset called \"{name}\"");

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
