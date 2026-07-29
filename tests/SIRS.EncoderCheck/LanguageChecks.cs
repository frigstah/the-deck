using System.Text.Json;
using Sirs.Core.Audio;
using Sirs.Core.Localisation;
using Sirs.Core.Streaming;
using Sirs.Core.Updates;

namespace Sirs.EncoderCheck;

/// <summary>
/// The translation framework (I8) and the update check (I9).
/// <para>
/// The case that matters is the partial translation. Community translations are always partly done,
/// and a framework that shows an identifier where a translation is missing is one nobody will finish
/// - so falling back to English has to be provably automatic.
/// </para>
/// </summary>
internal static class LanguageChecks
{
    public static int Run()
    {
        var failures = 0;
        var folder = Strings.Directory;
        var written = new List<string>();

        try
        {
            failures += Check("English is the reference and covers every id", () =>
            {
                Expect(Strings.English.Count > 0, "the English catalogue is empty");

                foreach (var (id, text) in Strings.English)
                {
                    Expect(!string.IsNullOrWhiteSpace(text), $"\"{id}\" has no English text");
                }
            });

            failures += Check("the exported template holds every id", () =>
            {
                var json = Strings.ExportTemplate("xx", "Test");
                using var document = JsonDocument.Parse(json);

                var entries = document.RootElement.GetProperty("Entries");
                var count = entries.EnumerateObject().Count();

                Expect(count == Strings.English.Count,
                    $"the template holds {count} entries against {Strings.English.Count} in the catalogue");
            });

            failures += Check("a translation is used where it has one", () =>
            {
                var path = WritePack(folder, "tl", "Testish", new Dictionary<string, string>
                {
                    [StringId.AdviceGood] = "Suena bien",
                    [StringId.StateLive] = "AL AIRE",
                });

                written.Add(path);

                Strings.Use("tl");

                ExpectText(LevelAdvice.Good.Headline(), "Suena bien");
                ExpectText(StreamState.Live.Headline(), "AL AIRE");
            });

            failures += Check("anything untranslated falls back to English", () =>
            {
                Strings.Use("tl");

                // Deliberately absent from the pack above.
                ExpectText(LevelAdvice.TooQuiet.Headline(), "Too quiet");
                ExpectText(StreamState.Failed.Headline(), "Could not connect");
            });

            failures += Check("coverage is reported honestly", () =>
            {
                var pack = Strings.Available().First(p => p.Code == "tl");
                var coverage = pack.Coverage(Strings.English.Keys.ToList());

                var expected = 2.0 / Strings.English.Count;
                Expect(Math.Abs(coverage - expected) < 0.001,
                    $"coverage reported as {coverage:P1}, expected {expected:P1} for two translated entries");

                Expect(Strings.MissingIds(pack).Count == Strings.English.Count - 2,
                    "the missing-id list does not match the coverage figure");
            });

            failures += Check("ids a pack invented are reported, not used", () =>
            {
                var path = WritePack(folder, "tj", "Junk", new Dictionary<string, string>
                {
                    ["not.a.real.id"] = "nonsense",
                    [StringId.AdviceGood] = "Fine",
                });

                written.Add(path);

                var pack = Strings.Available().First(p => p.Code == "tj");
                var unknown = Strings.UnknownIds(pack);

                Expect(unknown.Count == 1 && unknown[0] == "not.a.real.id",
                    $"unknown ids came back as [{string.Join(", ", unknown)}]");
            });

            failures += Check("an unknown language falls back to English", () =>
            {
                Strings.Use("this-language-does-not-exist");

                Expect(Strings.CurrentCode == "en", $"SIRS switched to \"{Strings.CurrentCode}\"");
                ExpectText(LevelAdvice.Good.Headline(), "Sounds good");
            });

            failures += Check("a corrupt language file is ignored rather than fatal", () =>
            {
                var path = Path.Combine(folder, "tbroken.json");
                File.WriteAllText(path, "{ this is not json");
                written.Add(path);

                // Available() must still work, simply without the broken file.
                var packs = Strings.Available();
                Expect(packs.Any(p => p.Code == "en"), "English disappeared when a file would not parse");
                Expect(packs.All(p => p.Code != "tbroken"), "a file that would not parse was offered anyway");
            });

            failures += Check("formatted text keeps its placeholders", () =>
            {
                Strings.Use("en");

                var text = Strings.Get(StringId.ListenerMany, 42);
                Expect(text == "42 listeners", $"got \"{text}\"");
            });

            // ---------------------------------------------------------------- updates (I9)

            failures += Check("the update check never offers to run anything", () =>
            {
                // Only http(s) pages are ever opened, so a feed cannot point SIRS at a local file
                // or an installer and have it followed.
                Expect(!UpdateChecker.CanOpen(null), "a missing release was treated as openable");

                Expect(!UpdateChecker.CanOpen(new ReleaseInfo { Url = "file:///C:/Windows/System32/cmd.exe" }),
                    "a file:// URL was treated as openable");

                Expect(!UpdateChecker.CanOpen(new ReleaseInfo { Url = "javascript:alert(1)" }),
                    "a javascript: URL was treated as openable");

                Expect(UpdateChecker.CanOpen(new ReleaseInfo { Url = "https://example.com/releases" }),
                    "an ordinary https page was refused");
            });

            failures += Check("a feed that does not answer is reported, not swallowed", () =>
            {
                // The default feed points at an .invalid host, which cannot resolve by definition -
                // so this exercises the failure path without reaching anything real.
                var checker = new UpdateChecker { FeedUrl = UpdateChecker.DefaultFeedUrl };
                var result = checker.CheckAsync().GetAwaiter().GetResult();

                Expect(!result.Available, "an unreachable feed reported an update");
                Expect(result.Summary.Contains("could not check", StringComparison.OrdinalIgnoreCase),
                    $"the failure was reported as \"{result.Summary}\"");
                Expect(checker.LastChecked is not null, "the attempt was not recorded");
            });

            failures += Check("the running version is readable", () =>
            {
                var version = UpdateChecker.CurrentVersion;
                Expect(version.Major >= 1, $"the running version reads as {version}");
            });

            return failures;
        }
        finally
        {
            Strings.Use("en");

            foreach (var path in written)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A leftover test file is not worth failing the run over.
                }
            }
        }
    }

    private static string WritePack(string folder, string code, string name, Dictionary<string, string> entries)
    {
        var path = Path.Combine(folder, $"{code}.json");
        var json = JsonSerializer.Serialize(
            new { Code = code, Name = name, Entries = entries },
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(path, json);
        return path;
    }

    private static void ExpectText(string actual, string expected)
    {
        if (actual != expected) throw new Exception($"got \"{actual}\", expected \"{expected}\"");
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
