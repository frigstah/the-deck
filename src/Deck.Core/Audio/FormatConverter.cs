namespace Deck.Core.Audio;

/// <summary>
/// Channel-maps then resamples an interleaved block from one <see cref="AudioFormat"/> to another.
/// <para>
/// Both stages in that order on purpose: downmixing first means the resampler, the expensive half,
/// only ever runs over the channels that survive. It also matters when going stereo to mono, where
/// resampling first would do twice the work for the same result.
/// </para>
/// </summary>
public sealed class FormatConverter
{
    private readonly Resampler? _resampler;
    private float[] _mapped = new float[8192];

    public FormatConverter(AudioFormat source, AudioFormat target)
    {
        Source = source;
        Target = target;

        _resampler = source.SampleRate == target.SampleRate
            ? null
            : new Resampler(source.SampleRate, target.SampleRate, target.Channels);
    }

    public AudioFormat Source { get; }

    public AudioFormat Target { get; }

    /// <summary>True when this converter has nothing to do and simply hands the block back.</summary>
    public bool IsPassthrough => Source == Target;

    /// <summary>
    /// Converts one block. The returned span stays valid only until the next call, which is enough
    /// for every caller here: they encode it before returning.
    /// </summary>
    public ReadOnlySpan<float> Process(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.IsEmpty) return interleaved;

        var source = interleaved;

        if (Source.Channels != Target.Channels)
        {
            var frames = interleaved.Length / Source.Channels;
            var required = frames * Target.Channels;
            if (_mapped.Length < required) _mapped = new float[required * 2];

            var length = ChannelMapper.Map(interleaved, Source.Channels, _mapped, Target.Channels);
            source = _mapped.AsSpan(0, length);
        }

        return _resampler is null ? source : _resampler.Process(source);
    }

    public void Reset() => _resampler?.Reset();
}
