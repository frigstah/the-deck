namespace Sirs.Core.Audio.Dsp;

/// <summary>
/// Bass, middle and treble (E5). Three controls, not BUTT's ten-band graphic: a broadcaster who
/// needs to lift the bass has no trouble finding "Bass", and one who needs 1.6 kHz cut by 2 dB is
/// running a different kind of station.
/// <para>
/// The gains can be changed while audio is flowing. Coefficients are swapped in place rather than
/// the filters being rebuilt, so moving a slider on air is silent.
/// </para>
/// </summary>
public sealed class ToneControl
{
    /// <summary>Chosen to be recognisably "bass", "presence" and "air" without overlapping much.</summary>
    private const double LowHz = 200;
    private const double MidHz = 1200;
    private const double HighHz = 4500;
    private const double MidQ = 0.7;

    public const float MaxGainDb = 12f;

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly Biquad[] _low;
    private readonly Biquad[] _mid;
    private readonly Biquad[] _high;

    private float _lowGainDb;
    private float _midGainDb;
    private float _highGainDb;

    public ToneControl(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);

        _low = Create(_channels);
        _mid = Create(_channels);
        _high = Create(_channels);

        UpdateCoefficients();
    }

    public float LowGainDb
    {
        get => _lowGainDb;
        set => SetGain(ref _lowGainDb, value);
    }

    public float MidGainDb
    {
        get => _midGainDb;
        set => SetGain(ref _midGainDb, value);
    }

    public float HighGainDb
    {
        get => _highGainDb;
        set => SetGain(ref _highGainDb, value);
    }

    /// <summary>True when every control is centred, so the whole stage can be skipped.</summary>
    public bool IsFlat => _lowGainDb == 0f && _midGainDb == 0f && _highGainDb == 0f;

    public void Process(Span<float> interleaved)
    {
        if (IsFlat) return;

        var frames = interleaved.Length / _channels;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            for (var ch = 0; ch < _channels; ch++)
            {
                var sample = interleaved[baseIndex + ch];
                sample = _low[ch].Process(sample);
                sample = _mid[ch].Process(sample);
                interleaved[baseIndex + ch] = _high[ch].Process(sample);
            }
        }
    }

    public void Reset()
    {
        for (var ch = 0; ch < _channels; ch++)
        {
            _low[ch].Reset();
            _mid[ch].Reset();
            _high[ch].Reset();
        }
    }

    private void SetGain(ref float field, float value)
    {
        var clamped = Math.Clamp(value, -MaxGainDb, MaxGainDb);
        if (Math.Abs(field - clamped) < 0.01f) return;

        field = clamped;
        UpdateCoefficients();
    }

    private void UpdateCoefficients()
    {
        for (var ch = 0; ch < _channels; ch++)
        {
            _low[ch].SetLowShelf(_sampleRate, LowHz, _lowGainDb);
            _mid[ch].SetPeaking(_sampleRate, MidHz, MidQ, _midGainDb);
            _high[ch].SetHighShelf(_sampleRate, HighHz, _highGainDb);
        }
    }

    private static Biquad[] Create(int channels)
    {
        var filters = new Biquad[channels];
        for (var i = 0; i < channels; i++) filters[i] = new Biquad();
        return filters;
    }
}
