using Sirs.Core.Servers;

namespace Sirs.EncoderCheck;

/// <summary>
/// Cases for the paste-a-URL parser (C2), taken from the shapes real hosts actually hand out:
/// plain listen URLs, URLs carrying credentials, bare host:port, and the labelled blocks that
/// control panels email. Getting this wrong is invisible until a user cannot connect, so it is
/// pinned down here.
/// </summary>
internal static class ParserChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Case("plain Icecast listen URL",
            "http://stream.example.com:8000/live",
            p => Expect(p, host: "stream.example.com", port: 8000, mount: "/live", tls: false));

        failures += Case("https with no port implies 443 and TLS",
            "https://radio.example.org/live",
            p => Expect(p, host: "radio.example.org", port: 443, mount: "/live", tls: true));

        failures += Case("bare host and port",
            "stream.example.com:8000",
            p => Expect(p, host: "stream.example.com", port: 8000, tls: false));

        failures += Case("credentials embedded in the URL",
            "http://source:s3cret@radio.example.com:8010/mount1",
            p => Expect(p, host: "radio.example.com", port: 8010, mount: "/mount1",
                username: "source", password: "s3cret"));

        failures += Case("labelled block from a control panel",
            """
            Thanks for signing up! Here are your details:

            Server: stream.myhost.net
            Port: 8020
            Mount point: /radio
            Username: source
            Password: abc123xyz
            """,
            p => Expect(p, host: "stream.myhost.net", port: 8020, mount: "/radio",
                username: "source", password: "abc123xyz"));

        // The same block with Windows line endings, spelled out rather than left to whatever the
        // repository happened to check out. This is not hypothetical tidiness: the multiline
        // pattern used to fail outright on CRLF, so a block copied out of a control panel on
        // Windows - the exact input this feature exists for - parsed as nothing at all. It passed
        // locally and failed in CI purely because the two checkouts had different line endings.
        failures += Case("the same block with Windows line endings",
            "Server: stream.myhost.net\r\nPort: 8020\r\nMount point: /radio\r\n" +
            "Username: source\r\nPassword: abc123xyz\r\n",
            p => Expect(p, host: "stream.myhost.net", port: 8020, mount: "/radio",
                username: "source", password: "abc123xyz"));

        // And with old Mac endings, since normalising handles all three and a lone \r is the one
        // that would otherwise leave every field glued into a single line.
        failures += Case("a block with bare carriage returns",
            "Server: stream.myhost.net\rPort: 8020\rMount point: /radio\r",
            p => Expect(p, host: "stream.myhost.net", port: 8020, mount: "/radio"));

        failures += Case("SHOUTcast block with a stream id",
            """
            Server IP: 198.51.100.20
            Port: 8000
            SID: 2
            Encoder password: letmein
            """,
            p => Expect(p, host: "198.51.100.20", port: 8000, password: "letmein",
                serverType: ServerType.ShoutcastV2, streamId: 2));

        failures += Case("host line carrying an inline port",
            """
            Server: stream.example.com:9000
            Password: hunter2
            """,
            p => Expect(p, host: "stream.example.com", port: 9000, password: "hunter2"));

        failures += Case("port 8443 is treated as secure",
            "stream.example.com:8443/live",
            p => Expect(p, host: "stream.example.com", port: 8443, mount: "/live", tls: true));

        failures += ExpectFailure("free text with no server in it", "Hello, when does your show start?");
        failures += ExpectFailure("empty input", "   ");

        return failures;
    }

    private static int Case(string name, string input, Func<ServerProfile, string?> verify)
    {
        var result = StreamUrlParser.Parse(input);

        if (!result.Success || result.Profile is null)
        {
            Console.WriteLine($"  FAIL  {name}: parser rejected the input ({result.Message})");
            return 1;
        }

        var problem = verify(result.Profile);
        if (problem is not null)
        {
            Console.WriteLine($"  FAIL  {name}: {problem}");
            return 1;
        }

        Console.WriteLine($"  ok    {name}  [{string.Join(", ", result.Recognised)}]");
        return 0;
    }

    private static int ExpectFailure(string name, string input)
    {
        var result = StreamUrlParser.Parse(input);
        if (result.Success)
        {
            Console.WriteLine($"  FAIL  {name}: expected a rejection but got host \"{result.Profile?.Host}\"");
            return 1;
        }

        Console.WriteLine($"  ok    {name}  (rejected as intended)");
        return 0;
    }

    private static string? Expect(
        ServerProfile profile,
        string? host = null,
        int? port = null,
        string? mount = null,
        bool? tls = null,
        string? username = null,
        string? password = null,
        ServerType? serverType = null,
        int? streamId = null)
    {
        if (host is not null && !string.Equals(profile.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            return $"host was \"{profile.Host}\", expected \"{host}\"";
        }

        if (port is not null && profile.Port != port) return $"port was {profile.Port}, expected {port}";
        if (mount is not null && profile.NormalisedMount != mount) return $"mount was \"{profile.NormalisedMount}\", expected \"{mount}\"";
        if (tls is not null && profile.UseTls != tls) return $"TLS was {profile.UseTls}, expected {tls}";
        if (username is not null && profile.Username != username) return $"username was \"{profile.Username}\", expected \"{username}\"";
        if (password is not null && profile.Password != password) return $"password was \"{profile.Password}\", expected \"{password}\"";
        if (serverType is not null && profile.ServerType != serverType) return $"server type was {profile.ServerType}, expected {serverType}";
        if (streamId is not null && profile.StreamId != streamId) return $"stream id was {profile.StreamId}, expected {streamId}";

        return null;
    }
}
