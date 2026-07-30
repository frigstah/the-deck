using NAudio.CoreAudioApi;

namespace Deck.Core.Audio;

/// <summary>Enumerates WASAPI endpoints and resolves them back to NAudio device objects.</summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDevice> Inputs() => Enumerate(DataFlow.Capture, AudioDeviceKind.Input);

    public static IReadOnlyList<AudioDevice> Outputs() => Enumerate(DataFlow.Render, AudioDeviceKind.Output);

    /// <summary>Output devices offered as capture sources ("stream what this PC is playing").</summary>
    public static IReadOnlyList<AudioDevice> LoopbackSources() => Enumerate(DataFlow.Render, AudioDeviceKind.Loopback);

    /// <summary>
    /// Everything Deck can broadcast from: real inputs first, then the loopback sources (A4), then the
    /// programs currently making a noise (A9), then any ASIO drivers (A8). Microphones lead because
    /// that is what most people are here for, and ASIO comes last because on almost every machine that
    /// list is empty.
    /// <para>
    /// Programs sit next to whole-desktop loopback because they answer the same question in a narrower
    /// way, and because the pair of them is the karaoke setup: microphone on the main input, the
    /// backing-track program on the second.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AudioDevice> AllInputSources() =>
    [
        .. Inputs(),
        .. LoopbackSources(),
        .. AudioProcesses.Playing(),
        .. AudioProcesses.Open(),
        .. AsioCapture.Devices(),
    ];

    private static IReadOnlyList<AudioDevice> Enumerate(DataFlow flow, AudioDeviceKind kind)
    {
        var result = new List<AudioDevice>();
        using var enumerator = new MMDeviceEnumerator();

        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = def.ID;
        }
        catch (Exception)
        {
            // No default endpoint (e.g. no devices at all). Not an error worth surfacing.
        }

        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDevice(device.ID, device.FriendlyName, kind, device.ID == defaultId));
            }
        }

        // Default first, then alphabetical - the order a user expects to scan.
        return result
            .OrderByDescending(d => d.IsSystemDefault)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves a stored device id back to a live endpoint, or null if it is gone.</summary>
    public static MMDevice? Resolve(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            var device = enumerator.GetDevice(deviceId);
            return device.State == DeviceState.Active ? device : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Falls back to the system default when the stored device is unavailable.</summary>
    public static MMDevice? ResolveOrDefault(string? deviceId, AudioDeviceKind kind)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            var device = Resolve(deviceId);
            if (device is not null) return device;
        }

        var flow = kind == AudioDeviceKind.Input ? DataFlow.Capture : DataFlow.Render;
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
