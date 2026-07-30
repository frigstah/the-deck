using System.Text.RegularExpressions;

namespace Deck.EncoderCheck;

/// <summary>
/// Catches the bits of SIRS that the rebrand missed.
/// <para>
/// The Deck is a fork, and a rebrand done by hand leaves strays. Namespaces and assembly names fail
/// loudly if they are wrong, but a name baked into a <em>string</em> compiles perfectly and only
/// shows up when somebody looks: the session log was still being written as "sirs-2026-07-30.log",
/// the server export still offered to save "sirs-servers.json", and the update installer's
/// write-probe still dropped a ".sirs-write-test" file into the install folder. All three were found
/// by reading a log directory, not by any test.
/// </para>
/// <para>
/// So this reads the source as text. Being about names rather than behaviour, it cannot be checked
/// any other way - and a scan is the only thing that will catch the next one.
/// </para>
/// <para>
/// It missed one anyway, and the miss is instructive twice over. The installer inherited SIRS's AppId,
/// which to Windows <em>is</em> a product's identity - so setup looked that id up, found SIRS's
/// installation, ignored its own DefaultDirName and offered to install The Deck into a folder called
/// SIRS, on top of it. The scan could not have caught it: it read only <c>src</c>, and the stray was
/// not the word SIRS but a GUID. Both of those are fixed below - the scan reaches the installer and the
/// packaging script now, and the one value that carries the old identity without spelling it is named.
/// </para>
/// </summary>
internal static class LineageChecks
{
    /// <summary>
    /// SIRS's AppId, named as a forbidden value rather than left to a person to notice. It is the one
    /// piece of the old product that can be copied into the new one while reading as meaningless hex.
    /// </summary>
    private const string SirsAppId = "8D3F1C6E-5A47-4C2B-9E88-1B7A2F0D6C13";

    /// <summary>
    /// The places the old name is meant to survive, each with the reason. Anything else is a miss.
    /// Keyed by file so a stray somewhere new is caught even in a file that has one allowed mention.
    /// </summary>
    private static readonly (string File, string Snippet, string Why)[] Deliberate =
    [
        (
            Path.Combine("src", "Deck.Core", "Servers", "SecretProtector.cs"),
            "SIRS.ServerPassword.v1",
            "DPAPI entropy, which only has to match whatever encrypted the value - changing it would " +
            "make every saved password undecryptable, including for someone migrating from SIRS"
        ),
    ];

    public static int Run()
    {
        var failures = 0;

        var root = FindRepositoryRoot();
        if (root is null)
        {
            Console.WriteLine("  FAIL  could not find the repository root from the test binary");
            return 1;
        }

        // Source, plus the two places that decide what a user's machine ends up calling this program.
        // The docs talk about the lineage on purpose, and git's own files are history.
        var files = new[] { "src", "installer", "build" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".iss", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f)
            .ToList();

        failures += Check("there is source to scan", () =>
            files.Count > 40 ? null : $"only found {files.Count} source files - has the layout moved?");

        // Anything that would reach a user: a filename, a path, a window title, a registry key.
        var strays = new List<string>();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file);
            var text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(text, @"""[^""\r\n]*sirs[^""\r\n]*""", RegexOptions.IgnoreCase))
            {
                if (IsDeliberate(relative, match.Value)) continue;

                strays.Add($"{relative}: {match.Value}");
            }
        }

        failures += Check("no string literal still says SIRS", () => strays.Count == 0
            ? null
            : "found " + strays.Count + ":\n          " + string.Join("\n          ", strays));

        // The comments are allowed to discuss the fork - and should - but the identity must be Deck's.
        failures += Check("the settings folder is Deck's own", () =>
        {
            var appPaths = File.ReadAllText(Path.Combine(root, "src", "Deck.Core", "AppPaths.cs"));
            var match = Regex.Match(appPaths, @"ApplicationData\s*\)\s*,\s*""([^""]+)""");

            if (!match.Success) return "could not find the settings folder name in AppPaths";

            // Guarded because a careless rebrand sed once pointed this at "%APPDATA%\The Deck" while
            // every other path still said "Deck", and the two disagreeing is invisible until a user
            // wonders where their servers went.
            return match.Groups[1].Value == "Deck"
                ? null
                : $"settings folder is \"{match.Groups[1].Value}\", expected \"Deck\"";
        });

        failures += Check("the installer does not claim to be SIRS", () =>
        {
            // An AppId is a product's identity to Windows, and sharing one does not read as an error
            // anywhere - it reads as an upgrade. Deck inherited this and setup duly offered to install
            // it into SIRS's folder, over the top of SIRS, under SIRS's uninstaller.
            var script = Path.Combine(root, "installer", "Deck.iss");
            if (!File.Exists(script)) return "there is no installer script to check";

            var text = File.ReadAllText(script);
            var match = Regex.Match(text, @"^\s*AppId\s*=\s*\{*([0-9A-Fa-f\-]{36})", RegexOptions.Multiline);

            if (!match.Success) return "could not find an AppId in the installer script";

            if (string.Equals(match.Groups[1].Value, SirsAppId, StringComparison.OrdinalIgnoreCase))
            {
                return "the installer still uses SIRS's AppId, so Windows treats The Deck as SIRS and " +
                       "setup will install it into SIRS's folder";
            }

            // Also guard the visible names, which is what someone reads on the folder page.
            foreach (var key in new[] { "AppName", "DefaultDirName", "DefaultGroupName" })
            {
                var line = Regex.Match(text, $@"^\s*{key}\s*=\s*(.+)$", RegexOptions.Multiline);

                if (line.Success && line.Groups[1].Value.Contains("SIRS", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{key} still says SIRS: {line.Groups[1].Value.Trim()}";
                }
            }

            return null;
        });

        failures += Check("the deliberate exceptions are all still there", () =>
        {
            var lost = Deliberate
                .Where(d => !File.Exists(Path.Combine(root, d.File)) ||
                            !File.ReadAllText(Path.Combine(root, d.File)).Contains(d.Snippet, StringComparison.Ordinal))
                .Select(d => $"{d.File} no longer contains \"{d.Snippet}\" - {d.Why}")
                .ToList();

            // Worth failing on: dropping one of these silently is a data-loss bug, not a tidy-up.
            return lost.Count == 0 ? null : string.Join("; ", lost);
        });

        return failures;
    }

    private static bool IsDeliberate(string relativePath, string literal) => Deliberate.Any(d =>
        string.Equals(d.File, relativePath, StringComparison.OrdinalIgnoreCase) &&
        literal.Contains(d.Snippet, StringComparison.Ordinal));

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Deck.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static int Check(string name, Func<string?> verify)
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
}
