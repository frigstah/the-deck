namespace Deck.Core.Audio;

/// <summary>
/// Converts an interleaved buffer between channel counts. Devices turn up as mono, stereo or
/// multi-channel; the stream is always 1 or 2 channels.
/// </summary>
public static class ChannelMapper
{
    /// <summary>Frames the destination must hold for a given source length.</summary>
    public static int FramesFor(int sourceLength, int sourceChannels) => sourceLength / sourceChannels;

    /// <summary>
    /// Writes <paramref name="source"/> into <paramref name="destination"/> converting channel
    /// count. Returns the number of samples written.
    /// </summary>
    /// <summary>
    /// Maps only the device channels the user picked (A7), then converts to the destination width.
    /// Everything the device sends outside the selection is discarded here rather than being mixed
    /// in, which is the point: input 5 should not be audible when input 3 was chosen.
    /// </summary>
    public static int Map(
        ReadOnlySpan<float> source,
        int sourceChannels,
        Span<float> destination,
        int destinationChannels,
        ChannelSelection selection)
    {
        var clamped = selection.ClampTo(sourceChannels);
        if (clamped == ChannelSelection.Default && sourceChannels <= 2)
        {
            return Map(source, sourceChannels, destination, destinationChannels);
        }

        var frames = source.Length / sourceChannels;
        var first = clamped.FirstChannel;

        if (clamped.SingleChannel)
        {
            if (destinationChannels == 1)
            {
                for (var frame = 0; frame < frames; frame++)
                {
                    destination[frame] = source[(frame * sourceChannels) + first];
                }

                return frames;
            }

            // One input to a stereo stream: centred, not stuck on one side.
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = source[(frame * sourceChannels) + first];
                destination[frame * 2] = sample;
                destination[(frame * 2) + 1] = sample;
            }

            return frames * 2;
        }

        if (destinationChannels == 1)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var baseIndex = (frame * sourceChannels) + first;
                destination[frame] = (source[baseIndex] + source[baseIndex + 1]) / 2f;
            }

            return frames;
        }

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = (frame * sourceChannels) + first;
            destination[frame * 2] = source[baseIndex];
            destination[(frame * 2) + 1] = source[baseIndex + 1];
        }

        return frames * 2;
    }

    public static int Map(ReadOnlySpan<float> source, int sourceChannels, Span<float> destination, int destinationChannels)
    {
        if (sourceChannels == destinationChannels)
        {
            source.CopyTo(destination);
            return source.Length;
        }

        var frames = source.Length / sourceChannels;

        if (destinationChannels == 1)
        {
            // Downmix: average every source channel so nothing disappears.
            for (var frame = 0; frame < frames; frame++)
            {
                var baseIndex = frame * sourceChannels;
                var sum = 0f;
                for (var ch = 0; ch < sourceChannels; ch++) sum += source[baseIndex + ch];
                destination[frame] = sum / sourceChannels;
            }

            return frames;
        }

        if (sourceChannels == 1)
        {
            // Mono source to stereo: duplicate, so a mono mic sits centred rather than hard left.
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = source[frame];
                destination[frame * 2] = sample;
                destination[(frame * 2) + 1] = sample;
            }

            return frames * 2;
        }

        // Multi-channel source to stereo: take the first pair, which on every interface we care
        // about is the main L/R.
        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * sourceChannels;
            destination[frame * 2] = source[baseIndex];
            destination[(frame * 2) + 1] = source[baseIndex + 1];
        }

        return frames * 2;
    }
}
