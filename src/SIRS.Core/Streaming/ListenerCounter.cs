using System.Text.Json;
using Sirs.Core.Servers;

namespace Sirs.Core.Streaming;

/// <summary>
/// Asks the server how many people are listening (H4). Every server family reports this somewhere
/// different, and none of them make it part of the source protocol, so each is queried over plain
/// HTTP on its public status endpoint.
/// <para>
/// A failure here is never surfaced as an error: not knowing the listener count is a curiosity, not
/// a problem with the broadcast.
/// </para>
/// </summary>
public static partial class ListenerCounter
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<int?> QueryAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            return profile.ServerType switch
            {
                ServerType.Icecast => await QueryIcecastAsync(profile, cancellationToken).ConfigureAwait(false),
                ServerType.ShoutcastV2 => await QueryShoutcastV2Async(profile, cancellationToken).ConfigureAwait(false),
                ServerType.ShoutcastV1 => await QueryShoutcastV1Async(profile, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BaseUrl(ServerProfile profile, int port) =>
        $"{(profile.UseTls ? "https" : "http")}://{profile.Host}:{port}";

    private static async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // SHOUTcast refuses some status endpoints unless the request looks like a browser.
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SIRS/1.0)");

        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int?> QueryIcecastAsync(ServerProfile profile, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"{BaseUrl(profile, profile.Port)}/status-json.xsl", cancellationToken)
            .ConfigureAwait(false);

        return ParseIcecast(json, profile);
    }

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

    private static async Task<int?> QueryShoutcastV2Async(ServerProfile profile, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"{BaseUrl(profile, profile.Port)}/statistics?json=1", cancellationToken)
            .ConfigureAwait(false);

        return ParseShoutcastV2(json, profile);
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

    private static async Task<int?> QueryShoutcastV1Async(ServerProfile profile, CancellationToken cancellationToken)
    {
        // The v1 stats page lives on the listener port, one below the source port.
        var listenPort = Math.Max(1, profile.Port - 1);
        var body = await GetAsync($"{BaseUrl(profile, listenPort)}/7.html", cancellationToken).ConfigureAwait(false);

        return ParseShoutcastV1(body);
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
