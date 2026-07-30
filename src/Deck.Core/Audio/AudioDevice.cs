namespace Deck.Core.Audio;

public enum AudioDeviceKind
{
    /// <summary>A microphone or line input.</summary>
    Input,

    /// <summary>A speaker / headphone device used for monitoring and playback.</summary>
    Output,

    /// <summary>An output device captured via WASAPI loopback ("what the computer is playing").</summary>
    Loopback,

    /// <summary>A professional interface reached through its own ASIO driver rather than Windows (A8).</summary>
    Asio,
}

/// <summary>A selectable audio endpoint, described in the words a user would recognise.</summary>
public sealed record AudioDevice(
    string Id,
    string Name,
    AudioDeviceKind Kind,
    bool IsSystemDefault)
{
    /// <summary>Name as shown in a picker, e.g. "Microphone (Yeti X) — default".</summary>
    public string DisplayName => IsSystemDefault ? $"{Name} — default" : Name;

    /// <summary>
    /// The shortest form of the name that still says which input this is, for the deck's chip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows names an endpoint "&lt;what it is&gt; (&lt;what it is plugged into&gt;)" — "IN 1-8
    /// (BEHRINGER X-AIR)", "Microphone (Yeti X)". The part before the bracket is the part a person
    /// would actually say out loud, and it is a third of the length, so that is what a chip carries.
    /// The full name is a hover away and spelled out in the list and on the Sound pane.
    /// </para>
    /// <para>
    /// Two devices whose names differ only inside the brackets — two interfaces both offering "IN 1-2"
    /// — will show the same thing here. That is the cost of a short chip, and the list still tells
    /// them apart. Length is capped for the case with no bracket at all, where the whole name is one
    /// long run and would set the width of the row on its own.
    /// </para>
    /// </remarks>
    public string ShortName => Shorten(Name);

    private const int ShortNameLimit = 20;

    internal static string Shorten(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;

        var bracket = trimmed.IndexOf(" (", StringComparison.Ordinal);
        if (bracket > 0) trimmed = trimmed[..bracket].TrimEnd();

        if (trimmed.Length <= ShortNameLimit) return trimmed;

        // Cut on a space when there is one to cut on, so the ellipsis follows a whole word rather
        // than landing in the middle of one.
        var cut = trimmed.LastIndexOf(' ', ShortNameLimit);
        var kept = cut > ShortNameLimit / 2 ? trimmed[..cut] : trimmed[..ShortNameLimit];

        return kept.TrimEnd() + "…";
    }

    /// <summary>
    /// Group heading in the input picker. A render device offered as a capture source needs saying
    /// out loud, because "Speakers" in a list of microphones is otherwise baffling.
    /// </summary>
    public string CategoryLabel => Kind switch
    {
        AudioDeviceKind.Loopback => "Sound playing on this PC",
        AudioDeviceKind.Input => "Microphones and line inputs",
        AudioDeviceKind.Asio => "Professional interfaces (ASIO)",
        _ => "Speakers and headphones",
    };

    /// <summary>
    /// A device is identified by id and kind together: the same endpoint appears as both an output
    /// to monitor through and a loopback source to capture from.
    /// </summary>
    public bool Matches(string? id, AudioDeviceKind kind) => Id == id && Kind == kind;

    public override string ToString() => DisplayName;
}
