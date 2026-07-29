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

        return failures;
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
