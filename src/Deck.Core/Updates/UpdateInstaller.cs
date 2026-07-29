using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;

namespace Deck.Core.Updates;

public sealed record InstallResult(bool Ok, string Message);

/// <summary>Progress of a download, for a bar that has to mean something.</summary>
public sealed record UpdateProgress(string Stage, double Fraction);

/// <summary>
/// Downloads a release and replaces the running copy of Deck with it.
/// <para>
/// This is the part that needed convincing. Deck spent four phases deliberately <em>not</em> doing
/// this, on the grounds that an encoder able to replace its own binary is one that can be made to
/// run someone else's code by whoever controls the URL. Building it anyway means being honest
/// about what does and does not make that safe:
/// </para>
/// <list type="bullet">
/// <item>The repository is pinned in <see cref="UpdateChecker"/>. There is no setting for it.</item>
/// <item>Only https, and only a <c>github.com</c> or <c>githubusercontent.com</c> host, is ever
/// fetched — a release whose asset URL points anywhere else is refused rather than followed.</item>
/// <item>The download must match the SHA-256 published beside it in the same release, or it is
/// deleted unread.</item>
/// </list>
/// <para>
/// What that does <em>not</em> do is protect against a compromised GitHub account: the digest and
/// the file come from the same place, so whoever can replace one can replace the other. Closing
/// that would need the build to be signed with a key that never touches CI, and these builds are
/// not signed at all. Anyone who considers that unacceptable should leave the update check off and
/// download by hand — which still works, and is still what the button did before this existed.
/// </para>
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>Hosts a release asset is allowed to live on.</summary>
    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    /// <summary>Refuses anything absurd before a byte is written, rather than after 4 GB of it.</summary>
    private const long MaxPayloadBytes = 600L * 1024 * 1024;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    static UpdateInstaller()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd($"Deck/{UpdateChecker.CurrentVersion}");
    }

    /// <summary>Where Deck is installed — the folder that will be replaced.</summary>
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Downloads are staged out of the install folder so a half-written one cannot break it.</summary>
    public static string StagingRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Deck", "update");

    public event EventHandler<UpdateProgress>? Progress;

    /// <summary>
    /// Whether Deck can replace its own files. False for an install under Program Files without
    /// elevation, which is why the installer puts Deck in the per-user location instead.
    /// </summary>
    public static bool CanInstallInPlace(out string reason)
    {
        try
        {
            var probe = Path.Combine(InstallDirectory, $".sirs-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            reason = $"Deck cannot write to its own folder ({InstallDirectory}). " +
                     "Download the new version from the release page instead.";
            return false;
        }
    }

    /// <summary>
    /// Fetches the release, checks it, unpacks it, and hands over to a copy of the new build that
    /// waits for this one to exit. Returns only if something went wrong — on success the caller is
    /// expected to shut down.
    /// </summary>
    public async Task<InstallResult> InstallAsync(ReleaseInfo release, CancellationToken cancellationToken = default)
    {
        if (!CanInstallInPlace(out var reason)) return new InstallResult(false, reason);

        if (release.UpdatePayload is not { } payload)
        {
            return new InstallResult(false,
                "That release has no update package attached. Download it from the release page instead.");
        }

        if (!IsAllowed(payload.Url)) return new InstallResult(false, RefusedMessage(payload.Url));

        if (payload.Size > MaxPayloadBytes)
        {
            return new InstallResult(false, $"That download is {payload.Size / 1024 / 1024} MB, which is not credible for Deck.");
        }

        try
        {
            Cleanup();

            var expected = await ExpectedDigestAsync(release, payload.Name, cancellationToken).ConfigureAwait(false);
            if (expected is null)
            {
                return new InstallResult(false,
                    "That release does not publish a checksum, so Deck will not install it. " +
                    "Download it from the release page instead.");
            }

            Directory.CreateDirectory(StagingRoot);
            var zipPath = Path.Combine(StagingRoot, payload.Name);

            var actual = await DownloadAsync(payload, zipPath, cancellationToken).ConfigureAwait(false);

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                // Deleted rather than kept for inspection: a file that failed its checksum is the
                // one thing that must not be left sitting next to an installer.
                TryDelete(zipPath);

                return new InstallResult(false,
                    "The download did not match its checksum, so Deck threw it away. " +
                    "Try again, and if it keeps happening, download from the release page instead.");
            }

            Report("Unpacking", 0.9);

            var staged = Path.Combine(StagingRoot, "staged");
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, staged);

            var stagedExe = Path.Combine(staged, "Deck.exe");
            if (!File.Exists(stagedExe))
            {
                return new InstallResult(false, "The download did not contain Deck.exe. Nothing has been changed.");
            }

            Report("Restarting", 1.0);

            // The swap is done by the *new* build, running from the staging folder. It is already a
            // complete self-contained copy, so nothing else has to be shipped to do this - and a
            // program cannot overwrite the files it is itself running from.
            Process.Start(new ProcessStartInfo(stagedExe)
            {
                ArgumentList =
                {
                    "--apply-update",
                    "--target", InstallDirectory,
                    "--wait", Environment.ProcessId.ToString(),
                },
                UseShellExecute = false,
                WorkingDirectory = staged,
            });

            return new InstallResult(true, "Installing the update. Deck will restart on its own.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or TaskCanceledException)
        {
            return new InstallResult(false, $"Deck could not install that update: {ex.Message}");
        }
    }

    private async Task<string> DownloadAsync(ReleaseAsset asset, string path, CancellationToken cancellationToken)
    {
        using var response = await Client
            .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // Redirects are followed by HttpClient, so where it actually ended up is checked too - not
        // just where the release said it would go.
        if (response.RequestMessage?.RequestUri is { } finalUri && !IsAllowed(finalUri.ToString()))
        {
            throw new HttpRequestException(RefusedMessage(finalUri.ToString()));
        }

        var total = response.Content.Headers.ContentLength ?? asset.Size;
        if (total > MaxPayloadBytes) throw new IOException("The download is larger than Deck will accept.");

        using var sha = SHA256.Create();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(path);

        var buffer = new byte[81920];
        long read = 0;
        int count;

        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            read += count;
            if (read > MaxPayloadBytes) throw new IOException("The download grew beyond what Deck will accept.");

            // Hashed as it is written, so the file is never read back to check it and there is no
            // window where a different file could be substituted in between.
            sha.TransformBlock(buffer, 0, count, null, 0);
            await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);

            if (total > 0) Report("Downloading", 0.85 * read / total);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Reads the digest for one file out of the release's SHA256SUMS.txt.</summary>
    private async Task<string?> ExpectedDigestAsync(ReleaseInfo release, string name, CancellationToken cancellationToken)
    {
        if (release.Checksums is not { } sums || !IsAllowed(sums.Url)) return null;

        var text = await Client.GetStringAsync(sums.Url, cancellationToken).ConfigureAwait(false);

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            if (!parts[^1].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

            var digest = parts[0].Trim();
            return digest.Length == 64 ? digest : null;
        }

        return null;
    }

    private static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    private static string RefusedMessage(string url) =>
        $"That release points somewhere Deck will not download from ({url}). Nothing has been changed.";

    /// <summary>Clears out what a previous update left behind. Called before staging a new one.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(StagingRoot))
            {
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still running, or still locked. The next attempt will get it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing here is worth failing a launch over.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It failed its checksum and it is inert either way.
        }
    }

    private void Report(string stage, double fraction) =>
        Progress?.Invoke(this, new UpdateProgress(stage, Math.Clamp(fraction, 0, 1)));
}
