using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.EncoderCheck;

/// <summary>
/// Parsing checks for the listener count (H4), using the response shapes each server family
/// actually returns. Icecast in particular emits a single object when one mount is live and an
/// array when several are — the classic way this feature breaks the moment a station adds a
/// second stream.
/// </summary>
internal static class ListenerCountChecks
{
    public static int Run()
    {
        var failures = 0;

        var icecast = new ServerProfile { ServerType = ServerType.Icecast, Host = "radio.example.com", Port = 8000, MountPoint = "/live" };

        failures += Case("Icecast, one mount live (single object)", () =>
        {
            const string json = """
            {"icestats":{"source":{"listenurl":"http://radio.example.com:8000/live","listeners":7}}}
            """;
            return Expect(ListenerCounter.ParseIcecast(json, icecast), 7);
        });

        failures += Case("Icecast, several mounts, ours matched", () =>
        {
            const string json = """
            {"icestats":{"source":[
              {"listenurl":"http://radio.example.com:8000/other","listeners":99},
              {"listenurl":"http://radio.example.com:8000/live","listeners":12}
            ]}}
            """;
            return Expect(ListenerCounter.ParseIcecast(json, icecast), 12);
        });

        failures += Case("Icecast, no mount matches, falls back to the total", () =>
        {
            const string json = """
            {"icestats":{"source":[
              {"listenurl":"http://radio.example.com:8000/a","listeners":3},
              {"listenurl":"http://radio.example.com:8000/b","listeners":4}
            ]}}
            """;
            return Expect(ListenerCounter.ParseIcecast(json, icecast), 7);
        });

        failures += Case("Icecast with nothing broadcasting", () =>
        {
            const string json = """{"icestats":{"admin":"you@example.com"}}""";
            return Expect(ListenerCounter.ParseIcecast(json, icecast), null);
        });

        var shoutcast = new ServerProfile { ServerType = ServerType.ShoutcastV2, Host = "radio.example.com", Port = 8000, StreamId = 2 };

        failures += Case("SHOUTcast v2 picks our stream id", () =>
        {
            const string json = """
            {"streams":[{"id":1,"currentlisteners":5},{"id":2,"currentlisteners":9}]}
            """;
            return Expect(ListenerCounter.ParseShoutcastV2(json, shoutcast), 9);
        });

        failures += Case("SHOUTcast v2 single-stream server", () =>
        {
            const string json = """{"currentlisteners":4}""";
            return Expect(ListenerCounter.ParseShoutcastV2(json, shoutcast), 4);
        });

        failures += Case("SHOUTcast v1 7.html wrapped in HTML", () =>
            Expect(ListenerCounter.ParseShoutcastV1("<HTML><body>6,1,11,200,5,128,Artist - Title</body></HTML>"), 6));

        failures += Case("SHOUTcast v1 7.html bare", () =>
            Expect(ListenerCounter.ParseShoutcastV1("3,1,8,200,3,128,Something"), 3));

        failures += Case("SHOUTcast v1 nonsense is refused", () =>
            Expect(ListenerCounter.ParseShoutcastV1("not a stats line at all"), null));

        // ------------------------------------------------- status2.xsl, the endpoint that still answers
        //
        // status-json.xsl is the modern endpoint and it 404s on at least one ordinary shared Icecast
        // host - the server is Icecast 2.4.0-kh22 and perfectly healthy, the host has simply removed the
        // status templates. That is what made this feature look built and be broken. The document below
        // is the real shape, columns and all.

        failures += Case("status2.xsl, our mount among others", () =>
        {
            const string body = """

            Global,Clients:1536,Sources:59,,0,,
            MountPoint,Connections,Stream Name,Current Listeners,Description,Currently Playing,Stream URL
            /other,44,Someone Else,31,Their station,A Song,http://radio.example.com:8000/other
            /live,12,Example FM,8,Our station,Artist - Title,http://radio.example.com:8000/live
            """;
            return Expect(ListenerCounter.ParseIcecastTable(body, icecast), 8);
        });

        failures += Case("status2.xsl with the mount written without its slash", () =>
        {
            const string body = "live,12,Example FM,3,desc,song,http://radio.example.com:8000/live";
            return Expect(ListenerCounter.ParseIcecastTable(body, icecast), 3);
        });

        failures += Case("status2.xsl with the mount written as the whole URL", () =>
        {
            const string body = "http://radio.example.com:8000/live,12,Example FM,5,desc,song,url";
            return Expect(ListenerCounter.ParseIcecastTable(body, icecast), 5);
        });

        failures += Case("status2.xsl listing nought listeners means nought", () =>
        {
            // Not the same as no answer, and this is the case that used to be indistinguishable.
            const string body = "/live,12,Example FM,0,desc,song,url";
            return Expect(ListenerCounter.ParseIcecastTable(body, icecast), 0);
        });

        failures += Case("status2.xsl with nothing broadcasting", () =>
        {
            // Exactly what the real server returns while the mount is idle: the global line, the column
            // names, and no rows. Verified against it.
            const string body = "\nGlobal,Clients:1536,Sources:59,,0,,\n" +
                                "MountPoint,Connections,Stream Name,Current Listeners,Description,Currently Playing,Stream URL\n";
            return Expect(ListenerCounter.ParseIcecastTable(body, icecast), null);
        });

        failures += Case("status2.xsl never guesses past a comma in a station name", () =>
        {
            // "Rock, Pop and More" moves every column after it along. A missing count is a small
            // disappointment; a count taken from the middle of a song title would be worse than none.
            const string body = "/live,12,Rock, Pop and More,8,desc,song,url";
            return Expect(ListenerCounter.ParseIcecastTable(body, icecast), null);
        });

        failures += Case("status2.xsl listing only somebody else's mount", () =>
        {
            const string body = "/other,44,Someone Else,31,Their station,A Song,url";
            return Expect(ListenerCounter.ParseIcecastTable(body, icecast), null);
        });

        // ------------------------------------------------- the mount's own admin stats

        failures += Case("admin stats for our mount", () =>
        {
            const string xml = """
            <?xml version="1.0"?>
            <icestats><source mount="/live"><listeners>4</listeners><listener_peak>9</listener_peak></source></icestats>
            """;
            return Expect(ListenerCounter.ParseIcecastAdminStats(xml, icecast), 4);
        });

        failures += Case("admin stats for the only mount there is", () =>
        {
            const string xml = """<icestats><source><Listeners>6</Listeners></source></icestats>""";
            return Expect(ListenerCounter.ParseIcecastAdminStats(xml, icecast), 6);
        });

        failures += Case("admin stats never reports somebody else's listeners as ours", () =>
        {
            // A shared host's document can carry several stations. Summing them would put another
            // broadcaster's audience on this station's deck.
            const string xml = """
            <icestats>
              <source mount="/theirs"><listeners>90</listeners></source>
              <source mount="/alsotheirs"><listeners>40</listeners></source>
            </icestats>
            """;
            return Expect(ListenerCounter.ParseIcecastAdminStats(xml, icecast), null);
        });

        failures += Case("admin stats that is not XML at all", () =>
            Expect(ListenerCounter.ParseIcecastAdminStats("401 Authentication Required", icecast), null));

        // ------------------------------------------------- nought, unknown, and the difference

        failures += Check("nought listeners and no answer are not the same thing", () =>
        {
            var counted = ListenerReport.Counted(0, "status2.xsl");
            var missing = ListenerReport.NotPublished("this host publishes nothing");

            ExpectTrue(counted.Known, "a server reporting nought listeners is still an answer");
            ExpectTrue(counted.Value == 0, "nought should survive as nought");
            ExpectTrue(!missing.Known, "not published should not read as a count");
            ExpectTrue(missing.Value is null, "not published should have no number at all");
            ExpectTrue(missing.Detail.Length > 0, "a missing count has to come with a reason");
        });

        failures += Check("one server counting, one silent, still counts", () =>
        {
            var combined = ListenerTally.Combine([
                ListenerReport.Counted(3, "status-json.xsl"),
                ListenerReport.NotPublished("the relay publishes nothing"),
            ]);

            ExpectTrue(combined.Value == 3, $"got {combined.Value}, expected 3 from the one that answered");
            ExpectTrue(combined.Detail.Contains("may be higher"),
                $"a short total has to admit it is short: \"{combined.Detail}\"");
        });

        failures += Check("two servers counting are added up", () =>
        {
            var combined = ListenerTally.Combine([
                ListenerReport.Counted(3, "a"),
                ListenerReport.Counted(4, "b"),
            ]);

            ExpectTrue(combined.Value == 7, $"got {combined.Value}, expected 7");
            ExpectTrue(!combined.Detail.Contains("may be higher"),
                "a complete total should not apologise for itself");
        });

        failures += Check("when nobody says, the reason survives", () =>
        {
            var combined = ListenerTally.Combine([
                ListenerReport.NotPublished("frig2 does not publish a listener count."),
                ListenerReport.NotPublished("the backup does not either."),
            ]);

            ExpectTrue(combined.Value is null, "invented a number out of two refusals");
            ExpectTrue(combined.Detail.Contains("frig2"), $"lost the explanation: \"{combined.Detail}\"");
        });

        failures += Check("unreachable is reported ahead of merely unpublished", () =>
        {
            // One is worth acting on - the address or the port is wrong - and the other is not.
            var combined = ListenerTally.Combine([
                ListenerReport.NotPublished("this one publishes nothing"),
                ListenerReport.Unreachable("could not reach backup.example.com"),
            ]);

            ExpectTrue(combined.Status == ListenerStatus.Unreachable,
                $"got {combined.Status}, expected the reachability problem to win");
        });

        failures += Check("nothing on air has nothing to report", () =>
        {
            var combined = ListenerTally.Combine([]);
            ExpectTrue(combined.Value is null, "counted listeners while off air");
        });

        return failures;
    }

    private static void ExpectTrue(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  ok    {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL  {name}: {ex.Message}");
            return 1;
        }
    }

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

    private static string? Expect(int? actual, int? expected) =>
        actual == expected ? null : $"got {Describe(actual)}, expected {Describe(expected)}";

    private static string Describe(int? value) => value?.ToString() ?? "no count";
}
