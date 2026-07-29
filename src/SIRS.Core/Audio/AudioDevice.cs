namespace Sirs.Core.Audio;

public enum AudioDeviceKind
{
    /// <summary>A microphone or line input.</summary>
    Input,

    /// <summary>A speaker / headphone device used for monitoring and playback.</summary>
    Output,

    /// <summary>An output device captured via WASAPI loopback ("what the computer is playing").</summary>
    Loopback,

    /// <summary>A professional interface reached through its own ASIO driver rather than Windows (A8).</summary>
    Asio,
}

/// <summary>A selectable audio endpoint, described in the words a user would recognise.</summary>
public sealed record AudioDevice(
    string Id,
    string Name,
    AudioDeviceKind Kind,
    bool IsSystemDefault)
{
    /// <summary>Name as shown in a picker, e.g. "Microphone (Yeti X) — default".</summary>
    public string DisplayName => IsSystemDefault ? $"{Name} — default" : Name;

    /// <summary>
    /// Group heading in the input picker. A render device offered as a capture source needs saying
    /// out loud, because "Speakers" in a list of microphones is otherwise baffling.
    /// </summary>
    public string CategoryLabel => Kind switch
    {
        AudioDeviceKind.Loopback => "Sound playing on this PC",
        AudioDeviceKind.Input => "Microphones and line inputs",
        AudioDeviceKind.Asio => "Professional interfaces (ASIO)",
        _ => "Speakers and headphones",
    };

    /// <summary>
    /// A device is identified by id and kind together: the same endpoint appears as both an output
    /// to monitor through and a loopback source to capture from.
    /// </summary>
    public bool Matches(string? id, AudioDeviceKind kind) => Id == id && Kind == kind;

    public override string ToString() => DisplayName;
}
