using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using Deck.Core.Servers;

namespace Deck.Core.Streaming;

/// <summary>
/// Asks the server how many people are listening (H4). Every server family reports this somewhere
/// different, and none of them make it part of the source protocol, so each is queried over plain
/// HTTP.
/// <para>
/// There is no single endpoint that works, which is the thing that took this from "built" to broken
/// on a real station. Icecast has published its stats as JSON since 2.4, but a shared host can remove
/// the status templates - and one does: <c>status-json.xsl</c> answers 404 while the server is a
/// perfectly ordinary Icecast underneath. So the public endpoints are tried in order of how much they
/// can say, and then, if none of them will, the mount's own admin stats are asked for using the
/// broadcast credentials Deck already has.
/// </para>
/// <para>
/// A failure here is never surfaced as an error - not knowing the listener count is not a problem with
/// the broadcast - but it is no longer surfaced as silence either. See <see cref="ListenerReport"/>.
/// </para>
/// </summary>
public static partial class ListenerCounter
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<ListenerReport> QueryAsync(
        ServerProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            return profile.ServerType switch
            {
                ServerType.Icecast => await QueryIcecastAsync(profile, cancellationToken).ConfigureAwait(false),
                ServerType.ShoutcastV2 => await QueryShoutcastV2Async(profile, cancellationToken).ConfigureAwait(false),
                ServerType.ShoutcastV1 => await QueryShoutcastV1Async(profile, cancellationToken).ConfigureAwait(false),

                // A SHOUTcast that has not settled which version it is. Knowing the family is enough
                // to ask - it just means asking twice, which is the entire cost of not having put the
                // question to somebody who could not answer it.
                _ when profile.ServerType.IsShoutcast() =>
                    await QueryShoutcastEitherAsync(profile, cancellationToken).ConfigureAwait(false),

                _ => ListenerReport.Unsupported(
                    "Deck has not worked out what kind of server this is yet, so it cannot ask for a listener count."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ListenerReport.Unreachable($"Could not reach {profile.Host} to ask: {ex.Message}");
        }
    }

    private static string BaseUrl(ServerProfile profile, int port) =>
        $"{(profile.UseTls ? "https" : "http")}://{profile.Host}:{port}";

    /// <summary>The body, or null when the server did not answer with one.</summary>
    private static async Task<string?> TryGetAsync(
        string url, ServerProfile? credentials, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // SHOUTcast refuses some status endpoints unless the request looks like a browser.
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Deck/1.0)");

        if (credentials is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    $"{credentials.Username}:{credentials.Password}")));
        }

        try
        {
            using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>One way of asking an Icecast server how many people are listening.</summary>
    private sealed record Endpoint(
        string Name,
        string Path,
        bool NeedsPassword,
        Func<string, ServerProfile, int?> Parse,
        string WhenSilent,
        string WhenUseless);

    /// <summary>
    /// Three places to ask, in order of how little they cost: the JSON stats every modern Icecast
    /// publishes, the plain-text table older ones publish, and the mount's own admin stats, which need
    /// the broadcast password and are the only thing left on a host that has removed the public pages.
    /// </summary>
    private static Endpoint[] IcecastEndpoints(ServerProfile profile) =>
    [
        new("status-json.xsl", "/status-json.xsl", false, ParseIcecast,
            "status-json.xsl", "status-json.xsl, which answered about some other mount"),

        new("status2.xsl", "/status2.xsl", false, ParseIcecastTable,
            "status2.xsl", "status2.xsl, which listed no such mount"),

        // Mount-scoped admin, authenticated with the source credentials for that mount - the same way
        // Deck already sends now-playing titles, so it needs nothing the user has not already given it.
        // Deliberately /admin/stats and not /admin/listclients: both give the number, and only one of
        // them makes Deck download a list of the listeners' addresses in order to count them.
        new("the mount's admin stats", $"/admin/stats?mount={Uri.EscapeDataString(profile.NormalisedMount)}",
            true, ParseIcecastAdminStats,
            "the mount's admin stats, which needed a password this server would not accept",
            "the mount's admin stats, which said nothing about listeners"),
    ];

    /// <summary>
    /// Which endpoint last answered for a given server.
    /// <para>
    /// A show polls every fifteen seconds for hours, and on a host that publishes nothing publicly two
    /// of the three requests are known in advance to be futile - four hundred pointless 404s in an
    /// evening, from a client that has already been told. So the one that worked is tried first next
    /// time, and the others are still there if it stops working.
    /// </para>
    /// <para>
    /// Keyed by profile, and a profile that is edited keeps its id, so the worst a stale entry costs is
    /// one wasted request before the rest of the chain runs anyway.
    /// </para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> LastWorked = new();

    private static async Task<ListenerReport> QueryIcecastAsync(
        ServerProfile profile, CancellationToken cancellationToken)
    {
        var root = BaseUrl(profile, profile.Port);
        var endpoints = IcecastEndpoints(profile).ToList();

        if (LastWorked.TryGetValue(profile.Id, out var remembered))
        {
            var index = endpoints.FindIndex(e => e.Name == remembered);
            if (index > 0)
            {
                var first = endpoints[index];
                endpoints.RemoveAt(index);
                endpoints.Insert(0, first);
            }
        }

        var tried = new List<string>();

        foreach (var endpoint in endpoints)
        {
            var body = await TryGetAsync(
                root + endpoint.Path,
                endpoint.NeedsPassword ? profile : null,
                cancellationToken).ConfigureAwait(false);

            if (body is null)
            {
                tried.Add(endpoint.WhenSilent);
                continue;
            }

            if (endpoint.Parse(body, profile) is { } counted)
            {
                LastWorked[profile.Id] = endpoint.Name;
                return ListenerReport.Counted(counted, $"Counted from {endpoint.Name}.");
            }

            tried.Add(endpoint.WhenUseless);
        }

        LastWorked.TryRemove(profile.Id, out _);

        return ListenerReport.NotPublished(
            $"{profile.Name} does not publish a listener count. Deck asked for {Join(tried)}.");
    }

    /// <summary>
    /// Semicolons rather than commas, because the items have commas of their own - "status2.xsl, which
    /// listed no such mount" - and a comma-separated list of comma-containing phrases reads as one long
    /// muddle. This sentence is the only explanation the user gets; it has to be readable.
    /// </summary>
    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "nothing",
        1 => parts[0],
        _ => string.Join("; ", parts.Take(parts.Count - 1)) + "; and " + parts[^1],
    };

    /// <summary>
    /// Reads an Icecast status document. Public because the response shape - a single object with
    /// one mount live, an array with several - is the part most likely to be got wrong, and it is
    /// worth pinning down without a server.
    /// </summary>
    public static int? ParseIcecast(string json, ServerProfile profile)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("icestats", out var stats)) return null;
        if (!stats.TryGetProperty("source", out var source)) return null;

        // Icecast emits a single object when only one mount is live, and an array otherwise.
        return source.ValueKind == JsonValueKind.Array
            ? MatchMount(source, profile)
            : ReadListeners(source);
    }

    private static int? MatchMount(JsonElement sources, ServerProfile profile)
    {
        var mount = profile.NormalisedMount;
        var total = 0;
        var matched = false;

        foreach (var entry in sources.EnumerateArray())
        {
            var listeners = ReadListeners(entry);
            if (listeners is null) continue;

            // listenurl is the only field that reliably carries the mount point.
            if (entry.TryGetProperty("listenurl", out var url) &&
                url.GetString() is { } text &&
                text.EndsWith(mount, StringComparison.OrdinalIgnoreCase))
            {
                return listeners;
            }

            total += listeners.Value;
            matched = true;
        }

        // No mount matched, so report the whole server rather than nothing.
        return matched ? total : null;
    }

    private static int? ReadListeners(JsonElement source) =>
        source.TryGetProperty("listeners", out var listeners) && listeners.TryGetInt32(out var count)
            ? count
            : null;

    /// <summary>
    /// Reads <c>status2.xsl</c>, the plain-text table Icecast has published since long before the JSON
    /// one, and the only public page still answering on some hosts. One line per live mount:
    /// <c>MountPoint,Connections,Stream Name,Current Listeners,Description,Currently Playing,Stream URL</c>
    /// with a <c>Global,Clients:…</c> line above and a header line naming the columns.
    /// <para>
    /// The awkward part is that three of those columns are free text and any of them can contain a
    /// comma - a station called "Rock, Pop and More" moves every column after it along. So the count
    /// is only read when the column that should hold it does hold a number. A missing listener count is
    /// a small disappointment; a wrong one, taken from the middle of a song title, would be worse than
    /// none at all.
    /// </para>
    /// </summary>
    public static int? ParseIcecastTable(string body, ServerProfile profile)
    {
        var mount = profile.NormalisedMount;

        foreach (var line in body.Split('\n'))
        {
            var row = line.Trim();
            if (row.Length == 0) continue;

            // The server-wide line and the column names are not mounts.
            if (row.StartsWith("Global,", StringComparison.OrdinalIgnoreCase)) continue;
            if (row.StartsWith("MountPoint,", StringComparison.OrdinalIgnoreCase)) continue;

            var fields = row.Split(',');
            if (fields.Length < 4) continue;

            if (!SameMount(fields[0].Trim(), mount)) continue;

            return int.TryParse(fields[3].Trim(), out var listeners) ? listeners : null;
        }

        return null;
    }

    /// <summary>
    /// Reads the XML from <c>/admin/stats?mount=…</c>. Asked for last, because it needs the broadcast
    /// password, and reached at all because a host that has removed the public status pages has left
    /// this as the only way a source client can find out.
    /// </summary>
    public static int? ParseIcecastAdminStats(string xml, ServerProfile profile)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var sources = document.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "source", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The mount asked for, or the only one there is. Never the sum: on a shared host the document
        // could carry someone else's station, and reporting their listeners as yours is worse than
        // reporting none.
        var wanted = sources.FirstOrDefault(e =>
            SameMount((string?)e.Attribute("mount") ?? string.Empty, profile.NormalisedMount));

        var source = wanted ?? (sources.Count == 1 ? sources[0] : null);
        if (source is null) return null;

        var listeners = source.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, "listeners", StringComparison.OrdinalIgnoreCase));

        return listeners is not null && int.TryParse(listeners.Value.Trim(), out var count) ? count : null;
    }

    /// <summary>
    /// Whether a mount named in a status document is the one being broadcast to. Servers write it as
    /// "/stream", as "stream", and occasionally as the whole listen URL, so all three have to mean the
    /// same thing.
    /// </summary>
    private static bool SameMount(string reported, string mount)
    {
        var text = reported.Trim();
        if (text.Length == 0) return false;

        if (string.Equals(text, mount, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals("/" + text.TrimStart('/'), mount, StringComparison.OrdinalIgnoreCase)) return true;

        // A full URL: only the path counts, or "http://host:8000/live" would fail to match "/live".
        return Uri.TryCreate(text, UriKind.Absolute, out var url) &&
               string.Equals(url.AbsolutePath, mount, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ListenerReport> QueryShoutcastV2Async(
        ServerProfile profile, CancellationToken cancellationToken)
    {
        var json = await TryGetAsync($"{BaseUrl(profile, profile.Port)}/statistics?json=1", null, cancellationToken)
            .ConfigureAwait(false);

        if (json is null)
        {
            return ListenerReport.NotPublished(
                $"{profile.Name} did not answer on /statistics, which is where SHOUTcast v2 publishes its figures.");
        }

        return ParseShoutcastV2(json, profile) is { } count
            ? ListenerReport.Counted(count, "Counted from /statistics.")
            : ListenerReport.NotPublished(
                $"{profile.Name} answered on /statistics but said nothing about stream {profile.StreamId}.");
    }

    public static int? ParseShoutcastV2(string json, ServerProfile profile)
    {
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.TryGetProperty("streams", out var streams) &&
            streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("id", out var id) || !id.TryGetInt32(out var streamId)) continue;
                if (streamId != profile.StreamId) continue;

                if (stream.TryGetProperty("currentlisteners", out var current) && current.TryGetInt32(out var count))
                {
                    return count;
                }
            }
        }

        return document.RootElement.TryGetProperty("currentlisteners", out var top) && top.TryGetInt32(out var topCount)
            ? topCount
            : null;
    }

    /// <summary>
    /// Both SHOUTcast stats endpoints, for a profile that knows its family but not its version.
    /// <para>
    /// v2's <c>/statistics</c> first, because it is explicit about which stream it is answering for,
    /// then v1's <c>7.html</c> - which a DNAS 2 also serves for compatibility, so the order costs a
    /// v1 server one wasted request and never costs a v2 server anything.
    /// </para>
    /// </summary>
    private static async Task<ListenerReport> QueryShoutcastEitherAsync(
        ServerProfile profile, CancellationToken cancellationToken)
    {
        var modern = await QueryShoutcastV2Async(profile, cancellationToken).ConfigureAwait(false);
        if (modern.Known) return modern;

        var legacy = await QueryShoutcastV1Async(profile, cancellationToken).ConfigureAwait(false);

        // The v1 answer only when it is an answer. Otherwise the v2 report is the more informative
        // of two "nothing published" messages, because it names the endpoint a modern host would use.
        return legacy.Known ? legacy : modern;
    }

    private static async Task<ListenerReport> QueryShoutcastV1Async(
        ServerProfile profile, CancellationToken cancellationToken)
    {
        // The v1 stats page lives on the listener port, one below the source port - but hosts differ
        // about which of the two they quoted, so both are worth asking before giving up.
        var listenPort = Math.Max(1, profile.Port - 1);

        foreach (var port in new[] { listenPort, profile.Port })
        {
            if (await TryGetAsync($"{BaseUrl(profile, port)}/7.html", null, cancellationToken).ConfigureAwait(false)
                is { } body && ParseShoutcastV1(body) is { } count)
            {
                return ListenerReport.Counted(count, $"Counted from 7.html on port {port}.");
            }
        }

        return ListenerReport.NotPublished(
            $"{profile.Name} did not answer on 7.html, on port {listenPort} or {profile.Port}.");
    }

    /// <summary>
    /// Reads SHOUTcast v1's 7.html, a comma-separated line usually wrapped in a stray HTML tag:
    /// currentlisteners, status, peaklisteners, maxlisteners, uniquelisteners, bitrate, songtitle.
    /// </summary>
    public static int? ParseShoutcastV1(string body)
    {
        // Servers wrap the line in assorted markup - <HTML><body>…</body></HTML> and variations -
        // so strip every tag rather than trying to find where the payload starts.
        var payload = HtmlTagRegex().Replace(body, string.Empty).Trim();

        var fields = payload.Split(',');
        return fields.Length > 0 && int.TryParse(fields[0].Trim(), out var listeners) ? listeners : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]*>")]
    private static partial System.Text.RegularExpressions.Regex HtmlTagRegex();
}
