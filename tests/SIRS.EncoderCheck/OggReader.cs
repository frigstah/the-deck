namespace Sirs.EncoderCheck;

internal sealed class OggPage
{
    public required byte HeaderType { get; init; }

    public required long GranulePosition { get; init; }

    public required int SerialNumber { get; init; }

    public required int SequenceNumber { get; init; }

    public required List<byte[]> Packets { get; init; }

    public bool IsContinuation => (HeaderType & 0x01) != 0;

    public bool IsBeginningOfStream => (HeaderType & 0x02) != 0;

    public bool IsEndOfStream => (HeaderType & 0x04) != 0;
}

/// <summary>
/// An Ogg parser written independently of the muxer, including its own bit-by-bit CRC. Because it
/// does not share the writer's lookup table, a page that verifies here really was framed correctly.
/// </summary>
internal static class OggReader
{
    public static List<OggPage> ReadPages(byte[] data)
    {
        VerifyCrcAgainstKnownVector();

        var pages = new List<OggPage>();
        var offset = 0;
        var carried = new List<byte>();

        while (offset < data.Length)
        {
            if (offset + 27 > data.Length) throw new Exception($"truncated page header at offset {offset}");

            if (data[offset] != (byte)'O' || data[offset + 1] != (byte)'g' ||
                data[offset + 2] != (byte)'g' || data[offset + 3] != (byte)'S')
            {
                throw new Exception($"no OggS capture pattern at offset {offset}");
            }

            if (data[offset + 4] != 0) throw new Exception($"unexpected Ogg version {data[offset + 4]}");

            var headerType = data[offset + 5];
            var granule = BitConverter.ToInt64(data, offset + 6);
            var serial = BitConverter.ToInt32(data, offset + 14);
            var sequence = BitConverter.ToInt32(data, offset + 18);
            var storedCrc = BitConverter.ToUInt32(data, offset + 22);
            var segmentCount = data[offset + 26];

            var headerLength = 27 + segmentCount;
            if (offset + headerLength > data.Length) throw new Exception($"truncated segment table at offset {offset}");

            var payloadLength = 0;
            for (var i = 0; i < segmentCount; i++) payloadLength += data[offset + 27 + i];

            var pageLength = headerLength + payloadLength;
            if (offset + pageLength > data.Length) throw new Exception($"truncated page payload at offset {offset}");

            // The CRC is computed over the whole page with the checksum field zeroed.
            var page = data.AsSpan(offset, pageLength).ToArray();
            page[22] = page[23] = page[24] = page[25] = 0;
            var computed = Crc32(page);
            if (computed != storedCrc)
            {
                throw new Exception(
                    $"page {sequence} CRC mismatch: stored 0x{storedCrc:X8}, computed 0x{computed:X8}");
            }

            // Reassemble packets: a packet ends on the first segment shorter than 255. A packet may
            // legitimately span pages - Vorbis setup headers routinely do - so a partial packet is
            // carried across into the next page.
            var packets = new List<byte[]>();
            var payloadOffset = offset + headerLength;
            var current = carried;
            carried = new List<byte>();

            for (var i = 0; i < segmentCount; i++)
            {
                var segmentLength = data[offset + 27 + i];
                current.AddRange(data.AsSpan(payloadOffset, segmentLength).ToArray());
                payloadOffset += segmentLength;

                if (segmentLength < 255)
                {
                    packets.Add(current.ToArray());
                    current = new List<byte>();
                }
            }

            // Whatever is left over continues on the following page.
            carried = current;

            pages.Add(new OggPage
            {
                HeaderType = headerType,
                GranulePosition = granule,
                SerialNumber = serial,
                SequenceNumber = sequence,
                Packets = packets,
            });

            offset += pageLength;
        }

        if (carried.Count > 0)
        {
            throw new Exception("the stream ends with an unfinished packet");
        }

        return pages;
    }

    /// <summary>
    /// Ogg's CRC32 with poly 0x04C11DB7, zero init, no reflection and no final XOR. The catalogued
    /// check value for "123456789" under those parameters is 0x89A1897F.
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0u;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
            }
        }

        return crc;
    }

    private static void VerifyCrcAgainstKnownVector()
    {
        var check = Crc32("123456789"u8);
        if (check != 0x89A1897F)
        {
            throw new Exception($"the reader's own CRC is wrong: got 0x{check:X8}, expected 0x89A1897F");
        }
    }
}
