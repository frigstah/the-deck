using Deck.Core.Audio.Dsp;

namespace Deck.EncoderCheck;

/// <summary>
/// Checks the programme processing (E4, E5) by measuring what it actually does to a tone at a given
/// frequency, rather than trusting the coefficients.
/// <para>
/// The one that matters most is crossover flatness. A three-band compressor that is not doing any
/// compressing must be inaudible; if the bands do not sum back flat, every station that leaves it on
/// gets a permanent dip or bump at the crossover and no way to tell where it came from.
/// </para>
/// </summary>
internal static class ProcessingChecks
{
    private const int SampleRate = 48000;

    public static int Run()
    {
        var failures = 0;

        failures += Check("the three bands sum back flat", () =>
        {
            // "Music" applies the same 2 dB of make-up to each band, so below its thresholds the
            // processor is a flat 2 dB gain and nothing else. Any deviation across frequency is the
            // crossover failing to sum.
            double[] frequencies = [60, 120, 250, 400, 800, 1500, 3000, 6000, 12000];
            var worst = 0.0;
            var worstHz = 0.0;

            foreach (var hz in frequencies)
            {
                var gainDb = MeasureProcessor(ProcessingPreset.Music, hz, levelDbFs: -40);
                var error = Math.Abs(gainDb - 2.0);

                if (error > worst)
                {
                    worst = error;
                    worstHz = hz;
                }
            }

            if (worst > 0.5)
            {
                throw new Exception($"the response is {worst:0.00} dB off flat at {worstHz:0} Hz");
            }

            Console.WriteLine($"       within {worst:0.00} dB of flat across 60 Hz to 12 kHz");
        });

        failures += Check("the crossover frequencies themselves stay flat", () =>
        {
            // Right on a crossover is where a wrong polarity or a missing allpass shows up worst.
            foreach (var hz in new double[] { 250, 3000 })
            {
                var gainDb = MeasureProcessor(ProcessingPreset.Music, hz, levelDbFs: -40);
                if (Math.Abs(gainDb - 2.0) > 0.5)
                {
                    throw new Exception($"at the {hz:0} Hz crossover the gain is {gainDb:0.00} dB, expected 2 dB");
                }
            }
        });

        failures += Check("Off leaves the audio completely alone", () =>
        {
            var processor = new MultibandCompressor(SampleRate, 2) { Preset = ProcessingPreset.Off };
            var block = new float[] { 0.1f, -0.2f, 0.3f, -0.4f, 0.5f, -0.6f };
            var original = (float[])block.Clone();

            processor.Process(block);

            for (var i = 0; i < block.Length; i++)
            {
                if (block[i] != original[i]) throw new Exception($"sample {i} changed from {original[i]} to {block[i]}");
            }
        });

        failures += Check("a loud band is pulled down", () =>
        {
            // Well above the -26 dBFS threshold of the Loud preset's mid band, so it must compress.
            var quiet = MeasureProcessor(ProcessingPreset.Loud, 1000, levelDbFs: -40);
            var loud = MeasureProcessor(ProcessingPreset.Loud, 1000, levelDbFs: -6);

            if (loud >= quiet)
            {
                throw new Exception($"loud audio got {loud:0.0} dB and quiet audio {quiet:0.0} dB; the compressor is not working");
            }

            if (quiet - loud < 6)
            {
                throw new Exception($"only {quiet - loud:0.0} dB of difference between -40 and -6 dBFS; that is barely compressing");
            }
        });

        failures += Check("compressing one band leaves the others where they were", () =>
        {
            // The point of multiband: a loud bass note must not duck the vocal range with it.
            var processor = new MultibandCompressor(SampleRate, 2) { Preset = ProcessingPreset.Loud };
            var frames = SampleRate * 2;
            var block = new float[frames * 2];

            for (var i = 0; i < frames; i++)
            {
                // Heavy 80 Hz, quiet 5 kHz on top.
                var sample = (float)((Math.Sin(2 * Math.PI * 80 * i / SampleRate) * 0.8)
                                     + (Math.Sin(2 * Math.PI * 5000 * i / SampleRate) * 0.01));
                block[i * 2] = sample;
                block[(i * 2) + 1] = sample;
            }

            processor.Process(block);

            var highOut = BandLevel(block, 5000);
            var highIn = 0.01;

            // The high band's own make-up is +5 dB; anything close to that means it was left alone.
            var gainDb = 20 * Math.Log10(highOut / highIn);
            if (gainDb < 2)
            {
                throw new Exception($"the quiet 5 kHz tone was pulled down to {gainDb:0.0} dB by the loud bass");
            }
        });

        failures += Check("bass lifts bass and leaves treble alone", () =>
        {
            var lowGain = MeasureTone(low: 6, mid: 0, high: 0, hz: 60);
            var highGain = MeasureTone(low: 6, mid: 0, high: 0, hz: 10000);

            if (Math.Abs(lowGain - 6) > 1.0) throw new Exception($"60 Hz was lifted by {lowGain:0.0} dB, expected 6");
            if (Math.Abs(highGain) > 0.5) throw new Exception($"10 kHz moved by {highGain:0.0} dB and should not have");
        });

        failures += Check("treble lifts treble and leaves bass alone", () =>
        {
            var highGain = MeasureTone(low: 0, mid: 0, high: -6, hz: 10000);
            var lowGain = MeasureTone(low: 0, mid: 0, high: -6, hz: 60);

            if (Math.Abs(highGain + 6) > 1.0) throw new Exception($"10 kHz moved by {highGain:0.0} dB, expected -6");
            if (Math.Abs(lowGain) > 0.5) throw new Exception($"60 Hz moved by {lowGain:0.0} dB and should not have");
        });

        failures += Check("flat tone controls are a true bypass", () =>
        {
            var tone = new ToneControl(SampleRate, 2);
            if (!tone.IsFlat) throw new Exception("a new tone control does not report itself flat");

            var block = new float[] { 0.25f, -0.5f, 0.75f, -1f };
            var original = (float[])block.Clone();

            tone.Process(block);

            for (var i = 0; i < block.Length; i++)
            {
                if (block[i] != original[i]) throw new Exception($"sample {i} changed with the controls centred");
            }
        });

        failures += Check("the controls stop at their limits", () =>
        {
            var tone = new ToneControl(SampleRate, 2) { LowGainDb = 40f, HighGainDb = -40f };

            if (Math.Abs(tone.LowGainDb - ToneControl.MaxGainDb) > 0.01f)
            {
                throw new Exception($"bass accepted {tone.LowGainDb} dB, above the {ToneControl.MaxGainDb} dB limit");
            }

            if (Math.Abs(tone.HighGainDb + ToneControl.MaxGainDb) > 0.01f)
            {
                throw new Exception($"treble accepted {tone.HighGainDb} dB, below the limit");
            }
        });

        return failures;
    }

    /// <summary>Gain the processor applies to a steady tone, in dB, once its envelope has settled.</summary>
    private static double MeasureProcessor(ProcessingPreset preset, double hz, double levelDbFs)
    {
        var processor = new MultibandCompressor(SampleRate, 2) { Preset = preset };
        var amplitude = Math.Pow(10, levelDbFs / 20);

        var frames = SampleRate * 2;
        var block = new float[frames * 2];

        for (var i = 0; i < frames; i++)
        {
            var sample = (float)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * amplitude);
            block[i * 2] = sample;
            block[(i * 2) + 1] = sample;
        }

        processor.Process(block);

        // Measure over the second half only: filters need time to fill and the envelope to settle.
        var output = Rms(block, frames / 2, frames);
        var input = amplitude / Math.Sqrt(2);

        return 20 * Math.Log10(output / input);
    }

    /// <summary>Gain the tone controls apply to a steady tone, in dB.</summary>
    private static double MeasureTone(float low, float mid, float high, double hz)
    {
        var tone = new ToneControl(SampleRate, 2)
        {
            LowGainDb = low,
            MidGainDb = mid,
            HighGainDb = high,
        };

        const double amplitude = 0.25;
        var frames = SampleRate;
        var block = new float[frames * 2];

        for (var i = 0; i < frames; i++)
        {
            var sample = (float)(Math.Sin(2 * Math.PI * hz * i / SampleRate) * amplitude);
            block[i * 2] = sample;
            block[(i * 2) + 1] = sample;
        }

        tone.Process(block);

        var output = Rms(block, frames / 2, frames);
        return 20 * Math.Log10(output / (amplitude / Math.Sqrt(2)));
    }

    private static double Rms(float[] interleaved, int firstFrame, int lastFrame)
    {
        var sum = 0.0;
        for (var i = firstFrame; i < lastFrame; i++)
        {
            var sample = interleaved[i * 2];
            sum += sample * sample;
        }

        return Math.Sqrt(sum / (lastFrame - firstFrame));
    }

    /// <summary>
    /// Amplitude of one frequency in a buffer, by correlating against a sine and cosine at that
    /// frequency. Enough to pick a quiet tone out from under a loud one.
    /// </summary>
    private static double BandLevel(float[] interleaved, double hz)
    {
        var frames = interleaved.Length / 2;
        var start = frames / 2;

        var real = 0.0;
        var imaginary = 0.0;

        for (var i = start; i < frames; i++)
        {
            var angle = 2 * Math.PI * hz * i / SampleRate;
            real += interleaved[i * 2] * Math.Cos(angle);
            imaginary += interleaved[i * 2] * Math.Sin(angle);
        }

        var count = frames - start;
        return 2 * Math.Sqrt((real * real) + (imaginary * imaginary)) / count;
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
