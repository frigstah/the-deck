using System.Text.RegularExpressions;

namespace Sirs.Core.Servers;

public sealed record UrlParseResult(
    bool Success,
    ServerProfile? Profile,
    IReadOnlyList<string> Recognised,
    string? Message);

/// <summary>
/// Turns whatever a host emailed the user into filled-in fields (C2). Handles plain URLs, URLs with
/// credentials, bare host:port, and the "Server: ... Port: ... Mount: ..." blocks that most hosting
/// control panels hand out. This is the single biggest reduction in setup friction in the product,
/// so it is deliberately forgiving about format.
/// </summary>
public static partial class StreamUrlParser
{
    public static UrlParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new UrlParseResult(false, null, [], "Paste the details your host gave you.");
        }

        // Line endings are normalised before anything looks at the text. This is not tidiness: the
        // labelled-field pattern is multiline, and in .NET a multiline "$" matches *after* a
        // carriage return, so on Windows line endings the value group - which cannot contain one -
        // failed to reach the anchor and the whole block silently parsed as nothing. A control
        // panel's instructions copied on Windows are exactly the input this feature exists for.
        input = input.Replace("\r\n", "\n").Replace('\r', '\n');

        var profile = new ServerProfile();
        var recognised = new List<string>();

        var fields = ExtractLabelledFields(input);
        ApplyLabelledFields(profile, fields, recognised);

        // A URL anywhere in the text fills anything the labels did not cover.
        foreach (var candidate in FindUrlCandidates(input))
        {
            if (TryApplyUrl(profile, candidate, recognised)) break;
        }

        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            return new UrlParseResult(false, null, recognised,
                "SIRS could not find a server address in that. Paste the whole message from your host, or type the details in below.");
        }

        InferServerType(profile, fields);
        InferTls(profile);

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            profile.Name = profile.Host;
        }

        return new UrlParseResult(true, profile, recognised, null);
    }

    private static Dictionary<string, string> ExtractLabelledFields(string input)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in LabelledFieldRegex().Matches(input))
        {
            var key = NormaliseKey(match.Groups["key"].Value);
            var value = match.Groups["value"].Value.Trim().Trim('"', '\'');

            // A "Server: http://..." line is a URL, not a bare host; leave it for the URL pass.
            if (value.Length == 0) continue;
            if (!fields.ContainsKey(key)) fields[key] = value;
        }

        return fields;
    }

    private static string NormaliseKey(string key) =>
        WhitespaceRegex().Replace(key.Trim().ToLowerInvariant(), " ").Replace("-", " ").Replace("_", " ");

    private static void ApplyLabelledFields(ServerProfile profile, Dictionary<string, string> fields, List<string> recognised)
    {
        if (TryGet(fields, out var host, "server", "server address", "host", "hostname", "server hostname", "ip", "server ip", "address")
            && !host.Contains("://"))
        {
            // Some panels put "host:port" on the Server line.
            var parts = host.Split(':', 2);
            profile.Host = parts[0].Trim();
            recognised.Add("Server address");

            if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out var inlinePort))
            {
                profile.Port = inlinePort;
                recognised.Add("Port");
            }
        }

        if (TryGet(fields, out var port, "port", "server port", "source port", "encoder port")
            && int.TryParse(port, out var portNumber))
        {
            profile.Port = portNumber;
            if (!recognised.Contains("Port")) recognised.Add("Port");
        }

        if (TryGet(fields, out var mount, "mount", "mountpoint", "mount point", "stream name", "stream path", "mount name"))
        {
            profile.MountPoint = mount.StartsWith('/') ? mount : "/" + mount;
            recognised.Add("Stream address");
        }

        if (TryGet(fields, out var user, "username", "user", "source user", "user name", "login"))
        {
            profile.Username = user;
            recognised.Add("Username");
        }

        if (TryGet(fields, out var password, "password", "source password", "encoder password", "broadcast password", "stream password", "pass"))
        {
            profile.Password = password;
            recognised.Add("Password");
        }

        if (TryGet(fields, out var streamId, "sid", "stream id", "streamid", "stream number")
            && int.TryParse(streamId, out var id))
        {
            profile.StreamId = id;
            profile.ServerType = ServerType.ShoutcastV2;
            recognised.Add("Stream number");
        }

        if (TryGet(fields, out var name, "station", "station name", "name", "stream title"))
        {
            profile.StationName = name;
            profile.Name = name;
            recognised.Add("Station name");
        }

        if (TryGet(fields, out var genre, "genre"))
        {
            profile.Genre = genre;
            recognised.Add("Genre");
        }

        if (TryGet(fields, out var website, "website", "url", "web site", "homepage") && website.Contains('.'))
        {
            profile.Website = website;
            recognised.Add("Website");
        }
    }

    private static bool TryGet(Dictionary<string, string> fields, out string value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var found) && !string.IsNullOrWhiteSpace(found))
            {
                value = found;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static IEnumerable<string> FindUrlCandidates(string input)
    {
        foreach (Match match in UrlRegex().Matches(input))
        {
            yield return match.Value.TrimEnd('.', ',', ';', ')');
        }
    }

    private static bool TryApplyUrl(ServerProfile profile, string candidate, List<string> recognised)
    {
        var text = candidate.Trim();
        var hadScheme = text.Contains("://", StringComparison.Ordinal);

        var scheme = "http";
        if (hadScheme)
        {
            scheme = text[..text.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant();
            text = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
        }

        // Uri wants a scheme it recognises; icecast:// and friends are not among them.
        if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            profile.Host = uri.Host;
            recognised.Add("Server address");
        }
        else if (!string.Equals(profile.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            // A different host than the labelled fields gave; the labels win.
            return false;
        }

        var explicitPort = ExplicitPortRegex().IsMatch(text);
        if (explicitPort)
        {
            profile.Port = uri.Port;
            if (!recognised.Contains("Port")) recognised.Add("Port");
        }
        else if (!recognised.Contains("Port"))
        {
            profile.Port = scheme switch
            {
                "https" => 443,
                "http" => 80,
                _ => 8000,
            };
        }

        if (scheme is "https" or "icecast+ssl" or "sc+ssl") profile.UseTls = true;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var credentials = uri.UserInfo.Split(':', 2);
            if (credentials[0].Length > 0)
            {
                profile.Username = Uri.UnescapeDataString(credentials[0]);
                if (!recognised.Contains("Username")) recognised.Add("Username");
            }

            if (credentials.Length == 2 && credentials[1].Length > 0 && string.IsNullOrEmpty(profile.Password))
            {
                profile.Password = Uri.UnescapeDataString(credentials[1]);
                if (!recognised.Contains("Password")) recognised.Add("Password");
            }
        }

        var path = uri.AbsolutePath;
        if (path.Length > 1 && !recognised.Contains("Stream address"))
        {
            // Trim the trailing marker some listen URLs carry, and any file extension.
            profile.MountPoint = path.TrimEnd(';', '/');
            if (profile.MountPoint.Length > 1) recognised.Add("Stream address");
        }

        return true;
    }

    private static void InferServerType(ServerProfile profile, Dictionary<string, string> fields)
    {
        if (profile.ServerType != ServerType.Unknown) return;

        if (fields.ContainsKey("sid") || fields.ContainsKey("stream id"))
        {
            profile.ServerType = ServerType.ShoutcastV2;
            return;
        }

        // Leave it Unknown otherwise: the probe gives a far more reliable answer than guessing
        // from a mount point, and Unknown is what triggers auto-detection in the editor.
    }

    private static void InferTls(ServerProfile profile)
    {
        if (profile.UseTls) return;
        if (profile.Port is 443 or 8443) profile.UseTls = true;
    }

    [GeneratedRegex(@"^[^\S\r\n]*(?<key>[A-Za-z][A-Za-z \-_]{1,28}?)[^\S\r\n]*[:=][^\S\r\n]*(?<value>[^\r\n]+)$",
        RegexOptions.Multiline)]
    private static partial Regex LabelledFieldRegex();

    [GeneratedRegex(@"(?:[a-zA-Z][a-zA-Z0-9+.\-]*://)?(?:[^\s:@/]+(?::[^\s@/]*)?@)?(?:[A-Za-z0-9\-]+\.)+[A-Za-z]{2,}(?::\d{1,5})?(?:/[^\s]*)?|(?:[a-zA-Z][a-zA-Z0-9+.\-]*://)?(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?:/[^\s]*)?")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^[^/]*:\d{1,5}(?:[/?#]|$)")]
    private static partial Regex ExplicitPortRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
