using Sirs.Core.Audio;

namespace Sirs.Core.Codecs;

/// <summary>
/// Everything that defines the outgoing stream. Users normally pick a <see cref="QualityPreset"/>
/// and never see these fields; the Advanced panel exposes them directly (D5).
/// </summary>
public sealed record EncoderSettings
{
    public StreamCodec Codec { get; init; } = StreamCodec.Mp3;

    public int BitrateKbps { get; init; } = 128;

    public int SampleRate { get; init; } = 44100;

    public int Channels { get; init; } = 2;

    public AudioFormat Format => new(SampleRate, Channels);

    /// <summary>Bitrates we offer, unrestricted by any licence tier (see spec: no feature gating).</summary>
    public static IReadOnlyList<int> AvailableBitrates(StreamCodec codec) => codec switch
    {
        StreamCodec.Mp3 => new[] { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },
        StreamCodec.OggOpus => new[] { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 256, 320 },

        // Vorbis is quality-driven, so these are targets it averages around rather than fixed rates.
        StreamCodec.OggVorbis => new[] { 48, 64, 80, 96, 128, 160, 192, 224, 256, 320 },

        // FLAC throws nothing away, so there is nothing to choose: the rate falls out of the audio.
        StreamCodec.OggFlac => Array.Empty<int>(),
        _ => new[] { 128 },
    };

    public static IReadOnlyList<int> AvailableSampleRates(StreamCodec codec) => codec switch
    {
        // LAME supports the MPEG-1 and MPEG-2 rate families.
        StreamCodec.Mp3 => new[] { 22050, 24000, 32000, 44100, 48000 },

        // Opus resamples internally but only accepts these input rates.
        StreamCodec.OggOpus => new[] { 16000, 24000, 48000 },

        StreamCodec.OggVorbis => new[] { 22050, 24000, 32000, 44100, 48000 },

        // 16 kHz is included so a recording can follow an Opus capture without being resampled.
        StreamCodec.OggFlac => new[] { 16000, 22050, 24000, 32000, 44100, 48000 },
        _ => new[] { 44100 },
    };

    /// <summary>
    /// What a lossless stream actually costs, in kbps. FLAC on speech and typical music lands
    /// around 55-65% of the raw 16-bit rate; 60% is the honest middle, and it is used to size the
    /// send buffer as well as to tell the user what to expect.
    /// </summary>
    public int EstimatedLosslessKbps => (int)Math.Round(SampleRate * Channels * 16 * 0.6 / 1000.0);

    /// <summary>Nudges the settings onto values the chosen codec can actually accept.</summary>
    public EncoderSettings Normalised()
    {
        var rates = AvailableSampleRates(Codec);
        var sampleRate = rates.Contains(SampleRate)
            ? SampleRate
            : rates.OrderBy(r => Math.Abs(r - SampleRate)).First();

        var channels = Math.Clamp(Channels, 1, 2);

        var bitrates = AvailableBitrates(Codec);
        int bitrate;

        if (bitrates.Count == 0)
        {
            // Lossless: nothing to pick, but the rest of the pipeline sizes buffers from this
            // number, so it has to be a realistic one rather than a leftover 128.
            bitrate = (this with { SampleRate = sampleRate, Channels = channels }).EstimatedLosslessKbps;
        }
        else
        {
            bitrate = bitrates.Contains(BitrateKbps)
                ? BitrateKbps
                : bitrates.OrderBy(b => Math.Abs(b - BitrateKbps)).First();
        }

        return this with
        {
            SampleRate = sampleRate,
            BitrateKbps = bitrate,
            Channels = channels,
        };
    }

    /// <summary>Short human summary, e.g. "MP3 128 kbps, 44.1 kHz stereo".</summary>
    public string Summary
    {
        get
        {
            var shape = $"{SampleRate / 1000.0:0.#} kHz {(Channels == 1 ? "mono" : "stereo")}";

            return Codec.IsLossless()
                ? $"{Codec.DisplayName()} lossless, {shape} — around {EstimatedLosslessKbps} kbps"
                : $"{Codec.DisplayName()} {BitrateKbps} kbps, {shape}";
        }
    }
}
