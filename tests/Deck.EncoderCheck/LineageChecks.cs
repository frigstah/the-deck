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
/// </summary>
internal static class LineageChecks
{
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

        // Source only. The docs talk about the lineage on purpose, and git's own files are history.
        var files = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
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
