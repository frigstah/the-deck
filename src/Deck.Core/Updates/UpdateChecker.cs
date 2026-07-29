using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deck.Core.Localisation;

namespace Deck.Core.Updates;

/// <summary>What the release list says about one build.</summary>
public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("prerelease")]
    public bool IsPrerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool IsDraft { get; set; }

    [JsonPropertyName("published_at")]
    public string Published { get; set; } = string.Empty;

    [JsonPropertyName("assets")]
    public List<ReleaseAsset> Assets { get; set; } = [];

    /// <summary>The tag as a comparable number. Tags are written "v1.3.0.42".</summary>
    public Version? ParsedVersion =>
        Version.TryParse(Tag.TrimStart('v', 'V'), out var version) ? version : null;

    public string DisplayVersion => ParsedVersion?.ToString() ?? Tag;

    /// <summary>
    /// The zip the updater installs. Named on purpose so it can never pick up the portable
    /// download, which carries the portable marker and would silently move a normal install's
    /// settings folder.
    /// </summary>
    public ReleaseAsset? UpdatePayload =>
        Assets.FirstOrDefault(a => a.Name.Contains("-update-", StringComparison.OrdinalIgnoreCase)
                                   && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    public ReleaseAsset? Checksums =>
        Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
}

public sealed class ReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed record UpdateResult(bool Available, string Summary, ReleaseInfo? Release);

/// <summary>
/// Checks whether a newer Deck has been released (I9).
/// <para>
/// Off by default, because a check is an outbound request that says "this machine is running Deck"
/// to whoever answers it, and nobody agreed to that by installing an audio encoder.
/// </para>
/// <para>
/// The repository is pinned. There is no setting for "check some other URL", and that is the
/// point: once Deck can install what it downloads, the address it downloads from stops being a
/// preference and becomes the thing that decides what code runs on the machine. See
/// <see cref="UpdateInstaller"/> for what is done with the answer.
/// </para>
/// </summary>
public sealed class UpdateChecker
{
    public const string Repository = "frigstah/the-deck";

    private const string ReleasesUrl = $"https://api.github.com/repos/{Repository}/releases?per_page=20";

    public const string ReleasesPage = $"https://github.com/{Repository}/releases";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    static UpdateChecker()
    {
        // GitHub rejects requests without one.
        Client.DefaultRequestHeaders.UserAgent.ParseAdd($"Deck/{CurrentVersion}");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>The running version. Four parts, matching the release tags.</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>
    /// Whether alpha builds count. On, because for now alphas are the only builds there are — but
    /// it is a switch rather than an assumption, since that will stop being true.
    /// </summary>
    public bool IncludePrereleases { get; set; } = true;

    public DateTimeOffset? LastChecked { get; private set; }

    public async Task<UpdateResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        LastChecked = DateTimeOffset.Now;

        try
        {
            using var response = await Client.GetAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);

            // A private repository answers 404 to an unauthenticated caller, exactly as a missing
            // one does. Saying so plainly beats "not found", which would read as "no releases yet".
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            {
                return new UpdateResult(false, Strings.Get(StringId.UpdateCheckFailed,
                    "the release list is not public, so Deck cannot see it"), null);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new UpdateResult(false, Strings.Get(StringId.UpdateCheckFailed,
                    "GitHub is rate-limiting this machine — try again in an hour"), null);
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var releases = JsonSerializer.Deserialize<List<ReleaseInfo>>(json) ?? [];

            var newest = releases
                .Where(r => !r.IsDraft)
                .Where(r => IncludePrereleases || !r.IsPrerelease)
                .Where(r => r.ParsedVersion is not null)
                .OrderByDescending(r => r.ParsedVersion)
                .FirstOrDefault();

            if (newest is null)
            {
                return new UpdateResult(false, Strings.Get(StringId.UpdateCheckFailed,
                    "there are no releases yet"), null);
            }

            if (newest.ParsedVersion! <= CurrentVersion)
            {
                return new UpdateResult(false, Strings.Get(StringId.UpdateUpToDate), newest);
            }

            return new UpdateResult(
                true,
                Strings.Get(StringId.UpdateAvailable, newest.DisplayVersion, CurrentVersion.ToString()),
                newest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            var reason = ex is TaskCanceledException ? "it took too long to answer" : ex.Message;
            return new UpdateResult(false, Strings.Get(StringId.UpdateCheckFailed, reason), null);
        }
    }

    /// <summary>
    /// Whether the release page can be opened in a browser. Still only ever an https page, and
    /// still only when the user clicks: installing is a separate, explicit act.
    /// </summary>
    public static bool CanOpen(ReleaseInfo? release) =>
        release is not null &&
        Uri.TryCreate(release.Url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http";
}
