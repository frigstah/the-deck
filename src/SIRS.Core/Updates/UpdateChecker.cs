using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sirs.Core.Localisation;

namespace Sirs.Core.Updates;

/// <summary>What the release feed says about the newest version.</summary>
public sealed class ReleaseInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>The page to open. Deliberately a page, not a file - see <see cref="UpdateChecker"/>.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("published")]
    public string Published { get; set; } = string.Empty;
}

public sealed record UpdateResult(bool Available, string Summary, ReleaseInfo? Release);

/// <summary>
/// Checks whether a newer SIRS has been released (I9).
/// <para>
/// Off by default, because a check is an outbound request that says "this machine is running SIRS"
/// to whoever answers it, and nobody agreed to that by installing an audio encoder.
/// </para>
/// <para>
/// It never downloads or installs anything. It reports the version and opens the release page if
/// the user asks. An encoder that can silently replace its own binary is an encoder that can be
/// made to run someone else's code by whoever controls that URL, and no amount of convenience is
/// worth handing over that key on a machine that goes on air.
/// </para>
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>
    /// Where the release feed would live. There is no SIRS release server yet, so nothing answers
    /// this - the check reports that honestly rather than pretending to be up to date.
    /// </summary>
    public const string DefaultFeedUrl = "https://sirs.invalid/releases/latest.json";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public UpdateChecker() => Client.DefaultRequestHeaders.UserAgent.ParseAdd($"SIRS/{CurrentVersion}");

    /// <summary>The running version, as three numbers.</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public string FeedUrl { get; set; } = DefaultFeedUrl;

    public DateTimeOffset? LastChecked { get; private set; }

    public async Task<UpdateResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await Client.GetStringAsync(FeedUrl, cancellationToken).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<ReleaseInfo>(json);

            LastChecked = DateTimeOffset.Now;

            if (release is null || !Version.TryParse(release.Version, out var latest))
            {
                return new UpdateResult(false, Strings.Get(StringId.UpdateCheckFailed, "the reply made no sense"), null);
            }

            if (latest <= Normalise(CurrentVersion))
            {
                return new UpdateResult(false, Strings.Get(StringId.UpdateUpToDate), release);
            }

            return new UpdateResult(
                true,
                Strings.Get(StringId.UpdateAvailable, Describe(latest), Describe(CurrentVersion)),
                release);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LastChecked = DateTimeOffset.Now;

            var reason = ex is TaskCanceledException ? "it took too long to answer" : ex.Message;
            return new UpdateResult(false, Strings.Get(StringId.UpdateCheckFailed, reason), null);
        }
    }

    /// <summary>
    /// Only the release page is ever opened, and only when the user clicks. Nothing is downloaded
    /// and nothing is run; the browser and the user decide what happens next.
    /// </summary>
    public static bool CanOpen(ReleaseInfo? release) =>
        release is not null &&
        Uri.TryCreate(release.Url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http";

    /// <summary>Build and revision numbers are noise in a version a user reads.</summary>
    private static Version Normalise(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build));

    private static string Describe(Version version) =>
        version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";
}
