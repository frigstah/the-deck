using NAudio.Midi;

namespace Deck.Core.Control;

/// <summary>
/// Opens a MIDI input and turns its events into <see cref="MidiMessage"/> (I11).
/// <para>
/// Deliberately thin. Everything that decides what a message means lives in
/// <see cref="MidiControl"/>, which can be checked without hardware; this class only opens a device
/// and forwards raw numbers. A MIDI desk is the one thing in Deck that cannot be simulated, so the
/// part that cannot be tested is kept down to the few lines that genuinely need a device.
/// </para>
/// </summary>
public sealed class MidiInput : IDisposable
{
    private readonly object _lock = new();

    private MidiIn? _device;

    public bool IsRunning { get; private set; }

    public string? DeviceName { get; private set; }

    /// <summary>Why the device is not open, phrased for the user.</summary>
    public string? Problem { get; private set; }

    public event EventHandler<MidiMessage>? MessageReceived;

    /// <summary>Every MIDI input Windows can see. Empty is normal - most machines have none.</summary>
    public static IReadOnlyList<string> Devices()
    {
        var names = new List<string>();

        try
        {
            for (var i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                names.Add(MidiIn.DeviceInfo(i).ProductName);
            }
        }
        catch (Exception)
        {
            // No MIDI stack, or a driver that will not enumerate. An empty list is the right answer
            // either way, and this must never stop the window opening.
        }

        return names;
    }

    /// <summary>
    /// Opens a device by name rather than by index. Indices shift when another device is plugged in,
    /// so a saved index would silently start listening to the wrong controller.
    /// </summary>
    public bool Start(string? deviceName)
    {
        lock (_lock)
        {
            Stop();

            if (string.IsNullOrWhiteSpace(deviceName)) return false;

            var devices = Devices();
            var index = -1;

            for (var i = 0; i < devices.Count; i++)
            {
                if (devices[i] != deviceName) continue;

                index = i;
                break;
            }

            if (index < 0)
            {
                Problem = $"Deck could not find \"{deviceName}\". Is it plugged in?";
                return false;
            }

            try
            {
                var device = new MidiIn(index);
                device.MessageReceived += OnMessage;
                device.ErrorReceived += OnError;
                device.Start();

                _device = device;
                DeviceName = deviceName;
                Problem = null;
                IsRunning = true;

                return true;
            }
            catch (Exception ex)
            {
                // Usually another program already has it: MIDI inputs are exclusive on Windows.
                Problem = $"Deck could not open \"{deviceName}\": {ex.Message}";
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_device is not null)
            {
                _device.MessageReceived -= OnMessage;
                _device.ErrorReceived -= OnError;

                try
                {
                    _device.Stop();
                }
                catch (Exception)
                {
                    // Device may already be gone.
                }

                _device.Dispose();
                _device = null;
            }

            IsRunning = false;
            DeviceName = null;
        }
    }

    private void OnMessage(object? sender, MidiInMessageEventArgs e)
    {
        if (MidiMessage.From(e.RawMessage) is { } message) MessageReceived?.Invoke(this, message);
    }

    private void OnError(object? sender, MidiInMessageEventArgs e)
    {
        // A malformed message is not worth reporting; desks send odd things and the show goes on.
    }

    public void Dispose() => Stop();
}
