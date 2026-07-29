namespace Sirs.Core.Codecs;

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

    /// <summary>Finds the preset matching these settings exactly, or null if the user has customised.</summary>
    public static QualityPreset? Match(EncoderSettings settings) =>
        All.FirstOrDefault(p => p.Settings == settings);

    /// <summary>Rough monthly data estimate to make bitrate choices concrete.</summary>
    public static string BandwidthPerListener(EncoderSettings settings)
    {
        var megabytesPerHour = settings.BitrateKbps * 3600 / 8 / 1024.0;
        return $"about {megabytesPerHour:0} MB per listener per hour";
    }
}
