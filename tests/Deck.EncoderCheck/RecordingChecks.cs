using Deck.Core.Audio;
using Deck.Core.Codecs;
using Deck.Core.Recording;

namespace Deck.EncoderCheck;

/// <summary>
/// Checks the recorder writes real files, and in particular that it records what came in rather
/// than what went out. Those stopped being the same thing once a broadcast could go to two servers
/// at different rates, and a recording that silently declared the wrong sample rate would play back
/// at the wrong speed with nothing on screen to explain it.
/// </summary>
internal static class RecordingChecks
{
    public static int Run()
    {
        var failures = 0;
        var folder = Path.Combine(Path.GetTempPath(), "sirs-recording-check-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            failures += Check("lossless recording follows the capture, not the stream", () =>
            {
                // The awkward case: capture at 48 kHz stereo while the show goes out as 44.1 kHz
                // mono MP3. A lossless recording has to keep the 48 kHz stereo.
                var capture = new AudioFormat(48000, 2);
                var stream = new EncoderSettings
                {
                    Codec = StreamCodec.Mp3, SampleRate = 44100, Channels = 1, BitrateKbps = 64,
                };

                var settings = new RecordingSettings
                {
                    Folder = folder,
                    FilenameTemplate = "lossless",
                    Format = RecordingFormat.Lossless,
                };

                var chosen = settings.EncoderFor(stream, capture);
                Expect(chosen.Codec == StreamCodec.OggFlac, $"recording codec is {chosen.Codec}");
                Expect(chosen.SampleRate == 48000, $"recording rate is {chosen.SampleRate}, expected 48000");
                Expect(chosen.Channels == 2, $"recording is {chosen.Channels} channel(s), expected 2");

                var path = RecordOneSecond(settings, stream, capture);
                var bytes = File.ReadAllBytes(path);

                Expect(Path.GetExtension(path) == ".oga", $"the file is named {Path.GetFileName(path)}");

                var packets = OggReader.ReadPages(bytes).SelectMany(p => p.Packets).ToList();
                Expect(packets[0][0] == 0x7F, "the file does not start with an Ogg FLAC mapping header");

                var frames = 0;
                foreach (var packet in packets.Skip(2))
                {
                    var reader = new FlacReader(packet);
                    reader.Decode(16);

                    Expect(reader.SampleRate == 48000, $"a frame declares {reader.SampleRate} Hz");
                    Expect(reader.Channels == 2, $"a frame declares {reader.Channels} channels");
                    frames += reader.BlockSize;
                }

                Expect(frames == 48000, $"the recording holds {frames} frames, expected 48000");
            });

            failures += Check("recording the stream's own format still works", () =>
            {
                var capture = new AudioFormat(44100, 2);
                var stream = new EncoderSettings
                {
                    Codec = StreamCodec.Mp3, SampleRate = 44100, Channels = 2, BitrateKbps = 128,
                };

                var settings = new RecordingSettings
                {
                    Folder = folder,
                    FilenameTemplate = "same-as-stream",
                    Format = RecordingFormat.SameAsStream,
                };

                var path = RecordOneSecond(settings, stream, capture);
                Expect(Path.GetExtension(path) == ".mp3", $"the file is named {Path.GetFileName(path)}");
                Expect(new FileInfo(path).Length > 8000, "the MP3 is too small to be a second of audio");
            });

            failures += Check("a WAV recording keeps the captured rate", () =>
            {
                var capture = new AudioFormat(48000, 2);
                var stream = new EncoderSettings
                {
                    Codec = StreamCodec.Mp3, SampleRate = 44100, Channels = 1, BitrateKbps = 64,
                };

                var settings = new RecordingSettings
                {
                    Folder = folder,
                    FilenameTemplate = "wav",
                    Format = RecordingFormat.Wav,
                };

                var path = RecordOneSecond(settings, stream, capture);
                var bytes = File.ReadAllBytes(path);

                Expect(bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8), "the file is not a RIFF");

                var channels = BitConverter.ToInt16(bytes, 22);
                var rate = BitConverter.ToInt32(bytes, 24);
                var dataBytes = BitConverter.ToInt32(bytes, 40);

                Expect(channels == 2, $"the WAV header says {channels} channels");
                Expect(rate == 48000, $"the WAV header says {rate} Hz");
                Expect(dataBytes == 48000 * 2 * 2, $"the WAV holds {dataBytes} bytes of audio, expected {48000 * 2 * 2}");
            });

            return failures;
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not worth failing the run over.
            }
        }
    }

    /// <summary>Feeds a second of tone through a real Recorder and returns the file it wrote.</summary>
    private static string RecordOneSecond(RecordingSettings settings, EncoderSettings stream, AudioFormat capture)
    {
        using var recorder = new Recorder();
        recorder.Start(settings, stream, capture, "Check", string.Empty);

        var blockFrames = capture.SampleRate / 100; // 10 ms
        var block = new float[blockFrames * capture.Channels];

        for (var b = 0; b < 100; b++)
        {
            for (var i = 0; i < blockFrames; i++)
            {
                var index = (b * blockFrames) + i;
                var sample = (float)(Math.Sin(2 * Math.PI * 440 * index / capture.SampleRate) * 0.5);
                for (var ch = 0; ch < capture.Channels; ch++) block[(i * capture.Channels) + ch] = sample;
            }

            recorder.Write(block);
        }

        return recorder.Stop() ?? throw new Exception("the recorder did not report a file");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  ok   {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }
}
