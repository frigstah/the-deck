using System.Globalization;
using System.Text.RegularExpressions;

namespace Deck.EncoderCheck;

/// <summary>
/// The two palettes (I5), checked as text rather than as pixels.
/// <para>
/// The light palette lives in Theme.xaml and the dark one is applied over it in code, which means a
/// colour can be added to one and forgotten in the other. Nothing catches that: it builds, it runs,
/// and one element stays stubbornly light on a dark window until somebody notices. So the two key
/// sets are compared here.
/// </para>
/// <para>
/// Contrast is checked for the same reason. The Deck is meant to be readable across a room, and the
/// dark palette was designed first - a light theme derived from it can drift below legibility in
/// places nobody looks at often. The figures come from the WCAG relative-luminance formula rather
/// than from anybody's judgement.
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
        var appCode = File.ReadAllText(Path.Combine(root, "src", "Deck.App", "App.xaml.cs"));

        var light = ParseLight(themeXaml);
        var dark = ParseDark(appCode);

        failures += Check("both palettes define the same colours", () =>
        {
            Expect(light.Count > 20, $"only {light.Count} colours found in Theme.xaml - has the format changed?");

            var missing = light.Keys.Except(dark.Keys).OrderBy(k => k).ToList();
            var extra = dark.Keys.Except(light.Keys).OrderBy(k => k).ToList();

            Expect(missing.Count == 0,
                $"declared for light but never overridden for dark: {string.Join(", ", missing)}");

            Expect(extra.Count == 0,
                $"overridden for dark but not declared for light: {string.Join(", ", extra)}");
        });

        failures += Check("every colour is a readable ARGB value", () =>
        {
            foreach (var (name, value) in light.Concat(dark))
            {
                Expect(TryParse(value, out _), $"{name} = \"{value}\" is not an ARGB hex value");
            }
        });

        // ---------------------------------------------------------------- legibility

        failures += Check("text is legible on both grounds", () =>
        {
            foreach (var (theme, palette) in Both(light, dark))
            {
                var ratio = Contrast(palette["TextColor"], palette["BackgroundColor"]);
                Expect(ratio >= BodyMinimum, $"{theme}: text on the window is {ratio:0.0}:1, needs {BodyMinimum}");

                var onSurface = Contrast(palette["TextColor"], palette["SurfaceColor"]);
                Expect(onSurface >= BodyMinimum, $"{theme}: text on a chip is {onSurface:0.0}:1, needs {BodyMinimum}");
            }
        });

        failures += Check("the credit line is readable, and still gold", () =>
        {
            // It is small text, so it is held to the body standard rather than waved through as
            // decoration - a credit nobody can read is not much of a credit. The two palettes carry
            // genuinely different golds because the metallic shade people picture is a pale yellow
            // on a white window; this is the check that stops anyone "tidying" them into one value.
            //
            // Measured at the bottom of the pulse, not the top, which is the whole reason this check
            // earns its place: the first gold read 4.6:1 at full strength and 2.4:1 at its dimmest,
            // so a line that passed every obvious test was unreadable most of the time it was on
            // screen. Keep this figure in step with the animation in MainWindow.xaml.
            const double PulseFloor = 0.80;

            foreach (var (theme, palette) in Both(light, dark))
            {
                var gold = palette["GoldColor"];
                var ratio = Contrast(Fade(gold, PulseFloor), palette["BackgroundColor"]);

                Expect(ratio >= BodyMinimum,
                    $"{theme}: the credit is {ratio:0.0}:1 at its dimmest, needs {BodyMinimum}");

                // Gold, not "some yellowish grey". Red and green well clear of blue is what makes it
                // read as gold rather than as another neutral.
                Expect(TryParse(gold, out var c), $"{theme}: {gold} is not a colour");
                Expect(c.R > c.B + 0.15 && c.G > c.B + 0.08, $"{theme}: {gold} does not read as gold");
            }
        });

        failures += Check("hints and readouts are legible, not just present", () =>
        {
            // Muted text carries every hint under every setting row. It is small and it is prose, so
            // it is held to the body threshold rather than the large-text one.
            foreach (var (theme, palette) in Both(light, dark))
            {
                var ratio = Contrast(palette["MutedTextColor"], palette["BackgroundColor"]);
                Expect(ratio >= BodyMinimum, $"{theme}: hint text is {ratio:0.0}:1, needs {BodyMinimum}");
            }
        });

        failures += Check("button text is legible on the accent", () =>
        {
            foreach (var (theme, palette) in Both(light, dark))
            {
                var ratio = Contrast(palette["OnAccentColor"], palette["AccentColor"]);
                Expect(ratio >= BodyMinimum, $"{theme}: text on the accent is {ratio:0.0}:1, needs {BodyMinimum}");
            }
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

        failures += Check("the on-air state block reads on both palettes", () =>
        {
            // Always white text on the live colour, in the strip and on the deck.
            foreach (var (theme, palette) in Both(light, dark))
            {
                var ratio = Contrast("#FFFFFFFF", palette["LiveColor"]);
                Expect(ratio >= LargeMinimum,
                    $"{theme}: white on the on-air red is {ratio:0.0}:1, needs {LargeMinimum}");
            }
        });

        failures += Check("a lit meter segment is distinguishable from an unlit one", () =>
        {
            // The whole meter depends on this and none of it is text, so the large-element threshold
            // applies. It is also the check that would have caught the first light palette, where the
            // quiet zone was nearly as dark as the good zone.
            foreach (var (theme, palette) in Both(light, dark))
            {
                foreach (var zone in new[] { "Quiet", "Good", "Loud", "Clip" })
                {
                    var ratio = Contrast(palette[$"Meter{zone}Color"], palette[$"Meter{zone}OffColor"]);
                    Expect(ratio >= 1.6,
                        $"{theme}: the {zone.ToLowerInvariant()} zone lit vs unlit is only {ratio:0.00}:1");
                }
            }
        });

        failures += Check("the two palettes are genuinely different", () =>
        {
            // A key overridden to the value it already had is almost always a copy-paste slip.
            var same = light.Keys
                .Where(k => string.Equals(light[k], dark[k], StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k)
                .ToList();

            Expect(same.Count == 0, $"identical in both palettes, so probably unintended: {string.Join(", ", same)}");
        });

        return failures;
    }

    private static IEnumerable<(string Theme, Dictionary<string, string> Palette)> Both(
        Dictionary<string, string> light, Dictionary<string, string> dark) =>
        [("light", light), ("dark", dark)];

    private static Dictionary<string, string> ParseLight(string xaml) =>
        Regex.Matches(xaml, "<Color x:Key=\"(?<name>\\w+)\">(?<value>#[0-9A-Fa-f]+)</Color>")
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value);

    /// <summary>Reads the Set("Background", "#FF15181B") calls out of ApplyDarkPalette.</summary>
    private static Dictionary<string, string> ParseDark(string code) =>
        Regex.Matches(code, "Set\\(\"(?<name>\\w+)\",\\s*\"(?<value>#[0-9A-Fa-f]+)\"\\)")
            .ToDictionary(
                m => m.Groups["name"].Value.EndsWith("Color", StringComparison.Ordinal)
                    ? m.Groups["name"].Value
                    : m.Groups["name"].Value + "Color",
                m => m.Groups["value"].Value);

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

    /// <summary>
    /// WCAG contrast ratio. Colours carrying alpha are composited over the value behind them first,
    /// because a wash at 12% opacity is not the colour it names.
    /// </summary>
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
        if (!TryParse(hex, out var c)) throw new Exception($"cannot read colour {hex}");

        return $"#{(int)Math.Round(opacity * 255):X2}" +
               $"{(int)Math.Round(c.R * 255):X2}{(int)Math.Round(c.G * 255):X2}{(int)Math.Round(c.B * 255):X2}";
    }

    private static double Contrast(string foreground, string background)
    {
        if (!TryParse(foreground, out var fg)) throw new Exception($"cannot read colour {foreground}");
        if (!TryParse(background, out var bg)) throw new Exception($"cannot read colour {background}");

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
