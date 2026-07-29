using NAudio.Dsp;

namespace Sirs.Core.Audio.Dsp;

/// <summary>
/// The single "Voice Enhance" toggle (E1): a high-pass filter to remove rumble and handling noise,
/// then gentle compression so a presenter who leans in and out stays at a consistent level, then a
/// little make-up gain. Off by default. Deliberately has no knobs - the whole point is that the
/// user does not have to understand compression to benefit from it.
/// </summary>
public sealed class VoiceEnhance
{
    private const float ThresholdDb = -22f;
    private const float Ratio = 2.5f;
    private const float MakeUpDb = 4f;

    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly BiQuadFilter[] _highPass;
    private readonly float _attackCoefficient;
    private readonly float _releaseCoefficient;
    private readonly float _makeUpGain;
    private float _envelopeDb;

    public VoiceEnhance(int sampleRate, int channels)
    {
        _channels = channels;
        _sampleRate = sampleRate;
        _highPass = new BiQuadFilter[channels];
        BuildFilters();

        _attackCoefficient = MathF.Exp(-1f / (0.010f * sampleRate));
        _releaseCoefficient = MathF.Exp(-1f / (0.200f * sampleRate));
        _makeUpGain = AudioMath.FromDb(MakeUpDb);
        _envelopeDb = AudioMath.MinDb;
    }

    public float GainReductionDb { get; private set; }

    public void Process(Span<float> interleaved)
    {
        var frames = interleaved.Length / _channels;
        var maxReductionDb = 0f;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            var peak = 0f;
            for (var ch = 0; ch < _channels; ch++)
            {
                var filtered = _highPass[ch].Transform(interleaved[baseIndex + ch]);
                interleaved[baseIndex + ch] = filtered;

                var magnitude = MathF.Abs(filtered);
                if (magnitude > peak) peak = magnitude;
            }

            var levelDb = AudioMath.ToDb(peak);
            var coefficient = levelDb > _envelopeDb ? _attackCoefficient : _releaseCoefficient;
            _envelopeDb = (coefficient * _envelopeDb) + ((1f - coefficient) * levelDb);

            var overshootDb = _envelopeDb - ThresholdDb;
            var reductionDb = overshootDb > 0f ? overshootDb * (1f - (1f / Ratio)) : 0f;
            if (reductionDb > maxReductionDb) maxReductionDb = reductionDb;

            var gain = AudioMath.FromDb(-reductionDb) * _makeUpGain;
            for (var ch = 0; ch < _channels; ch++) interleaved[baseIndex + ch] *= gain;
        }

        GainReductionDb = maxReductionDb;
    }

    public void Reset()
    {
        // BiQuadFilter carries no reset, so rebuild to clear the delay line.
        BuildFilters();
        _envelopeDb = AudioMath.MinDb;
        GainReductionDb = 0f;
    }

    private void BuildFilters()
    {
        for (var ch = 0; ch < _channels; ch++)
        {
            // 80 Hz: below the fundamental of any voice, above most desk thumps and AC rumble.
            _highPass[ch] = BiQuadFilter.HighPassFilter(_sampleRate, 80f, 0.707f);
        }
    }
}
