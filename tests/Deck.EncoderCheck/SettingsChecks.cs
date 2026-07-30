using System.Reflection;
using System.Text.Json.Serialization;
using Deck.Core;

namespace Deck.EncoderCheck;

/// <summary>
/// The settings file, which is the one piece of state the user builds up by hand and the one place a
/// silent failure costs them everything they set.
/// <para>
/// Three ways it goes wrong, none of which shows up as an error. A new setting is added and does not
/// persist, so it works all session and forgets overnight. A setting is removed and the old key left
/// in the file stops the whole file loading, so every preference resets at once. Or the file is
/// damaged and Deck will not start. <see cref="SettingsStore"/> is written to survive all three, and
/// this is what says so.
/// </para>
/// </summary>
internal static class SettingsChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("being a strip is remembered", () =>
        {
            // Mini mode is a way of working, not a passing choice: parked along the top of a screen
            // today, wanted there again tomorrow.
            var settings = new AppSettings { MiniMode = true };
            Expect(RoundTrip(settings).MiniMode, "Deck came back as the whole deck after being left as a strip");

            Expect(!new AppSettings().MiniMode, "Deck should open as the deck until someone asks for the strip");
        });

        failures += Check("every setting survives being written and read back", () =>
        {
            // Reflected over rather than listed, so a setting added later is covered without anyone
            // remembering to come back here. The failure this catches is a property that looks saved
            // and is not - a private setter, a missing converter, a type the serialiser skips.
            //
            // What it cannot catch is a property marked ignored that should not have been, because
            // ignored is exactly what it is told to respect. That needs a check by name, like the one
            // above it - which is why the one above it exists.
            var settings = new AppSettings();
            var changed = new List<PropertyInfo>();

            foreach (var property in Persisted())
            {
                if (Vary(property, settings)) changed.Add(property);
            }

            Expect(changed.Count >= 30, $"only {changed.Count} settings could be varied - has AppSettings changed shape?");

            var reloaded = RoundTrip(settings);

            var lost = changed
                .Where(p => !Equals(p.GetValue(reloaded), p.GetValue(settings)))
                .Select(p => $"{p.Name} (saved {p.GetValue(settings)}, read back {p.GetValue(reloaded)})")
                .ToList();

            Expect(lost.Count == 0, $"did not survive the file: {string.Join(", ", lost)}");
        });

        failures += Check("a file from an older Deck still loads", () =>
        {
            // A key that no longer exists must not cost the user the keys that do. InputLocked is the
            // real case: it was a setting until the chips learned to lock themselves, and files
            // written before that still carry it.
            var path = TempFile();
            try
            {
                File.WriteAllText(path, """
                    {
                      "InputLocked": true,
                      "ThisNeverExisted": { "nested": [1, 2, 3] },
                      "MiniMode": true,
                      "SilenceAlertSeconds": 22
                    }
                    """);

                var loaded = new SettingsStore(path).Load();

                Expect(loaded.MiniMode, "a stale key in the file cost the user their other settings");
                Expect(Math.Abs(loaded.SilenceAlertSeconds - 22) < 0.001,
                    $"the silence alert came back as {loaded.SilenceAlertSeconds}, not 22");
            }
            finally
            {
                File.Delete(path);
            }
        });

        failures += Check("a damaged file costs preferences, not the session", () =>
        {
            var path = TempFile();
            try
            {
                // Half a file, which is what a power cut during a save used to leave behind.
                File.WriteAllText(path, "{ \"MiniMode\": true, \"Recordi");

                var loaded = new SettingsStore(path).Load();

                Expect(loaded is not null, "a damaged settings file stopped Deck loading at all");
                Expect(loaded!.MinimiseToTray, "the defaults did not come back after a damaged file");
            }
            finally
            {
                File.Delete(path);
            }
        });

        failures += Check("a save never leaves half a file behind", () =>
        {
            // Written to a temporary name and moved into place, so an interrupted save cannot destroy
            // the settings that were already there.
            var path = TempFile();
            try
            {
                var store = new SettingsStore(path);
                store.Save(new AppSettings { MiniMode = true, SelectedSection = 3 });

                Expect(File.Exists(path), "nothing was written");
                Expect(!File.Exists(path + ".tmp"), "the temporary file was left behind");

                var again = store.Load();
                Expect(again is { MiniMode: true, SelectedSection: 3 }, "the second save did not read back");

                store.Save(new AppSettings { MiniMode = false });
                Expect(!store.Load().MiniMode, "overwriting an existing settings file did not take");
                Expect(!File.Exists(path + ".tmp"), "the temporary file was left behind on the second save");
            }
            finally
            {
                File.Delete(path);
            }
        });

        return failures;
    }

    /// <summary>The properties that are meant to reach the file: writable, and not marked ignored.</summary>
    private static IEnumerable<PropertyInfo> Persisted() =>
        typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true })
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null);

    /// <summary>
    /// Moves a setting off whatever it currently is, so a value that failed to save is distinguishable
    /// from one that saved correctly. Returns false for anything it cannot move, and those are simply
    /// not counted rather than quietly passing.
    /// </summary>
    private static bool Vary(PropertyInfo property, AppSettings settings)
    {
        var type = property.PropertyType;
        var current = property.GetValue(settings);

        if (type == typeof(bool)) { property.SetValue(settings, !(bool)current!); return true; }
        if (type == typeof(int)) { property.SetValue(settings, (int)current! + 7); return true; }
        if (type == typeof(double)) { property.SetValue(settings, (double)current! + 1.5); return true; }
        if (type == typeof(float)) { property.SetValue(settings, (float)current! + 1.5f); return true; }
        if (type == typeof(string)) { property.SetValue(settings, "checked-" + current); return true; }
        if (type == typeof(Guid)) { property.SetValue(settings, Guid.NewGuid()); return true; }

        if (type == typeof(Guid?)) { property.SetValue(settings, Guid.NewGuid()); return true; }
        if (type == typeof(int?)) { property.SetValue(settings, ((int?)current ?? 0) + 7); return true; }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            foreach (var value in values)
            {
                if (!Equals(value, current)) { property.SetValue(settings, value); return true; }
            }
        }

        return false;
    }

    private static AppSettings RoundTrip(AppSettings settings)
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            store.Save(settings);
            return store.Load();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"deck-settings-check-{Guid.NewGuid():N}.json");

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
