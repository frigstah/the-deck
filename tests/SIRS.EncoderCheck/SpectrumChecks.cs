using Sirs.Core.Audio;
using Sirs.Core.Audio.Dsp;

namespace Sirs.EncoderCheck;

/// <summary>
/// The spectrum and phase display (B9).
/// <para>
/// A display is easy to get subtly wrong and never notice, because a plausible-looking wall of bars
/// is indistinguishable from a correct one by eye. So the FFT is checked against a brute-force DFT
/// computed here - a different algorithm, not a rearrangement of the same one - and the correlation
/// meter against signals whose answer can be worked out on paper.
/// </para>
/// </summary>
internal static class SpectrumChecks
{
    private const int SampleRate = 48000;

    public static int Run()
    {
        var failures = 0;

        failures += Check("the FFT agrees with a plain DFT", () =>
        {
            // Deliberately not a single sine: a sum of unrelated tones plus a DC offset exercises
            // the butterflies at every stage, where a pure tone can pass by luck.
            var real = new double[64];
            var imaginary = new double[64];

            for (var i = 0; i < real.Length; i++)
            {
                real[i] = 0.3
                    + Math.Sin(2 * Math.PI * 3 * i / real.Length)
                    + 0.5 * Math.Cos(2 * Math.PI * 11 * i / real.Length)
                    - 0.25 * Math.Sin(2 * Math.PI * 27 * i / real.Length);
            }

            var (expectedReal, expectedImaginary) = Dft(real);
            InvokeFft(real, imaginary);

            for (var bin = 0; bin < real.Length; bin++)
            {
                var error = Math.Abs(real[bin] - expectedReal[bin]) + Math.Abs(imaginary[bin] - expectedImaginary[bin]);
                Expect(error < 1e-9, $"bin {bin} differs from the DFT by {error:E2}");
            }
        });

        failures += Check("a tone lands in the right bar", () =>
        {
            // 1 kHz and 100 Hz are two and a half octaves apart, so they must not share a bar.
            var lowBar = LoudestBar(Tone(100));
            var highBar = LoudestBar(Tone(1000));

            Expect(lowBar < highBar, $"100 Hz landed in bar {lowBar} and 1 kHz in bar {highBar}");

            var edges = new SpectrumAnalyser(SampleRate, 2).BandEdgesHz;

            Expect(100 >= edges[lowBar] && 100 < edges[lowBar + 1],
                $"100 Hz landed in the bar covering {edges[lowBar]:0}–{edges[lowBar + 1]:0} Hz");

            Expect(1000 >= edges[highBar] && 1000 < edges[highBar + 1],
                $"1 kHz landed in the bar covering {edges[highBar]:0}–{edges[highBar + 1]:0} Hz");
        });

        failures += Check("a full-scale tone reads near the top and silence near the bottom", () =>
        {
            var loud = Bars(Tone(1000));
            Expect(loud.Max() > 0.9, $"a full-scale tone only reached {loud.Max():0.00} of the scale");

            var quiet = Bars(new float[SampleRate * 2]);
            Expect(quiet.Max() < 0.02, $"silence read as {quiet.Max():0.00}");
        });

        failures += Check("the bars fall rather than flicker", () =>
        {
            var analyser = new SpectrumAnalyser(SampleRate, 2);
            analyser.Process(Tone(1000));

            var bars = new double[SpectrumAnalyser.BandCount];
            analyser.Read(bars, 0.02);
            var peak = bars.Max();

            // The sound stops - which in a live pipeline means silent samples keep arriving, not
            // that audio stops being delivered. One frame later the bar must still be visible, and
            // a second later it must be gone. The two together are what "falls smoothly" means.
            var silence = new float[SampleRate / 50 * 2];
            analyser.Process(silence);
            analyser.Read(bars, 0.02);

            Expect(bars.Max() > peak * 0.8, $"the bar dropped from {peak:0.00} to {bars.Max():0.00} in one frame");

            // The bars span 72 dB and fall at 60 dB per second, so a full drop takes 1.2 s. Checked
            // either side of that rather than at a round number: at one second there should still be
            // something visible, and by a second and a half it should be gone.
            for (var i = 0; i < 50; i++)
            {
                analyser.Process(silence);
                analyser.Read(bars, 0.02);
            }

            var afterOneSecond = bars.Max();
            Expect(afterOneSecond is > 0.05 and < 0.35,
                $"a second after the sound stopped the bar was at {afterOneSecond:0.00}, expected part-way down");

            for (var i = 0; i < 25; i++)
            {
                analyser.Process(silence);
                analyser.Read(bars, 0.02);
            }

            Expect(bars.Max() < 0.01,
                $"the bar was still at {bars.Max():0.00} a second and a half after the sound stopped");
        });

        // ---------------------------------------------------------------- correlation

        failures += Check("identical channels read as mono", () =>
        {
            var meter = Measure((left, _) => (left, left));

            Expect(meter.Correlation > 0.99, $"identical channels read {meter.Correlation:0.000}");
            Expect(Math.Abs(meter.MonoLevelRatio - 1.0) < 0.02,
                $"summing identical channels changed the level by {meter.MonoLevelRatio:0.000}");
        });

        failures += Check("an inverted channel is caught", () =>
        {
            // The fault this whole meter exists for: a microphone cable wired backwards. It sounds
            // normal in stereo and disappears on a phone speaker.
            var meter = Measure((left, _) => (left, -left));

            Expect(meter.Correlation < -0.99, $"an inverted channel read {meter.Correlation:0.000}");
            Expect(meter.MonoLevelRatio < 0.02,
                $"{meter.MonoLevelRatio:P0} of the level survived mono, when it should have cancelled");

            Expect(meter.Severity() == AdviceSeverity.Bad, "an out-of-phase signal was not reported as bad");
            Expect(meter.Verdict().Contains("mono", StringComparison.OrdinalIgnoreCase),
                $"the warning did not mention mono: \"{meter.Verdict()}\"");
        });

        failures += Check("unrelated channels read as wide, not broken", () =>
        {
            // Two different frequencies are orthogonal over a whole number of cycles, so the honest
            // answer is zero: wide, no cancellation, nothing wrong.
            var meter = Measure((_, i) => (
                (float)Math.Sin(2 * Math.PI * 300 * i / SampleRate),
                (float)Math.Sin(2 * Math.PI * 700 * i / SampleRate)));

            Expect(Math.Abs(meter.Correlation) < 0.15, $"unrelated channels read {meter.Correlation:0.000}");
            Expect(meter.Severity() != AdviceSeverity.Bad, "wide stereo was reported as a fault");
        });

        failures += Check("a mono input is not reported as a phase problem", () =>
        {
            var meter = new CorrelationMeter(SampleRate);
            var mono = new float[SampleRate / 2];

            for (var i = 0; i < mono.Length; i++) mono[i] = (float)Math.Sin(2 * Math.PI * 440 * i / SampleRate);

            meter.Process(mono, channels: 1);

            Expect(meter.Correlation > 0.99, $"a mono input read {meter.Correlation:0.000}");
        });

        failures += Check("silence holds the last reading instead of swinging", () =>
        {
            var meter = Measure((left, _) => (left, -left));
            var before = meter.Correlation;

            // Two seconds, fed in blocks the size the audio thread actually delivers.
            for (var i = 0; i < 100; i++) meter.Process(new float[SampleRate / 50 * 2], channels: 2);

            Expect(Math.Abs(meter.Correlation - before) < 0.01,
                $"the reading moved from {before:0.000} to {meter.Correlation:0.000} during silence");

            Expect(meter.IsQuiet, "silence was not reported as too quiet to measure");
            Expect(meter.Severity() == AdviceSeverity.Neutral, "silence was given a verdict");
        });

        return failures;
    }

    private static CorrelationMeter Measure(Func<float, int, (float Left, float Right)> shape)
    {
        var meter = new CorrelationMeter(SampleRate);
        var block = new float[SampleRate * 2];

        for (var i = 0; i < SampleRate; i++)
        {
            var source = (float)Math.Sin(2 * Math.PI * 440 * i / SampleRate);
            var (left, right) = shape(source, i);

            block[i * 2] = left;
            block[i * 2 + 1] = right;
        }

        meter.Process(block, channels: 2);
        return meter;
    }

    private static float[] Tone(double hz)
    {
        var samples = new float[SampleRate * 2];

        for (var i = 0; i < SampleRate; i++)
        {
            var value = (float)Math.Sin(2 * Math.PI * hz * i / SampleRate);
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return samples;
    }

    private static double[] Bars(float[] interleaved)
    {
        var analyser = new SpectrumAnalyser(SampleRate, 2);
        analyser.Process(interleaved);

        var bars = new double[SpectrumAnalyser.BandCount];
        analyser.Read(bars, 0.0);
        return bars;
    }

    private static int LoudestBar(float[] interleaved)
    {
        var bars = Bars(interleaved);
        var best = 0;

        for (var i = 1; i < bars.Length; i++)
        {
            if (bars[i] > bars[best]) best = i;
        }

        return best;
    }

    /// <summary>The definition, computed directly. Slow, and that is the point - it shares no code.</summary>
    private static (double[] Real, double[] Imaginary) Dft(double[] input)
    {
        var n = input.Length;
        var real = new double[n];
        var imaginary = new double[n];

        for (var k = 0; k < n; k++)
        {
            for (var t = 0; t < n; t++)
            {
                var angle = -2.0 * Math.PI * k * t / n;
                real[k] += input[t] * Math.Cos(angle);
                imaginary[k] += input[t] * Math.Sin(angle);
            }
        }

        return (real, imaginary);
    }

    /// <summary>
    /// Reaches the FFT the only way it is exposed - through a spectrum whose size matches. The
    /// transform is private because nothing outside the analyser should be calling it.
    /// </summary>
    private static void InvokeFft(double[] real, double[] imaginary)
    {
        var method = typeof(SpectrumAnalyser).GetMethod(
            "Fft",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new Exception("the FFT has been renamed; this check needs updating");

        method.Invoke(null, [real, imaginary]);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  ok   {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }
}
