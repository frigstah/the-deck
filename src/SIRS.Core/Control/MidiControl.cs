using System.Globalization;

namespace Sirs.Core.Control;

/// <summary>What a control on a MIDI desk can be made to do (I11).</summary>
public enum MidiAction
{
    None,
    GoLive,
    GoOff,
    ToggleBroadcast,
    ToggleMute,
    ToggleRecording,

    /// <summary>Continuous: a fader or knob rides the input level.</summary>
    InputGain,
}

public static class MidiActions
{
    public static readonly IReadOnlyList<MidiAction> Assignable =
    [
        MidiAction.ToggleBroadcast, MidiAction.GoLive, MidiAction.GoOff,
        MidiAction.ToggleMute, MidiAction.ToggleRecording, MidiAction.InputGain,
    ];

    public static string Label(this MidiAction action) => action switch
    {
        MidiAction.GoLive => "Go on air",
        MidiAction.GoOff => "Go off air",
        MidiAction.ToggleBroadcast => "On air / off air",
        MidiAction.ToggleMute => "Mute / unmute",
        MidiAction.ToggleRecording => "Start / stop recording",
        MidiAction.InputGain => "Input level (a fader or knob)",
        _ => "Nothing",
    };

    /// <summary>
    /// True for the one action that wants a position rather than a press. Everything else is a
    /// button, and buttons need edge detection where a fader does not.
    /// </summary>
    public static bool IsContinuous(this MidiAction action) => action == MidiAction.InputGain;
}

public enum MidiMessageKind
{
    ControlChange,
    Note,
}

/// <summary>One MIDI message, decoded far enough to act on.</summary>
public readonly record struct MidiMessage(MidiMessageKind Kind, int Channel, int Number, int Value)
{
    /// <summary>
    /// Decodes a raw MIDI event. Returns null for anything SIRS does not act on - clock, aftertouch,
    /// pitch bend, system exclusive - which is most of what a busy desk actually sends.
    /// </summary>
    public static MidiMessage? From(int status, int data1, int data2)
    {
        var channel = status & 0x0F;

        return (status & 0xF0) switch
        {
            0xB0 => new MidiMessage(MidiMessageKind.ControlChange, channel, data1, data2),

            // Note on with zero velocity is note off. Most controllers send it that way rather than
            // 0x80, so treating them differently would leave buttons stuck down.
            0x90 => new MidiMessage(MidiMessageKind.Note, channel, data1, data2),
            0x80 => new MidiMessage(MidiMessageKind.Note, channel, data1, 0),

            _ => null,
        };
    }

    /// <summary>Reads a raw three-byte message, as a driver hands it over.</summary>
    public static MidiMessage? From(int packed) =>
        From(packed & 0xFF, (packed >> 8) & 0x7F, (packed >> 16) & 0x7F);

    public string Describe() =>
        Kind == MidiMessageKind.ControlChange
            ? $"controller {Number} on channel {Channel + 1}"
            : $"note {Number} on channel {Channel + 1}";
}

/// <summary>One control on the desk wired to one thing in SIRS.</summary>
public sealed record MidiBinding(MidiAction Action, MidiMessageKind Kind, int Channel, int Number)
{
    /// <summary>
    /// -1 means any channel. Desks are often set to a channel the user never chose and does not know
    /// how to find, so matching on the control number alone is the forgiving default.
    /// </summary>
    public const int AnyChannel = -1;

    public bool Matches(MidiMessage message) =>
        message.Kind == Kind &&
        message.Number == Number &&
        (Channel == AnyChannel || message.Channel == Channel);

    public string Describe() =>
        (Kind == MidiMessageKind.ControlChange ? $"Controller {Number}" : $"Note {Number}") +
        (Channel == AnyChannel ? string.Empty : $" on channel {Channel + 1}");
}

/// <summary>
/// Maps a MIDI desk onto SIRS (I11).
/// <para>
/// The decoding and the dispatch live here, apart from any device, so the whole of it can be checked
/// by feeding raw bytes in. That matters more than usual: MIDI hardware is the one thing in SIRS
/// that cannot be simulated, so the part that can be tested has to be all of the logic.
/// </para>
/// </summary>
public sealed class MidiControl
{
    /// <summary>Above this a button counts as pressed. The MIDI convention, and what desks send.</summary>
    private const int PressThreshold = 64;

    private readonly IControlSurface _surface;
    private readonly List<MidiBinding> _bindings = [];
    private readonly HashSet<(MidiMessageKind, int, int)> _held = [];

    public MidiControl(IControlSurface surface) => _surface = surface;

    public IReadOnlyList<MidiBinding> Bindings => _bindings;

    /// <summary>Set while waiting for the user to wiggle the control they want to assign.</summary>
    public MidiAction Learning { get; private set; } = MidiAction.None;

    public bool IsLearning => Learning != MidiAction.None;

    /// <summary>The most recent message, whether or not it did anything. Shown while learning.</summary>
    public string? LastMessage { get; private set; }

    /// <summary>Raised after a binding is learned or a command runs, so the UI can refresh.</summary>
    public event EventHandler? Changed;

    public void Learn(MidiAction action)
    {
        Learning = action;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CancelLearning()
    {
        Learning = MidiAction.None;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(MidiAction action)
    {
        _bindings.RemoveAll(b => b.Action == action);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public MidiBinding? For(MidiAction action) => _bindings.FirstOrDefault(b => b.Action == action);

    /// <summary>
    /// Handles one message. Returns what it did, or null if it did nothing - which is the common
    /// case, since a desk sends far more than SIRS is listening for.
    /// </summary>
    public ControlResult? Handle(MidiMessage message)
    {
        LastMessage = message.Describe();

        if (IsLearning)
        {
            var action = Learning;
            Learning = MidiAction.None;

            // Whatever this control was doing before, it does the new thing now, and nothing else
            // is left wired to the old action.
            _bindings.RemoveAll(b => b.Action == action || (b.Kind == message.Kind && b.Number == message.Number));
            _bindings.Add(new MidiBinding(action, message.Kind, MidiBinding.AnyChannel, message.Number));

            Changed?.Invoke(this, EventArgs.Empty);
            return ControlResult.Done($"{action.Label()} is now {message.Describe()}.");
        }

        var binding = _bindings.FirstOrDefault(b => b.Matches(message));
        if (binding is null) return null;

        return binding.Action.IsContinuous()
            ? Continuous(binding, message)
            : Button(binding, message);
    }

    private ControlResult Continuous(MidiBinding binding, MidiMessage message)
    {
        // 0..127 across the same range the fader in the window covers, so the two agree.
        var db = -30.0 + (message.Value / 127.0 * 60.0);

        var result = binding.Action switch
        {
            MidiAction.InputGain => _surface.SetGainDb(db),
            _ => ControlResult.Refused("That control does nothing."),
        };

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>
    /// Buttons act on the press and not the release, and not again until the control has been let
    /// go. Without that, a desk that repeats its value while a button is held - and plenty do -
    /// would toggle the station on and off many times a second for as long as a finger was on it.
    /// </summary>
    private ControlResult? Button(MidiBinding binding, MidiMessage message)
    {
        var key = (message.Kind, message.Channel, message.Number);
        var pressed = message.Value >= PressThreshold;

        if (!pressed)
        {
            _held.Remove(key);
            return null;
        }

        if (!_held.Add(key)) return null;

        var result = binding.Action switch
        {
            MidiAction.GoLive => _surface.GoLiveAsync().GetAwaiter().GetResult(),
            MidiAction.GoOff => _surface.GoOffAsync().GetAwaiter().GetResult(),
            MidiAction.ToggleBroadcast => Toggle(),

            // Read back rather than remembered. A MIDI button and the checkbox in the window are
            // two ways to change one thing, and a toggle that keeps its own copy of the state ends
            // up inverted the moment the other one is used.
            MidiAction.ToggleMute => _surface.SetMuted(!_surface.Status().IsMuted),

            MidiAction.ToggleRecording => _surface.Status().IsRecording
                ? _surface.StopRecording()
                : _surface.StartRecording(),

            _ => ControlResult.Refused("That control does nothing."),
        };

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private ControlResult Toggle() =>
        _surface.Status().IsLive
            ? _surface.GoOffAsync().GetAwaiter().GetResult()
            : _surface.GoLiveAsync().GetAwaiter().GetResult();

    // ---------------------------------------------------------------- saving

    /// <summary>"ToggleBroadcast=cc:-1:64,InputGain=cc:-1:7" - one line, so it fits in the settings file.</summary>
    public string Save() =>
        string.Join(',', _bindings.Select(b =>
            $"{b.Action}={(b.Kind == MidiMessageKind.ControlChange ? "cc" : "note")}:{b.Channel}:{b.Number}"));

    public void Load(string? saved)
    {
        _bindings.Clear();
        _held.Clear();

        if (string.IsNullOrWhiteSpace(saved)) return;

        foreach (var entry in saved.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = entry.IndexOf('=');
            if (equals <= 0) continue;

            if (!Enum.TryParse<MidiAction>(entry[..equals], out var action)) continue;

            var parts = entry[(equals + 1)..].Split(':');
            if (parts.Length != 3) continue;

            var kind = parts[0] == "note" ? MidiMessageKind.Note : MidiMessageKind.ControlChange;

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)) continue;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;

            // A saved file can be edited by hand or carried between machines; a number outside the
            // range would simply never match, which looks like the binding silently vanishing.
            if (number is < 0 or > 127 || channel is < MidiBinding.AnyChannel or > 15) continue;

            _bindings.Add(new MidiBinding(action, kind, channel, number));
        }
    }
}
