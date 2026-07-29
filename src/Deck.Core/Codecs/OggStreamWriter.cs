namespace Deck.Core.Codecs;

/// <summary>
/// Minimal Ogg container muxer, enough to carry a live Opus stream.
/// <para>
/// Only whole packets are placed in a page and a page is closed before it would exceed the 255
/// segment limit, so a packet never spans two pages. Opus packets are far smaller than the 65025
/// bytes that would force a continuation, which lets this skip the continuation bookkeeping
/// entirely while still producing a spec-compliant stream.
/// </para>
/// </summary>
internal sealed class OggStreamWriter
{
    private const int MaxSegmentsPerPage = 255;

    /// <summary>Close a page around this size to keep latency low rather than filling to 64 KB.</summary>
    private const int TargetPageBytes = 4000;

    private readonly int _serialNumber;
    private readonly GrowableBuffer _pendingData = new();
    private readonly List<byte> _pendingSegments = new(MaxSegmentsPerPage);

    private int _pageSequence;
    private long _pendingGranule;
    private bool _firstPageWritten;

    public OggStreamWriter(int serialNumber) => _serialNumber = serialNumber;

    /// <summary>
    /// Queues a packet, emitting a completed page into <paramref name="output"/> first if this
    /// packet would not fit.
    /// </summary>
    public void AddPacket(ReadOnlySpan<byte> packet, long granulePosition, GrowableBuffer output, bool forceFlush = false)
    {
        var segmentsNeeded = (packet.Length / 255) + 1;

        if (_pendingSegments.Count > 0 &&
            (_pendingSegments.Count + segmentsNeeded > MaxSegmentsPerPage || _pendingData.Length >= TargetPageBytes))
        {
            WritePage(output, endOfStream: false);
        }

        var remaining = packet.Length;
        while (remaining >= 255)
        {
            _pendingSegments.Add(255);
            remaining -= 255;
        }

        _pendingSegments.Add((byte)remaining);
        _pendingData.Append(packet);
        _pendingGranule = granulePosition;

        if (forceFlush) WritePage(output, endOfStream: false);
    }

    /// <summary>Emits any queued packets as a page. Used for the header pages and at shutdown.</summary>
    public void Flush(GrowableBuffer output, bool endOfStream = false)
    {
        if (_pendingSegments.Count == 0 && !endOfStream) return;
        WritePage(output, endOfStream);
    }

    private void WritePage(GrowableBuffer output, bool endOfStream)
    {
        var segmentCount = _pendingSegments.Count;
        var payload = _pendingData.AsSpan();

        byte headerType = 0;
        if (!_firstPageWritten) headerType |= 0x02; // beginning of stream
        if (endOfStream) headerType |= 0x04;

        var headerLength = 27 + segmentCount;
        var pageLength = headerLength + payload.Length;

        var page = new byte[pageLength];
        var span = page.AsSpan();

        "OggS"u8.CopyTo(span);
        span[4] = 0; // stream structure version
        span[5] = headerType;
        BitConverter.TryWriteBytes(span[6..], _pendingGranule);
        BitConverter.TryWriteBytes(span[14..], _serialNumber);
        BitConverter.TryWriteBytes(span[18..], _pageSequence);
        // span[22..26] is the CRC, left zero while it is computed.
        span[26] = (byte)segmentCount;

        for (var i = 0; i < segmentCount; i++) span[27 + i] = _pendingSegments[i];
        payload.CopyTo(span[headerLength..]);

        var crc = OggCrc.Compute(span);
        BitConverter.TryWriteBytes(span[22..], crc);

        output.Append(span);

        _pageSequence++;
        _firstPageWritten = true;
        _pendingSegments.Clear();
        _pendingData.Clear();
    }
}

/// <summary>
/// Ogg's CRC32: polynomial 0x04c11db7, no input or output reflection, zero initial value and no
/// final XOR. Notably different from the zlib CRC32, so the standard table cannot be reused.
/// </summary>
internal static class OggCrc
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0u;
        foreach (var b in data)
        {
            crc = (crc << 8) ^ Table[((crc >> 24) & 0xFF) ^ b];
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var value = (uint)(i << 24);
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80000000) != 0 ? (value << 1) ^ 0x04c11db7 : value << 1;
            }

            table[i] = value;
        }

        return table;
    }
}
