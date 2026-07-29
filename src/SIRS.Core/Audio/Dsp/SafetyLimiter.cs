namespace Sirs.Core.Audio.Dsp;

/// <summary>
/// Always-on brickwall limiter sitting immediately before the encoder (E2). It is invisible in the
/// UI on purpose: its job is to make sure nothing ever hits the encoder above full scale, no matter
/// what the user does with the gain slider. Gain reduction is applied across all channels together
/// so the stereo image does not shift when it engages.
/// </summary>
public sealed class SafetyLimiter
{
    private readonly int _channels;
    private readonly float _attackCoefficient;
    private readonly float _releaseCoefficient;
    private float _envelope;

    public SafetyLimiter(int sampleRate, int channels, float ceilingDb = -0.3f)
    {
        _channels = channels;
        Ceiling = AudioMath.FromDb(ceilingDb);

        // 1 ms attack catches transients without audible distortion; 120 ms release keeps it from
        // pumping on speech.
        _attackCoefficient = MathF.Exp(-1f / (0.001f * sampleRate));
        _releaseCoefficient = MathF.Exp(-1f / (0.120f * sampleRate));
        _envelope = 1f;
    }

    public float Ceiling { get; }

    /// <summary>Current gain reduction in dB (0 when the limiter is doing nothing).</summary>
    public float GainReductionDb { get; private set; }

    public void Process(Span<float> interleaved)
    {
        var frames = interleaved.Length / _channels;
        var maxReduction = 0f;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            var peak = 0f;
            for (var ch = 0; ch < _channels; ch++)
            {
                var magnitude = MathF.Abs(interleaved[baseIndex + ch]);
                if (magnitude > peak) peak = magnitude;
            }

            var targetGain = peak > Ceiling ? Ceiling / peak : 1f;

            // Attack fast when we need to pull down, release slowly when we can let go.
            var coefficient = targetGain < _envelope ? _attackCoefficient : _releaseCoefficient;
            _envelope = (coefficient * _envelope) + ((1f - coefficient) * targetGain);

            for (var ch = 0; ch < _channels; ch++) interleaved[baseIndex + ch] *= _envelope;

            var reduction = 1f - _envelope;
            if (reduction > maxReduction) maxReduction = reduction;
        }

        GainReductionDb = maxReduction <= 0f ? 0f : -AudioMath.ToDb(1f - maxReduction);
    }

    public void Reset()
    {
        _envelope = 1f;
        GainReductionDb = 0f;
    }
}
