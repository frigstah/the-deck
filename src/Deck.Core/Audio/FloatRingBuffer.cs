namespace Deck.Core.Audio;

/// <summary>
/// A fixed-size ring of interleaved float samples, written by one capture thread and read by
/// another. Used to hand the secondary mixer source across to whichever thread owns the mix clock.
/// <para>
/// On overflow it drops the oldest audio rather than blocking or growing: a source that has run
/// ahead is better rejoined at the present moment than played back late for the rest of the show.
/// </para>
/// </summary>
public sealed class FloatRingBuffer
{
    private readonly object _lock = new();
    private readonly float[] _data;
    private int _readIndex;
    private int _writeIndex;
    private int _count;

    public FloatRingBuffer(int capacitySamples) => _data = new float[Math.Max(1, capacitySamples)];

    public int Capacity => _data.Length;

    public int Count
    {
        get
        {
            lock (_lock) return _count;
        }
    }

    /// <summary>Samples discarded because the reader could not keep up.</summary>
    public long DroppedSamples { get; private set; }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return;

        lock (_lock)
        {
            // A write larger than the whole ring can only keep its tail.
            var source = samples.Length > _data.Length ? samples[^_data.Length..] : samples;

            var overflow = _count + source.Length - _data.Length;
            if (overflow > 0)
            {
                _readIndex = (_readIndex + overflow) % _data.Length;
                _count -= overflow;
                DroppedSamples += overflow;
            }

            var firstChunk = Math.Min(source.Length, _data.Length - _writeIndex);
            source[..firstChunk].CopyTo(_data.AsSpan(_writeIndex));

            if (firstChunk < source.Length)
            {
                source[firstChunk..].CopyTo(_data.AsSpan(0));
            }

            _writeIndex = (_writeIndex + source.Length) % _data.Length;
            _count += source.Length;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/>, padding with silence when the source has not delivered
    /// enough yet. Returns how many real samples were available.
    /// </summary>
    public int Read(Span<float> destination)
    {
        lock (_lock)
        {
            var available = Math.Min(destination.Length, _count);

            var firstChunk = Math.Min(available, _data.Length - _readIndex);
            _data.AsSpan(_readIndex, firstChunk).CopyTo(destination);

            if (firstChunk < available)
            {
                _data.AsSpan(0, available - firstChunk).CopyTo(destination[firstChunk..]);
            }

            _readIndex = (_readIndex + available) % _data.Length;
            _count -= available;

            destination[available..].Clear();
            return available;
        }
    }

    /// <summary>Throws away <paramref name="samples"/> of the oldest audio, used for drift correction.</summary>
    public void Skip(int samples)
    {
        lock (_lock)
        {
            var toSkip = Math.Min(samples, _count);
            _readIndex = (_readIndex + toSkip) % _data.Length;
            _count -= toSkip;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _readIndex = 0;
            _writeIndex = 0;
            _count = 0;
        }
    }
}
