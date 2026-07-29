using System.Text.RegularExpressions;

namespace Deck.Core.Metadata;

/// <summary>
/// Turns the pieces of a track into the one line listeners see (F5).
/// <para>
/// The interesting part is what happens when a piece is missing. A station ident has no artist, and
/// a template of "{artist} - {title}" must not put " - Talk Show" on every player in the country.
/// Empty tokens take their neighbouring punctuation with them.
/// </para>
/// </summary>
public static partial class TitleTemplate
{
    public const string Default = "{artist} - {title}";

    public static IReadOnlyList<(string Token, string Description)> Tokens { get; } =
    [
        ("{artist}", "Who is playing"),
        ("{title}", "The track title"),
        ("{album}", "The album, when the source gives one"),
    ];

    public static IReadOnlyList<string> Examples { get; } =
    [
        "{artist} - {title}",
        "{title} by {artist}",
        "{artist} — {title} ({album})",
        "{title}",
    ];

    public static string Build(string? template, string? artist, string? title, string? album = null)
    {
        var text = string.IsNullOrWhiteSpace(template) ? Default : template;

        text = text
            .Replace("{artist}", artist?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", title?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{album}", album?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        return Tidy(text);
    }

    /// <summary>What the template would produce for a made-up track, so the user can see it work.</summary>
    public static string Preview(string? template) =>
        Build(template, "The Clash", "London Calling", "London Calling");

    private static string Tidy(string text)
    {
        // Empty brackets left behind by a missing album, then doubled and dangling separators.
        text = EmptyBrackets().Replace(text, string.Empty);
        text = RepeatedWhitespace().Replace(text, " ");
        text = DoubledSeparator().Replace(text, "$1");
        text = EdgeSeparators().Replace(text, string.Empty);

        return text.Trim();
    }

    [GeneratedRegex(@"\(\s*\)|\[\s*\]|\{\s*\}")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedWhitespace();

    [GeneratedRegex(@"([-–—|:,])\s*[-–—|:,]")]
    private static partial Regex DoubledSeparator();

    [GeneratedRegex(@"^[\s\-–—|:,]+|[\s\-–—|:,]+$")]
    private static partial Regex EdgeSeparators();
}
