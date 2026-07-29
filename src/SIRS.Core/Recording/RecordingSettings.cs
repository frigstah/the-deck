using Sirs.Core.Audio;
using Sirs.Core.Codecs;

namespace Sirs.Core.Recording;

public enum RecordingFormat
{
    /// <summary>Record in the same codec as the broadcast - small files, no extra decisions.</summary>
    SameAsStream,

    /// <summary>Uncompressed 16-bit WAV, for editing afterwards.</summary>
    Wav,

    /// <summary>
    /// FLAC, whatever the broadcast is using (D4). Identical audio to the WAV at roughly half the
    /// size, which is what makes it the right default for anyone keeping their shows.
    /// </summary>
    Lossless,
}

public sealed record RecordingSettings
{
    public string Folder { get; init; } = AppPaths.DefaultRecordingDirectory;

    /// <summary>
    /// Filename pattern (G2). Tokens are spelled out rather than using strftime codes, because
    /// %Y-%m-%d is exactly the kind of thing this product should never ask anyone to learn.
    /// </summary>
    public string FilenameTemplate { get; init; } = "{station} {date} {time}";

    public RecordingFormat Format { get; init; } = RecordingFormat.SameAsStream;

    /// <summary>
    /// Start a new file every so many minutes (G3). Zero means one file for the whole show.
    /// Stations that keep recordings for compliance usually want hourly.
    /// </summary>
    public int SplitMinutes { get; init; }

    /// <summary>
    /// Stop recording when the drive falls below this, so a full disk never takes the broadcast
    /// down with it (G4).
    /// </summary>
    public long MinimumFreeBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>Warn while there is still time to do something about it.</summary>
    public long LowSpaceWarningBytes { get; init; } = 1024L * 1024 * 1024;

    public string Extension(StreamCodec streamCodec) => Format switch
    {
        RecordingFormat.Wav => ".wav",
        RecordingFormat.Lossless => StreamCodec.OggFlac.FileExtension(),
        _ => streamCodec.FileExtension(),
    };

    /// <summary>
    /// What the recorder should actually encode with.
    /// <para>
    /// Only "same as the stream" follows the broadcast. The other two mean "keep what came in", so
    /// they take the capture format instead - recording a mono 64 kbps show as a mono FLAC would
    /// preserve nothing worth preserving, and the point of picking lossless is the opposite.
    /// </para>
    /// </summary>
    public EncoderSettings EncoderFor(EncoderSettings streamSettings, AudioFormat captureFormat) => Format switch
    {
        RecordingFormat.Lossless => new EncoderSettings
        {
            Codec = StreamCodec.OggFlac,
            SampleRate = captureFormat.SampleRate,
            Channels = captureFormat.Channels,
        }.Normalised(),

        RecordingFormat.Wav => streamSettings with
        {
            SampleRate = captureFormat.SampleRate,
            Channels = captureFormat.Channels,
        },

        _ => streamSettings,
    };

    public static IReadOnlyList<(RecordingFormat Format, string Label)> FormatOptions { get; } =
    [
        (RecordingFormat.SameAsStream, "Same as the broadcast — smallest files"),
        (RecordingFormat.Lossless, "Lossless FLAC — keeps every detail, about half the size of WAV"),
        (RecordingFormat.Wav, "WAV — uncompressed, for editing"),
    ];

    public static IReadOnlyList<(int Minutes, string Label)> SplitOptions { get; } =
    [
        (0, "One file for the whole show"),
        (15, "A new file every 15 minutes"),
        (30, "A new file every 30 minutes"),
        (60, "A new file every hour"),
        (120, "A new file every 2 hours"),
    ];
}

public static class FilenameTemplate
{
    public static IReadOnlyList<(string Token, string Description)> Tokens { get; } =
    [
        ("{station}", "Your station name"),
        ("{date}", "The date, as 2026-07-28"),
        ("{time}", "The time, as 14-05"),
        ("{title}", "What was playing when the recording started"),
    ];

    /// <summary>Builds a filename, guaranteed safe for Windows and never empty.</summary>
    public static string Build(string template, string stationName, string title, DateTime timestamp)
    {
        var text = string.IsNullOrWhiteSpace(template) ? "{station} {date} {time}" : template;

        text = text
            .Replace("{station}", Sanitise(stationName))
            .Replace("{date}", timestamp.ToString("yyyy-MM-dd"))
            .Replace("{time}", timestamp.ToString("HH-mm"))
            .Replace("{title}", Sanitise(title));

        var name = Sanitise(text).Trim();
        if (name.Length == 0) name = timestamp.ToString("yyyy-MM-dd HH-mm");

        // Leave room for the extension and a uniqueness suffix inside the 255 character limit.
        return name.Length > 200 ? name[..200] : name;
    }

    /// <summary>Adds a number if the file already exists, so a recording never overwrites another.</summary>
    public static string EnsureUnique(string folder, string baseName, string extension)
    {
        var path = Path.Combine(folder, baseName + extension);
        if (!File.Exists(path)) return path;

        for (var i = 2; i < 1000; i++)
        {
            path = Path.Combine(folder, $"{baseName} ({i}){extension}");
            if (!File.Exists(path)) return path;
        }

        return Path.Combine(folder, $"{baseName} {Guid.NewGuid():N}{extension}");
    }

    private static string Sanitise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) ? '-' : c);
        }

        return builder.ToString().Trim();
    }
}
