namespace Sirs.Core.Audio.Dsp;

/// <summary>One band's compression settings. Never edited by hand - only chosen through a preset.</summary>
public sealed record BandSettings(
    float ThresholdDb,
    float Ratio,
    float AttackMs,
    float ReleaseMs,
    float MakeUpDb);

/// <summary>
/// The whole processing chain as one choice (E4). This is the deliberate limit: a broadcaster picks
/// the kind of show they are making, not twelve numbers per band. Anyone who genuinely wants those
/// numbers is better served by a dedicated processor than by SIRS growing one.
/// </summary>
public sealed record ProcessingPreset(
    string Name,
    string Description,
    BandSettings? Low = null,
    BandSettings? Mid = null,
    BandSettings? High = null)
{
    public static readonly ProcessingPreset Off = new(
        "Off",
        "No processing beyond the safety limiter. Right if your audio is already produced.");

    public static readonly ProcessingPreset Talk = new(
        "Talk",
        "Evens out a voice and pushes it forward. For speech, interviews and phone-ins.",

        // Firm on the low band: proximity effect and desk thumps live here, and holding them down
        // is most of what makes an untreated room sound acceptable.
        Low: new BandSettings(ThresholdDb: -30, Ratio: 3.5f, AttackMs: 12, ReleaseMs: 180, MakeUpDb: 2),
        Mid: new BandSettings(ThresholdDb: -24, Ratio: 3.5f, AttackMs: 8, ReleaseMs: 150, MakeUpDb: 5),
        High: new BandSettings(ThresholdDb: -26, Ratio: 2.5f, AttackMs: 5, ReleaseMs: 120, MakeUpDb: 3));

    public static readonly ProcessingPreset Music = new(
        "Music",
        "Light touch. Keeps tracks from a mixed library sitting at the same level as each other.",
        Low: new BandSettings(ThresholdDb: -20, Ratio: 2f, AttackMs: 25, ReleaseMs: 260, MakeUpDb: 2),
        Mid: new BandSettings(ThresholdDb: -20, Ratio: 2f, AttackMs: 18, ReleaseMs: 220, MakeUpDb: 2),
        High: new BandSettings(ThresholdDb: -22, Ratio: 2f, AttackMs: 10, ReleaseMs: 160, MakeUpDb: 2));

    public static readonly ProcessingPreset Loud = new(
        "Loud",
        "Dense and consistent, the way commercial stations sound. Costs some detail.",
        Low: new BandSettings(ThresholdDb: -28, Ratio: 4.5f, AttackMs: 15, ReleaseMs: 150, MakeUpDb: 6),
        Mid: new BandSettings(ThresholdDb: -26, Ratio: 4.5f, AttackMs: 6, ReleaseMs: 110, MakeUpDb: 6),
        High: new BandSettings(ThresholdDb: -28, Ratio: 3.5f, AttackMs: 4, ReleaseMs: 90, MakeUpDb: 5));

    public static IReadOnlyList<ProcessingPreset> All { get; } = [Off, Talk, Music, Loud];

    public bool IsActive => Low is not null || Mid is not null || High is not null;
}

/// <summary>
/// Three-band compressor (E4), sitting between the tone controls and the safety limiter.
/// <para>
/// Bands are split with fourth-order Linkwitz-Riley crossovers, which sum back to a flat response -
/// the reason a multiband processor can be left on without colouring audio that needs no work. The
/// low band is passed through the upper crossover's allpass so all three bands share the same phase
/// on the way back together; without that the summed response has an audible dip at the crossover.
/// </para>
/// <para>
/// Gain reduction is computed once per band across all channels and applied to every channel
/// equally, so a loud moment on one side never pulls the stereo image across.
/// </para>
/// </summary>
public sealed class MultibandCompressor
{
    private const double LowCrossoverHz = 250;
    private const double HighCrossoverHz = 3000;

    /// <summary>Butterworth Q. Two of these cascaded make one Linkwitz-Riley fourth-order section.</summary>
    private const double ButterworthQ = 0.70710678;

    private readonly int _channels;

    private readonly Crossover _lowSplit;
    private readonly Crossover _highSplit;
    private readonly Crossover _lowAllPass;

    private readonly BandCompressor _low;
    private readonly BandCompressor _mid;
    private readonly BandCompressor _high;

    private ProcessingPreset _preset = ProcessingPreset.Off;

    public MultibandCompressor(int sampleRate, int channels)
    {
        _channels = Math.Max(1, channels);

        _lowSplit = new Crossover(sampleRate, _channels, LowCrossoverHz);
        _highSplit = new Crossover(sampleRate, _channels, HighCrossoverHz);
        _lowAllPass = new Crossover(sampleRate, _channels, HighCrossoverHz);

        _low = new BandCompressor(sampleRate);
        _mid = new BandCompressor(sampleRate);
        _high = new BandCompressor(sampleRate);
    }

    public ProcessingPreset Preset
    {
        get => _preset;
        set
        {
            if (_preset == value) return;

            _preset = value;
            _low.Configure(value.Low);
            _mid.Configure(value.Mid);
            _high.Configure(value.High);
        }
    }

    public bool IsActive => _preset.IsActive;

    /// <summary>Worst gain reduction across the three bands, for a meter.</summary>
    public float GainReductionDb =>
        MathF.Max(_low.GainReductionDb, MathF.Max(_mid.GainReductionDb, _high.GainReductionDb));

    public void Process(Span<float> interleaved)
    {
        if (!IsActive) return;

        var frames = interleaved.Length / _channels;

        Span<float> lowBand = stackalloc float[_channels];
        Span<float> midBand = stackalloc float[_channels];
        Span<float> highBand = stackalloc float[_channels];

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            var lowPeak = 0f;
            var midPeak = 0f;
            var highPeak = 0f;

            for (var ch = 0; ch < _channels; ch++)
            {
                var sample = interleaved[baseIndex + ch];

                var below = _lowSplit.LowPass(ch, sample);
                var above = _lowSplit.HighPass(ch, sample);

                // The allpass keeps the low band in step with the other two through the upper
                // crossover, so the three sum back flat rather than notching at 3 kHz.
                lowBand[ch] = _lowAllPass.LowPass(ch, below) + _lowAllPass.HighPass(ch, below);
                midBand[ch] = _highSplit.LowPass(ch, above);
                highBand[ch] = _highSplit.HighPass(ch, above);

                lowPeak = MathF.Max(lowPeak, MathF.Abs(lowBand[ch]));
                midPeak = MathF.Max(midPeak, MathF.Abs(midBand[ch]));
                highPeak = MathF.Max(highPeak, MathF.Abs(highBand[ch]));
            }

            var lowGain = _low.NextGain(lowPeak);
            var midGain = _mid.NextGain(midPeak);
            var highGain = _high.NextGain(highPeak);

            for (var ch = 0; ch < _channels; ch++)
            {
                interleaved[baseIndex + ch] =
                    (lowBand[ch] * lowGain) + (midBand[ch] * midGain) + (highBand[ch] * highGain);
            }
        }
    }

    public void Reset()
    {
        _lowSplit.Reset();
        _highSplit.Reset();
        _lowAllPass.Reset();
        _low.Reset();
        _mid.Reset();
        _high.Reset();
    }

    /// <summary>
    /// One Linkwitz-Riley fourth-order crossover: two cascaded Butterworth sections per side, per
    /// channel. Low and high outputs of the same crossover sum to an allpass, which is what makes
    /// the band split transparent.
    /// </summary>
    private sealed class Crossover
    {
        private readonly Biquad[] _lowA;
        private readonly Biquad[] _lowB;
        private readonly Biquad[] _highA;
        private readonly Biquad[] _highB;

        public Crossover(int sampleRate, int channels, double frequency)
        {
            _lowA = new Biquad[channels];
            _lowB = new Biquad[channels];
            _highA = new Biquad[channels];
            _highB = new Biquad[channels];

            for (var ch = 0; ch < channels; ch++)
            {
                _lowA[ch] = new Biquad();
                _lowB[ch] = new Biquad();
                _highA[ch] = new Biquad();
                _highB[ch] = new Biquad();

                _lowA[ch].SetLowPass(sampleRate, frequency, ButterworthQ);
                _lowB[ch].SetLowPass(sampleRate, frequency, ButterworthQ);
                _highA[ch].SetHighPass(sampleRate, frequency, ButterworthQ);
                _highB[ch].SetHighPass(sampleRate, frequency, ButterworthQ);
            }
        }

        public float LowPass(int channel, float sample) =>
            _lowB[channel].Process(_lowA[channel].Process(sample));

        /// <summary>
        /// Not inverted. At the crossover both fourth-order halves land at -1/2, so they sum to
        /// unity as they are - the property that makes Linkwitz-Riley worth the extra order. A
        /// second-order crossover would need one side flipped here, and flipping it at fourth order
        /// would produce a deep notch instead of a flat sum.
        /// </summary>
        public float HighPass(int channel, float sample) =>
            _highB[channel].Process(_highA[channel].Process(sample));

        public void Reset()
        {
            for (var ch = 0; ch < _lowA.Length; ch++)
            {
                _lowA[ch].Reset();
                _lowB[ch].Reset();
                _highA[ch].Reset();
                _highB[ch].Reset();
            }
        }
    }

    /// <summary>Feed-forward compressor for one band, with the gain linked across channels.</summary>
    private sealed class BandCompressor(int sampleRate)
    {
        private BandSettings? _settings;
        private float _attackCoefficient = 1f;
        private float _releaseCoefficient = 1f;
        private float _makeUpGain = 1f;
        private float _envelopeDb = AudioMath.MinDb;
        private float _reductionDb;

        public float GainReductionDb => _reductionDb;

        public void Configure(BandSettings? settings)
        {
            _settings = settings;

            if (settings is null)
            {
                _makeUpGain = 1f;
                return;
            }

            _attackCoefficient = MathF.Exp(-1f / (settings.AttackMs / 1000f * sampleRate));
            _releaseCoefficient = MathF.Exp(-1f / (settings.ReleaseMs / 1000f * sampleRate));
            _makeUpGain = AudioMath.FromDb(settings.MakeUpDb);
        }

        public float NextGain(float peak)
        {
            var settings = _settings;
            if (settings is null) return 1f;

            var levelDb = AudioMath.ToDb(peak);
            var coefficient = levelDb > _envelopeDb ? _attackCoefficient : _releaseCoefficient;
            _envelopeDb = (coefficient * _envelopeDb) + ((1f - coefficient) * levelDb);

            var overshootDb = _envelopeDb - settings.ThresholdDb;
            _reductionDb = overshootDb > 0f ? overshootDb * (1f - (1f / settings.Ratio)) : 0f;

            return AudioMath.FromDb(-_reductionDb) * _makeUpGain;
        }

        public void Reset()
        {
            _envelopeDb = AudioMath.MinDb;
            _reductionDb = 0f;
        }
    }
}
