namespace Deck.EncoderCheck;

/// <summary>
/// A FLAC frame decoder written independently of the encoder, so a stream that decodes here really
/// is a FLAC stream and not just something the encoder agrees with itself about. Both frame CRCs
/// are verified, which means the bit packing has to be exactly right for a frame to be accepted.
/// <para>
/// Only what Deck produces is supported - fixed predictors, Rice partitions, 16-bit samples. An LPC
/// subframe is reported as unsupported rather than skipped, so a change in the encoder that started
/// emitting one could not slip past this.
/// </para>
/// </summary>
internal sealed class FlacReader(byte[] frame)
{
    private readonly BitReader _bits = new(frame);

    public int BlockSize { get; private set; }

    public int Channels { get; private set; }

    public int SampleRate { get; private set; }

    /// <summary>Decodes one frame into per-channel samples, verifying both checksums.</summary>
    public int[][] Decode(int bitsPerSample)
    {
        var sync = _bits.Read(14);
        if (sync != 0x3FFE) throw new Exception($"frame sync was 0x{sync:X4}, expected 0x3FFE");

        if (_bits.Read(1) != 0) throw new Exception("the reserved bit after the sync is set");

        var variableBlocking = _bits.Read(1) == 1;
        var blockSizeCode = (int)_bits.Read(4);
        var sampleRateCode = (int)_bits.Read(4);
        var channelAssignment = (int)_bits.Read(4);
        var sampleSizeCode = (int)_bits.Read(3);

        if (_bits.Read(1) != 0) throw new Exception("the reserved bit at the end of the header is set");

        ReadUtf8();

        BlockSize = blockSizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)_bits.Read(8) + 1,
            7 => (int)_bits.Read(16) + 1,
            >= 8 and <= 15 => 256 << (blockSizeCode - 8),
            _ => throw new Exception("the frame declares a reserved block size"),
        };

        SampleRate = sampleRateCode switch
        {
            0 => 0, // deferred to STREAMINFO
            1 => 88200, 2 => 176400, 3 => 192000, 4 => 8000, 5 => 16000, 6 => 22050,
            7 => 24000, 8 => 32000, 9 => 44100, 10 => 48000, 11 => 96000,
            12 => (int)_bits.Read(8) * 1000,
            13 => (int)_bits.Read(16),
            14 => (int)_bits.Read(16) * 10,
            _ => throw new Exception("the frame declares an invalid sample rate"),
        };

        var declaredBits = sampleSizeCode switch
        {
            0 => bitsPerSample,
            1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32,
            _ => throw new Exception($"the frame declares a reserved sample size ({sampleSizeCode})"),
        };

        if (declaredBits != bitsPerSample)
        {
            throw new Exception($"the frame declares {declaredBits} bits per sample, expected {bitsPerSample}");
        }

        if (variableBlocking) throw new Exception("variable blocking was used; Deck should always use fixed");

        // The header ends byte-aligned, and the CRC-8 covers exactly those bytes.
        if (!_bits.IsByteAligned) throw new Exception("the frame header did not end on a byte boundary");

        var headerBytes = _bits.Position;
        var expectedCrc8 = Crc8(frame.AsSpan(0, headerBytes));
        var actualCrc8 = (byte)_bits.Read(8);
        if (actualCrc8 != expectedCrc8)
        {
            throw new Exception($"header CRC-8 was 0x{actualCrc8:X2}, computed 0x{expectedCrc8:X2}");
        }

        Channels = channelAssignment < 8 ? channelAssignment + 1 : 2;

        var decoded = new int[Channels][];
        for (var ch = 0; ch < Channels; ch++)
        {
            // The side channel of a decorrelated pair carries one extra bit of range.
            var subframeBits = bitsPerSample + channelAssignment switch
            {
                8 => ch == 1 ? 1 : 0,
                9 => ch == 0 ? 1 : 0,
                10 => ch == 1 ? 1 : 0,
                _ => 0,
            };

            decoded[ch] = ReadSubframe(subframeBits);
        }

        _bits.AlignToByte();

        var frameBytes = _bits.Position;
        var expectedCrc16 = Crc16(frame.AsSpan(0, frameBytes));
        var actualCrc16 = (ushort)_bits.Read(16);
        if (actualCrc16 != expectedCrc16)
        {
            throw new Exception($"frame CRC-16 was 0x{actualCrc16:X4}, computed 0x{expectedCrc16:X4}");
        }

        Undecorrelate(decoded, channelAssignment);
        return decoded;
    }

    /// <summary>Total bytes this frame occupied, so the caller can confirm nothing was left over.</summary>
    public int BytesConsumed => _bits.Position;

    private static void Undecorrelate(int[][] channels, int assignment)
    {
        if (assignment < 8) return;

        var a = channels[0];
        var b = channels[1];

        for (var i = 0; i < a.Length; i++)
        {
            switch (assignment)
            {
                case 8: // left / side
                    b[i] = a[i] - b[i];
                    break;

                case 9: // side / right
                    a[i] += b[i];
                    break;

                default: // mid / side
                {
                    var side = b[i];
                    var mid = (a[i] << 1) | (side & 1);
                    a[i] = (mid + side) >> 1;
                    b[i] = (mid - side) >> 1;
                    break;
                }
            }
        }
    }

    private int[] ReadSubframe(int bitsPerSample)
    {
        if (_bits.Read(1) != 0) throw new Exception("a subframe header does not start with a zero bit");

        var type = (int)_bits.Read(6);
        var wasted = 0;

        if (_bits.Read(1) == 1)
        {
            wasted = 1;
            while (_bits.Read(1) == 0) wasted++;
        }

        var effectiveBits = bitsPerSample - wasted;
        var samples = new int[BlockSize];

        switch (type)
        {
            case 0: // constant
            {
                var value = _bits.ReadSigned(effectiveBits);
                Array.Fill(samples, value);
                break;
            }

            case 1: // verbatim
                for (var i = 0; i < BlockSize; i++) samples[i] = _bits.ReadSigned(effectiveBits);
                break;

            case >= 8 and <= 12: // fixed predictor
            {
                var order = type - 8;
                for (var i = 0; i < order; i++) samples[i] = _bits.ReadSigned(effectiveBits);

                var residual = ReadResidual(order);
                Restore(samples, residual, order);
                break;
            }

            case >= 32:
                throw new Exception($"subframe type {type} is an LPC subframe; Deck should only emit fixed predictors");

            default:
                throw new Exception($"subframe type {type} is reserved");
        }

        if (wasted > 0)
        {
            for (var i = 0; i < BlockSize; i++) samples[i] <<= wasted;
        }

        return samples;
    }

    private static void Restore(int[] samples, int[] residual, int order)
    {
        for (var i = order; i < samples.Length; i++)
        {
            var r = residual[i - order];
            samples[i] = order switch
            {
                0 => r,
                1 => r + samples[i - 1],
                2 => r + (2 * samples[i - 1]) - samples[i - 2],
                3 => r + (3 * samples[i - 1]) - (3 * samples[i - 2]) + samples[i - 3],
                _ => r + (4 * samples[i - 1]) - (6 * samples[i - 2]) + (4 * samples[i - 3]) - samples[i - 4],
            };
        }
    }

    private int[] ReadResidual(int order)
    {
        var method = (int)_bits.Read(2);
        if (method > 1) throw new Exception($"residual coding method {method} is reserved");

        var parameterBits = method == 0 ? 4 : 5;
        var escape = method == 0 ? 15u : 31u;

        var partitionOrder = (int)_bits.Read(4);
        var partitions = 1 << partitionOrder;
        var residual = new int[BlockSize - order];
        var written = 0;

        for (var p = 0; p < partitions; p++)
        {
            var count = (BlockSize >> partitionOrder) - (p == 0 ? order : 0);
            var parameter = _bits.Read(parameterBits);

            if (parameter == escape)
            {
                var rawBits = (int)_bits.Read(5);
                for (var i = 0; i < count; i++) residual[written++] = rawBits == 0 ? 0 : _bits.ReadSigned(rawBits);
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                var quotient = 0u;
                while (_bits.Read(1) == 0) quotient++;

                var value = (quotient << (int)parameter) | (parameter > 0 ? _bits.Read((int)parameter) : 0);
                residual[written++] = (value & 1) == 1 ? -(int)(value >> 1) - 1 : (int)(value >> 1);
            }
        }

        if (written != residual.Length)
        {
            throw new Exception($"partitions covered {written} residuals, expected {residual.Length}");
        }

        return residual;
    }

    private ulong ReadUtf8()
    {
        var first = _bits.Read(8);
        if ((first & 0x80) == 0) return first;

        var extra = 0;
        while ((first & (0x80u >> extra)) != 0) extra++;
        extra--; // the count of continuation bytes

        var value = first & ((1u << (7 - extra - 1)) - 1);
        for (var i = 0; i < extra; i++)
        {
            var next = _bits.Read(8);
            if ((next & 0xC0) != 0x80) throw new Exception("malformed frame number");
            value = (value << 6) | (next & 0x3F);
        }

        return value;
    }

    // ---------------------------------------------------------------- checksums

    private static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
            }
        }

        return crc;
    }

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
            }
        }

        return crc;
    }

    /// <summary>Straightforward MSB-first bit reader. Deliberately unoptimised - clarity wins here.</summary>
    private sealed class BitReader(byte[] data)
    {
        private int _bitOffset;

        /// <summary>Byte position, valid when aligned.</summary>
        public int Position => _bitOffset / 8;

        public bool IsByteAligned => _bitOffset % 8 == 0;

        public void AlignToByte()
        {
            if (!IsByteAligned) _bitOffset += 8 - (_bitOffset % 8);
        }

        public uint Read(int bits)
        {
            var value = 0u;
            for (var i = 0; i < bits; i++)
            {
                var byteIndex = _bitOffset >> 3;
                if (byteIndex >= data.Length) throw new Exception("the frame ended early");

                var bit = (data[byteIndex] >> (7 - (_bitOffset & 7))) & 1;
                value = (value << 1) | (uint)bit;
                _bitOffset++;
            }

            return value;
        }

        public int ReadSigned(int bits)
        {
            var value = Read(bits);
            var signBit = 1u << (bits - 1);
            return (value & signBit) != 0 ? (int)(value - (signBit << 1)) : (int)value;
        }
    }
}
