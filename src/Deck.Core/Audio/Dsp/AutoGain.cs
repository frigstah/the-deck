namespace Deck.Core.Audio.Dsp;

/// <summary>
/// Automatic gain control (E3): slowly rides the level towards a target so a presenter who drifts
/// away from the microphone, or a guest who is simply quieter, stays audible.
/// <para>
/// Deliberately slow and bounded. AGC that reacts quickly turns the gaps between words into a wash
/// of room noise, which is the usual reason people turn it off again. It also holds its gain during
/// silence rather than winding up, and never boosts more than <see cref="MaxBoostDb"/>.
/// </para>
/// </summary>
public sealed class AutoGain
{
    private const float TargetDb = -16f;
    private const float MaxBoostDb = 12f;
    private const float MaxCutDb = -12f;

    /// <summary>Below this the input counts as silence and the gain is left where it is.</summary>
    private const float SilenceDb = -50f;

    private readonly int _channels;
    private readonly float _riseCoefficient;
    private readonly float _fallCoefficient;
    private float _envelopeDb = TargetDb;
    private float _gainDb;

    public AutoGain(int sampleRate, int channels)
    {
        _channels = channels;

        // Level tracking: quick enough to notice a change, slow enough not to chase syllables.
        _riseCoefficient = MathF.Exp(-1f / (0.050f * sampleRate));
        _fallCoefficient = MathF.Exp(-1f / (1.500f * sampleRate));
    }

    /// <summary>Gain currently being applied, for display.</summary>
    public float GainDb => _gainDb;

    public void Process(Span<float> interleaved)
    {
        var frames = interleaved.Length / _channels;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            var peak = 0f;
            for (var ch = 0; ch < _channels; ch++)
            {
                var magnitude = MathF.Abs(interleaved[baseIndex + ch]);
                if (magnitude > peak) peak = magnitude;
            }

            var levelDb = AudioMath.ToDb(peak);

            if (levelDb > SilenceDb)
            {
                var coefficient = levelDb > _envelopeDb ? _riseCoefficient : _fallCoefficient;
                _envelopeDb = (coefficient * _envelopeDb) + ((1f - coefficient) * levelDb);

                // Move a fraction of the way to the target each sample, so the correction is a
                // gentle ride rather than a step.
                var wanted = Math.Clamp(TargetDb - _envelopeDb, MaxCutDb, MaxBoostDb);
                _gainDb += (wanted - _gainDb) * 0.00002f;
            }

            var gain = AudioMath.FromDb(_gainDb);
            for (var ch = 0; ch < _channels; ch++) interleaved[baseIndex + ch] *= gain;
        }
    }

    public void Reset()
    {
        _envelopeDb = TargetDb;
        _gainDb = 0f;
    }
}
