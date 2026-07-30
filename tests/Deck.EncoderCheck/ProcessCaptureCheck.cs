using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Deck.Core.Audio;

namespace Deck.EncoderCheck;

/// <summary>
/// Verifies that Deck can take one program's sound and only that program's (A9).
/// <para>
/// The claim being tested is isolation, not merely "some audio arrived" - so two helper processes play
/// two different tones at the same time through the same output device, Deck captures one of them, and
/// the check looks for the one tone and the absence of the other. Whole-desktop loopback would hear
/// both; that is exactly the difference this feature exists for, and a test that only looked for signal
/// would pass just as happily on the old behaviour.
/// </para>
/// <para>
/// The helpers are this same executable re-run with <c>--tone</c>, so there is nothing extra to build
/// and no dependence on what happens to be installed on the machine.
/// </para>
/// <para>
/// Opt-in via <c>--process</c>: it needs audio hardware and briefly makes real sound.
/// </para>
/// </summary>
internal static class ProcessCaptureCheck
{
    private const int MineHz = 440;
    private const int TheirsHz = 1600;
    private const int SampleRate = 48000;

    public static int Run()
    {
        Console.WriteLine("--- One program's sound on its own (A9) ---");

        Console.WriteLine($"  Windows build {Environment.OSVersion.Version.Build}, " +
                          $"process capture {(ProcessLoopbackCapture.IsSupported ? "supported" : "NOT supported")}");

        if (!ProcessLoopbackCapture.IsSupported)
        {
            // Not a failure. The feature is meant to be absent here, and the check that matters on this
            // machine is that Deck offers nothing it cannot deliver.
            var offered = AudioProcesses.Playing();
            Console.WriteLine(offered.Count == 0
                ? "  ok   nothing is offered on a version of Windows that cannot do it\n  PASS\n"
                : $"  FAIL {offered.Count} program(s) offered on a Windows that cannot capture them\n");
            return offered.Count == 0 ? 0 : 1;
        }

        var failures = 0;

        Console.WriteLine("  starting two programs, each playing a different tone…");

        using var mine = StartTone(MineHz);
        using var theirs = StartTone(TheirsHz);

        // Long enough for both to have opened their device and be actually rendering.
        Thread.Sleep(2500);

        if (mine.HasExited || theirs.HasExited)
        {
            Console.WriteLine("  FAIL a tone helper exited early; no audio hardware?\n");
            Kill(mine, theirs);
            return 1;
        }

        Console.WriteLine($"  program A is pid {mine.Id} at {MineHz} Hz, program B is pid {theirs.Id} at {TheirsHz} Hz");

        // ---------------------------------------------------------------- what the sessions say
        var offeredNow = AudioProcesses.Playing();
        Console.WriteLine($"  Windows lists {offeredNow.Count} program(s) playing: " +
                          string.Join(", ", offeredNow.Select(d => d.Name)));

        failures += Check("the helper appears in the list of programs playing", () =>
        {
            var us = Process.GetCurrentProcess().ProcessName;
            Expect(offeredNow.Any(d => d.Id.Contains(us, StringComparison.OrdinalIgnoreCase)),
                $"nothing named like \"{us}\" was offered, though two of them are playing tones");
        });

        failures += Check("a program is offered by name, not by process id", () =>
        {
            foreach (var device in offeredNow)
            {
                Expect(device.Kind == AudioDeviceKind.Process, $"{device.Name} came back as {device.Kind}");
                Expect(ProcessLoopbackCapture.IsProcessId(device.Id), $"{device.Id} is not a program id");

                var name = ProcessLoopbackCapture.ProgramNameFrom(device.Id);
                Expect(!int.TryParse(name, out _), $"the stored id \"{device.Id}\" is a process id, which will be wrong tomorrow");
            }
        });

        // ---------------------------------------------------------------- the capture itself
        float[]? captured = null;

        failures += Check("audio arrives from the program that was asked for", () =>
        {
            captured = Capture(mine.Id, TimeSpan.FromSeconds(3), out var format);

            Console.WriteLine($"       captured {captured.Length / Math.Max(1, format.Channels)} frames at " +
                              $"{format.SampleRate} Hz, {format.Channels}ch, {format.BitsPerSample}-bit " +
                              $"{format.Encoding}");

            Expect(captured.Length > format.SampleRate, // at least half a second of audio
                $"only {captured.Length} samples arrived");

            var mineEnergy = Energy(captured, format, MineHz);
            var theirsEnergy = Energy(captured, format, TheirsHz);
            var floor = Energy(captured, format, 5000);

            Console.WriteLine($"       {MineHz} Hz: {Db(mineEnergy):0.0} dB   " +
                              $"{TheirsHz} Hz: {Db(theirsEnergy):0.0} dB   " +
                              $"5 kHz (nothing there): {Db(floor):0.0} dB");

            Expect(mineEnergy > floor * 100,
                $"the tone that should be there is only {Db(mineEnergy) - Db(floor):0.0} dB above the noise floor");

            // The whole point. Whole-desktop loopback would have both.
            Expect(mineEnergy > theirsEnergy * 100,
                $"the other program's tone came through too: {MineHz} Hz is only " +
                $"{Db(mineEnergy) - Db(theirsEnergy):0.0} dB above {TheirsHz} Hz");
        });

        failures += Check("a program that is playing nothing still keeps time", () =>
        {
            // This decides whether a program may be the *main* input rather than only the second one.
            // The main source owns the clock: its callbacks are what push blocks through the mix, so if
            // a silent program delivers nothing at all, choosing one as the main input would stop the
            // whole broadcast the moment the music paused. Measured, because the alternative is to guess
            // and find out on air.
            using var quiet = Process.Start(new ProcessStartInfo(
                Environment.ProcessPath!, "--tone 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new Exception("could not start a silent helper");

            try
            {
                Thread.Sleep(1500);

                using var capture = new ProcessLoopbackCapture(quiet.Id, "a silent program", SampleRate);
                var blocks = 0;
                var bytes = 0L;

                capture.DataAvailable += (_, e) => { blocks++; bytes += e.BytesRecorded; };
                capture.StartRecording();
                Thread.Sleep(2000);
                capture.StopRecording();

                Console.WriteLine($"       a silent program delivered {blocks} block(s), {bytes / 1024} KB in two seconds");

                Expect(blocks > 20,
                    $"a silent program delivered only {blocks} blocks, so it cannot be trusted to drive the clock");
            }
            finally
            {
                Kill(quiet);
            }
        });

        failures += Check("a program that is not running is refused with something to act on", () =>
        {
            var source = new AudioSource("a program that is not there");
            try
            {
                source.Start(
                    ProcessLoopbackCapture.IdFor("deck-no-such-program"),
                    AudioDeviceKind.Process,
                    AudioFormat.CdStereo);

                throw new Exception("started capturing a program that does not exist");
            }
            catch (AudioDeviceUnavailableException ex)
            {
                Expect(ex.Message.Contains("deck-no-such-program"), $"the message does not name it: {ex.Message}");
                Expect(ex.Message.Contains("running"), $"the message does not say what is wrong: {ex.Message}");
            }
            finally
            {
                source.Dispose();
            }
        });

        failures += Check("the mix carries the microphone and the program together", () =>
        {
            // The actual feature: two sources summed. The primary is a real input so the clock is a real
            // device's, exactly as it would be on stage; the program is the second source.
            var inputs = AudioDevices.Inputs();
            if (inputs.Count == 0) throw new Exception("no capture device to be the microphone");

            using var engine = new CaptureEngine();
            var blocks = 0;
            var samples = new List<float>();

            engine.BlockCaptured += (interleaved, _) =>
            {
                blocks++;
                if (samples.Count < SampleRate * 4) samples.AddRange(interleaved);
            };

            engine.Start(inputs[0].Id, AudioDeviceKind.Input, AudioFormat.CdStereo);
            engine.StartSecondary(ProcessLoopbackCapture.IdFor(Process.GetCurrentProcess().ProcessName), AudioDeviceKind.Process);

            // The secondary is whichever helper Windows finds first by name; both are playing, so the
            // mix should carry a tone either way.
            Thread.Sleep(3000);

            var mixing = engine.IsMixing;
            var dropped = engine.SecondaryDroppedSamples;
            engine.Stop();

            Console.WriteLine($"       {blocks} block(s) through the mix, dropped {dropped} secondary sample(s)");

            Expect(mixing, "the second source stopped on its own");
            Expect(blocks > 10, $"only {blocks} blocks came through the mix");
            Expect(dropped == 0, $"{dropped} secondary samples were dropped, so the mix could not keep up");

            var mix = samples.ToArray();
            var format = new WaveFormat(AudioFormat.CdStereo.SampleRate, 32, AudioFormat.CdStereo.Channels);
            var tone = Math.Max(Energy(mix, format, MineHz), Energy(mix, format, TheirsHz));
            var floor = Energy(mix, format, 5000);

            Console.WriteLine($"       a helper tone in the mix: {Db(tone):0.0} dB against {Db(floor):0.0} dB at 5 kHz");
            Expect(tone > floor * 20, $"no program audio reached the mix ({Db(tone) - Db(floor):0.0} dB above the floor)");
        });

        Kill(mine, theirs);

        Console.WriteLine(failures == 0 ? "  PASS\n" : $"  {failures} case(s) FAILED\n");
        return failures;
    }

    /// <summary>Plays a tone until killed. Runs as its own process so it can be captured as one.</summary>
    public static int PlayTone(int hertz)
    {
        try
        {
            using var output = new WasapiOut();

            // Zero hertz means "open the device and render silence" - a program that is playing, as far
            // as Windows is concerned, with nothing in it.
            var signal = new SignalGenerator(SampleRate, 2)
            {
                Gain = hertz == 0 ? 0.0 : 0.25,
                Frequency = hertz == 0 ? 440 : hertz,
                Type = SignalGeneratorType.Sin,
            };

            output.Init(signal);
            output.Play();

            while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(200);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tone {hertz} Hz failed: {ex.Message}");
            return 1;
        }
    }

    private static Process StartTone(int hertz)
    {
        var exe = Environment.ProcessPath ?? throw new Exception("cannot find this executable to re-run it");

        var process = Process.Start(new ProcessStartInfo(exe, $"--tone {hertz}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new Exception($"could not start a helper to play {hertz} Hz");

        return process;
    }

    private static void Kill(params Process[] processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited) process.Kill();
                process.WaitForExit(2000);
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }

    /// <summary>Captures from one process for a while and returns every sample it delivered.</summary>
    private static float[] Capture(int processId, TimeSpan howLong, out WaveFormat format)
    {
        using var capture = new ProcessLoopbackCapture(processId, "the tone helper", SampleRate);
        var samples = new List<float>();
        var done = new ManualResetEventSlim(false);

        capture.DataAvailable += (_, e) =>
        {
            var f = capture.WaveFormat;

            if (f.Encoding == WaveFormatEncoding.IeeeFloat || f.BitsPerSample == 32)
            {
                for (var i = 0; i + 3 < e.BytesRecorded; i += 4)
                {
                    samples.Add(BitConverter.ToSingle(e.Buffer, i));
                }
            }
            else
            {
                for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    samples.Add(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                }
            }
        };

        capture.RecordingStopped += (_, _) => done.Set();
        capture.StartRecording();

        format = capture.WaveFormat;
        Thread.Sleep(howLong);

        capture.StopRecording();
        done.Wait(TimeSpan.FromSeconds(2));

        return samples.ToArray();
    }

    /// <summary>
    /// Energy at one frequency, by Goertzel. A whole FFT would answer a question nobody asked; this
    /// needs three specific numbers - the tone that should be there, the tone that should not, and a
    /// frequency where neither is, to have something to call the floor.
    /// </summary>
    private static double Energy(float[] interleaved, WaveFormat format, double hertz)
    {
        if (interleaved.Length == 0) return 0;

        var channels = Math.Max(1, format.Channels);
        var frames = interleaved.Length / channels;
        if (frames < 64) return 0;

        var coefficient = 2 * Math.Cos(2 * Math.PI * hertz / format.SampleRate);
        double s1 = 0, s2 = 0;

        for (var frame = 0; frame < frames; frame++)
        {
            // Left channel only: both tones are the same in both channels.
            var sample = interleaved[frame * channels];
            var s0 = sample + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        var magnitude = (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
        return Math.Sqrt(Math.Max(0, magnitude)) / frames;
    }

    private static double Db(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-12));

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
