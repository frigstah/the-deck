namespace Deck.Core.Codecs;

/// <summary>
/// A plain append-only byte buffer that keeps its array between blocks. The encode path runs
/// hundreds of times a second, so avoiding a fresh allocation per block keeps the GC quiet enough
/// that a network buffer of a second or two absorbs any remaining pause.
/// </summary>
internal sealed class GrowableBuffer
{
    private byte[] _data;

    public GrowableBuffer(int initialCapacity = 8192) => _data = new byte[initialCapacity];

    public int Length { get; private set; }

    public void Clear() => Length = 0;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(Length + bytes.Length);
        bytes.CopyTo(_data.AsSpan(Length));
        Length += bytes.Length;
    }

    public void Append(byte value)
    {
        EnsureCapacity(Length + 1);
        _data[Length++] = value;
    }

    /// <summary>Reserves space and hands back the region to write into directly.</summary>
    public Span<byte> Reserve(int count)
    {
        EnsureCapacity(Length + count);
        var span = _data.AsSpan(Length, count);
        Length += count;
        return span;
    }

    public ReadOnlySpan<byte> AsSpan() => _data.AsSpan(0, Length);

    public byte[] ToArray() => _data.AsSpan(0, Length).ToArray();

    private void EnsureCapacity(int required)
    {
        if (_data.Length >= required) return;
        var capacity = Math.Max(required, _data.Length * 2);
        Array.Resize(ref _data, capacity);
    }
}
