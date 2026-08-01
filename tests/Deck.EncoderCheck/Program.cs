using Concentus;
using Deck.Core.Codecs;

namespace Deck.EncoderCheck;

/// <summary>
/// Smoke test for the encoder layer. Encodes a known tone, then verifies the bytes are a real
/// MP3 frame stream and a real Ogg Opus stream - page CRCs, header pages, and a decode back to
/// audio that still contains the tone. Ogg muxing fails silently in ways a build cannot catch,
/// so this runs as a check rather than living only in someone's head.
/// </summary>
internal static class Program
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const double ToneHz = 440.0;
    private const double Seconds = 3.0;

    private static int Main(string[] args)
    {
        var failures = 0;

        // A helper for the process-capture check: this executable, re-run to be a program that plays a
        // tone, so there is something to capture that is unmistakably not us.
        var tone = Array.IndexOf(args, "--tone");
        if (tone >= 0 && tone + 1 < args.Length && int.TryParse(args[tone + 1], out var hertz))
        {
            return ProcessCaptureCheck.PlayTone(hertz);
        }

        // Prints the palette block for the website. Not a check: it is how the block in index.html is
        // produced in the first place, and the check below is what keeps it current afterwards.
        if (args.Contains("--site-palettes"))
        {
            Console.WriteLine(SitePalettes.Css());
            return 0;
        }

        // Needs audio hardware and briefly makes real sound, so these are opt-in.
        if (args.Contains("--loopback") || args.Contains("--mixer") ||
            args.Contains("--recovery") || args.Contains("--metadata") || args.Contains("--process"))
        {
            if (args.Contains("--loopback")) failures += LoopbackCheck.Run();
            if (args.Contains("--mixer")) failures += LoopbackCheck.RunMixer();
            if (args.Contains("--recovery")) failures += RecoveryCheck.Run();
            if (args.Contains("--metadata")) failures += MediaSessionCheck.Run();
            if (args.Contains("--process")) failures += ProcessCaptureCheck.Run();

            Console.WriteLine(failures == 0 ? "Audio device checks passed." : "Audio device checks FAILED.");
            return failures == 0 ? 0 : 1;
        }

        var pcm = GenerateTone();

        Console.WriteLine($"Source: {Seconds}s {ToneHz} Hz tone, {SampleRate} Hz {Channels}ch\n");

        failures += Check("MP3", () => CheckMp3(pcm));
        failures += Check("Ogg Opus", () => CheckOpus(pcm));
        failures += Check("Ogg Vorbis", () => CheckVorbis(pcm));
        failures += Check("Ogg FLAC", () => CheckFlac(pcm));
        failures += Check("Ogg FLAC — awkward audio", CheckFlacHardCases);

        Console.WriteLine("--- Paste-a-URL parser ---");
        var parserFailures = ParserChecks.Run();
        Console.WriteLine(parserFailures == 0 ? "PASS\n" : $"{parserFailures} case(s) FAILED\n");
        failures += parserFailures;

        Console.WriteLine("--- Inputs and automatic on-air ---");
        var inputFailures = InputChecks.Run();
        Console.WriteLine(inputFailures == 0 ? "PASS\n" : $"{inputFailures} case(s) FAILED\n");
        failures += inputFailures;

        Console.WriteLine("--- Mixer ring buffer ---");
        var ringFailures = RingBufferChecks.Run();
        Console.WriteLine(ringFailures == 0 ? "PASS\n" : $"{ringFailures} case(s) FAILED\n");
        failures += ringFailures;

        Console.WriteLine("--- Listener count parsing ---");
        var listenerFailures = ListenerCountChecks.Run();
        Console.WriteLine(listenerFailures == 0 ? "PASS\n" : $"{listenerFailures} case(s) FAILED\n");
        failures += listenerFailures;

        Console.WriteLine("--- What Deck says when it signs in ---");
        var handshakeFailures = HandshakeChecks.Run();
        Console.WriteLine(handshakeFailures == 0 ? "PASS\n" : $"{handshakeFailures} case(s) FAILED\n");
        failures += handshakeFailures;

        Console.WriteLine("--- Why a broadcast is not going out ---");
        var failureFailures = ConnectionFailureChecks.Run();
        Console.WriteLine(failureFailures == 0 ? "PASS\n" : $"{failureFailures} case(s) FAILED\n");
        failures += failureFailures;

        Console.WriteLine("--- Asking a server for its listener count ---");
        var chainFailures = ListenerChainChecks.Run();
        Console.WriteLine(chainFailures == 0 ? "PASS\n" : $"{chainFailures} case(s) FAILED\n");
        failures += chainFailures;

        Console.WriteLine("--- The icon the product ships with ---");
        var iconFailures = IconChecks.Run();
        Console.WriteLine(iconFailures == 0 ? "PASS\n" : $"{iconFailures} case(s) FAILED\n");
        failures += iconFailures;

        Console.WriteLine("--- Nothing still calls itself SIRS ---");
        var lineageFailures = LineageChecks.Run();
        Console.WriteLine(lineageFailures == 0 ? "PASS\n" : $"{lineageFailures} case(s) FAILED\n");
        failures += lineageFailures;

        Console.WriteLine("--- The settings file ---");
        var settingsFailures = SettingsChecks.Run();
        Console.WriteLine(settingsFailures == 0 ? "PASS\n" : $"{settingsFailures} case(s) FAILED\n");
        failures += settingsFailures;

        Console.WriteLine("--- Who hosts your stream ---");
        var hostFailures = HostQuestionChecks.Run();
        Console.WriteLine(hostFailures == 0 ? "PASS\n" : $"{hostFailures} case(s) FAILED\n");
        failures += hostFailures;

        Console.WriteLine("--- Working out the server type ---");
        var serverTypeFailures = ServerTypeChecks.Run();
        Console.WriteLine(serverTypeFailures == 0 ? "PASS\n" : $"{serverTypeFailures} case(s) FAILED\n");
        failures += serverTypeFailures;

        Console.WriteLine("--- Now playing ---");
        var metadataFailures = MetadataChecks.Run();
        Console.WriteLine(metadataFailures == 0 ? "PASS\n" : $"{metadataFailures} case(s) FAILED\n");
        failures += metadataFailures;

        Console.WriteLine("--- Sound processing ---");
        var processingFailures = ProcessingChecks.Run();
        Console.WriteLine(processingFailures == 0 ? "PASS\n" : $"{processingFailures} case(s) FAILED\n");
        failures += processingFailures;

        Console.WriteLine("--- Loudness (EBU Tech 3341) ---");
        var loudnessFailures = LoudnessChecks.Run();
        Console.WriteLine(loudnessFailures == 0 ? "PASS\n" : $"{loudnessFailures} case(s) FAILED\n");
        failures += loudnessFailures;

        Console.WriteLine("--- Recording ---");
        var recordingFailures = RecordingChecks.Run();
        Console.WriteLine(recordingFailures == 0 ? "PASS\n" : $"{recordingFailures} case(s) FAILED\n");
        failures += recordingFailures;

        Console.WriteLine("--- Streaming to several servers ---");
        var multiTargetFailures = MultiTargetChecks.Run();
        Console.WriteLine(multiTargetFailures == 0 ? "PASS\n" : $"{multiTargetFailures} case(s) FAILED\n");
        failures += multiTargetFailures;

        Console.WriteLine("--- Language and updates ---");
        var languageFailures = LanguageChecks.Run();
        Console.WriteLine(languageFailures == 0 ? "PASS\n" : $"{languageFailures} case(s) FAILED\n");
        failures += languageFailures;

        Console.WriteLine("--- Sharing server settings ---");
        var sharingFailures = ProfileSharingChecks.Run();
        Console.WriteLine(sharingFailures == 0 ? "PASS\n" : $"{sharingFailures} case(s) FAILED\n");
        failures += sharingFailures;

        Console.WriteLine("--- Importing from BUTT ---");
        var buttFailures = ButtImportChecks.Run();
        Console.WriteLine(buttFailures == 0 ? "PASS\n" : $"{buttFailures} case(s) FAILED\n");
        failures += buttFailures;

        Console.WriteLine("--- Spectrum and phase ---");
        var spectrumFailures = SpectrumChecks.Run();
        Console.WriteLine(spectrumFailures == 0 ? "PASS\n" : $"{spectrumFailures} case(s) FAILED\n");
        failures += spectrumFailures;

        Console.WriteLine("--- ASIO input ---");
        var asioFailures = AsioChecks.Run();
        Console.WriteLine(asioFailures == 0 ? "PASS\n" : $"{asioFailures} case(s) FAILED\n");
        failures += asioFailures;

        Console.WriteLine("--- MIDI control ---");
        var midiFailures = MidiChecks.Run();
        Console.WriteLine(midiFailures == 0 ? "PASS\n" : $"{midiFailures} case(s) FAILED\n");
        failures += midiFailures;

        Console.WriteLine("--- Updates ---");
        var updateFailures = UpdateChecks.Run();
        Console.WriteLine(updateFailures == 0 ? "PASS\n" : $"{updateFailures} case(s) FAILED\n");
        failures += updateFailures;

        Console.WriteLine("--- Remote control and command line ---");
        var controlFailures = ControlChecks.Run();
        Console.WriteLine(controlFailures == 0 ? "PASS\n" : $"{controlFailures} case(s) FAILED\n");
        failures += controlFailures;

        Console.WriteLine("--- Sample rate, and what the deck offers for quality ---");
        var rateFailures = SampleRateChecks.Run();
        Console.WriteLine(rateFailures == 0 ? "PASS\n" : $"{rateFailures} case(s) FAILED\n");
        failures += rateFailures;

        Console.WriteLine("--- Where the meter changes colour ---");
        var meterFailures = MeterChecks.Run();
        Console.WriteLine(meterFailures == 0 ? "PASS\n" : $"{meterFailures} case(s) FAILED\n");
        failures += meterFailures;

        Console.WriteLine("--- Palettes and contrast ---");
        var themeFailures = ThemeChecks.Run();
        Console.WriteLine(themeFailures == 0 ? "PASS\n" : $"{themeFailures} case(s) FAILED\n");
        failures += themeFailures;

        Console.WriteLine(failures == 0 ? "\nAll encoder checks passed." : $"\n{failures} check(s) FAILED.");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string name, Func<string> action)
    {
        Console.WriteLine($"--- {name} ---");
        try
        {
            Console.WriteLine(action());
            Console.WriteLine("PASS\n");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.Message}\n");
            return 1;
        }
    }

    private static float[] GenerateTone()
    {
        var frames = (int)(SampleRate * Seconds);
        var buffer = new float[frames * Channels];
        for (var i = 0; i < frames; i++)
        {
            // -6 dBFS so the safety limiter would not be involved in a real pipeline.
            var sample = (float)(Math.Sin(2 * Math.PI * ToneHz * i / SampleRate) * 0.5);
            for (var ch = 0; ch < Channels; ch++) buffer[(i * Channels) + ch] = sample;
        }

        return buffer;
    }

    /// <summary>Feeds the encoder in realistic block sizes rather than one giant call.</summary>
    private static byte[] EncodeInBlocks(IAudioEncoder encoder, float[] pcm)
    {
        var output = new MemoryStream();
        output.Write(encoder.StreamHeader);

        const int blockFrames = 480; // 10 ms, similar to a WASAPI callback
        var blockSamples = blockFrames * Channels;

        for (var offset = 0; offset < pcm.Length; offset += blockSamples)
        {
            var count = Math.Min(blockSamples, pcm.Length - offset);
            output.Write(encoder.Encode(pcm.AsSpan(offset, count)));
        }

        output.Write(encoder.Finish());
        return output.ToArray();
    }

    private static string CheckMp3(float[] pcm)
    {
        var settings = new EncoderSettings
        {
            Codec = StreamCodec.Mp3, BitrateKbps = 128, SampleRate = SampleRate, Channels = Channels,
        };

        using var encoder = new Mp3Encoder(settings);
        var bytes = EncodeInBlocks(encoder, pcm);

        if (bytes.Length == 0) throw new Exception("encoder produced no output");

        var frames = CountMp3Frames(bytes, out var firstFrameOffset);
        if (frames < 50) throw new Exception($"only {frames} MP3 frames found; expected roughly {Seconds * 38:0}");

        // 128 kbps for 3 seconds is ~48 KB. Allow a wide band; we are catching gross errors.
        var expectedBytes = 128 * 1000 / 8 * Seconds;
        var ratio = bytes.Length / expectedBytes;
        if (ratio is < 0.5 or > 2.0)
        {
            throw new Exception($"output size {bytes.Length} bytes is far from the expected {expectedBytes:0} at 128 kbps");
        }

        WriteArtifact("tone.mp3", bytes);
        return $"{bytes.Length:N0} bytes, {frames} frames, first sync at offset {firstFrameOffset}";
    }

    private static int CountMp3Frames(byte[] data, out int firstFrameOffset)
    {
        var count = 0;
        firstFrameOffset = -1;

        for (var i = 0; i < data.Length - 1; i++)
        {
            // Frame sync: eleven set bits.
            if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0) continue;

            if (firstFrameOffset < 0) firstFrameOffset = i;
            count++;
        }

        return count;
    }

    private static string CheckOpus(float[] pcm)
    {
        var settings = new EncoderSettings
        {
            Codec = StreamCodec.OggOpus, BitrateKbps = 128, SampleRate = SampleRate, Channels = Channels,
        };

        using var encoder = new OpusEncoder(settings);
        var bytes = EncodeInBlocks(encoder, pcm);

        if (bytes.Length == 0) throw new Exception("encoder produced no output");

        var pages = OggReader.ReadPages(bytes);
        if (pages.Count < 3) throw new Exception($"expected header pages plus audio, found {pages.Count} pages");

        if (!pages[0].IsBeginningOfStream) throw new Exception("first page is not marked beginning-of-stream");
        if (!pages[^1].IsEndOfStream) throw new Exception("last page is not marked end-of-stream");

        var serials = pages.Select(p => p.SerialNumber).Distinct().Count();
        if (serials != 1) throw new Exception($"pages carry {serials} different serial numbers");

        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].SequenceNumber != i)
            {
                throw new Exception($"page {i} has sequence number {pages[i].SequenceNumber}");
            }
        }

        // Our Opus muxer never splits a packet across pages, unlike Vorbis; hold it to that.
        if (pages.Any(p => p.IsContinuation))
        {
            throw new Exception("a page is marked as continuing a packet; the Opus muxer should never split one");
        }

        var packets = pages.SelectMany(p => p.Packets).ToList();

        var head = packets[0];
        if (head.Length < 19 || !head.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
        {
            throw new Exception("first packet is not an OpusHead");
        }

        var headerChannels = head[9];
        var headerRate = BitConverter.ToInt32(head, 12);
        if (headerChannels != Channels) throw new Exception($"OpusHead says {headerChannels} channels");
        if (headerRate != SampleRate) throw new Exception($"OpusHead says {headerRate} Hz input rate");

        if (!packets[1].AsSpan(0, 8).SequenceEqual("OpusTags"u8))
        {
            throw new Exception("second packet is not an OpusTags");
        }

        // Decode the audio packets back and confirm we recover the tone rather than noise.
        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels, null);
        var decoded = new List<float>();
        var frame = new float[5760 * Channels];

        foreach (var packet in packets.Skip(2))
        {
            var samples = decoder.Decode(packet.AsSpan(), frame.AsSpan(), 5760, false);
            for (var i = 0; i < samples * Channels; i++) decoded.Add(frame[i]);
        }

        var decodedFrames = decoded.Count / Channels;
        var expectedFrames = pcm.Length / Channels;
        if (decodedFrames < expectedFrames * 0.9)
        {
            throw new Exception($"decoded {decodedFrames} frames, expected around {expectedFrames}");
        }

        var (peak, dominantHz) = Analyse(decoded, decodedFrames);
        if (peak < 0.2f) throw new Exception($"decoded peak {peak:0.000} is too quiet - the audio is not surviving the round trip");
        if (Math.Abs(dominantHz - ToneHz) > 15) throw new Exception($"dominant frequency {dominantHz:0} Hz, expected {ToneHz} Hz");

        WriteArtifact("tone.opus", bytes);
        return $"{bytes.Length:N0} bytes, {pages.Count} pages, {packets.Count} packets, " +
               $"decoded {decodedFrames} frames, peak {peak:0.00}, tone {dominantHz:0} Hz";
    }

    private static string CheckVorbis(float[] pcm)
    {
        var settings = new EncoderSettings
        {
            Codec = StreamCodec.OggVorbis, BitrateKbps = 128, SampleRate = SampleRate, Channels = Channels,
        };

        using var encoder = new VorbisEncoder(settings);
        var bytes = EncodeInBlocks(encoder, pcm);

        if (bytes.Length == 0) throw new Exception("encoder produced no output");

        var pages = OggReader.ReadPages(bytes);
        if (pages.Count < 3) throw new Exception($"expected header pages plus audio, found {pages.Count} pages");
        if (!pages[0].IsBeginningOfStream) throw new Exception("first page is not marked beginning-of-stream");
        if (!pages[^1].IsEndOfStream) throw new Exception("last page is not marked end-of-stream");

        var serials = pages.Select(p => p.SerialNumber).Distinct().Count();
        if (serials != 1) throw new Exception($"pages carry {serials} different serial numbers");

        var packets = pages.SelectMany(p => p.Packets).ToList();
        if (packets.Count < 3) throw new Exception($"only {packets.Count} packets; the three headers should be there");

        // The three Vorbis headers are identified by a leading type byte plus the "vorbis" tag.
        CheckVorbisHeader(packets[0], 1, "identification");
        CheckVorbisHeader(packets[1], 3, "comment");
        CheckVorbisHeader(packets[2], 5, "setup");

        var channels = packets[0][11];
        var rate = BitConverter.ToInt32(packets[0], 12);
        if (channels != Channels) throw new Exception($"the header says {channels} channels");
        if (rate != SampleRate) throw new Exception($"the header says {rate} Hz");

        // Not a size check: Vorbis is quality-driven VBR and a pure sine is trivially compressible,
        // so a 128 kbps target legitimately produces a fraction of that. What must hold is that
        // every sample went in - the final granule position counts samples encoded.
        var expectedSamples = (long)(SampleRate * Seconds);
        var finalGranule = pages[^1].GranulePosition;

        if (Math.Abs(finalGranule - expectedSamples) > expectedSamples * 0.05)
        {
            throw new Exception($"the stream ends at sample {finalGranule}, expected about {expectedSamples}");
        }

        if (bytes.Length < 1024) throw new Exception($"only {bytes.Length} bytes of output; that cannot be 3 seconds of audio");

        WriteArtifact("tone.ogg", bytes);
        return $"{bytes.Length:N0} bytes, {pages.Count} pages, {packets.Count} packets, " +
               $"{channels}ch {rate} Hz, ends at sample {finalGranule:N0}";
    }

    /// <summary>
    /// FLAC is lossless, which makes this the strictest check in the suite: the decoded samples
    /// must equal the input exactly, not approximately. Any error in the bit packing, the Rice
    /// coding, the predictors or the stereo decorrelation shows up as a mismatched sample.
    /// </summary>
    private static string CheckFlac(float[] pcm)
    {
        var settings = new EncoderSettings
        {
            Codec = StreamCodec.OggFlac, SampleRate = SampleRate, Channels = Channels,
        };

        using var encoder = new FlacEncoder(settings);
        var bytes = EncodeInBlocks(encoder, pcm);

        if (bytes.Length == 0) throw new Exception("encoder produced no output");

        var pages = OggReader.ReadPages(bytes);
        if (!pages[0].IsBeginningOfStream) throw new Exception("first page is not marked beginning-of-stream");
        if (!pages[^1].IsEndOfStream) throw new Exception("last page is not marked end-of-stream");

        if (pages.Any(p => p.IsContinuation))
        {
            throw new Exception("a page continues a packet; FLAC frames should each fit in one page");
        }

        var packets = pages.SelectMany(p => p.Packets).ToList();
        if (packets.Count < 3) throw new Exception($"only {packets.Count} packets; the two headers and audio should be there");

        // Packet 1: the Ogg-to-FLAC mapping header, carrying STREAMINFO.
        var mapping = packets[0];
        if (mapping[0] != 0x7F || !mapping.AsSpan(1, 4).SequenceEqual("FLAC"u8))
        {
            throw new Exception("the first packet is not an Ogg FLAC mapping header");
        }

        if (mapping[5] != 1) throw new Exception($"mapping version major is {mapping[5]}, expected 1");

        var headerPackets = (mapping[7] << 8) | mapping[8];
        if (headerPackets != 1) throw new Exception($"the mapping declares {headerPackets} further header packets, expected 1");

        if (!mapping.AsSpan(9, 4).SequenceEqual("fLaC"u8)) throw new Exception("the fLaC signature is missing");

        if (mapping[13] != 0x00) throw new Exception("STREAMINFO is not the first metadata block, or is wrongly marked last");

        var streamInfoLength = (mapping[14] << 16) | (mapping[15] << 8) | mapping[16];
        if (streamInfoLength != 34) throw new Exception($"STREAMINFO is {streamInfoLength} bytes, expected 34");

        var info = mapping.AsSpan(17, 34);
        var maxBlockSize = (info[2] << 8) | info[3];
        var infoRate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
        var infoChannels = ((info[12] >> 1) & 0x07) + 1;
        var infoBits = (((info[12] & 0x01) << 4) | (info[13] >> 4)) + 1;

        if (infoRate != SampleRate) throw new Exception($"STREAMINFO says {infoRate} Hz");
        if (infoChannels != Channels) throw new Exception($"STREAMINFO says {infoChannels} channels");
        if (infoBits != 16) throw new Exception($"STREAMINFO says {infoBits} bits per sample");

        // Packet 2: the comment block, marked as the last metadata block.
        if (packets[1][0] != 0x84) throw new Exception($"the second packet has block header 0x{packets[1][0]:X2}, expected 0x84");

        // Everything after that is audio. Decode it and compare against what went in.
        var expected = ToInt16(pcm);
        var decodedCount = 0;
        var maxError = 0;

        foreach (var packet in packets.Skip(2))
        {
            var reader = new FlacReader(packet);
            var block = reader.Decode(16);

            if (reader.SampleRate != SampleRate) throw new Exception($"a frame declares {reader.SampleRate} Hz");
            if (reader.Channels != Channels) throw new Exception($"a frame declares {reader.Channels} channels");
            if (reader.BlockSize > maxBlockSize) throw new Exception($"a frame is {reader.BlockSize} samples, above the declared maximum {maxBlockSize}");

            for (var i = 0; i < reader.BlockSize; i++)
            {
                for (var ch = 0; ch < Channels; ch++)
                {
                    var index = ((decodedCount + i) * Channels) + ch;
                    if (index >= expected.Length) throw new Exception("the stream contains more samples than went in");

                    var error = Math.Abs(block[ch][i] - expected[index]);
                    if (error > maxError) maxError = error;
                }
            }

            decodedCount += reader.BlockSize;
        }

        var expectedFrames = pcm.Length / Channels;
        if (decodedCount != expectedFrames)
        {
            throw new Exception($"decoded {decodedCount} frames, expected exactly {expectedFrames}");
        }

        if (maxError != 0) throw new Exception($"the audio changed: worst sample is off by {maxError}");

        var finalGranule = pages[^1].GranulePosition;
        if (finalGranule != expectedFrames)
        {
            throw new Exception($"the stream ends at sample {finalGranule}, expected {expectedFrames}");
        }

        var raw = pcm.Length * 2;
        var ratio = (double)bytes.Length / raw;
        var kbps = bytes.Length * 8 / Seconds / 1000;

        WriteArtifact("tone.oga", bytes);
        return $"{bytes.Length:N0} bytes, {pages.Count} pages, {packets.Count - 2} frames, " +
               $"{decodedCount:N0} samples decoded bit-for-bit identical, " +
               $"{ratio:P0} of raw ({kbps:0} kbps)";
    }

    /// <summary>
    /// A sine wave only ever exercises the fixed predictors. This runs material that forces the
    /// other paths: digital silence takes the constant subframe, white noise defeats prediction
    /// entirely and should fall back to verbatim, and hard-panned content makes stereo
    /// decorrelation the wrong choice. All of it still has to come back bit for bit.
    /// </summary>
    private static string CheckFlacHardCases()
    {
        var settings = new EncoderSettings
        {
            Codec = StreamCodec.OggFlac, SampleRate = 44100, Channels = 2,
        };

        const int frames = 44100 * 2;
        var pcm = new float[frames * 2];
        var random = new Random(1234);
        var sections = new List<string>();

        for (var i = 0; i < frames; i++)
        {
            float left, right;

            switch (i / (frames / 4))
            {
                case 0: // digital silence
                    left = right = 0f;
                    break;

                case 1: // white noise at a healthy level
                    left = (float)((random.NextDouble() * 2) - 1) * 0.9f;
                    right = (float)((random.NextDouble() * 2) - 1) * 0.9f;
                    break;

                case 2: // hard panned: nothing in common between the channels
                    left = (float)Math.Sin(2 * Math.PI * 220 * i / 44100.0) * 0.8f;
                    right = 0f;
                    break;

                default: // full scale, to catch clipping and sign handling at the extremes
                    left = right = (i % 64) < 32 ? 1f : -1f;
                    break;
            }

            pcm[i * 2] = left;
            pcm[(i * 2) + 1] = right;
        }

        sections.Add("silence");
        sections.Add("noise");
        sections.Add("hard panned");
        sections.Add("full-scale square");

        using var encoder = new FlacEncoder(settings);

        var output = new MemoryStream();
        output.Write(encoder.StreamHeader);

        const int blockSamples = 480 * 2;
        for (var offset = 0; offset < pcm.Length; offset += blockSamples)
        {
            var count = Math.Min(blockSamples, pcm.Length - offset);
            output.Write(encoder.Encode(pcm.AsSpan(offset, count)));
        }

        output.Write(encoder.Finish());
        var bytes = output.ToArray();

        var packets = OggReader.ReadPages(bytes).SelectMany(p => p.Packets).Skip(2).ToList();
        var expected = ToInt16(pcm);
        var decodedCount = 0;

        foreach (var packet in packets)
        {
            var reader = new FlacReader(packet);
            var block = reader.Decode(16);

            for (var i = 0; i < reader.BlockSize; i++)
            {
                for (var ch = 0; ch < 2; ch++)
                {
                    var index = ((decodedCount + i) * 2) + ch;
                    if (block[ch][i] != expected[index])
                    {
                        throw new Exception(
                            $"sample {index} came back as {block[ch][i]}, expected {expected[index]} " +
                            $"(in the {sections[Math.Min(3, index / 2 / (frames / 4))]} section)");
                    }
                }
            }

            decodedCount += reader.BlockSize;
        }

        if (decodedCount != frames) throw new Exception($"decoded {decodedCount} frames, expected {frames}");

        var ratio = (double)bytes.Length / (pcm.Length * 2);
        return $"{string.Join(", ", sections)} — {frames:N0} frames, all identical, {ratio:P0} of raw";
    }

    /// <summary>The same float-to-16-bit conversion the encoder does, for an exact comparison.</summary>
    private static int[] ToInt16(float[] pcm)
    {
        var result = new int[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            var clamped = pcm[i] > 1f ? 1f : pcm[i] < -1f ? -1f : pcm[i];
            result[i] = (int)(clamped * 32767f);
        }

        return result;
    }

    private static void CheckVorbisHeader(byte[] packet, byte expectedType, string name)
    {
        if (packet.Length < 7) throw new Exception($"the {name} header is too short");
        if (packet[0] != expectedType) throw new Exception($"the {name} header has type {packet[0]}, expected {expectedType}");

        if (!packet.AsSpan(1, 6).SequenceEqual("vorbis"u8))
        {
            throw new Exception($"the {name} header is not tagged \"vorbis\"");
        }
    }

    /// <summary>Peak level plus a zero-crossing frequency estimate on the left channel.</summary>
    private static (float Peak, double DominantHz) Analyse(List<float> interleaved, int frames)
    {
        var peak = 0f;
        var crossings = 0;
        var previous = 0f;

        // Skip the first 100 ms: Opus needs a moment to converge and the pre-skip is still in there.
        var start = SampleRate / 10;

        for (var i = start; i < frames; i++)
        {
            var sample = interleaved[i * Channels];
            var magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;

            if (previous <= 0f && sample > 0f) crossings++;
            previous = sample;
        }

        var seconds = (frames - start) / (double)SampleRate;
        return (peak, seconds > 0 ? crossings / seconds : 0);
    }

    private static void WriteArtifact(string name, byte[] bytes)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        Console.WriteLine($"wrote {path}");
    }
}
