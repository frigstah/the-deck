using Deck.Core.Control;

namespace Deck.EncoderCheck;

/// <summary>
/// MIDI control (I11), driven from raw bytes.
/// <para>
/// No MIDI hardware exists on the machine this runs on, and none can be simulated - so the decoding
/// and the dispatch were deliberately built apart from the device, and everything below the driver
/// is checked here. The case that matters most is the held button: a desk that repeats its value
/// while a finger is down would otherwise toggle a station on and off many times a second.
/// </para>
/// </summary>
internal static class MidiChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("the messages Deck acts on are decoded, and the rest ignored", () =>
        {
            var cc = MidiMessage.From(0xB2, 7, 100);
            Expect(cc is { Kind: MidiMessageKind.ControlChange, Channel: 2, Number: 7, Value: 100 },
                $"a control change decoded as {cc}");

            var note = MidiMessage.From(0x90, 60, 127);
            Expect(note is { Kind: MidiMessageKind.Note, Channel: 0, Number: 60, Value: 127 },
                $"a note decoded as {note}");

            // Note-on at zero velocity is how most controllers say "released". Treating it as a
            // press would leave every button stuck down.
            var release = MidiMessage.From(0x90, 60, 0);
            Expect(release is { Value: 0 }, $"a zero-velocity note decoded as {release}");

            var explicitOff = MidiMessage.From(0x80, 60, 64);
            Expect(explicitOff is { Kind: MidiMessageKind.Note, Value: 0 },
                $"an explicit note-off decoded as {explicitOff}");

            // Clock, pitch bend and aftertouch are most of what a busy desk sends.
            Expect(MidiMessage.From(0xF8, 0, 0) is null, "a clock tick was decoded as a command");
            Expect(MidiMessage.From(0xE0, 0, 64) is null, "pitch bend was decoded as a command");
            Expect(MidiMessage.From(0xD0, 64, 0) is null, "aftertouch was decoded as a command");
        });

        failures += Check("a packed three-byte message decodes the same way", () =>
        {
            // How a driver actually hands it over: status, then the two data bytes, low to high.
            var packed = MidiMessage.From(0xB0 | (0x07 << 8) | (0x64 << 16));

            Expect(packed is { Kind: MidiMessageKind.ControlChange, Channel: 0, Number: 7, Value: 0x64 },
                $"the packed message decoded as {packed}");
        });

        failures += Check("learning wires the next control that moves", () =>
        {
            var (surface, midi) = New();

            midi.Learn(MidiAction.ToggleBroadcast);
            Expect(midi.IsLearning, "it did not go into learning mode");

            midi.Handle(Cc(64, 127));

            Expect(!midi.IsLearning, "it stayed in learning mode after a control moved");

            var binding = midi.For(MidiAction.ToggleBroadcast);
            Expect(binding is { Number: 64, Kind: MidiMessageKind.ControlChange },
                $"it learned {binding?.Describe() ?? "nothing"}");

            // And now it works.
            midi.Handle(Cc(64, 0));
            midi.Handle(Cc(64, 127));
            Expect(surface.IsLive, "the control it just learned did nothing");
        });

        failures += Check("learning a control takes it off whatever it did before", () =>
        {
            var (_, midi) = New();

            midi.Learn(MidiAction.ToggleMute);
            midi.Handle(Cc(20, 127));

            midi.Learn(MidiAction.ToggleRecording);
            midi.Handle(Cc(20, 127));

            Expect(midi.For(MidiAction.ToggleMute) is null,
                "one control was left wired to two things at once");
            Expect(midi.For(MidiAction.ToggleRecording) is { Number: 20 }, "the new binding did not take");
        });

        failures += Check("a held button fires once, not continuously", () =>
        {
            var (surface, midi) = New();
            midi.Learn(MidiAction.ToggleBroadcast);
            midi.Handle(Cc(64, 127));

            // A desk repeating its value while the finger is down. Without edge detection this
            // toggles the station on and off thirty times.
            for (var i = 0; i < 30; i++) midi.Handle(Cc(64, 127));

            Expect(surface.IsLive, "a held button toggled the station repeatedly");
            Expect(surface.LiveCalls == 1, $"the button fired {surface.LiveCalls} times while held down");

            // Released, then pressed again: that must work.
            midi.Handle(Cc(64, 0));
            midi.Handle(Cc(64, 127));

            Expect(!surface.IsLive, "pressing the button a second time did nothing");
        });

        failures += Check("a fader rides the level continuously", () =>
        {
            var (surface, midi) = New();
            midi.Learn(MidiAction.InputGain);
            midi.Handle(Cc(7, 64));

            midi.Handle(Cc(7, 0));
            Expect(Math.Abs(surface.GainDb + 30) < 0.01, $"fader at the bottom gave {surface.GainDb:0.0} dB");

            midi.Handle(Cc(7, 127));
            Expect(Math.Abs(surface.GainDb - 30) < 0.01, $"fader at the top gave {surface.GainDb:0.0} dB");

            // Halfway must land at 0 dB - unity - or the fader will not have a sensible centre.
            midi.Handle(Cc(7, 64));
            Expect(Math.Abs(surface.GainDb - 0.24) < 0.3, $"fader at the centre gave {surface.GainDb:0.0} dB");

            // And unlike a button, every move counts.
            Expect(surface.GainCalls == 3, $"the fader was acted on {surface.GainCalls} times, expected 3");
        });

        failures += Check("mute reads the real state rather than its own", () =>
        {
            var (surface, midi) = New();
            midi.Learn(MidiAction.ToggleMute);
            midi.Handle(Cc(30, 127));
            midi.Handle(Cc(30, 0));

            midi.Handle(Cc(30, 127));
            Expect(surface.Muted, "the MIDI button did not mute");

            // Someone clicks the checkbox in the window instead. The next press must unmute, not
            // re-mute - which is what happens if the button keeps its own copy of the state.
            midi.Handle(Cc(30, 0));
            surface.SetMuted(false);

            midi.Handle(Cc(30, 127));
            Expect(surface.Muted, "the button and the window disagreed about what muted meant");
        });

        failures += Check("messages nothing is bound to are ignored", () =>
        {
            var (surface, midi) = New();
            midi.Learn(MidiAction.ToggleBroadcast);
            midi.Handle(Cc(64, 127));
            midi.Handle(Cc(64, 0));

            for (var number = 0; number < 128; number++)
            {
                if (number == 64) continue;

                midi.Handle(Cc(number, 127));
                midi.Handle(new MidiMessage(MidiMessageKind.Note, 0, number, 127));
            }

            Expect(!surface.IsLive, "an unbound control put the station on air");
            Expect(surface.LiveCalls == 0, $"unbound controls caused {surface.LiveCalls} commands");
        });

        failures += Check("a binding on one channel ignores the others", () =>
        {
            var (surface, midi) = New();
            midi.Load("ToggleBroadcast=cc:3:64");

            midi.Handle(new MidiMessage(MidiMessageKind.ControlChange, 5, 64, 127));
            Expect(!surface.IsLive, "a message on the wrong channel was acted on");

            midi.Handle(new MidiMessage(MidiMessageKind.ControlChange, 3, 64, 127));
            Expect(surface.IsLive, "a message on the right channel was ignored");
        });

        failures += Check("bindings survive being saved and loaded", () =>
        {
            var (_, midi) = New();

            midi.Learn(MidiAction.ToggleBroadcast);
            midi.Handle(Cc(64, 127));
            midi.Learn(MidiAction.InputGain);
            midi.Handle(Cc(7, 100));

            var saved = midi.Save();

            var (_, reloaded) = New();
            reloaded.Load(saved);

            Expect(reloaded.Bindings.Count == 2, $"{reloaded.Bindings.Count} binding(s) came back, expected 2");
            Expect(reloaded.For(MidiAction.ToggleBroadcast) is { Number: 64 }, "the button did not survive");
            Expect(reloaded.For(MidiAction.InputGain) is { Number: 7 }, "the fader did not survive");
        });

        failures += Check("a damaged settings line loses only the damaged part", () =>
        {
            var (_, midi) = New();

            // Hand-edited, half-written, or carried from a newer version of Deck.
            midi.Load("ToggleBroadcast=cc:-1:64,Nonsense=cc:-1:9,InputGain=cc:-1:999,,GoLive=,ToggleMute=cc:-1:30");

            Expect(midi.For(MidiAction.ToggleBroadcast) is { Number: 64 }, "a good binding was lost");
            Expect(midi.For(MidiAction.ToggleMute) is { Number: 30 }, "a good binding after a bad one was lost");
            Expect(midi.For(MidiAction.InputGain) is null, "a controller number of 999 was accepted");
            Expect(midi.Bindings.Count == 2, $"{midi.Bindings.Count} bindings survived, expected 2");
        });

        failures += Check("asking for MIDI devices never throws", () =>
        {
            // Most machines have no MIDI at all, which is the point: this must return an empty list
            // rather than fail, or the window would not open on them.
            var devices = MidiInput.Devices();
            Console.WriteLine($"       ({devices.Count} MIDI input(s): {(devices.Count == 0 ? "none" : string.Join(", ", devices))})");

            using var input = new MidiInput();
            Expect(!input.Start(null), "opening a nameless device reported success");
            Expect(!input.Start("No Such Device"), "opening a device that does not exist reported success");
            Expect(input.Problem is not null, "it failed to open a missing device without saying why");
        });

        failures += Check("a real MIDI device opens and closes cleanly", () =>
        {
            // The only part of I11 that touches hardware. It cannot be made to send anything without
            // a physical desk, but opening it, starting it and closing it again is exactly where
            // NAudio's MIDI layer would fail if it were going to - so it is worth doing for real
            // rather than assuming.
            var devices = MidiInput.Devices();

            if (devices.Count == 0)
            {
                Console.WriteLine("       (skipped: no MIDI input on this machine)");
                return;
            }

            using var input = new MidiInput();

            if (!input.Start(devices[0]))
            {
                // Exclusive access: another program already has it. Not a fault in Deck, and the
                // message has to say so in a way a user could act on.
                Console.WriteLine($"       (skipped: {input.Problem})");
                Expect(input.Problem is not null, "it failed to open without saying why");
                return;
            }

            Expect(input.IsRunning, "it reported success but was not running");
            Expect(input.DeviceName == devices[0], $"it opened \"{input.DeviceName}\" rather than \"{devices[0]}\"");
            Expect(input.Problem is null, $"it opened but still reported a problem: {input.Problem}");

            input.Stop();
            Expect(!input.IsRunning, "it was still running after being stopped");

            // Re-opening after a stop is what happens every time the user picks a device twice.
            Expect(input.Start(devices[0]), $"it could not be reopened: {input.Problem}");
        });

        return failures;
    }

    private static MidiMessage Cc(int number, int value) =>
        new(MidiMessageKind.ControlChange, 0, number, value);

    private static (CountingSurface Surface, MidiControl Midi) New()
    {
        var surface = new CountingSurface();
        return (surface, new MidiControl(surface));
    }

    /// <summary>Counts calls as well as recording state, so "fired once" can be asserted.</summary>
    private sealed class CountingSurface : IControlSurface
    {
        public bool IsLive { get; private set; }

        public bool Muted { get; private set; }

        public bool Recording { get; private set; }

        public double GainDb { get; private set; }

        public int LiveCalls { get; private set; }

        public int GainCalls { get; private set; }

        public ControlStatus Status() => new()
        {
            IsLive = IsLive,
            IsMuted = Muted,
            IsRecording = Recording,
        };

        public Task<ControlResult> GoLiveAsync()
        {
            LiveCalls++;
            IsLive = true;
            return Task.FromResult(ControlResult.Done("On air."));
        }

        public Task<ControlResult> GoOffAsync()
        {
            LiveCalls++;
            IsLive = false;
            return Task.FromResult(ControlResult.Done("Off air."));
        }

        public ControlResult SetTitle(string title) => ControlResult.Done(title);

        public ControlResult StartRecording()
        {
            Recording = true;
            return ControlResult.Done("Recording.");
        }

        public ControlResult StopRecording()
        {
            Recording = false;
            return ControlResult.Done("Stopped.");
        }

        public ControlResult SetMuted(bool muted)
        {
            Muted = muted;
            return ControlResult.Done(muted ? "Muted." : "Unmuted.");
        }

        public ControlResult SetGainDb(double db)
        {
            GainCalls++;
            GainDb = db;
            return ControlResult.Done($"{db:0.0} dB");
        }
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
