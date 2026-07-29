using NAudio.Dsp;

namespace Sirs.Core.Audio;

/// <summary>
/// Push-mode sample rate conversion wrapping WDL's windowed-sinc resampler. Used whenever the
/// capture device rate differs from the stream rate (D8) - which, given devices default to 48 kHz
/// and most stations run 44.1 kHz, is most of the time.
/// </summary>
public sealed class Resampler
{
    private readonly WdlResampler _resampler = new();
    private readonly int _channels;
    private readonly double _ratio;
    private float[] _output = new float[8192];

    public Resampler(int inputRate, int outputRate, int channels)
    {
        _channels = channels;
        _ratio = (double)outputRate / inputRate;
        InputRate = inputRate;
        OutputRate = outputRate;

        // Sinc filtering with a 64-tap window: transparent for broadcast, cheap enough that a
        // single stream costs a fraction of a core.
        _resampler.SetMode(interp: true, filtercnt: 0, sinc: true, sinc_size: 64, sinc_interpsize: 32);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(wantInputDriven: true);
        _resampler.SetRates(inputRate, outputRate);
    }

    public int InputRate { get; }

    public int OutputRate { get; }

    public bool IsPassthrough => InputRate == OutputRate;

    /// <summary>
    /// Feeds one interleaved block and returns the resampled result. The returned span is valid
    /// until the next call - callers consume it immediately, so no copy is made.
    /// </summary>
    public ReadOnlySpan<float> Process(ReadOnlySpan<float> input)
    {
        if (IsPassthrough) return input;

        var inputFrames = input.Length / _channels;
        if (inputFrames == 0) return ReadOnlySpan<float>.Empty;

        var wanted = _resampler.ResamplePrepare(inputFrames, _channels, out var scratch, out var scratchOffset);
        var framesToWrite = Math.Min(wanted, inputFrames);

        input[..(framesToWrite * _channels)].CopyTo(scratch.AsSpan(scratchOffset));

        // Allow generous headroom: the resampler can emit slightly more than the ratio suggests
        // when it flushes buffered input.
        var maxOutputFrames = (int)(framesToWrite * _ratio) + 64;
        EnsureOutput(maxOutputFrames * _channels);

        var producedFrames = _resampler.ResampleOut(_output, 0, framesToWrite, maxOutputFrames, _channels);
        return _output.AsSpan(0, producedFrames * _channels);
    }

    public void Reset() => _resampler.Reset(0);

    private void EnsureOutput(int samples)
    {
        if (_output.Length < samples) _output = new float[samples * 2];
    }
}
