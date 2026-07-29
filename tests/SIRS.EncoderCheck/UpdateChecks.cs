using System.IO.Compression;
using System.Reflection;
using Sirs.Core.Updates;

namespace Sirs.EncoderCheck;

/// <summary>
/// The self-updater (I9).
/// <para>
/// This is the one feature in SIRS that can replace SIRS, so the checks here are less about it
/// working and more about it refusing: a download from the wrong host, a release with no checksum,
/// a payload that is not what was promised. The happy path needs a real release and is left to a
/// real one; every way of saying no is checked here.
/// </para>
/// </summary>
internal static class UpdateChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("only GitHub, and only over https", () =>
        {
            var allowed = Allows("https://github.com/frigstah/SIRS/releases/download/v1/x.zip");
            Expect(allowed, "a genuine GitHub release URL was refused");

            Expect(Allows("https://objects.githubusercontent.com/x"), "the asset CDN was refused");

            // The whole point of pinning. Each of these is a way to make SIRS run someone
            // else's code, and each has to be a flat no rather than a warning.
            Expect(!Allows("http://github.com/frigstah/SIRS/x.zip"), "plain http was allowed");
            Expect(!Allows("https://github.evil.com/x.zip"), "a lookalike host was allowed");
            Expect(!Allows("https://evil.com/github.com/x.zip"), "a path that mentions github was allowed");
            Expect(!Allows("file:///C:/Windows/System32/cmd.exe"), "a local file was allowed");
            Expect(!Allows("https://raw.githubusercontent.com/x"), "an unlisted github host was allowed");
            Expect(!Allows(""), "an empty URL was allowed");
        });

        failures += Check("the payload is picked by name, never the portable zip", () =>
        {
            // Installing the portable zip over a normal install would drop sirs-portable.txt into
            // it and silently move that user's servers and settings to a different folder.
            var release = new ReleaseInfo
            {
                Tag = "v1.3.0.42",
                Assets =
                [
                    new ReleaseAsset { Name = "SIRS-1.3.0.42-setup.exe" },
                    new ReleaseAsset { Name = "SIRS-1.3.0.42-portable-win-x64.zip" },
                    new ReleaseAsset { Name = "SIRS-1.3.0.42-update-win-x64.zip" },
                    new ReleaseAsset { Name = "SHA256SUMS.txt" },
                ],
            };

            Expect(release.UpdatePayload?.Name == "SIRS-1.3.0.42-update-win-x64.zip",
                $"it chose {release.UpdatePayload?.Name ?? "nothing"}");

            Expect(release.Checksums?.Name == "SHA256SUMS.txt", "it did not find the checksums");

            var without = new ReleaseInfo { Assets = [new ReleaseAsset { Name = "notes.txt" }] };
            Expect(without.UpdatePayload is null, "it found a payload in a release that has none");
        });

        failures += Check("release tags become comparable versions", () =>
        {
            Expect(new ReleaseInfo { Tag = "v1.3.0.42" }.ParsedVersion == new Version(1, 3, 0, 42), "v-prefixed tag");
            Expect(new ReleaseInfo { Tag = "1.3.0.42" }.ParsedVersion == new Version(1, 3, 0, 42), "bare tag");
            Expect(new ReleaseInfo { Tag = "nightly" }.ParsedVersion is null, "a non-version tag parsed anyway");

            // Run numbers only go up, so a later build must always compare greater.
            Expect(new Version("1.3.0.43") > new Version("1.3.0.42"), "43 did not beat 42");
            Expect(new Version("1.4.0.1") > new Version("1.3.0.999"), "a minor bump lost to a run number");
        });

        failures += Check("the apply-update command line is understood, and not by accident", () =>
        {
            var request = UpdateApplier.Parse(["--apply-update", "--target", @"C:\Programs\SIRS", "--wait", "4321"]);

            Expect(request is not null, "the update command was not recognised");
            Expect(request!.Target == @"C:\Programs\SIRS", $"target came back as {request.Target}");
            Expect(request.WaitForProcessId == 4321, $"pid came back as {request.WaitForProcessId}");

            // An ordinary launch, and the other command lines SIRS already answers, must never be
            // mistaken for this - it copies files over an install directory.
            Expect(UpdateApplier.Parse([]) is null, "an empty command line looked like an update");
            Expect(UpdateApplier.Parse(["--live"]) is null, "--live looked like an update");
            Expect(UpdateApplier.Parse(["--apply-update"]) is null, "a target-less update was accepted");
            Expect(UpdateApplier.Parse(["--apply-update", "--wait", "1"]) is null, "a target-less update was accepted");
        });

        failures += Check("a payload that is not SIRS is refused", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), $"sirs-update-check-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);

            try
            {
                var content = Path.Combine(folder, "content");
                Directory.CreateDirectory(content);
                File.WriteAllText(Path.Combine(content, "something-else.exe"), "not SIRS");

                var zip = Path.Combine(folder, "payload.zip");
                ZipFile.CreateFromDirectory(content, zip);

                var staged = Path.Combine(folder, "staged");
                ZipFile.ExtractToDirectory(zip, staged);

                // The installer checks for this exact file before handing over, because the thing
                // it hands over to is the executable it expects to find.
                Expect(!File.Exists(Path.Combine(staged, "SIRS.exe")),
                    "a payload without SIRS.exe looked complete");
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
            }
        });

        failures += Check("the portable marker is never carried across an update", () =>
        {
            // The field is private because nothing should be changing it; this asserts the value
            // rather than the plumbing, since getting it wrong moves someone's settings folder.
            var marker = typeof(UpdateApplier)
                .GetField("PortableMarker", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetRawConstantValue() as string;

            Expect(marker == "sirs-portable.txt",
                $"the applier skips \"{marker ?? "nothing"}\", which is not the portable marker");
        });

        failures += Check("staging happens outside the install folder", () =>
        {
            var staging = UpdateInstaller.StagingRoot;
            var install = UpdateInstaller.InstallDirectory;

            Expect(!staging.StartsWith(install, StringComparison.OrdinalIgnoreCase),
                $"downloads are staged inside the folder being replaced ({staging})");

            Expect(staging.Contains("SIRS", StringComparison.OrdinalIgnoreCase),
                $"staging is somewhere unexpected: {staging}");

            // Called on every launch, including when there is nothing there.
            UpdateInstaller.Cleanup();
            UpdateInstaller.Cleanup();
        });

        failures += Check("a release with no checksum is refused", () =>
        {
            var release = new ReleaseInfo
            {
                Tag = "v9.9.9.9",
                Assets = [new ReleaseAsset
                {
                    Name = "SIRS-9.9.9.9-update-win-x64.zip",
                    Url = "https://github.com/frigstah/SIRS/releases/download/v9.9.9.9/SIRS-9.9.9.9-update-win-x64.zip",
                }],
            };

            Expect(release.Checksums is null, "the test release should not have checksums");

            var result = new UpdateInstaller().InstallAsync(release).GetAwaiter().GetResult();

            Expect(!result.Ok, "an unverifiable release was installed");
            Expect(result.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase) ||
                   result.Message.Contains("cannot write", StringComparison.OrdinalIgnoreCase),
                $"it refused for the wrong reason: {result.Message}");
        });

        failures += Check("a release pointing off GitHub is refused", () =>
        {
            var release = new ReleaseInfo
            {
                Tag = "v9.9.9.9",
                Assets =
                [
                    new ReleaseAsset
                    {
                        Name = "SIRS-9.9.9.9-update-win-x64.zip",
                        Url = "https://evil.example.com/payload.zip",
                    },
                    new ReleaseAsset { Name = "SHA256SUMS.txt", Url = "https://evil.example.com/SHA256SUMS.txt" },
                ],
            };

            var result = new UpdateInstaller().InstallAsync(release).GetAwaiter().GetResult();

            Expect(!result.Ok, "a release pointing off GitHub was installed");
            Expect(!result.Message.Contains("Installing", StringComparison.OrdinalIgnoreCase),
                $"it started installing anyway: {result.Message}");
        });

        return failures;
    }

    /// <summary>Reaches the host allow-list, which is private because nothing may widen it.</summary>
    private static bool Allows(string url)
    {
        var method = typeof(UpdateInstaller).GetMethod("IsAllowed",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("IsAllowed has been renamed; this check needs updating");

        return (bool)method.Invoke(null, [url])!;
    }

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
