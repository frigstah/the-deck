namespace Deck.Core.Codecs;

/// <summary>
/// The three choices most users should ever have to make (D6). Each maps to a settings combination
/// that is known to work on every host we care about. Raw controls live behind Advanced.
/// </summary>
public sealed record QualityPreset(
    string Name,
    string Description,
    EncoderSettings Settings)
{
    public static readonly QualityPreset Talk = new(
        "Voice / Talk",
        "Speech, interviews, podcasts. Uses the least bandwidth.",
        new EncoderSettings { Codec = StreamCodec.Mp3, BitrateKbps = 64, SampleRate = 44100, Channels = 1 });

    public static readonly QualityPreset MusicStandard = new(
        "Music — Standard",
        "The usual choice for a music station. Works everywhere.",
        new EncoderSettings { Codec = StreamCodec.Mp3, BitrateKbps = 128, SampleRate = 44100, Channels = 2 });

    public static readonly QualityPreset MusicHigh = new(
        "Music — High",
        "Better sound, roughly twice the bandwidth per listener.",
        new EncoderSettings { Codec = StreamCodec.Mp3, BitrateKbps = 256, SampleRate = 44100, Channels = 2 });

    public static IReadOnlyList<QualityPreset> All { get; } = new[] { Talk, MusicStandard, MusicHigh };

    public static QualityPreset Default => MusicStandard;

    /// <summary>
    /// Finds the preset matching these settings, or null if the user has customised.
    /// <para>
    /// The sample rate is not part of the comparison, and the rate written into each preset above is
    /// only there because the record needs one. Since the rate became a single setting under Sound,
    /// a preset no longer has an opinion about it - and comparing the whole record meant that
    /// somebody running at 48 kHz saw every preset as "custom" and could not pick one.
    /// </para>
    /// </summary>
    public static QualityPreset? Match(EncoderSettings settings) =>
        All.FirstOrDefault(p =>
            p.Settings.Codec == settings.Codec &&
            p.Settings.BitrateKbps == settings.BitrateKbps &&
            p.Settings.Channels == settings.Channels);

    /// <summary>Rough monthly data estimate to make bitrate choices concrete.</summary>
    public static string BandwidthPerListener(EncoderSettings settings)
    {
        var megabytesPerHour = settings.BitrateKbps * 3600 / 8 / 1024.0;
        return $"about {megabytesPerHour:0} MB per listener per hour";
    }

    /// <summary>
    /// What a screen reader says when it reaches this choice. A list item announces the item's
    /// ToString, not whatever the item template draws, and a record's generated one is every
    /// property it holds - here that is the description and the whole encoder settings record (I6).
    /// </summary>
    public override string ToString() => Name;
}
