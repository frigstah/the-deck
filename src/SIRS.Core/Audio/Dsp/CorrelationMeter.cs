namespace Sirs.Core.Audio.Dsp;

/// <summary>
/// The correlation half of B9: whether the two channels agree, and what happens to the sound if a
/// listener hears it in mono.
/// <para>
/// This is the part of B9 worth having. A microphone wired with its two conductors swapped, or a
/// stereo widener pushed too far, sounds fine on headphones and then largely disappears on a phone
/// speaker or a kitchen radio - which is where a lot of radio is actually heard. Nothing else in
/// SIRS can see that fault, and no meter that only shows level ever will.
/// </para>
/// </summary>
public sealed class CorrelationMeter
{
    /// <summary>About 300 ms of memory. Long enough to be steady, short enough to react to a fault.</summary>
    private const double WindowSeconds = 0.3;

    /// <summary>-80 dBFS. Below this there is nothing to have a phase relationship.</summary>
    private const double QuietPeak = 1e-4;

    private readonly double _decay;
    private readonly int _sampleRate;

    private double _left;
    private double _right;
    private double _product;
    private double _peakHold = 1.0;

    public CorrelationMeter(int sampleRate)
    {
        _sampleRate = Math.Max(1, sampleRate);
        _decay = Math.Exp(-1.0 / (_sampleRate * WindowSeconds));
    }

    /// <summary>
    /// +1 when both channels are identical, 0 when they are unrelated, -1 when one is the other
    /// inverted. Mono input reads +1, which is correct: it is perfectly mono-compatible.
    /// </summary>
    public double Correlation { get; private set; } = 1.0;

    /// <summary>
    /// How much of the level survives being folded down to mono, as a ratio. 1 means nothing is
    /// lost; 0.71 is the ordinary 3 dB that wide stereo gives up; near 0 means the sound largely
    /// cancels itself out.
    /// </summary>
    public double MonoLevelRatio
    {
        get
        {
            var stereo = _left + _right;
            if (stereo <= 1e-12) return 1.0;

            // A mono fold-down is (L+R)/2, whose power is (ΣL² + ΣR² + 2ΣLR)/4, and the stereo it is
            // being compared against is the average of the two channels, (ΣL² + ΣR²)/2. Comparing
            // against the sum instead of the average - which is what this did first - reports a
            // perfectly mono signal as 3 dB louder in mono, which is nonsense.
            var mono = Math.Max(0, stereo + 2 * _product);
            return Math.Sqrt(mono / (2 * stereo));
        }
    }

    /// <summary>
    /// True when there is too little signal for the reading to mean anything.
    /// <para>
    /// Read off a falling peak hold rather than the correlation window. That window is 300 ms of
    /// exponential average, so after real silence it takes several seconds to reach any sensible
    /// threshold - and a meter that keeps insisting your cables are wrong for five seconds after
    /// you stop talking is a meter people learn to ignore.
    /// </para>
    /// </summary>
    public bool IsQuiet => _peakHold < QuietPeak;

    public void Process(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels < 2)
        {
            // One channel is trivially in phase with itself. Saying so beats leaving the last
            // stereo reading on screen after someone switches to a mono input.
            Correlation = 1.0;
            _left = _right = _product = 0;
            UpdatePeak(interleaved, channels);
            return;
        }

        for (var i = 0; i + channels <= interleaved.Length; i += channels)
        {
            double left = interleaved[i];
            double right = interleaved[i + 1];

            _left = _left * _decay + left * left * (1 - _decay);
            _right = _right * _decay + right * right * (1 - _decay);
            _product = _product * _decay + left * right * (1 - _decay);
        }

        UpdatePeak(interleaved, channels);

        var denominator = Math.Sqrt(_left * _right);

        // Silence has no phase relationship to report, so the last real reading is held rather than
        // letting the meter swing wildly on a divide by almost nothing.
        if (denominator > 1e-12)
        {
            Correlation = Math.Clamp(_product / denominator, -1.0, 1.0);
        }
    }

    /// <summary>
    /// A peak that falls 60 dB per second. Fast enough that silence is noticed in about a second,
    /// slow enough that it does not blink out between words.
    /// </summary>
    private void UpdatePeak(ReadOnlySpan<float> interleaved, int channels)
    {
        var peak = 0.0;
        foreach (var sample in interleaved) peak = Math.Max(peak, Math.Abs(sample));

        var seconds = interleaved.Length / (double)(Math.Max(1, channels) * _sampleRate);
        _peakHold = Math.Max(peak, _peakHold * Math.Pow(10, -3 * seconds));
    }

    public void Reset()
    {
        _left = _right = _product = 0;
        _peakHold = 1.0;
        Correlation = 1.0;
    }

    /// <summary>The reading in the same plain language the level meter uses (B2).</summary>
    public string Verdict()
    {
        if (IsQuiet) return "Nothing to measure yet.";

        return Correlation switch
        {
            > 0.9 => "Almost mono. Fine, just not very wide.",
            > 0.3 => "Good, wide stereo that still works in mono.",
            > -0.1 => "Very wide. Check it still sounds right on a phone speaker.",
            _ => "Out of phase — this will largely vanish for anyone listening in mono. Check your cables.",
        };
    }

    public AdviceSeverity Severity()
    {
        if (IsQuiet) return AdviceSeverity.Neutral;

        return Correlation switch
        {
            > 0.3 => AdviceSeverity.Ok,
            > -0.1 => AdviceSeverity.Warning,
            _ => AdviceSeverity.Bad,
        };
    }
}
