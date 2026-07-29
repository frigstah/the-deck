namespace Deck.Core.Audio.Dsp;

/// <summary>
/// The spectrum half of B9: what frequencies are actually going out, in a handful of bars.
/// <para>
/// Twenty-four bars rather than a smooth curve, spaced by octave-thirds. A 1024-point FFT gives
/// linear bins, which puts eight of them under 400 Hz and four hundred above 10 kHz - the opposite
/// of how hearing works, and the reason a raw FFT display looks like a wall on the left and nothing
/// on the right. Grouping the bins logarithmically gives every bar roughly the same musical width.
/// </para>
/// <para>
/// This is a display, not a measurement. It is fed a copy of the mix from the audio thread and read
/// by the UI at whatever rate it repaints, so the two never wait for each other.
/// </para>
/// </summary>
public sealed class SpectrumAnalyser
{
    /// <summary>
    /// 4096 rather than the 1024 this started as. At 48 kHz a 1024-point transform has bins 47 Hz
    /// apart, which is wider than the bottom five bars are meant to be - so the low end collapsed
    /// into whichever single bin happened to be nearest and the bars beneath 250 Hz were labelled
    /// with frequencies they did not contain. 4096 gives 12 Hz bins, which resolves every band.
    /// The cost is one transform per 85 ms instead of per 21 ms, which is nothing.
    /// </summary>
    public const int FftSize = 4096;

    public const int BandCount = 24;

    /// <summary>Bars fall this far per second when the sound stops. Slow enough to read, fast enough to follow.</summary>
    private const double DecayDbPerSecond = 60.0;

    private const double FloorDb = -72.0;

    private readonly object _lock = new();
    private readonly int _sampleRate;
    private readonly int _channels;

    private readonly float[] _window = new float[FftSize];
    private readonly float[] _pending = new float[FftSize];
    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imaginary = new double[FftSize];
    private readonly int[] _bandStart = new int[BandCount + 1];
    private readonly double[] _bands = new double[BandCount];
    private readonly double[] _display = new double[BandCount];

    private int _pendingCount;

    public SpectrumAnalyser(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = Math.Max(1, channels);

        for (var i = 0; i < FftSize; i++)
        {
            // Hann. A rectangular window would smear a steady tone across neighbouring bars badly
            // enough that a sine looks like noise.
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
        }

        BuildBands();

        for (var i = 0; i < BandCount; i++) _display[i] = FloorDb;
    }

    /// <summary>The lower edge of each bar in Hz, for labelling.</summary>
    public double[] BandEdgesHz { get; } = new double[BandCount + 1];

    /// <summary>
    /// Each bar as 0..1, where 0 is the noise floor and 1 is full scale. Safe to call from the UI
    /// thread at any time; it copies under the lock rather than handing out the live array.
    /// </summary>
    public void Read(Span<double> bars, double elapsedSeconds)
    {
        lock (_lock)
        {
            var fall = DecayDbPerSecond * Math.Clamp(elapsedSeconds, 0, 0.5);

            for (var i = 0; i < BandCount && i < bars.Length; i++)
            {
                // Rises instantly, falls slowly. A bar that fell as fast as the audio does would
                // flicker too much to read at any sensible frame rate.
                _display[i] = _bands[i] >= _display[i] ? _bands[i] : Math.Max(_bands[i], _display[i] - fall);

                bars[i] = Math.Clamp((_display[i] - FloorDb) / -FloorDb, 0, 1);
            }
        }
    }

    /// <summary>Feeds interleaved audio. Analyses a frame whenever enough has arrived.</summary>
    public void Process(ReadOnlySpan<float> interleaved)
    {
        for (var i = 0; i + _channels <= interleaved.Length; i += _channels)
        {
            // Summed to mono. Two spectra would be twice the work to show something that differs
            // between the channels far less often than it is worth the width on screen.
            var sum = 0f;
            for (var c = 0; c < _channels; c++) sum += interleaved[i + c];

            _pending[_pendingCount++] = sum / _channels;

            if (_pendingCount < FftSize) continue;

            Analyse();
            _pendingCount = 0;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_bands);
            for (var i = 0; i < BandCount; i++) _display[i] = FloorDb;
        }

        _pendingCount = 0;
    }

    private void Analyse()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _real[i] = _pending[i] * _window[i];
            _imaginary[i] = 0;
        }

        Fft(_real, _imaginary);

        lock (_lock)
        {
            for (var band = 0; band < BandCount; band++)
            {
                var from = _bandStart[band];
                var to = _bandStart[band + 1];

                var peak = 0.0;

                // Peak within the band rather than an average: averaging over a band that spans
                // forty bins buries a single loud tone under its quiet neighbours.
                for (var bin = from; bin < to; bin++)
                {
                    var power = _real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin];
                    if (power > peak) peak = power;
                }

                // The 2/N and the window's coherent gain together put a full-scale sine at 0 dB.
                var magnitude = Math.Sqrt(peak) * 2.0 / (FftSize * 0.5);
                _bands[band] = magnitude <= 1e-9 ? FloorDb : Math.Max(FloorDb, 20.0 * Math.Log10(magnitude));
            }
        }
    }

    /// <summary>
    /// Bin ranges for logarithmically spaced bars, from 40 Hz to just under Nyquist. Bands that
    /// would be narrower than a bin are widened to one, so nothing comes out permanently empty.
    /// <para>
    /// The reported edges come back out of the bins that were chosen, not out of the ideal spacing
    /// that suggested them. At a low sample rate the two are not the same, and a bar labelled
    /// "52-67 Hz" that is actually showing a 94 Hz bin is worse than no label at all.
    /// </para>
    /// </summary>
    private void BuildBands()
    {
        const double lowestHz = 40.0;
        var highestHz = Math.Min(20000.0, _sampleRate * 0.45);

        var binHz = (double)_sampleRate / FftSize;
        var ratio = Math.Pow(highestHz / lowestHz, 1.0 / BandCount);

        var edge = lowestHz;
        var previousBin = 0;

        for (var band = 0; band <= BandCount; band++)
        {
            var bin = Math.Clamp((int)Math.Round(edge / binHz), 1, FftSize / 2);
            if (band > 0) bin = Math.Clamp(previousBin + 1, bin, FftSize / 2);

            _bandStart[band] = bin;
            BandEdgesHz[band] = bin * binHz;

            previousBin = bin;
            edge *= ratio;
        }
    }

    /// <summary>
    /// In-place iterative radix-2 FFT. Written here rather than pulled in: it is thirty lines, and a
    /// package for it would be another dependency to license, ship and keep current.
    /// </summary>
    private static void Fft(double[] real, double[] imaginary)
    {
        var n = real.Length;

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;

            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i >= j) continue;

            (real[i], real[j]) = (real[j], real[i]);
            (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2.0 * Math.PI / length;
            var stepReal = Math.Cos(angle);
            var stepImaginary = Math.Sin(angle);

            for (var start = 0; start < n; start += length)
            {
                var twiddleReal = 1.0;
                var twiddleImaginary = 0.0;

                for (var k = 0; k < length / 2; k++)
                {
                    var evenIndex = start + k;
                    var oddIndex = evenIndex + length / 2;

                    var oddReal = real[oddIndex] * twiddleReal - imaginary[oddIndex] * twiddleImaginary;
                    var oddImaginary = real[oddIndex] * twiddleImaginary + imaginary[oddIndex] * twiddleReal;

                    real[oddIndex] = real[evenIndex] - oddReal;
                    imaginary[oddIndex] = imaginary[evenIndex] - oddImaginary;
                    real[evenIndex] += oddReal;
                    imaginary[evenIndex] += oddImaginary;

                    var nextReal = twiddleReal * stepReal - twiddleImaginary * stepImaginary;
                    twiddleImaginary = twiddleReal * stepImaginary + twiddleImaginary * stepReal;
                    twiddleReal = nextReal;
                }
            }
        }
    }
}
