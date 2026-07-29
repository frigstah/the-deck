using NAudio.CoreAudioApi;
using NAudio.Wave;
using Deck.Core.Audio;
using Deck.Core.Codecs;

namespace Deck.EncoderCheck;

/// <summary>
/// Verifies loopback capture (A4) end to end: play a quiet tone through a render device while
/// capturing that same device via WASAPI loopback, and confirm the tone comes back.
/// <para>
/// Opt-in via <c>--loopback</c> because it needs audio hardware and briefly makes real sound.
/// </para>
/// </summary>
internal static class LoopbackCheck
{
    public static int Run()
    {
        Console.WriteLine("--- Device enumeration ---");

        var inputs = AudioDevices.Inputs();
        var loopbacks = AudioDevices.LoopbackSources();
        var all = AudioDevices.AllInputSources();

        foreach (var device in all)
        {
            Console.WriteLine($"  [{device.CategoryLabel}] {device.DisplayName}");
        }

        if (loopbacks.Count == 0)
        {
            Console.WriteLine("FAIL: no loopback sources found\n");
            return 1;
        }

        if (all.Count != inputs.Count + loopbacks.Count)
        {
            Console.WriteLine("FAIL: combined list does not match its parts\n");
            return 1;
        }

        // Real inputs must lead the list; a loopback source is never a sensible default.
        var firstLoopback = all.ToList().FindIndex(d => d.Kind == AudioDeviceKind.Loopback);
        if (inputs.Count > 0 && firstLoopback != inputs.Count)
        {
            Console.WriteLine("FAIL: loopback sources are not ordered after the real inputs\n");
            return 1;
        }

        Console.WriteLine("PASS\n");

        Console.WriteLine("--- Loopback capture ---");
        var target = loopbacks.FirstOrDefault(d => d.IsSystemDefault) ?? loopbacks[0];
        Console.WriteLine($"  capturing: {target.Name}");

        try
        {
            var peakDb = CaptureWhilePlaying(target);
            Console.WriteLine($"  captured peak {peakDb:0.0} dBFS");

            if (peakDb < -50f)
            {
                Console.WriteLine("FAIL: the tone did not come back through loopback\n");
                return 1;
            }

            Console.WriteLine("PASS\n");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.Message}\n");
            return 1;
        }
    }

    /// <summary>
    /// Mixing check (A5): capture the same playing device on both faders and confirm the second
    /// source really reaches the mix.
    /// <para>
    /// The two captures start microseconds apart and the secondary is delayed by the ring buffer,
    /// so the copies arrive slightly out of phase. Perfectly aligned they would sum to +6 dB; a
    /// fraction of a millisecond of offset is enough to pull that down several dB. The band below
    /// is therefore deliberately wide - the assertion is "the second fader contributes", not an
    /// exact figure that would depend on run-to-run phase luck.
    /// </para>
    /// </summary>
    public static int RunMixer()
    {
        Console.WriteLine("--- Two-source mixing ---");

        var loopbacks = AudioDevices.LoopbackSources();
        if (loopbacks.Count == 0)
        {
            Console.WriteLine("FAIL: no loopback sources found\n");
            return 1;
        }

        var target = loopbacks.FirstOrDefault(d => d.IsSystemDefault) ?? loopbacks[0];
        Console.WriteLine($"  both faders on: {target.Name}");

        try
        {
            var single = CaptureWhilePlaying(target, null);
            Console.WriteLine($"  one source:  {single:0.0} dBFS");

            var mixed = CaptureWhilePlaying(target, target);
            Console.WriteLine($"  two sources: {mixed:0.0} dBFS");

            var gain = mixed - single;
            Console.WriteLine($"  difference:  {gain:+0.0;-0.0} dB (+6 if perfectly in phase, less in practice)");

            if (gain < 2.5f)
            {
                Console.WriteLine("FAIL: the second source is not reaching the mix\n");
                return 1;
            }

            if (gain > 8f)
            {
                Console.WriteLine("FAIL: the mix is far louder than two copies should be - audio is being counted twice\n");
                return 1;
            }

            Console.WriteLine("PASS\n");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.Message}\n");
            return 1;
        }
    }

    private static float CaptureWhilePlaying(AudioDevice target, AudioDevice? secondary = null)
    {
        var engine = new CaptureEngine();
        var peak = 0f;
        var blocks = 0;

        engine.BlockCaptured += (samples, _) =>
        {
            blocks++;
            foreach (var sample in samples)
            {
                var magnitude = MathF.Abs(sample);
                if (magnitude > peak) peak = magnitude;
            }
        };

        engine.Start(target.Id, AudioDeviceKind.Loopback, QualityPreset.Default.Settings.Normalised().Format);
        if (secondary is not null) engine.StartSecondary(secondary.Id, AudioDeviceKind.Loopback);

        // Quiet and short: this is a test, not a performance.
        using var device = AudioDevices.Resolve(target.Id)
            ?? throw new Exception("render device disappeared between enumeration and playback");

        var provider = new ToneProvider(440, 0.15f, 48000, 2);
        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 80);
        output.Init(provider);
        output.Play();

        // Let the mix settle before measuring: the secondary buffer starts empty and takes a moment
        // to reach its target depth, during which the sum would read low.
        Thread.Sleep(700);
        peak = 0f;
        blocks = 0;

        Thread.Sleep(1500);

        output.Stop();
        engine.Stop();
        engine.Dispose();

        Console.WriteLine($"  {blocks} blocks captured");
        return AudioMath.ToDb(peak);
    }

    private sealed class ToneProvider(double frequency, float amplitude, int sampleRate, int channels) : IWaveProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = count / sizeof(float) / channels;
            var written = 0;

            for (var frame = 0; frame < frames; frame++)
            {
                var value = (float)(Math.Sin(2 * Math.PI * frequency * _position / sampleRate) * amplitude);
                _position++;

                for (var ch = 0; ch < channels; ch++)
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(offset + written), value);
                    written += sizeof(float);
                }
            }

            return written;
        }
    }
}
