using Deck.Core.Audio.Dsp;

namespace Deck.EncoderCheck;

/// <summary>
/// Loudness metering checked against EBU Tech 3341, the published compliance cases for an EBU Mode
/// meter. These numbers come from the standard, not from this codebase, which is what makes them
/// worth running: a K-weighting filter with a wrong coefficient still produces a plausible-looking
/// number, and only an external reference catches that.
/// </summary>
internal static class LoudnessChecks
{
    private const int SampleRate = 48000;
    private const double ToneHz = 1000;

    /// <summary>Tech 3341 requires an EBU Mode meter to be within ±0.1 LU on these cases.</summary>
    private const double Tolerance = 0.1;

    public static int Run()
    {
        var failures = 0;

        failures += Check("Tech 3341 case 1 — stereo tone at -23 dBFS reads -23.0 LUFS", () =>
        {
            var meter = Measure([Segment(20, -23, stereo: true)]);
            ExpectLufs(meter.IntegratedLufs, -23.0);
        });

        failures += Check("Tech 3341 case 2 — the same tone at -33 dBFS reads -33.0 LUFS", () =>
        {
            var meter = Measure([Segment(20, -33, stereo: true)]);
            ExpectLufs(meter.IntegratedLufs, -33.0);
        });

        failures += Check("one channel only reads 3 LU lower", () =>
        {
            // Tech 3341 case 2's variants: the same tone on a single channel is half the power.
            var meter = Measure([Segment(20, -23, stereo: false)]);
            ExpectLufs(meter.IntegratedLufs, -26.0);
        });

        failures += Check("Tech 3341 case 3 — quiet passages are gated out", () =>
        {
            // The -36 dBFS sections sit 13 LU below the -23 body, so the relative gate must drop
            // them entirely. Without the gate this reads about a decibel low.
            var meter = Measure(
            [
                Segment(10, -36, stereo: true),
                Segment(60, -23, stereo: true),
                Segment(10, -36, stereo: true),
            ]);

            ExpectLufs(meter.IntegratedLufs, -23.0);
        });

        failures += Check("Tech 3341 case 4 — near-silence is gated out too", () =>
        {
            // -72 dBFS is below the absolute gate and must not be averaged in at all.
            var meter = Measure(
            [
                Segment(10, -72, stereo: true),
                Segment(10, -36, stereo: true),
                Segment(60, -23, stereo: true),
                Segment(10, -36, stereo: true),
                Segment(10, -72, stereo: true),
            ]);

            ExpectLufs(meter.IntegratedLufs, -23.0);
        });

        failures += Check("Tech 3341 case 5 — levels within the gate are all counted", () =>
        {
            // Everything here is within 10 LU of the average, so nothing is dropped and the answer
            // is the plain power mean. This is the case a too-aggressive gate would get wrong.
            var meter = Measure(
            [
                Segment(20, -26, stereo: true),
                Segment(20.1, -20, stereo: true),
                Segment(20, -26, stereo: true),
            ]);

            ExpectLufs(meter.IntegratedLufs, -23.0);
        });

        failures += Check("short-term follows the audio, integrated averages power", () =>
        {
            // 20 s at -18 LUFS then 10 s at -28. Short-term should land on the quiet section, while
            // the whole-show figure averages power, not decibels: (20·10^-1.8 + 10·10^-2.8) / 30 is
            // -19.55 LUFS, far closer to the loud part than the midpoint of the two numbers.
            var meter = Measure([Segment(20, -18, stereo: true), Segment(10, -28, stereo: true)]);

            ExpectClose(meter.ShortTermLufs, -28.0, 0.3, "short-term");
            ExpectClose(meter.IntegratedLufs, -19.55, 0.3, "whole-show loudness");
        });

        failures += Check("silence never produces a reading", () =>
        {
            var meter = new LoudnessMeter(SampleRate, 2);
            var block = new float[4800 * 2];

            for (var i = 0; i < 100; i++) meter.Process(block);

            if (meter.HasIntegrated) throw new Exception("silence produced an integrated loudness");
            if (!double.IsNegativeInfinity(meter.IntegratedLufs))
            {
                throw new Exception($"silence read {meter.IntegratedLufs} instead of nothing at all");
            }
        });

        failures += Check("44.1 kHz measures the same as 48 kHz", () =>
        {
            // The K-weighting coefficients in the standard are given for 48 kHz only. These are
            // derived from the analog prototype instead, so the answer has to hold at 44.1 - which
            // is what most stations actually run.
            var meter = Measure([Segment(20, -23, stereo: true)], 44100);
            ExpectLufs(meter.IntegratedLufs, -23.0);
        });

        failures += Check("restarting clears the whole-show figure", () =>
        {
            var meter = Measure([Segment(10, -23, stereo: true)]);
            if (!meter.HasIntegrated) throw new Exception("nothing was measured to begin with");

            meter.Reset();
            if (meter.HasIntegrated) throw new Exception("the integrated figure survived a reset");
        });

        return failures;
    }

    private record Tone(double Seconds, double LevelDbFs, bool Stereo);

    private static Tone Segment(double seconds, double levelDbFs, bool stereo) =>
        new(seconds, levelDbFs, stereo);

    /// <summary>
    /// Feeds the segments through in 10 ms blocks, keeping the sine's phase continuous across the
    /// joins so a level change does not put a click in the signal.
    /// </summary>
    private static LoudnessMeter Measure(Tone[] segments, int sampleRate = SampleRate)
    {
        var meter = new LoudnessMeter(sampleRate, 2);
        var blockFrames = sampleRate / 100;
        var block = new float[blockFrames * 2];
        long sampleIndex = 0;

        foreach (var segment in segments)
        {
            // Level is given the way the standard gives it: dBFS relative to a full-scale sine, so
            // it is the peak amplitude that is scaled.
            var amplitude = Math.Pow(10, segment.LevelDbFs / 20);
            var blocks = (int)Math.Round(segment.Seconds * 100);

            for (var b = 0; b < blocks; b++)
            {
                for (var i = 0; i < blockFrames; i++)
                {
                    var sample = (float)(Math.Sin(2 * Math.PI * ToneHz * sampleIndex / sampleRate) * amplitude);
                    block[i * 2] = sample;
                    block[(i * 2) + 1] = segment.Stereo ? sample : 0f;
                    sampleIndex++;
                }

                meter.Process(block);
            }
        }

        return meter;
    }

    private static void ExpectLufs(double actual, double expected) =>
        ExpectClose(actual, expected, Tolerance, "loudness");

    private static void ExpectClose(double actual, double expected, double tolerance, string what)
    {
        if (double.IsNegativeInfinity(actual))
        {
            throw new Exception($"{what} was not measured at all, expected {expected:0.0} LUFS");
        }

        if (Math.Abs(actual - expected) > tolerance)
        {
            throw new Exception(
                $"{what} read {actual:0.00} LUFS, expected {expected:0.0} ±{tolerance:0.0}");
        }
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
