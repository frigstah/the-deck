using System.Globalization;

namespace Sirs.Core.Control;

/// <summary>A command line asking a running SIRS to do something (I10).</summary>
public sealed record CommandLineRequest(string Path, string? Description = null)
{
    /// <summary>True for <c>--help</c>, which is answered locally rather than sent anywhere.</summary>
    public bool IsHelp { get; init; }

    /// <summary>Why the arguments could not be understood, when they could not be.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Turns command-line arguments into a control request (I10).
/// <para>
/// Kept apart from the app so it can be checked without launching a window, and so the one place
/// that decides "these arguments mean go on air" is not buried in startup code. Parsing is pure: it
/// reads strings and returns a path, and never touches a socket.
/// </para>
/// <para>
/// Both <c>--title Something</c> and <c>--title=Something</c> are accepted. Windows users type the
/// first, scripts and shortcuts tend to produce the second, and rejecting either would be a support
/// question rather than a helpful strictness.
/// </para>
/// </summary>
public static class CommandLine
{
    public const string HelpText =
        """
        SIRS — Simple Internet Radio Streamer

        Run SIRS with no arguments to open the window. With any of these, SIRS instead sends the
        command to the copy already running and prints the answer:

          --status              show what SIRS is doing
          --status --json       the same, for a script to read
          --live                go on air
          --off                 go off air
          --title "Text"        set what listeners see as now playing
          --record              start recording
          --stop-recording      stop recording
          --mute / --unmute     mute or unmute the input
          --gain -3             set the input level in decibels
          --help                this list

        The control endpoint has to be switched on first, under "SIRS itself" in the window.
        """;

    /// <summary>
    /// Null means there was no command and SIRS should open normally. Anything else should be sent
    /// and the result printed, without a window ever appearing.
    /// </summary>
    public static CommandLineRequest? Parse(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            var (name, inlineValue) = Split(argument);

            switch (name)
            {
                case "--help" or "-h" or "-?" or "/?":
                    return new CommandLineRequest("/help") { IsHelp = true };

                case "--status":
                    // --json is a modifier on --status rather than a command of its own, so it is
                    // looked for across all the arguments and can appear on either side of it.
                    return new CommandLineRequest(
                        WantsJson(args) ? "/status?format=json" : "/status",
                        "status");

                case "--live" or "--connect" or "--on":
                    return new CommandLineRequest("/live", "go on air");

                case "--off" or "--disconnect":
                    return new CommandLineRequest("/off", "go off air");

                case "--record":
                    return new CommandLineRequest("/record", "start recording");

                case "--stop-recording" or "--stop-record":
                    return new CommandLineRequest("/record/stop", "stop recording");

                case "--mute":
                    return new CommandLineRequest("/mute?on=true", "mute");

                case "--unmute":
                    return new CommandLineRequest("/mute?on=false", "unmute");

                case "--title":
                {
                    var text = inlineValue ?? Next(args, i);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return Bad("--title needs some text, for example: SIRS --title \"Artist - Song\"");
                    }

                    return new CommandLineRequest($"/title?text={Uri.EscapeDataString(text)}", "set the title");
                }

                case "--gain":
                {
                    var text = inlineValue ?? Next(args, i);

                    if (text is null ||
                        !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
                    {
                        return Bad("--gain needs a number of decibels, for example: SIRS --gain -3");
                    }

                    var value = db.ToString(CultureInfo.InvariantCulture);
                    return new CommandLineRequest($"/gain?db={Uri.EscapeDataString(value)}", "set the level");
                }
            }
        }

        return null;
    }

    private static bool WantsJson(string[] args) =>
        args.Select(Split).Any(a => a.Name == "--json" || (a.Name == "--format" && a.Value == "json"));

    /// <summary>
    /// The value after a switch, unless that is another switch. Without this check,
    /// <c>--title --live</c> would quietly set the title to "--live" instead of complaining.
    /// </summary>
    private static string? Next(string[] args, int index)
    {
        if (index + 1 >= args.Length) return null;

        var candidate = args[index + 1];
        return candidate.StartsWith('-') && !IsNegativeNumber(candidate) ? null : candidate;
    }

    /// <summary>"-3" is a level, not a switch. Levels are usually negative, so this matters.</summary>
    private static bool IsNegativeNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static (string Name, string? Value) Split(string argument)
    {
        var equals = argument.IndexOf('=');
        return equals < 0
            ? (argument.ToLowerInvariant(), null)
            : (argument[..equals].ToLowerInvariant(), argument[(equals + 1)..]);
    }

    private static CommandLineRequest Bad(string error) =>
        new("/help") { IsHelp = true, Error = error };
}
