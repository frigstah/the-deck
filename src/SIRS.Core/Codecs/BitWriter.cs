namespace Sirs.Core.Codecs;

/// <summary>
/// Big-endian bit-level output, which is what FLAC frames are built from. Bits accumulate in a
/// 64-bit register and drain a byte at a time, so a write of up to 32 bits costs a shift and a
/// couple of stores rather than a loop over individual bits.
/// </summary>
internal sealed class BitWriter
{
    private byte[] _data;
    private ulong _accumulator;
    private int _bitsHeld;

    public BitWriter(int initialCapacity = 16384) => _data = new byte[initialCapacity];

    /// <summary>Bytes written so far. Only meaningful when the writer is byte-aligned.</summary>
    public int ByteLength { get; private set; }

    public bool IsByteAligned => _bitsHeld == 0;

    public void Clear()
    {
        ByteLength = 0;
        _accumulator = 0;
        _bitsHeld = 0;
    }

    /// <summary>Writes the low <paramref name="bits"/> bits of <paramref name="value"/>, most significant first.</summary>
    public void WriteBits(uint value, int bits)
    {
        if (bits <= 0) return;

        var masked = bits >= 32 ? value : value & ((1u << bits) - 1);

        // At most 7 bits are ever held over, so 7 + 32 stays well inside the register.
        _accumulator = (_accumulator << bits) | masked;
        _bitsHeld += bits;

        while (_bitsHeld >= 8)
        {
            _bitsHeld -= 8;
            AppendByte((byte)(_accumulator >> _bitsHeld));
        }
    }

    /// <summary>Writes <paramref name="value"/> zero bits followed by a one - Rice's quotient.</summary>
    public void WriteUnary(uint value)
    {
        while (value >= 32)
        {
            WriteBits(0, 32);
            value -= 32;
        }

        // The terminating one is the low bit of a (value + 1)-bit field of zeroes.
        WriteBits(1, (int)value + 1);
    }

    /// <summary>
    /// The UTF-8-style variable-length integer FLAC uses for frame numbers. Not text: the same
    /// continuation-byte scheme, extended to seven bytes so a 36-bit sample number fits.
    /// </summary>
    public void WriteUtf8(ulong value)
    {
        if (value < 0x80)
        {
            WriteBits((uint)value, 8);
            return;
        }

        int bytes;
        if (value < 0x800) bytes = 2;
        else if (value < 0x10000) bytes = 3;
        else if (value < 0x200000) bytes = 4;
        else if (value < 0x4000000) bytes = 5;
        else if (value < 0x80000000) bytes = 6;
        else bytes = 7;

        // Lead byte: (bytes) high bits set, then a zero, then the top of the value.
        var leadBits = 8 - bytes - 1;
        var lead = (0xFFu << (leadBits + 1)) & 0xFF;
        lead |= (uint)(value >> ((bytes - 1) * 6)) & ((1u << leadBits) - 1);
        WriteBits(lead, 8);

        for (var i = bytes - 2; i >= 0; i--)
        {
            WriteBits(0x80u | ((uint)(value >> (i * 6)) & 0x3F), 8);
        }
    }

    /// <summary>Pads with zero bits up to the next byte boundary.</summary>
    public void ByteAlign()
    {
        if (_bitsHeld > 0) WriteBits(0, 8 - _bitsHeld);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) WriteBits(b, 8);
    }

    public ReadOnlySpan<byte> AsSpan() => _data.AsSpan(0, ByteLength);

    private void AppendByte(byte value)
    {
        if (ByteLength == _data.Length) Array.Resize(ref _data, _data.Length * 2);
        _data[ByteLength++] = value;
    }
}
