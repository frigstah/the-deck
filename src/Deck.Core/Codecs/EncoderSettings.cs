using Deck.Core.Audio;

namespace Deck.Core.Codecs;

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

    /// <summary>
    /// The rates offered as one choice for the whole of Deck, under Sound.
    /// <para>
    /// The MP3 family's list, because that is the one every host asks about and it contains
    /// everything a broadcast realistically uses. A codec that cannot take the chosen rate is not an
    /// error and does not need its own menu: <see cref="Normalised"/> moves that server to the
    /// nearest rate its codec does accept, which is why Opus quietly runs at 48 kHz however this is
    /// set.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> OfferedSampleRates { get; } = [22050, 24000, 32000, 44100, 48000];

    /// <summary>
    /// What the one sample rate should be, given what is stored and what the saved servers already
    /// say. Returns <paramref name="stored"/> whenever there is one.
    /// <para>
    /// This exists because of the first run after the rate stopped being per server. There are
    /// already servers with rates of their own at that moment, and taking the default would move
    /// somebody who had deliberately set 48 kHz on every one of them down to 44,1 - silently,
    /// because nothing on screen changes until they next go live. So a Deck with no stored rate
    /// adopts what its servers are already set to.
    /// </para>
    /// <para>
    /// The most common rate wins and ties go to the higher one. A list imported from another encoder
    /// is usually all one rate; where it is not, the majority is the least surprising answer, and
    /// preferring the higher of a tie loses nothing that resampling can get back.
    /// </para>
    /// </summary>
    public static int ResolveSampleRate(int? stored, IEnumerable<int> existing)
    {
        if (stored is { } chosen) return chosen;

        return existing
            .GroupBy(rate => rate)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key)
            .Select(group => (int?)group.Key)
            .FirstOrDefault() ?? DefaultSampleRate;
    }

    /// <summary>What a Deck with no servers and no stored choice runs at: what every host expects.</summary>
    public const int DefaultSampleRate = 44100;

    /// <summary>
    /// The lowest bitrate the deck's own Quality chip offers. Below this is a voice setting somebody
    /// chose deliberately in the server editor, not something to reach for between songs.
    /// </summary>
    public const int DeckMinimumBitrate = 96;

    /// <summary>
    /// What the Quality chip on the deck offers for a codec: the standard ladder from 96 kbps up,
    /// taken from what that codec actually accepts, so Opus is not offered the 224 it cannot encode
    /// and Vorbis is not offered 112.
    /// <para>
    /// The current bitrate is included even when it falls below the floor. The chip used to hold
    /// three values and show nothing at all for a server set to anything else, which read as Deck
    /// not supporting that bitrate - and picking from the list was then the only way out, which
    /// silently moved the server off the rate its host had asked for.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> DeckBitrates(StreamCodec codec, int current) =>
        AvailableBitrates(codec)
            .Where(rate => rate >= DeckMinimumBitrate || rate == current)
            .ToList();

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

    /// <summary>
    /// The same thing in as few characters as it can be said in, e.g. "MP3 256k" - for the on-air
    /// strip, where it sits beside four other facts and has to earn its width.
    /// </summary>
    public string ShortSummary => Codec.IsLossless()
        ? $"{Codec.DisplayName()} lossless"
        : $"{Codec.DisplayName()} {BitrateKbps}k";
}
