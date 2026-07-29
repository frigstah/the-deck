using Sirs.Core.Audio;

namespace Sirs.EncoderCheck;

/// <summary>
/// Channel selection (A7) and the automatic on-air switch (G6). Both are places where getting it
/// subtly wrong is silent: the wrong input still produces audio, and a switch that never fires
/// leaves a station off air with nothing on screen to explain it.
/// </summary>
internal static class InputChecks
{
    public static int Run()
    {
        var failures = 0;

        // ---------------------------------------------------------------- channel selection (A7)

        failures += Check("a four-input device offers pairs first, then singles", () =>
        {
            var options = ChannelSelection.For(4).Select(o => o.Label).ToList();

            Expect(options.Count == 6, $"got {options.Count} options, expected 6");
            Expect(options[0] == "Inputs 1 and 2", $"first option is \"{options[0]}\"");
            Expect(options[1] == "Inputs 3 and 4", $"second option is \"{options[1]}\"");
            Expect(options[2] == "Input 1 only", $"third option is \"{options[2]}\"");
        });

        failures += Check("a stereo device offers both, left and right", () =>
        {
            var options = ChannelSelection.For(2).Select(o => o.Label).ToList();
            Expect(options.SequenceEqual(["Both channels", "Left only", "Right only"]),
                $"got [{string.Join(", ", options)}]");
        });

        failures += Check("picking input 3 really takes input 3", () =>
        {
            // Four channels carrying four distinguishable constants.
            float[] source = [0.1f, 0.2f, 0.3f, 0.4f, 0.1f, 0.2f, 0.3f, 0.4f];
            var destination = new float[8];

            var written = ChannelMapper.Map(
                source, 4, destination, 2, new ChannelSelection(2, SingleChannel: true));

            Expect(written == 4, $"wrote {written} samples, expected 4");

            // Input 3 is index 2, so 0.3 - centred across both stream channels.
            for (var i = 0; i < written; i++)
            {
                Expect(Math.Abs(destination[i] - 0.3f) < 1e-6, $"sample {i} is {destination[i]}, expected 0.3");
            }
        });

        failures += Check("picking inputs 3 and 4 keeps them apart", () =>
        {
            float[] source = [0.1f, 0.2f, 0.3f, 0.4f];
            var destination = new float[2];

            ChannelMapper.Map(source, 4, destination, 2, new ChannelSelection(2));

            Expect(Math.Abs(destination[0] - 0.3f) < 1e-6, $"left is {destination[0]}, expected 0.3");
            Expect(Math.Abs(destination[1] - 0.4f) < 1e-6, $"right is {destination[1]}, expected 0.4");
        });

        failures += Check("one input into a mono stream stays exactly itself", () =>
        {
            float[] source = [0.1f, 0.8f];
            var destination = new float[1];

            ChannelMapper.Map(source, 2, destination, 1, new ChannelSelection(1, SingleChannel: true));

            // Not averaged with the silent side, which would halve it.
            Expect(Math.Abs(destination[0] - 0.8f) < 1e-6, $"got {destination[0]}, expected 0.8");
        });

        failures += Check("a selection wider than the device is pulled back", () =>
        {
            var clamped = new ChannelSelection(7).ClampTo(2);
            Expect(clamped.FirstChannel == 1, $"first channel is {clamped.FirstChannel}, expected 1");
            Expect(clamped.SingleChannel, "the last channel of a device should have to be used alone");

            var mono = new ChannelSelection(3).ClampTo(1);
            Expect(mono is { FirstChannel: 0, SingleChannel: true }, $"a mono device gave {mono}");
        });

        failures += Check("the default selection behaves exactly as before", () =>
        {
            // Stereo in, stereo out, no selection made: nothing about the audio should change.
            float[] source = [0.25f, -0.5f, 0.75f, -1f];
            var withSelection = new float[4];
            var withoutSelection = new float[4];

            ChannelMapper.Map(source, 2, withSelection, 2, ChannelSelection.Default);
            ChannelMapper.Map(source, 2, withoutSelection, 2);

            for (var i = 0; i < 4; i++)
            {
                Expect(withSelection[i] == withoutSelection[i],
                    $"sample {i} differs: {withSelection[i]} against {withoutSelection[i]}");
            }
        });

        // ---------------------------------------------------------------- automatic on-air (G6)

        failures += Check("sound brings it on air, and only after the delay", () =>
        {
            var started = 0;
            var air = new AutoAirSwitch { Enabled = true, StartAfterSeconds = 2 };
            air.StartRequested += (_, _) => started++;

            air.Update(-20f, isBroadcasting: false, elapsedSeconds: 1.0);
            Expect(started == 0, "it went on air after one second, with a two second delay set");

            air.Update(-20f, isBroadcasting: false, elapsedSeconds: 1.5);
            Expect(started == 1, $"it fired {started} time(s) after the delay passed, expected once");
        });

        failures += Check("a brief noise does not trip it", () =>
        {
            // A door closing should not put a station on air.
            var started = 0;
            var air = new AutoAirSwitch { Enabled = true, StartAfterSeconds = 2 };
            air.StartRequested += (_, _) => started++;

            air.Update(-20f, isBroadcasting: false, elapsedSeconds: 0.5);
            air.Update(-70f, isBroadcasting: false, elapsedSeconds: 0.5);
            air.Update(-20f, isBroadcasting: false, elapsedSeconds: 1.4);

            Expect(started == 0, "a burst of sound with a gap in it still went on air");
        });

        failures += Check("a long silence takes it off air", () =>
        {
            var stopped = 0;
            var air = new AutoAirSwitch { Enabled = true, StopAfterSilentSeconds = 300 };
            air.StopRequested += (_, _) => stopped++;

            for (var i = 0; i < 299; i++) air.Update(-70f, isBroadcasting: true, elapsedSeconds: 1.0);
            Expect(stopped == 0, "it came off air before the silence had run its course");

            air.Update(-70f, isBroadcasting: true, elapsedSeconds: 1.0);
            Expect(stopped == 1, $"it fired {stopped} time(s), expected once");
        });

        failures += Check("a gap between tracks does not take it off air", () =>
        {
            // The reason the stop delay is minutes: silence happens constantly during a show.
            var stopped = 0;
            var air = new AutoAirSwitch { Enabled = true, StopAfterSilentSeconds = 300 };
            air.StopRequested += (_, _) => stopped++;

            for (var round = 0; round < 20; round++)
            {
                for (var i = 0; i < 30; i++) air.Update(-70f, isBroadcasting: true, elapsedSeconds: 1.0);
                air.Update(-15f, isBroadcasting: true, elapsedSeconds: 1.0);
            }

            Expect(stopped == 0, $"twenty half-minute gaps took it off air {stopped} time(s)");
        });

        failures += Check("turning it off stops it deciding anything", () =>
        {
            var events = 0;
            var air = new AutoAirSwitch { Enabled = false, StartAfterSeconds = 1 };
            air.StartRequested += (_, _) => events++;
            air.StopRequested += (_, _) => events++;

            for (var i = 0; i < 100; i++) air.Update(-10f, isBroadcasting: false, elapsedSeconds: 1.0);

            Expect(events == 0, $"a disabled switch fired {events} time(s)");
            Expect(air.Status(false) is null, "a disabled switch still has something to say");
        });

        failures += Check("it does not ask for what has already happened", () =>
        {
            var started = 0;
            var air = new AutoAirSwitch { Enabled = true, StartAfterSeconds = 1 };
            air.StartRequested += (_, _) => started++;

            for (var i = 0; i < 50; i++) air.Update(-10f, isBroadcasting: true, elapsedSeconds: 1.0);

            Expect(started == 0, $"it asked to go on air {started} time(s) while already broadcasting");
        });

        return failures;
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
