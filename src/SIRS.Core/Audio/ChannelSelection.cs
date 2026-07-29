namespace Sirs.Core.Audio;

/// <summary>
/// Which of a device's inputs actually feed the stream (A7).
/// <para>
/// It matters on two kinds of device. An audio interface with eight inputs needs to be told which
/// pair the microphone is on. And a plain stereo line input needs it just as often, because a mono
/// microphone plugged into one side of it is the single most common cause of "why am I only coming
/// out of the left speaker".
/// </para>
/// </summary>
public readonly record struct ChannelSelection(int FirstChannel = 0, bool SingleChannel = false)
{
    public static readonly ChannelSelection Default = new();

    /// <summary>How many device channels this selection consumes.</summary>
    public int Width => SingleChannel ? 1 : 2;

    /// <summary>Clamps the selection to what a device actually has.</summary>
    public ChannelSelection ClampTo(int deviceChannels)
    {
        if (deviceChannels <= 1) return new ChannelSelection(0, SingleChannel: true);

        var first = Math.Clamp(FirstChannel, 0, deviceChannels - 1);
        var single = SingleChannel || first + 1 >= deviceChannels;

        return new ChannelSelection(first, single);
    }

    /// <summary>
    /// The choices to offer for a device with this many channels: the pairs first, since that is
    /// what most people want, then the individual inputs.
    /// </summary>
    public static IReadOnlyList<(ChannelSelection Selection, string Label)> For(int deviceChannels)
    {
        var options = new List<(ChannelSelection, string)>();

        if (deviceChannels <= 1)
        {
            options.Add((new ChannelSelection(0, SingleChannel: true), "The only input"));
            return options;
        }

        if (deviceChannels == 2)
        {
            options.Add((new ChannelSelection(0), "Both channels"));
            options.Add((new ChannelSelection(0, SingleChannel: true), "Left only"));
            options.Add((new ChannelSelection(1, SingleChannel: true), "Right only"));
            return options;
        }

        for (var first = 0; first + 1 < deviceChannels; first += 2)
        {
            options.Add((new ChannelSelection(first), $"Inputs {first + 1} and {first + 2}"));
        }

        for (var channel = 0; channel < deviceChannels; channel++)
        {
            options.Add((new ChannelSelection(channel, SingleChannel: true), $"Input {channel + 1} only"));
        }

        return options;
    }

    /// <summary>The label this selection would carry on a device of the given width.</summary>
    public string LabelFor(int deviceChannels)
    {
        var clamped = ClampTo(deviceChannels);
        foreach (var (selection, label) in For(deviceChannels))
        {
            if (selection == clamped) return label;
        }

        return clamped.SingleChannel ? $"Input {clamped.FirstChannel + 1} only" : "Both channels";
    }
}
