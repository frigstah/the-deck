using System.Globalization;
using System.Text.RegularExpressions;
using Deck.Core;
using Deck.Core.Theming;

namespace Deck.EncoderCheck;

/// <summary>
/// Every palette Deck can wear (I5), checked as numbers rather than as pixels.
/// <para>
/// There used to be two palettes: one in Theme.xaml and one in a method beside the window, kept in
/// step by a check that read both files as text. There are now ten, so the colours moved into
/// <see cref="Palettes"/> where they can simply be asked. Theme.xaml still declares the Deck light
/// face - a resource dictionary has to be valid on its own before any code runs - and the first
/// check below is what stops that copy drifting from the real one.
/// </para>
/// <para>
/// Contrast is measured rather than judged. The Deck is meant to be readable across a room, and a
/// palette that was picked for how it feels can be a shade short of legible in places nobody looks
/// at often. The figures come from the WCAG relative-luminance formula.
/// </para>
/// </summary>
internal static class ThemeChecks
{
    /// <summary>Body text. WCAG AA for normal-size text.</summary>
    private const double BodyMinimum = 4.5;

    /// <summary>Large text and non-text indicators - meter segments, rules, chrome.</summary>
    private const double LargeMinimum = 3.0;

    public static int Run()
    {
        var failures = 0;

        var root = FindRepositoryRoot();
        if (root is null)
        {
            Console.WriteLine("  FAIL could not find the repository root from the test binary");
            return 1;
        }

        var themeXaml = File.ReadAllText(Path.Combine(root, "src", "Deck.App", "Theme.xaml"));
        var declared = ParseXaml(themeXaml);

        failures += Check("Theme.xaml still says what the Deck palette says", () =>
        {
            // The one place two copies of a colour still exist, and the reason they do is that WPF
            // needs a dictionary it can realise before Main runs. So it is checked value by value:
            // an edit to either one that is not made to the other fails here rather than showing up
            // as a single stubbornly wrong element on somebody's screen.
            var face = Palettes.Face(DeckPalette.Deck, dark: false).Colours();

            Expect(declared.Count > 20, $"only {declared.Count} colours found in Theme.xaml - has the format changed?");

            var missing = face.Keys.Except(declared.Keys).OrderBy(k => k).ToList();
            var extra = declared.Keys.Except(face.Keys).OrderBy(k => k).ToList();

            Expect(missing.Count == 0, $"in the Deck palette but not in Theme.xaml: {string.Join(", ", missing)}");
            Expect(extra.Count == 0, $"in Theme.xaml but not in any palette: {string.Join(", ", extra)}");

            var different = face.Keys
                .Where(k => !string.Equals(face[k], declared[k], StringComparison.OrdinalIgnoreCase))
                .Select(k => $"{k} is {declared[k]} in Theme.xaml and {face[k]} in Palettes")
                .ToList();

            Expect(different.Count == 0, string.Join("; ", different));
        });

        failures += Check("every palette declares every colour, and all of them are readable", () =>
        {
            var expected = declared.Keys.OrderBy(k => k).ToList();

            foreach (var (name, palette) in Faces())
            {
                Expect(palette.Keys.OrderBy(k => k).SequenceEqual(expected),
                    $"{name} does not declare the same colours as Theme.xaml");

                foreach (var (key, value) in palette)
                {
                    Expect(TryParse(value, out _), $"{name}: {key} = \"{value}\" is not an ARGB hex value");
                }
            }
        });

        // ---------------------------------------------------------------- legibility

        failures += Check("text is legible on every palette", () =>
        {
            foreach (var (name, palette) in Faces())
            {
                var ratio = Contrast(palette["TextColor"], palette["BackgroundColor"]);
                Expect(ratio >= BodyMinimum, $"{name}: text on the window is {ratio:0.0}:1, needs {BodyMinimum}");

                var onSurface = Contrast(palette["TextColor"], palette["SurfaceColor"]);
                Expect(onSurface >= BodyMinimum, $"{name}: text on a chip is {onSurface:0.0}:1, needs {BodyMinimum}");
            }
        });

        failures += Check("hints and readouts are legible, not just present", () =>
        {
            // Muted text carries every hint under every setting row, and the footer readouts along
            // the bottom of the deck. It is small and it is prose, so it is held to the body
            // threshold rather than the large-text one - on the window, on a chip, and on the status
            // strip, which is a different colour from both and was where the first four palettes
            // fell short.
            foreach (var (name, palette) in Faces())
            {
                foreach (var ground in new[] { "BackgroundColor", "SurfaceColor", "StatusBarColor" })
                {
                    var ratio = Contrast(palette["MutedTextColor"], palette[ground]);
                    Expect(ratio >= BodyMinimum,
                        $"{name}: hint text on {ground} is {ratio:0.0}:1, needs {BodyMinimum}");
                }
            }
        });

        failures += Check("the credit line is readable, and still gold", () =>
        {
            // It is small text, so it is held to the body standard rather than waved through as
            // decoration - a credit nobody can read is not much of a credit. Palettes carry
            // genuinely different golds because the metallic shade people picture is a pale yellow
            // on a white window; this is the check that stops anyone "tidying" them into one value.
            //
            // Measured at the bottom of the pulse, not the top, which is the whole reason this check
            // earns its place: the first gold read 4.6:1 at full strength and 2.4:1 at its dimmest,
            // so a line that passed every obvious test was unreadable most of the time it was on
            // screen. Keep this figure in step with the animation in MainWindow.xaml.
            const double PulseFloor = 0.80;

            foreach (var (name, palette) in Faces())
            {
                var gold = palette["GoldColor"];
                var ratio = Contrast(Fade(gold, PulseFloor), palette["BackgroundColor"]);

                Expect(ratio >= BodyMinimum,
                    $"{name}: the credit is {ratio:0.0}:1 at its dimmest, needs {BodyMinimum}");

                // Gold, not "some yellowish grey". Red and green well clear of blue is what makes it
                // read as gold rather than as another neutral.
                Expect(TryParse(gold, out var c), $"{name}: {gold} is not a colour");
                Expect(c.R > c.B + 0.15 && c.G > c.B + 0.08, $"{name}: {gold} does not read as gold");
            }
        });

        failures += Check("button text is legible on the accent", () =>
        {
            foreach (var (name, palette) in Faces())
            {
                var ratio = Contrast(palette["OnAccentColor"], palette["AccentColor"]);
                Expect(ratio >= BodyMinimum, $"{name}: text on the accent is {ratio:0.0}:1, needs {BodyMinimum}");
            }
        });

        failures += Check("a link is legible as text, not only as a button", () =>
        {
            // LinkButtonStyle draws in the accent at body size, with no fill behind it: the escape
            // hatch in the server editor, and the coffee link under Support. The accent is checked
            // above for text drawn *on* it, which is a different question from the accent itself
            // being read as words on the window - and that is the one a link asks.
            foreach (var (name, palette) in Faces())
            {
                foreach (var ground in new[] { "BackgroundColor", "SurfaceColor" })
                {
                    var ratio = Contrast(palette["AccentColor"], palette[ground]);

                    Expect(ratio >= BodyMinimum,
                        $"{name}: an accent-coloured link on {ground} is {ratio:0.0}:1, needs {BodyMinimum}");
                }
            }
        });

        failures += Check("the rail is readable, and reads as a rail", () =>
        {
            // The rail keeps its own colours on either brightness, which means nothing about the
            // window guarantees its labels can be read - it has to be checked on its own terms.
            foreach (var (name, palette) in Faces())
            {
                var ratio = Contrast(palette["RailTextColor"], palette["RailColor"]);
                Expect(ratio >= BodyMinimum, $"{name}: rail labels are {ratio:0.0}:1, needs {BodyMinimum}");

                var selected = Contrast(palette["RailSelectedColor"], palette["RailColor"]);
                Expect(selected >= 1.08,
                    $"{name}: the selected rail tab is only {selected:0.00}:1 against the rail, so it does not read as selected");
            }
        });

        failures += Check("a verdict pill's text is legible on its own fill", () =>
        {
            // The soft fills are worked out from the semantic colour on most palettes rather than
            // chosen, so this is the check that says the rule produces something usable rather than
            // a muddy tint that swallows the words on top of it.
            foreach (var (name, palette) in Faces())
            {
                foreach (var role in new[] { "Ok", "Warn", "Bad", "Neutral" })
                {
                    var ratio = Contrast(palette["TextColor"], palette[$"{role}SoftColor"]);
                    Expect(ratio >= BodyMinimum,
                        $"{name}: text on the {role.ToLowerInvariant()} pill is {ratio:0.0}:1, needs {BodyMinimum}");
                }
            }
        });

        failures += Check("a status colour is distinguishable from the window behind it", () =>
        {
            // Green fine, amber careful, red broken. They shift in tone between palettes but never in
            // meaning, and a status lamp that cannot be seen is not a status.
            foreach (var (name, palette) in Faces())
            {
                foreach (var role in new[] { "Ok", "Warn", "Bad", "Live" })
                {
                    var ratio = Contrast(palette[$"{role}Color"], palette["BackgroundColor"]);
                    Expect(ratio >= LargeMinimum,
                        $"{name}: the {role.ToLowerInvariant()} colour is {ratio:0.0}:1 on the window, needs {LargeMinimum}");
                }
            }
        });

        failures += Check("the on-air state block reads on every palette", () =>
        {
            // Always white text on the live colour, in the strip and on the deck.
            foreach (var (name, palette) in Faces())
            {
                var ratio = Contrast("#FFFFFFFF", palette["LiveColor"]);
                Expect(ratio >= LargeMinimum,
                    $"{name}: white on the on-air colour is {ratio:0.0}:1, needs {LargeMinimum}");
            }
        });

        failures += Check("a lit meter segment is distinguishable from an unlit one", () =>
        {
            // The whole meter depends on this and none of it is text, so the large-element threshold
            // applies. It is also the check that would have caught the first light palette, where the
            // quiet zone was nearly as dark as the good zone.
            foreach (var (name, palette) in Faces())
            {
                foreach (var zone in new[] { "Quiet", "Good", "Loud", "Clip" })
                {
                    var ratio = Contrast(palette[$"Meter{zone}Color"], palette[$"Meter{zone}OffColor"]);
                    Expect(ratio >= 1.6,
                        $"{name}: the {zone.ToLowerInvariant()} zone lit vs unlit is only {ratio:0.00}:1");
                }
            }
        });

        failures += Check("neighbouring meter zones are told apart", () =>
        {
            // Where one zone becomes the next is the only thing the meter has to say, so the two
            // colours either side of every boundary have to look different.
            //
            // Measured as perceptual distance rather than as contrast, and the difference matters.
            // Contrast only knows about lightness: it fails a bright cyan running into a bright
            // amber, which anybody can see is a boundary, and passes two browns of different
            // lightness, which nobody can. Written as contrast first, this check called Arcade's
            // best transition broken and let a real one through. CIE76 in Lab is crude by modern
            // standards and still right about both.
            const double Distinct = 20;

            var boundaries = new[] { ("Quiet", "Good"), ("Good", "Loud"), ("Loud", "Clip") };

            foreach (var (name, palette) in Faces())
            {
                foreach (var (below, above) in boundaries)
                {
                    var distance = Distance(palette[$"Meter{below}Color"], palette[$"Meter{above}Color"]);

                    Expect(distance >= Distinct,
                        $"{name}: the {below.ToLowerInvariant()} and {above.ToLowerInvariant()} zones are " +
                        $"only {distance:0} apart, so that part of the scale reads as one colour");
                }
            }
        });

        failures += Check("each palette's two faces are genuinely different", () =>
        {
            // A key that survived a copy-paste from the light face to the dark one is almost always
            // a slip, and it shows up as one element that ignores the theme.
            foreach (var palette in Enum.GetValues<DeckPalette>())
            {
                var light = Palettes.Face(palette, dark: false).Colours();
                var dark = Palettes.Face(palette, dark: true).Colours();

                var same = light.Keys
                    .Where(k => string.Equals(light[k], dark[k], StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k)
                    .ToList();

                Expect(same.Count == 0,
                    $"{palette}: identical on both faces, so probably unintended: {string.Join(", ", same)}");
            }
        });

        failures += Check("every palette has a name and a line explaining it", () =>
        {
            foreach (var palette in Enum.GetValues<DeckPalette>())
            {
                var (name, description) = Palettes.Describe(palette);

                Expect(!string.IsNullOrWhiteSpace(name), $"{palette} has no name for the picker");
                Expect(!string.IsNullOrWhiteSpace(description), $"{palette} has no description under the picker");
                Expect(description.EndsWith('.'), $"{palette}: \"{description}\" is a hint, so it ends in a full stop");
            }

            var names = Enum.GetValues<DeckPalette>().Select(p => Palettes.Describe(p).Name).ToList();
            Expect(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count,
                "two palettes share a name, so the picker cannot tell them apart");
        });

        failures += Check("a button's own colour actually reaches its text", () =>
        {
            // The check above passed for months while every accent button on screen ignored
            // OnAccentColor entirely: 1.9:1 on the dark palette, 2.7:1 on the light one. The palette
            // was never wrong - the button template simply never used it. A ContentPresenter handed a
            // string builds a TextBlock, and that TextBlock takes the application-wide implicit
            // TextBlock style ahead of anything it would have inherited from the control.
            //
            // So this checks the mechanism, not the numbers: the shared Button template has to push
            // its Foreground back onto its content. It is a text scan and it cannot prove the result
            // is on screen - only the pixels can - but it fails loudly if the line that makes it work
            // is ever dropped, which is what happened.
            var styleStart = themeXaml.IndexOf("<Style TargetType=\"Button\">", StringComparison.Ordinal);
            Expect(styleStart >= 0, "the shared Button style is no longer where this check looks for it");

            var template = Between(
                themeXaml[styleStart..], "<ControlTemplate TargetType=\"Button\">", "</ControlTemplate>");

            Expect(template is not null, "the shared Button style no longer carries a template");

            var presenter = Between(template!, "<ContentPresenter", "</ContentPresenter>")
                ?? throw new Exception(
                    "the Button template's ContentPresenter is self-closing, so nothing colours the text it builds");

            Expect(presenter.Contains("TargetType=\"TextBlock\"") && presenter.Contains("Property=\"Foreground\""),
                "the Button template does not push its Foreground onto the text it builds, so accent " +
                "buttons will silently draw in the body text colour");
        });

        failures += Check("the website shows the colours the product actually ships", () =>
        {
            // The site advertises the palettes, and a swatch showing a colour the app no longer has
            // is worse than showing nothing at all: somebody would be choosing from a picture of a
            // product that does not exist. The page's block is generated rather than written, so
            // this is an exact comparison against what the generator produces today.
            var site = File.ReadAllText(Path.Combine(root, "site", "index.html"));

            var start = site.IndexOf(SitePalettes.Open, StringComparison.Ordinal);
            Expect(start >= 0, "the generated palette block is no longer in site/index.html");

            var end = site.IndexOf(SitePalettes.Close, start, StringComparison.Ordinal);
            Expect(end >= 0, "the generated palette block in site/index.html has no end marker");

            var present = site[start..(end + SitePalettes.Close.Length)].Replace("\r\n", "\n");

            Expect(present == SitePalettes.Css(),
                "site/index.html no longer matches the palettes - regenerate it with " +
                "\"dotnet run --project tests/Deck.EncoderCheck -- --site-palettes\"");

            // And the words under each swatch are the words under the picker in the app, so somebody
            // who chose Dragon from the page finds the same sentence when they get there.
            foreach (var palette in Enum.GetValues<DeckPalette>())
            {
                var (name, description) = Palettes.Describe(palette);
                var slug = palette.ToString().ToLowerInvariant();

                Expect(site.Contains($"data-palette=\"{slug}\"", StringComparison.Ordinal),
                    $"the website has no swatch for {name}");

                Expect(site.Contains(Html(description), StringComparison.Ordinal),
                    $"the website does not describe {name} the way the app does: \"{description}\"");
            }
        });

        failures += Check("every brush the window is handed is re-read when the palette changes", () =>
        {
            // The failure this exists for is invisible in code review and obvious on screen: the
            // deck repaints, and one control - the on-air button - keeps the colour of the palette
            // before it. A Brush arrives at the window through a binding, and a binding is only read
            // again when the property says it changed. Nothing about swapping the palette says that,
            // so each one has to be named by hand in OnPaletteChanged.
            //
            // A text scan, because the check suite cannot reference the WPF project. It compares the
            // properties that exist against the names raised, so a brush added without a line there
            // fails here rather than shipping as one stale control.
            var source = File.ReadAllText(Path.Combine(root, "src", "Deck.App", "MainViewModel.cs"));

            var properties = Regex.Matches(source, @"public Brush (?<name>\w+)")
                .Select(m => m.Groups["name"].Value)
                .ToList();

            Expect(properties.Count > 5, $"only {properties.Count} brush properties found - has the file changed shape?");

            var handler = Between(source, "private void OnPaletteChanged()", "RefreshTargetStatus();")
                ?? throw new Exception("MainViewModel no longer has an OnPaletteChanged that refreshes the rows");

            var raised = Regex.Matches(handler, @"nameof\((?<name>\w+)\)")
                .Select(m => m.Groups["name"].Value)
                .ToHashSet(StringComparer.Ordinal);

            var forgotten = properties.Where(p => !raised.Contains(p)).OrderBy(p => p).ToList();

            Expect(forgotten.Count == 0,
                $"handed to the window but never re-read when the palette changes: {string.Join(", ", forgotten)}");
        });

        failures += Check("choosing colours does not choose light or dark for you", () =>
        {
            // The point of splitting the two settings. If a palette were missing a face, picking it
            // would silently drag somebody onto a brightness they did not ask for.
            foreach (var palette in Enum.GetValues<DeckPalette>())
            {
                Expect(!Palettes.Face(palette, dark: false).Dark, $"{palette}'s light face says it is dark");
                Expect(Palettes.Face(palette, dark: true).Dark, $"{palette}'s dark face says it is light");

                var light = Palettes.Face(palette, dark: false).Colours()["BackgroundColor"];
                var dark = Palettes.Face(palette, dark: true).Colours()["BackgroundColor"];

                Expect(Luminance(Read(light)) > 0.4, $"{palette}'s light window is not light");
                Expect(Luminance(Read(dark)) < 0.1, $"{palette}'s dark window is not dark");
            }
        });

        return failures;
    }

    /// <summary>Every palette in both brightnesses, named the way a failure message should read.</summary>
    private static IEnumerable<(string Name, IReadOnlyDictionary<string, string> Palette)> Faces()
    {
        foreach (var palette in Enum.GetValues<DeckPalette>())
        {
            foreach (var dark in new[] { false, true })
            {
                yield return ($"{palette} {(dark ? "dark" : "light")}", Palettes.Face(palette, dark).Colours());
            }
        }
    }

    /// <summary>
    /// The few characters the page writes as entities. Only what these descriptions actually contain -
    /// a general escaper would be pretending this handles more than it does.
    /// </summary>
    private static string Html(string text) =>
        text.Replace("&", "&amp;").Replace("é", "&eacute;").Replace("—", "&mdash;");

    private static Dictionary<string, string> ParseXaml(string xaml) =>
        Regex.Matches(xaml, "<Color x:Key=\"(?<name>\\w+)\">(?<value>#[0-9A-Fa-f]+)</Color>")
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value);

    private static bool TryParse(string hex, out (double R, double G, double B, double A) colour)
    {
        colour = default;
        var text = hex.TrimStart('#');

        if (text.Length != 8 && text.Length != 6) return false;
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;

        var (a, r, g, b) = text.Length == 8
            ? ((value >> 24) & 0xFF, (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF)
            : (0xFFu, (value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);

        colour = (r / 255.0, g / 255.0, b / 255.0, a / 255.0);
        return true;
    }

    private static (double R, double G, double B, double A) Read(string hex) =>
        TryParse(hex, out var c) ? c : throw new Exception($"cannot read colour {hex}");

    /// <summary>The text between the first opening marker and the first closing one after it.</summary>
    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;

        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }

    /// <summary>
    /// The same colour at part opacity. Only the alpha byte changes - <see cref="Contrast"/> already
    /// composites a translucent foreground over what is behind it, so this is all that is needed to
    /// ask what a fading element looks like at its dimmest.
    /// </summary>
    private static string Fade(string hex, double opacity)
    {
        var c = Read(hex);

        return $"#{(int)Math.Round(opacity * 255):X2}" +
               $"{(int)Math.Round(c.R * 255):X2}{(int)Math.Round(c.G * 255):X2}{(int)Math.Round(c.B * 255):X2}";
    }

    /// <summary>
    /// WCAG contrast ratio. Colours carrying alpha are composited over the value behind them first,
    /// because a wash at 12% opacity is not the colour it names.
    /// </summary>
    private static double Contrast(string foreground, string background)
    {
        var fg = Read(foreground);
        var bg = Read(background);

        if (fg.A < 1.0)
        {
            fg = (fg.R * fg.A + bg.R * (1 - fg.A),
                  fg.G * fg.A + bg.G * (1 - fg.A),
                  fg.B * fg.A + bg.B * (1 - fg.A),
                  1.0);
        }

        var lighter = Math.Max(Luminance(fg), Luminance(bg));
        var darker = Math.Min(Luminance(fg), Luminance(bg));

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// How far apart two colours look, CIE76: the straight-line distance between them in Lab, where
    /// equal steps are meant to look like equal steps. Roughly, 2 is the smallest difference anybody
    /// can see side by side and 20 is unmistakable across a room.
    /// </summary>
    private static double Distance(string first, string second)
    {
        var (l1, a1, b1) = Lab(Read(first));
        var (l2, a2, b2) = Lab(Read(second));

        return Math.Sqrt(((l1 - l2) * (l1 - l2)) + ((a1 - a2) * (a1 - a2)) + ((b1 - b2) * (b1 - b2)));
    }

    /// <summary>sRGB to CIE Lab, by way of XYZ under the D65 white point.</summary>
    private static (double L, double A, double B) Lab((double R, double G, double B, double A) c)
    {
        var r = Channel(c.R);
        var g = Channel(c.G);
        var b = Channel(c.B);

        var x = ((0.4124 * r) + (0.3576 * g) + (0.1805 * b)) / 0.95047;
        var y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        var z = ((0.0193 * r) + (0.1192 * g) + (0.9505 * b)) / 1.08883;

        return ((116 * F(y)) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));

        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t) + (16.0 / 116.0);
    }

    private static double Luminance((double R, double G, double B, double A) c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(double value) =>
        value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    /// <summary>Walks up from the test binary until it finds the solution, so paths do not hard-code depth.</summary>
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
