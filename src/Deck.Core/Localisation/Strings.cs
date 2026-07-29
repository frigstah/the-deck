using System.Globalization;
using System.Text.Json;

namespace Deck.Core.Localisation;

/// <summary>One language: a code, a name in its own language, and whatever it has translated.</summary>
public sealed class LanguagePack
{
    public required string Code { get; init; }

    /// <summary>The language's name in that language, which is how language pickers should read.</summary>
    public required string Name { get; init; }

    public Dictionary<string, string> Entries { get; init; } = [];

    /// <summary>Where this pack came from, so the picker can say which file to edit.</summary>
    public string? SourcePath { get; set; }

    /// <summary>How much of the reference text this pack covers, 0 to 1.</summary>
    public double Coverage(IReadOnlyCollection<string> referenceIds)
    {
        if (referenceIds.Count == 0) return 1;

        var translated = referenceIds.Count(id =>
            Entries.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text));

        return (double)translated / referenceIds.Count;
    }
}

/// <summary>
/// The text catalogue (I8). English is compiled in and is the reference; every other language is a
/// JSON file anyone can write, dropped into the <c>languages</c> folder next to the settings.
/// <para>
/// Anything a pack does not translate falls back to English rather than showing an identifier, and
/// the picker shows each pack's coverage. A community translation that is 70% done is useful at 70%
/// done, and pretending otherwise is how translation efforts stall.
/// </para>
/// <para>
/// Ids are compiled in as constants (see <see cref="StringId"/>) so a typo is a build error rather
/// than a blank label someone notices six months later.
/// </para>
/// </summary>
public static class Strings
{
    private static readonly object Lock = new();
    private static LanguagePack _current = EnglishPack();

    /// <summary>The reference text. Also the fallback for anything a pack leaves untranslated.</summary>
    public static IReadOnlyDictionary<string, string> English { get; } = EnglishPack().Entries;

    public static LanguagePack Current => _current;

    public static string CurrentCode => _current.Code;

    /// <summary>Raised when the language changes, so views can refresh what they have already drawn.</summary>
    public static event EventHandler? LanguageChanged;

    public static string Get(string id)
    {
        var pack = _current;

        if (pack.Entries.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text)) return text;

        // Falling back to English beats showing an id. A missing entry is a gap in a translation,
        // not a failure the user should have to look at.
        return English.TryGetValue(id, out var fallback) ? fallback : id;
    }

    public static string Get(string id, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(id), arguments);

    /// <summary>Where translations live: one JSON file per language.</summary>
    public static string Directory => AppPaths.LanguageDirectory;

    /// <summary>English plus every pack found on disk, English first.</summary>
    public static IReadOnlyList<LanguagePack> Available()
    {
        var packs = new List<LanguagePack> { EnglishPack() };

        try
        {
            foreach (var path in System.IO.Directory.GetFiles(Directory, "*.json"))
            {
                var pack = Load(path);
                if (pack is not null && !pack.Code.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    packs.Add(pack);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable folder just means no translations are installed.
        }

        return packs;
    }

    /// <summary>Switches language. An unknown code falls back to English rather than failing.</summary>
    public static void Use(string? code)
    {
        var wanted = string.IsNullOrWhiteSpace(code) ? "en" : code.Trim();

        var pack = wanted.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? EnglishPack()
            : Available().FirstOrDefault(p => p.Code.Equals(wanted, StringComparison.OrdinalIgnoreCase))
              ?? EnglishPack();

        lock (Lock)
        {
            if (_current.Code == pack.Code && _current.SourcePath == pack.SourcePath) return;
            _current = pack;
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Writes a starting point for a translator: every id with the English text, so translating is
    /// replacing the right-hand side of a file rather than hunting for what needs saying.
    /// </summary>
    public static string ExportTemplate(string code, string name)
    {
        var document = new LanguageFile
        {
            Code = code,
            Name = name,
            Entries = English.ToDictionary(e => e.Key, e => e.Value),
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    public static LanguagePack? Load(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize<LanguageFile>(File.ReadAllText(path));
            if (file is null || string.IsNullOrWhiteSpace(file.Code)) return null;

            return new LanguagePack
            {
                Code = file.Code,
                Name = string.IsNullOrWhiteSpace(file.Name) ? file.Code : file.Name,
                Entries = file.Entries ?? [],
                SourcePath = path,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Ids in a pack that no longer exist, so a translator can see what to delete.</summary>
    public static IReadOnlyList<string> UnknownIds(LanguagePack pack) =>
        pack.Entries.Keys.Where(id => !English.ContainsKey(id)).Order().ToList();

    /// <summary>Ids a pack has not translated yet.</summary>
    public static IReadOnlyList<string> MissingIds(LanguagePack pack) =>
        English.Keys
            .Where(id => !pack.Entries.TryGetValue(id, out var text) || string.IsNullOrWhiteSpace(text))
            .Order()
            .ToList();

    private sealed class LanguageFile
    {
        public string Code { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public Dictionary<string, string>? Entries { get; set; }
    }

    private static LanguagePack EnglishPack() => new()
    {
        Code = "en",
        Name = "English",
        Entries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- level coaching (B2)
            [StringId.AdviceNoSignal] = "No sound",
            [StringId.AdviceTooQuiet] = "Too quiet",
            [StringId.AdviceGood] = "Sounds good",
            [StringId.AdviceLoud] = "A bit loud",
            [StringId.AdviceClipping] = "Too loud — distorting",

            [StringId.AdviceNoSignalHint] =
                "Nothing is reaching Deck. Check the input is the right device, that it is plugged in, and that it is not muted in Windows.",
            [StringId.AdviceTooQuietHint] =
                "Listeners will have to turn you right up. Speak at your normal distance and raise the input level until this turns green.",
            [StringId.AdviceGoodHint] =
                "Your level is in the right range. Leave it here.",
            [StringId.AdviceLoudHint] =
                "You are close to the limit. Lower the input level a little for some headroom.",
            [StringId.AdviceClippingHint] =
                "The sound is being squashed and will crackle. Lower the input level until this turns green.",

            // ---- connection state (H3)
            [StringId.StateIdle] = "Off air",
            [StringId.StateConnecting] = "Connecting…",
            [StringId.StateLive] = "ON AIR",
            [StringId.StateReconnecting] = "Reconnecting…",
            [StringId.StateFailed] = "Could not connect",

            // ---- server setup problems (C1)
            [StringId.ServerNeedsName] = "Give this server a name so you can tell it apart from the others.",
            [StringId.ServerNeedsHost] = "Enter the server address your host gave you.",
            [StringId.ServerNeedsPort] = "The port must be a number between 1 and 65535.",
            [StringId.ServerNeedsPassword] = "Enter the broadcast password for this server.",
            [StringId.ServerNeedsMount] = "Enter the stream address, for example /live.",
            [StringId.ServerNeedsUsername] = "Enter the username. If your host did not give you one, use \"source\".",

            // ---- listeners (H4)
            [StringId.ListenerOne] = "1 listener",
            [StringId.ListenerMany] = "{0} listeners",

            // ---- updates (I9)
            [StringId.UpdateUpToDate] = "Deck is up to date.",
            [StringId.UpdateAvailable] = "Deck {0} is available. You are running {1}.",
            [StringId.UpdateCheckFailed] = "Deck could not check for updates: {0}",
            [StringId.UpdateNeverChecked] = "Not checked yet.",
        },
    };
}

/// <summary>
/// Every text id, as a constant. Compiled ids mean a mistyped one will not build, which is the only
/// reliable way to keep a catalogue and the code that uses it in step.
/// </summary>
public static class StringId
{
    public const string AdviceNoSignal = "advice.noSignal";
    public const string AdviceTooQuiet = "advice.tooQuiet";
    public const string AdviceGood = "advice.good";
    public const string AdviceLoud = "advice.loud";
    public const string AdviceClipping = "advice.clipping";

    public const string AdviceNoSignalHint = "advice.noSignal.hint";
    public const string AdviceTooQuietHint = "advice.tooQuiet.hint";
    public const string AdviceGoodHint = "advice.good.hint";
    public const string AdviceLoudHint = "advice.loud.hint";
    public const string AdviceClippingHint = "advice.clipping.hint";

    public const string StateIdle = "state.idle";
    public const string StateConnecting = "state.connecting";
    public const string StateLive = "state.live";
    public const string StateReconnecting = "state.reconnecting";
    public const string StateFailed = "state.failed";

    public const string ServerNeedsName = "server.needsName";
    public const string ServerNeedsHost = "server.needsHost";
    public const string ServerNeedsPort = "server.needsPort";
    public const string ServerNeedsPassword = "server.needsPassword";
    public const string ServerNeedsMount = "server.needsMount";
    public const string ServerNeedsUsername = "server.needsUsername";

    public const string ListenerOne = "listeners.one";
    public const string ListenerMany = "listeners.many";

    public const string UpdateUpToDate = "update.upToDate";
    public const string UpdateAvailable = "update.available";
    public const string UpdateCheckFailed = "update.failed";
    public const string UpdateNeverChecked = "update.never";
}
