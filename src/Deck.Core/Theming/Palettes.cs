using System.Globalization;

namespace Deck.Core.Theming;

/// <summary>
/// One complete set of colours: a single palette in a single brightness (I5).
/// <para>
/// Twenty colours are declared and ten are worked out from them. The declared ones are the decisions
/// - what the window is, what the accent is, what a meter segment means. The worked-out ones are the
/// same colours faded into the window: the soft fill behind a verdict, the unlit half of the meter.
/// Those have no design content of their own, and hand-writing them for every palette is how a
/// twenty-first shade of nearly-the-background gets into the product by accident.
/// </para>
/// <para>
/// Every derived colour can still be overridden, and Deck's own two faces override all of them,
/// because those were tuned by eye against the real window before the rule existed and the rule is
/// not a good enough reason to change what ships. New palettes take the rule.
/// </para>
/// </summary>
public sealed record PaletteFace
{
    public required bool Dark { get; init; }

    public required string Background { get; init; }
    public required string Surface { get; init; }
    public required string Border { get; init; }
    public required string Text { get; init; }
    public required string MutedText { get; init; }
    public required string Accent { get; init; }

    /// <summary>Text drawn on top of the accent - not always white, and never assumed to be.</summary>
    public required string OnAccent { get; init; }

    public required string Ok { get; init; }
    public required string Warn { get; init; }
    public required string Bad { get; init; }
    public required string Live { get; init; }

    /// <summary>The credit line, and nothing else. Never a status colour.</summary>
    public required string Gold { get; init; }

    public required string Rail { get; init; }
    public required string RailSelected { get; init; }
    public required string RailText { get; init; }
    public required string StatusBar { get; init; }

    public required string MeterQuiet { get; init; }
    public required string MeterGood { get; init; }
    public required string MeterLoud { get; init; }
    public required string MeterClip { get; init; }

    public string? OkSoft { get; init; }
    public string? WarnSoft { get; init; }
    public string? BadSoft { get; init; }
    public string? NeutralSoft { get; init; }
    public string? CaptionHover { get; init; }
    public string? CaptionPressed { get; init; }
    public string? MeterQuietOff { get; init; }
    public string? MeterGoodOff { get; init; }
    public string? MeterLoudOff { get; init; }
    public string? MeterClipOff { get; init; }

    /// <summary>
    /// How much of a colour survives when it is faded into the window behind it. Higher on dark,
    /// because a tint has to travel further from near-black to be seen at all than it does from
    /// near-white.
    /// </summary>
    private double SoftWeight => Dark ? 0.20 : 0.13;

    private double UnlitWeight => Dark ? 0.20 : 0.12;

    /// <summary>
    /// The quiet zone unlit is fainter still on a light window. A lit segment there is *darker* than
    /// the ground, so an unlit one carrying the usual weight draws a grey slab across the bottom
    /// two-thirds of the scale and a quiet signal looks like a loud one.
    /// </summary>
    private double QuietUnlitWeight => Dark ? 0.20 : 0.06;

    /// <summary>Every colour Theme.xaml declares, by the key it declares it under.</summary>
    public IReadOnlyDictionary<string, string> Colours() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["BackgroundColor"] = Background,
        ["SurfaceColor"] = Surface,
        ["BorderColor"] = Border,
        ["TextColor"] = Text,
        ["MutedTextColor"] = MutedText,
        ["AccentColor"] = Accent,
        ["OnAccentColor"] = OnAccent,
        ["OkColor"] = Ok,
        ["WarnColor"] = Warn,
        ["BadColor"] = Bad,
        ["LiveColor"] = Live,
        ["GoldColor"] = Gold,

        ["OkSoftColor"] = OkSoft ?? Mix(Ok, Background, SoftWeight),
        ["WarnSoftColor"] = WarnSoft ?? Mix(Warn, Background, SoftWeight),
        ["BadSoftColor"] = BadSoft ?? Mix(Bad, Background, SoftWeight),
        ["NeutralSoftColor"] = NeutralSoft ?? Mix(MutedText, Background, SoftWeight),

        ["RailColor"] = Rail,
        ["RailSelectedColor"] = RailSelected,
        ["RailTextColor"] = RailText,
        ["StatusBarColor"] = StatusBar,

        // A wash of the window's own ink rather than a fixed grey, so it stays a hint on either
        // brightness instead of a slab on one of them. Not a per-palette decision.
        ["CaptionHoverColor"] = CaptionHover ?? (Dark ? "#20FFFFFF" : "#18000000"),
        ["CaptionPressedColor"] = CaptionPressed ?? (Dark ? "#38FFFFFF" : "#30000000"),

        ["MeterQuietColor"] = MeterQuiet,
        ["MeterGoodColor"] = MeterGood,
        ["MeterLoudColor"] = MeterLoud,
        ["MeterClipColor"] = MeterClip,
        ["MeterQuietOffColor"] = MeterQuietOff ?? Mix(MeterQuiet, Background, QuietUnlitWeight),
        ["MeterGoodOffColor"] = MeterGoodOff ?? Mix(MeterGood, Background, UnlitWeight),
        ["MeterLoudOffColor"] = MeterLoudOff ?? Mix(MeterLoud, Background, UnlitWeight),
        ["MeterClipOffColor"] = MeterClipOff ?? Mix(MeterClip, Background, UnlitWeight),
    };

    /// <summary>
    /// <paramref name="weight"/> of a colour laid over its ground. Straight linear blend in sRGB,
    /// which is not how light works but is exactly how a designer picking a tint by eye works, and
    /// the tints it produces are the ones the eye expects.
    /// </summary>
    public static string Mix(string colour, string ground, double weight)
    {
        var (_, r1, g1, b1) = Parse(colour);
        var (_, r2, g2, b2) = Parse(ground);

        var r = (int)Math.Round(r2 + ((r1 - r2) * weight));
        var g = (int)Math.Round(g2 + ((g1 - g2) * weight));
        var b = (int)Math.Round(b2 + ((b1 - b2) * weight));

        return $"#FF{r:X2}{g:X2}{b:X2}";
    }

    private static (int A, int R, int G, int B) Parse(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 6) text = "FF" + text;

        if (text.Length != 8 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            throw new ArgumentException($"{hex} is not an ARGB hex colour", nameof(hex));
        }

        return ((int)(v >> 24 & 0xFF), (int)(v >> 16 & 0xFF), (int)(v >> 8 & 0xFF), (int)(v & 0xFF));
    }
}

/// <summary>
/// Every palette Deck can wear, in both brightnesses (I5).
/// <para>
/// The data lives here rather than in the WPF layer for one reason: it can be checked. The light
/// palette used to live in Theme.xaml and the dark one in a method beside the window, and the only
/// thing keeping them in step was a check that read both files as text and compared what it found.
/// That works for two. It does not work for ten.
/// </para>
/// <para>
/// Theme.xaml still declares the Deck light face, because a resource dictionary has to be valid on
/// its own for the designer and for the first frame before any code runs. It is checked against
/// <see cref="Face"/> rather than trusted.
/// </para>
/// </summary>
public static class Palettes
{
    /// <summary>What each palette is called on screen, and the single line under the picker.</summary>
    public static (string Name, string Description) Describe(DeckPalette palette) => palette switch
    {
        DeckPalette.Rose => ("Rosé", "Blush and plum, on white."),
        DeckPalette.Graphite => ("Graphite", "No colour except where colour means something."),
        DeckPalette.Arcade => ("Arcade", "Cyan on near-black, and a live lamp that shouts."),
        DeckPalette.Dragon => ("Dragon", "Charcoal, ember and gold."),
        _ => ("Deck", "The petrol teal Deck was drawn in."),
    };

    public static PaletteFace Face(DeckPalette palette, bool dark) => (palette, dark) switch
    {
        (DeckPalette.Rose, false) => RoseLight,
        (DeckPalette.Rose, true) => RoseDark,
        (DeckPalette.Graphite, false) => GraphiteLight,
        (DeckPalette.Graphite, true) => GraphiteDark,
        (DeckPalette.Arcade, false) => ArcadeLight,
        (DeckPalette.Arcade, true) => ArcadeDark,
        (DeckPalette.Dragon, false) => DragonLight,
        (DeckPalette.Dragon, true) => DragonDark,
        (_, true) => DeckDark,
        _ => DeckLight,
    };

    // ------------------------------------------------------------------ Deck
    //
    // Every derived colour is stated rather than worked out. These two were tuned against the real
    // window over months and the comments in Theme.xaml explain several of them; the blend rule
    // would move them a shade or two, which is a change nobody asked for.

    private static readonly PaletteFace DeckLight = new()
    {
        Dark = false,
        Background = "#FFF4F5F3", Surface = "#FFFFFFFF", Border = "#FFDCDED9",
        Text = "#FF1B1F23", MutedText = "#FF5E656B",
        Accent = "#FF2A6A70", OnAccent = "#FFFFFFFF",
        Ok = "#FF1F6E4C", Warn = "#FF8A5B12", Bad = "#FFC93F36", Live = "#FFC93F36",
        Gold = "#FF5E4606",
        Rail = "#FF1D2226", RailSelected = "#FF262D33", RailText = "#FF848E96", StatusBar = "#FFEAECE7",
        MeterQuiet = "#FFB8C1BE", MeterGood = "#FF2E8B62", MeterLoud = "#FFC08820", MeterClip = "#FFBE3A2E",
        OkSoft = "#FFE1EFE8", WarnSoft = "#FFF5EAD5", BadSoft = "#FFF5E1DE", NeutralSoft = "#FFE2E4DF",
        CaptionHover = "#18000000", CaptionPressed = "#30000000",
        MeterQuietOff = "#FFF1F2EF", MeterGoodOff = "#FFD6E8DE",
        MeterLoudOff = "#FFEFE1C2", MeterClipOff = "#FFF0D6D2",
    };

    private static readonly PaletteFace DeckDark = new()
    {
        Dark = true,
        Background = "#FF15181B", Surface = "#FF1C2024", Border = "#FF2C3238",
        Text = "#FFE7E9E7", MutedText = "#FF939BA2",
        Accent = "#FF5FB6B4", OnAccent = "#FF10211F",
        Ok = "#FF57C295", Warn = "#FFDFA84A", Bad = "#FFE8574C", Live = "#FFE8574C",
        Gold = "#FFE3C264",
        Rail = "#FF0F1215", RailSelected = "#FF1A2025", RailText = "#FF79828A", StatusBar = "#FF101317",
        MeterQuiet = "#FF6E7A78", MeterGood = "#FF3F9E76", MeterLoud = "#FFD7A64A", MeterClip = "#FFDD5A4F",
        OkSoft = "#FF17352A", WarnSoft = "#FF33290F", BadSoft = "#FF3A1F1D", NeutralSoft = "#FF23282C",
        CaptionHover = "#20FFFFFF", CaptionPressed = "#38FFFFFF",
        MeterQuietOff = "#FF262B2E", MeterGoodOff = "#FF1E332B",
        MeterLoudOff = "#FF332C1C", MeterClipOff = "#FF351F1D",
    };

    // ------------------------------------------------------------------ Rosé
    //
    // The cards stay white and the plum stays on the rail, which is what makes this read as clean
    // first and pink second. The meter's good zone runs in the accent rather than in green: on this
    // palette a green bar is the only thing on screen that belongs to another product.

    private static readonly PaletteFace RoseLight = new()
    {
        Dark = false,
        Background = "#FFFBF3F5", Surface = "#FFFFFFFF", Border = "#FFEFDCE2",
        Text = "#FF3A2A31", MutedText = "#FF7E5866",
        Accent = "#FFC0396B", OnAccent = "#FFFFFFFF",
        Ok = "#FF1F6E4C", Warn = "#FF8A5B12", Bad = "#FFC0392B", Live = "#FFC0396B",
        Gold = "#FF5F460C",
        Rail = "#FF4B1528", RailSelected = "#FF63213B", RailText = "#FFC79AA8", StatusBar = "#FFF5E8EC",
        MeterQuiet = "#FFB9A6AD", MeterGood = "#FFC0396B", MeterLoud = "#FFD89A2E", MeterClip = "#FFC0392B",
    };

    private static readonly PaletteFace RoseDark = new()
    {
        Dark = true,
        Background = "#FF1A1216", Surface = "#FF23191F", Border = "#FF3A2A32",
        Text = "#FFF3E7EC", MutedText = "#FFB79AA6",
        Accent = "#FFF2799F", OnAccent = "#FF2A0F1B",
        Ok = "#FF57C295", Warn = "#FFDFA84A", Bad = "#FFE8574C", Live = "#FFD4477A",
        Gold = "#FFE3C264",
        Rail = "#FF120A0E", RailSelected = "#FF26161D", RailText = "#FF9E8490", StatusBar = "#FF140C10",
        MeterQuiet = "#FF6E5C64", MeterGood = "#FFF2799F", MeterLoud = "#FFDFA84A", MeterClip = "#FFE8574C",
    };

    // ------------------------------------------------------------------ Graphite
    //
    // Even going on air is graphite. The only hues left on the window are the ones that carry a
    // reading, which is the whole idea: on this palette anything coloured is information.

    private static readonly PaletteFace GraphiteLight = new()
    {
        Dark = false,
        Background = "#FFF0F0EF", Surface = "#FFFAFAF9", Border = "#FFDBDBD8",
        Text = "#FF1F1F1E", MutedText = "#FF63635E",
        Accent = "#FF3D3D3B", OnAccent = "#FFFFFFFF",
        Ok = "#FF1F6E4C", Warn = "#FF8A5B12", Bad = "#FFC0392B", Live = "#FFC0392B",
        Gold = "#FF5E4606",
        Rail = "#FF1B1B1A", RailSelected = "#FF2E2E2C", RailText = "#FF8F8F8D", StatusBar = "#FFE7E7E5",
        MeterQuiet = "#FFB8B8B4", MeterGood = "#FF6E6E6A", MeterLoud = "#FFA8935C", MeterClip = "#FFC0392B",
    };

    private static readonly PaletteFace GraphiteDark = new()
    {
        Dark = true,
        Background = "#FF17191C", Surface = "#FF1F2226", Border = "#FF2E3238",
        Text = "#FFE4E6E8", MutedText = "#FF9BA2A9",
        Accent = "#FFA9B5C1", OnAccent = "#FF14171A",
        Ok = "#FF57C295", Warn = "#FFDFA84A", Bad = "#FFE8574C", Live = "#FFE8574C",
        Gold = "#FFE3C264",
        Rail = "#FF0F1113", RailSelected = "#FF1C2024", RailText = "#FF868E96", StatusBar = "#FF101317",
        MeterQuiet = "#FF4E5860", MeterGood = "#FF8B98A4", MeterLoud = "#FFD7A64A", MeterClip = "#FFDD5A4F",
    };

    // ------------------------------------------------------------------ Arcade
    //
    // The light face is not the dark one on white. Neon cyan on a pale window is 1,6:1 and unreadable,
    // so it becomes a deep teal that keeps the idea and can actually be seen; the shouting is left to
    // the live lamp, which is the one thing on the deck that has earned it.

    private static readonly PaletteFace ArcadeLight = new()
    {
        Dark = false,
        Background = "#FFF1F4FA", Surface = "#FFFFFFFF", Border = "#FFD9E0EC",
        Text = "#FF10141C", MutedText = "#FF5B6577",
        Accent = "#FF0E7490", OnAccent = "#FFFFFFFF",
        Ok = "#FF1F6E4C", Warn = "#FF8A5B12", Bad = "#FFC2185B", Live = "#FFC2185B",
        Gold = "#FF5E4606",
        Rail = "#FF0B1220", RailSelected = "#FF16203A", RailText = "#FF8A94A6", StatusBar = "#FFE7ECF5",
        MeterQuiet = "#FFA9B3C4", MeterGood = "#FF0E7490", MeterLoud = "#FFD89A2E", MeterClip = "#FFC2185B",
    };

    private static readonly PaletteFace ArcadeDark = new()
    {
        Dark = true,
        Background = "#FF0B0E14", Surface = "#FF121620", Border = "#FF232A38",
        Text = "#FFDDE4F2", MutedText = "#FF8791A6",
        Accent = "#FF35D6EE", OnAccent = "#FF04141A",
        Ok = "#FF3DDC97", Warn = "#FFE0B341", Bad = "#FFFF3B6B", Live = "#FFFF3B6B",
        Gold = "#FFE3C264",
        Rail = "#FF06080E", RailSelected = "#FF141B2A", RailText = "#FF737F96", StatusBar = "#FF080B11",
        MeterQuiet = "#FF566072", MeterGood = "#FF35D6EE", MeterLoud = "#FFE0B341", MeterClip = "#FFFF3B6B",
    };

    // ------------------------------------------------------------------ Dragon
    //
    // Gold is not the accent - it is the loud zone of the meter, so pushing the level hot turns the
    // scale to treasure. The light face is parchment rather than white: the same firelight, in
    // daylight.

    private static readonly PaletteFace DragonLight = new()
    {
        Dark = false,
        Background = "#FFF6F0E6", Surface = "#FFFFFCF6", Border = "#FFE3D8C4",
        Text = "#FF2B211A", MutedText = "#FF73604B",
        Accent = "#FFA34A16", OnAccent = "#FFFFFFFF",
        Ok = "#FF1F6E4C", Warn = "#FF8A5B12", Bad = "#FFB3301F", Live = "#FFB3301F",
        Gold = "#FF5C460C",
        Rail = "#FF2A1B10", RailSelected = "#FF3C2716", RailText = "#FFA08D74", StatusBar = "#FFEFE7D8",
        MeterQuiet = "#FFBFB1A0", MeterGood = "#FFA34A16", MeterLoud = "#FFC9A227", MeterClip = "#FFB3301F",
    };

    private static readonly PaletteFace DragonDark = new()
    {
        Dark = true,
        Background = "#FF17110E", Surface = "#FF201814", Border = "#FF392A21",
        Text = "#FFF0E5D8", MutedText = "#FFA99177",
        Accent = "#FFE0762F", OnAccent = "#FF241008",
        Ok = "#FF57C295", Warn = "#FFE3C264", Bad = "#FFE8574C", Live = "#FFE8574C",
        Gold = "#FFE3C264",
        Rail = "#FF0D0907", RailSelected = "#FF241811", RailText = "#FF8F7867", StatusBar = "#FF100B08",
        MeterQuiet = "#FF7A6A5C", MeterGood = "#FFE0762F", MeterLoud = "#FFE3C264", MeterClip = "#FFE8574C",
    };
}
