namespace Sirs.Core.Codecs;

/// <summary>
/// Ogg FLAC (D4) - lossless, for stations that would rather send bits than lose them, and for
/// archive-quality recordings.
/// <para>
/// Written here rather than bound to libFLAC, for the same reason Opus uses Concentus: a native
/// codec means shipping and loading a DLL per architecture, and FLAC's format is small enough to
/// implement honestly. It uses fixed polynomial predictors rather than full LPC, which gives up a
/// few percent of compression for a fraction of the CPU - the right trade for a live encoder that
/// may be running alongside three others.
/// </para>
/// </summary>
public sealed class FlacEncoder : IAudioEncoder
{
    /// <summary>The usual FLAC block size. Divisible by 64, which the partition search relies on.</summary>
    private const int BlockSize = 4096;

    private const int BitsPerSample = 16;
    private const int MaxFixedOrder = 4;
    private const int MaxPartitionOrder = 6;

    /// <summary>Rice parameters above this need the escape code; going verbatim is cheaper.</summary>
    private const int MaxRiceParameter = 14;

    private readonly OggStreamWriter _ogg;
    private readonly GrowableBuffer _output = new();
    private readonly BitWriter _frame = new();

    // One buffer per channel, plus the mid and side candidates for stereo decorrelation.
    private readonly int[][] _channels;
    private readonly int[] _mid = new int[BlockSize];
    private readonly int[] _side = new int[BlockSize];
    private readonly int[] _residual = new int[BlockSize];
    private readonly long[,] _partitionCost = new long[1 << MaxPartitionOrder, MaxRiceParameter + 1];

    private int _fill;
    private long _samplesEncoded;
    private uint _frameNumber;
    private bool _finished;
    private int _finestPartitionOrder;

    public FlacEncoder(EncoderSettings settings)
    {
        Settings = settings.Normalised();

        _channels = new int[Settings.Channels][];
        for (var i = 0; i < Settings.Channels; i++) _channels[i] = new int[BlockSize];

        _ogg = new OggStreamWriter(Random.Shared.Next(1, int.MaxValue));
        StreamHeader = BuildHeaderPages();
    }

    public StreamCodec Codec => StreamCodec.OggFlac;

    public EncoderSettings Settings { get; }

    public string ContentType => StreamCodec.OggFlac.ContentType();

    /// <summary>The mapping header and comment pages. Re-sent verbatim whenever a connection restarts.</summary>
    public byte[] StreamHeader { get; }

    public ReadOnlySpan<byte> Encode(ReadOnlySpan<float> interleaved)
    {
        if (_finished || interleaved.IsEmpty) return ReadOnlySpan<byte>.Empty;

        _output.Clear();

        var channels = Settings.Channels;
        var frames = interleaved.Length / channels;

        for (var frame = 0; frame < frames; frame++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var sample = interleaved[(frame * channels) + ch];
                var clamped = sample > 1f ? 1f : sample < -1f ? -1f : sample;
                _channels[ch][_fill] = (int)(clamped * 32767f);
            }

            if (++_fill == BlockSize) WriteBlock(BlockSize);
        }

        return _output.AsSpan();
    }

    public ReadOnlySpan<byte> Finish()
    {
        if (_finished) return ReadOnlySpan<byte>.Empty;
        _finished = true;

        _output.Clear();

        // The final frame is allowed to be shorter than the rest, so no padding is needed - which
        // matters for a lossless codec, where inventing silence would be a real change to the audio.
        if (_fill > 0) WriteBlock(_fill);

        _ogg.Flush(_output, endOfStream: true);
        return _output.AsSpan();
    }

    // ---------------------------------------------------------------- frames

    private void WriteBlock(int blockSamples)
    {
        _fill = 0;
        _frame.Clear();

        var assignment = Settings.Channels == 2
            ? ChooseStereoAssignment(blockSamples)
            : Settings.Channels - 1;

        WriteFrameHeader(blockSamples, assignment);

        // The header is byte-aligned by construction, so the CRC-8 covers exactly the bytes so far.
        _frame.WriteBits(FlacCrc.Crc8(_frame.AsSpan()), 8);

        switch (assignment)
        {
            case 8: // left / side
                WriteSubframe(_channels[0], blockSamples, BitsPerSample);
                WriteSubframe(_side, blockSamples, BitsPerSample + 1);
                break;

            case 9: // side / right
                WriteSubframe(_side, blockSamples, BitsPerSample + 1);
                WriteSubframe(_channels[1], blockSamples, BitsPerSample);
                break;

            case 10: // mid / side
                WriteSubframe(_mid, blockSamples, BitsPerSample);
                WriteSubframe(_side, blockSamples, BitsPerSample + 1);
                break;

            default:
                for (var ch = 0; ch < Settings.Channels; ch++)
                {
                    WriteSubframe(_channels[ch], blockSamples, BitsPerSample);
                }

                break;
        }

        _frame.ByteAlign();
        _frame.WriteBits(FlacCrc.Crc16(_frame.AsSpan()), 16);

        _frameNumber++;
        _samplesEncoded += blockSamples;
        _ogg.AddPacket(_frame.AsSpan(), _samplesEncoded, _output);
    }

    private void WriteFrameHeader(int blockSamples, int channelAssignment)
    {
        _frame.WriteBits(0b11111111111110, 14); // sync
        _frame.WriteBits(0, 1);                 // reserved
        _frame.WriteBits(0, 1);                 // fixed block size, so the frame number is coded

        // 4096 has its own code; a short final frame carries its length at the end of the header.
        var isStandardBlock = blockSamples == BlockSize;
        _frame.WriteBits(isStandardBlock ? 0b1100u : 0b0111u, 4);

        _frame.WriteBits(SampleRateCode(Settings.SampleRate), 4);
        _frame.WriteBits((uint)channelAssignment, 4);
        _frame.WriteBits(0b100, 3); // 16 bits per sample
        _frame.WriteBits(0, 1);     // reserved

        _frame.WriteUtf8(_frameNumber);

        if (!isStandardBlock) _frame.WriteBits((uint)(blockSamples - 1), 16);
    }

    /// <summary>
    /// Picks between plain stereo and the three decorrelated forms by estimating what each would
    /// cost. On correlated material - most music - mid/side is a real saving; on genuinely different
    /// left and right content it would cost, which is why this is measured rather than assumed.
    /// </summary>
    private int ChooseStereoAssignment(int blockSamples)
    {
        var left = _channels[0];
        var right = _channels[1];

        for (var i = 0; i < blockSamples; i++)
        {
            var l = left[i];
            var r = right[i];
            _side[i] = l - r;
            _mid[i] = (l + r) >> 1;
        }

        var leftBits = EstimateBits(left, blockSamples);
        var rightBits = EstimateBits(right, blockSamples);
        var midBits = EstimateBits(_mid, blockSamples);
        var sideBits = EstimateBits(_side, blockSamples);

        var best = leftBits + rightBits;
        var assignment = 1; // independent

        if (leftBits + sideBits < best)
        {
            best = leftBits + sideBits;
            assignment = 8;
        }

        if (sideBits + rightBits < best)
        {
            best = sideBits + rightBits;
            assignment = 9;
        }

        if (midBits + sideBits < best) assignment = 10;

        return assignment;
    }

    /// <summary>Rough cost of a signal, used only to compare channel arrangements against each other.</summary>
    private double EstimateBits(int[] signal, int blockSamples)
    {
        var order = BestFixedOrder(signal, blockSamples, out var residualSum);
        var count = blockSamples - order;
        if (count <= 0 || residualSum == 0) return count;

        var mean = (double)residualSum / count;
        return count * (Math.Log2(mean + 1) + 1);
    }

    // ---------------------------------------------------------------- subframes

    private void WriteSubframe(int[] signal, int blockSamples, int bitsPerSample)
    {
        if (IsConstant(signal, blockSamples))
        {
            // Digital silence and held DC are common enough on a live feed to be worth the check.
            _frame.WriteBits(0, 1);
            _frame.WriteBits(0b000000, 6);
            _frame.WriteBits(0, 1);
            _frame.WriteBits((uint)signal[0], bitsPerSample);
            return;
        }

        var order = BestFixedOrder(signal, blockSamples, out _);
        ComputeResidual(signal, blockSamples, order);

        var residualCount = blockSamples - order;
        var partitionOrder = ChoosePartitionOrder(residualCount, order, blockSamples, out var residualBits);

        var fixedBits = 8 + (order * bitsPerSample) + residualBits;
        var verbatimBits = 8 + (blockSamples * bitsPerSample);

        if (fixedBits >= verbatimBits)
        {
            // Nothing the predictor can do with this block - noise, or a residual too wide for the
            // Rice parameters we allow. Storing it raw is both smaller and simpler.
            _frame.WriteBits(0, 1);
            _frame.WriteBits(0b000001, 6);
            _frame.WriteBits(0, 1);

            for (var i = 0; i < blockSamples; i++) _frame.WriteBits((uint)signal[i], bitsPerSample);
            return;
        }

        _frame.WriteBits(0, 1);
        _frame.WriteBits((uint)(0b001000 | order), 6);
        _frame.WriteBits(0, 1);

        for (var i = 0; i < order; i++) _frame.WriteBits((uint)signal[i], bitsPerSample);

        WriteResidual(residualCount, order, blockSamples, partitionOrder);
    }

    private static bool IsConstant(int[] signal, int blockSamples)
    {
        var first = signal[0];
        for (var i = 1; i < blockSamples; i++)
        {
            if (signal[i] != first) return false;
        }

        return true;
    }

    /// <summary>
    /// Picks the fixed predictor order whose residuals are smallest in absolute terms - the standard
    /// heuristic, and a good one: Rice coding cost tracks the magnitude of the residuals closely.
    /// </summary>
    private static int BestFixedOrder(int[] signal, int blockSamples, out long bestSum)
    {
        var bestOrder = 0;
        bestSum = long.MaxValue;

        var maxOrder = Math.Min(MaxFixedOrder, blockSamples - 1);

        for (var order = 0; order <= maxOrder; order++)
        {
            long sum = 0;

            for (var i = order; i < blockSamples; i++)
            {
                long value = order switch
                {
                    0 => signal[i],
                    1 => signal[i] - signal[i - 1],
                    2 => signal[i] - (2L * signal[i - 1]) + signal[i - 2],
                    3 => signal[i] - (3L * signal[i - 1]) + (3L * signal[i - 2]) - signal[i - 3],
                    _ => signal[i] - (4L * signal[i - 1]) + (6L * signal[i - 2]) - (4L * signal[i - 3]) + signal[i - 4],
                };

                sum += Math.Abs(value);
            }

            if (sum >= bestSum) continue;

            bestSum = sum;
            bestOrder = order;
        }

        return bestOrder;
    }

    private void ComputeResidual(int[] signal, int blockSamples, int order)
    {
        for (var i = order; i < blockSamples; i++)
        {
            long value = order switch
            {
                0 => signal[i],
                1 => signal[i] - signal[i - 1],
                2 => signal[i] - (2L * signal[i - 1]) + signal[i - 2],
                3 => signal[i] - (3L * signal[i - 1]) + (3L * signal[i - 2]) - signal[i - 3],
                _ => signal[i] - (4L * signal[i - 1]) + (6L * signal[i - 2]) - (4L * signal[i - 3]) + signal[i - 4],
            };

            _residual[i - order] = (int)value;
        }
    }

    // ---------------------------------------------------------------- Rice partitioning

    /// <summary>
    /// Finds the partition order that codes the residuals in the fewest bits.
    /// <para>
    /// The cost of a partition at Rice parameter k is n(k+1) + Σ(v >> k), and that sum is additive
    /// across partitions. So the finest split is costed once, and every coarser split is read off by
    /// adding those numbers up - exact, not estimated, and one pass over the residuals rather than
    /// seven.
    /// </para>
    /// </summary>
    private int ChoosePartitionOrder(int residualCount, int order, int blockSamples, out long bestBits)
    {
        var maxOrder = MaxPartitionOrder;
        while (maxOrder > 0 && (blockSamples % (1 << maxOrder) != 0 || (blockSamples >> maxOrder) <= order))
        {
            maxOrder--;
        }

        _finestPartitionOrder = maxOrder;

        var finestCount = 1 << maxOrder;
        var partitionLength = blockSamples >> maxOrder;

        Array.Clear(_partitionCost);

        for (var i = 0; i < residualCount; i++)
        {
            var zigzag = ZigZag(_residual[i]);
            var partition = (i + order) / partitionLength;

            for (var k = 0; k <= MaxRiceParameter; k++) _partitionCost[partition, k] += zigzag >> k;
        }

        var bestOrder = 0;
        bestBits = long.MaxValue;

        for (var p = 0; p <= maxOrder; p++)
        {
            var group = finestCount >> p;
            long total = 2 + 4; // coding method and partition order

            for (var j = 0; j < (1 << p); j++)
            {
                var samples = (blockSamples >> p) - (j == 0 ? order : 0);
                total += 4 + PartitionBits(j * group, group, samples, out _);
            }

            if (total >= bestBits) continue;

            bestBits = total;
            bestOrder = p;
        }

        return bestOrder;
    }

    /// <summary>Cheapest coding of one partition, and the Rice parameter that achieves it.</summary>
    private long PartitionBits(int firstFinest, int finestCount, int samples, out int parameter)
    {
        parameter = 0;
        var best = long.MaxValue;

        for (var k = 0; k <= MaxRiceParameter; k++)
        {
            long quotients = 0;
            for (var f = firstFinest; f < firstFinest + finestCount; f++) quotients += _partitionCost[f, k];

            var bits = ((long)samples * (k + 1)) + quotients;
            if (bits >= best) continue;

            best = bits;
            parameter = k;
        }

        return best;
    }

    private void WriteResidual(int residualCount, int order, int blockSamples, int partitionOrder)
    {
        _frame.WriteBits(0, 2); // Rice coding with 4-bit parameters
        _frame.WriteBits((uint)partitionOrder, 4);

        // The cost table left behind by ChoosePartitionOrder is still valid, so the parameters are
        // read back out of it rather than recomputed from the residuals.
        var group = (1 << _finestPartitionOrder) >> partitionOrder;
        var written = 0;

        for (var j = 0; j < (1 << partitionOrder); j++)
        {
            var samples = (blockSamples >> partitionOrder) - (j == 0 ? order : 0);
            PartitionBits(j * group, group, samples, out var parameter);

            _frame.WriteBits((uint)parameter, 4);

            for (var i = 0; i < samples; i++)
            {
                var zigzag = ZigZag(_residual[written + i]);
                _frame.WriteUnary((uint)(zigzag >> parameter));
                if (parameter > 0) _frame.WriteBits((uint)zigzag, parameter);
            }

            written += samples;
        }

        if (written != residualCount)
        {
            throw new InvalidOperationException(
                $"FLAC partitioning covered {written} residuals of {residualCount}.");
        }
    }

    /// <summary>Folds a signed residual into an unsigned one, so small negatives stay small.</summary>
    private static uint ZigZag(int value) =>
        value < 0 ? (uint)((-(long)value << 1) - 1) : (uint)value << 1;

    // ---------------------------------------------------------------- headers

    /// <summary>
    /// The Ogg FLAC mapping header, then the comment block. The mapping header must sit alone in the
    /// first page, so both are flushed as they are added.
    /// </summary>
    private byte[] BuildHeaderPages()
    {
        var buffer = new GrowableBuffer(512);

        var streamInfo = BuildStreamInfo();

        var mapping = new GrowableBuffer(64);
        mapping.Append(0x7F);
        mapping.Append("FLAC"u8);
        mapping.Append(1); // mapping version major
        mapping.Append(0); // mapping version minor

        // One further header packet follows this one: the comment block.
        mapping.Append(0);
        mapping.Append(1);

        mapping.Append("fLaC"u8);
        mapping.Append(0);  // metadata block header: not the last block, type 0 (STREAMINFO)
        mapping.Append(0);
        mapping.Append(0);
        mapping.Append((byte)streamInfo.Length);
        mapping.Append(streamInfo);

        _ogg.AddPacket(mapping.AsSpan(), 0, buffer, forceFlush: true);

        var comment = BuildComment();
        var commentBlock = new GrowableBuffer(128);
        commentBlock.Append(0x84); // last metadata block, type 4 (VORBIS_COMMENT)
        commentBlock.Append((byte)(comment.Length >> 16));
        commentBlock.Append((byte)(comment.Length >> 8));
        commentBlock.Append((byte)comment.Length);
        commentBlock.Append(comment);

        _ogg.AddPacket(commentBlock.AsSpan(), 0, buffer, forceFlush: true);

        return buffer.ToArray();
    }

    private byte[] BuildStreamInfo()
    {
        var writer = new BitWriter(64);

        // A live stream has no idea how long it will run, so the smallest block size has to allow
        // for a short final frame, and the total sample count and MD5 are honestly left unknown.
        writer.WriteBits(16, 16);
        writer.WriteBits(BlockSize, 16);
        writer.WriteBits(0, 24); // minimum frame size, unknown
        writer.WriteBits(0, 24); // maximum frame size, unknown
        writer.WriteBits((uint)Settings.SampleRate, 20);
        writer.WriteBits((uint)(Settings.Channels - 1), 3);
        writer.WriteBits(BitsPerSample - 1, 5);
        writer.WriteBits(0, 18); // total samples, high bits
        writer.WriteBits(0, 18); // total samples, low bits
        for (var i = 0; i < 16; i++) writer.WriteBits(0, 8); // MD5, unknown

        return writer.AsSpan().ToArray();
    }

    private static byte[] BuildComment()
    {
        var vendor = System.Text.Encoding.UTF8.GetBytes("SIRS");
        var buffer = new GrowableBuffer(64);

        BitConverter.TryWriteBytes(buffer.Reserve(4), vendor.Length);
        buffer.Append(vendor);
        BitConverter.TryWriteBytes(buffer.Reserve(4), 0); // no user comments

        return buffer.ToArray();
    }

    /// <summary>The frame-header code for a sample rate, or 0 meaning "look it up in STREAMINFO".</summary>
    private static uint SampleRateCode(int sampleRate) => sampleRate switch
    {
        88200 => 1,
        176400 => 2,
        192000 => 3,
        8000 => 4,
        16000 => 5,
        22050 => 6,
        24000 => 7,
        32000 => 8,
        44100 => 9,
        48000 => 10,
        96000 => 11,
        _ => 0,
    };

    public void Dispose()
    {
    }
}

/// <summary>
/// FLAC's two frame checksums. Both are plain MSB-first CRCs with no reflection and no final XOR,
/// but they differ from each other and from Ogg's, so each gets its own table.
/// </summary>
internal static class FlacCrc
{
    private static readonly byte[] Table8 = BuildTable8();
    private static readonly ushort[] Table16 = BuildTable16();

    /// <summary>CRC-8, polynomial x^8 + x^2 + x + 1, over the frame header.</summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data) crc = Table8[crc ^ b];
        return crc;
    }

    /// <summary>CRC-16, polynomial x^16 + x^15 + x^2 + 1, over everything in the frame before it.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data) crc = (ushort)((crc << 8) ^ Table16[((crc >> 8) ^ b) & 0xFF]);
        return crc;
    }

    private static byte[] BuildTable8()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var value = (byte)i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (byte)((value & 0x80) != 0 ? (value << 1) ^ 0x07 : value << 1);
            }

            table[i] = value;
        }

        return table;
    }

    private static ushort[] BuildTable16()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var value = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                value = (ushort)((value & 0x8000) != 0 ? (value << 1) ^ 0x8005 : value << 1);
            }

            table[i] = value;
        }

        return table;
    }
}
