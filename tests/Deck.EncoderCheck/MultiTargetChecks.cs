using Deck.Core.Audio;
using Deck.Core.Codecs;
using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.EncoderCheck;

/// <summary>
/// Checks for streaming to several servers at once (C12). The risky part is not the fan-out, which
/// is a loop; it is that each destination gets audio converted into the format its own server was
/// set up for. A silent failure here means a server is told "44.1 kHz mono" and sent something
/// else, which most players will happily play back at the wrong speed.
/// </summary>
internal static class MultiTargetChecks
{
    public static int Run()
    {
        var failures = 0;

        failures += Check("capture format takes the highest rate anyone wants", () =>
        {
            var format = BroadcastSet.CaptureFormatFor(
            [
                Profile(StreamCodec.Mp3, 44100, 2),
                Profile(StreamCodec.OggOpus, 48000, 2),
                Profile(StreamCodec.Mp3, 22050, 1),
            ]);

            Expect(format.SampleRate == 48000, $"rate was {format.SampleRate}, expected 48000");
            Expect(format.Channels == 2, $"channels was {format.Channels}, expected 2");
        });

        failures += Check("a mono destination still gets stereo captured", () =>
        {
            // Everyone wants mono, so capture has no reason to be stereo.
            var format = BroadcastSet.CaptureFormatFor([Profile(StreamCodec.Mp3, 44100, 1)]);
            Expect(format.Channels == 1, $"channels was {format.Channels}, expected 1");

            // Add one stereo destination and capture must widen for it.
            var widened = BroadcastSet.CaptureFormatFor(
                [Profile(StreamCodec.Mp3, 44100, 1), Profile(StreamCodec.Mp3, 44100, 2)]);
            Expect(widened.Channels == 2, $"channels was {widened.Channels}, expected 2");
        });

        failures += Check("no destinations falls back to the default quality", () =>
        {
            var format = BroadcastSet.CaptureFormatFor([]);
            Expect(format == QualityPreset.Default.Settings.Format, $"got {format}");
        });

        failures += Check("48 kHz stereo converts to 44.1 kHz mono", () =>
        {
            var source = new AudioFormat(48000, 2);
            var target = new AudioFormat(44100, 1);
            var converter = new FormatConverter(source, target);

            Expect(!converter.IsPassthrough, "the converter thinks it has nothing to do");

            var pcm = Tone(source, seconds: 2.0, hz: 440, amplitude: 0.5f);
            var output = new List<float>();

            // Fed in 10 ms blocks, the way a capture callback would.
            var blockSamples = 480 * source.Channels;
            for (var offset = 0; offset < pcm.Length; offset += blockSamples)
            {
                var count = Math.Min(blockSamples, pcm.Length - offset);
                foreach (var sample in converter.Process(pcm.AsSpan(offset, count))) output.Add(sample);
            }

            var expected = 44100 * 2;
            Expect(Math.Abs(output.Count - expected) < expected * 0.01,
                $"produced {output.Count} mono samples, expected about {expected}");

            var (peak, hz) = Analyse(output, target.SampleRate);
            Expect(Math.Abs(hz - 440) < 5, $"the tone came out at {hz:0} Hz, expected 440");
            Expect(peak is > 0.4f and < 0.6f, $"peak {peak:0.000} - the level moved through the conversion");
        });

        failures += Check("matching formats pass straight through", () =>
        {
            var format = new AudioFormat(48000, 2);
            var converter = new FormatConverter(format, format);
            Expect(converter.IsPassthrough, "a converter between identical formats should do nothing");

            var block = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
            var output = converter.Process(block);

            Expect(output.Length == block.Length, $"length changed to {output.Length}");
            for (var i = 0; i < block.Length; i++)
            {
                Expect(output[i] == block[i], $"sample {i} changed from {block[i]} to {output[i]}");
            }
        });

        failures += Check("a converted destination encodes at the format it advertises", () =>
        {
            // The end-to-end case: capture at 48 kHz stereo, one destination set up for 44.1 kHz
            // mono MP3. The frame headers it produces must say 44.1 kHz mono, or the server will
            // announce one thing and serve another.
            var capture = new AudioFormat(48000, 2);
            var settings = new EncoderSettings
            {
                Codec = StreamCodec.Mp3, BitrateKbps = 96, SampleRate = 44100, Channels = 1,
            };

            var converter = new FormatConverter(capture, settings.Format);
            using var encoder = new Mp3Encoder(settings);

            var pcm = Tone(capture, seconds: 1.0, hz: 440, amplitude: 0.5f);
            var bytes = new List<byte>();

            var blockSamples = 480 * capture.Channels;
            for (var offset = 0; offset < pcm.Length; offset += blockSamples)
            {
                var count = Math.Min(blockSamples, pcm.Length - offset);
                var converted = converter.Process(pcm.AsSpan(offset, count));
                foreach (var b in encoder.Encode(converted)) bytes.Add(b);
            }

            foreach (var b in encoder.Finish()) bytes.Add(b);

            var (rate, channels, frames) = FirstMp3Frame([.. bytes]);
            Expect(frames > 20, $"only {frames} MP3 frames; that is not a second of audio");
            Expect(rate == 44100, $"the MP3 frames declare {rate} Hz, not the 44100 the server was told");
            Expect(channels == 1, $"the MP3 frames declare {channels} channel(s), not mono");
        });

        return failures;
    }

    private static ServerProfile Profile(StreamCodec codec, int sampleRate, int channels) => new()
    {
        Encoder = new EncoderSettings
        {
            Codec = codec, SampleRate = sampleRate, Channels = channels, BitrateKbps = 128,
        },
    };

    private static float[] Tone(AudioFormat format, double seconds, double hz, float amplitude)
    {
        var frames = (int)(format.SampleRate * seconds);
        var buffer = new float[frames * format.Channels];

        for (var i = 0; i < frames; i++)
        {
            var sample = (float)(Math.Sin(2 * Math.PI * hz * i / format.SampleRate) * amplitude);
            for (var ch = 0; ch < format.Channels; ch++) buffer[(i * format.Channels) + ch] = sample;
        }

        return buffer;
    }

    /// <summary>Peak plus a zero-crossing frequency estimate, on a mono buffer.</summary>
    private static (float Peak, double Hz) Analyse(List<float> mono, int sampleRate)
    {
        var peak = 0f;
        var crossings = 0;
        var previous = 0f;

        // Skip the first 50 ms: the resampler's filter is still filling.
        var start = sampleRate / 20;

        for (var i = start; i < mono.Count; i++)
        {
            var sample = mono[i];
            if (Math.Abs(sample) > peak) peak = Math.Abs(sample);
            if (previous <= 0f && sample > 0f) crossings++;
            previous = sample;
        }

        var seconds = (mono.Count - start) / (double)sampleRate;
        return (peak, seconds > 0 ? crossings / seconds : 0);
    }

    /// <summary>
    /// Reads the sample rate and channel mode out of the first MP3 frame header, and counts frames.
    /// </summary>
    private static (int SampleRate, int Channels, int Frames) FirstMp3Frame(byte[] data)
    {
        int[] mpeg1Rates = [44100, 48000, 32000];
        int[] mpeg2Rates = [22050, 24000, 16000];

        var rate = 0;
        var channels = 0;
        var frames = 0;

        for (var i = 0; i < data.Length - 3; i++)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0) continue;

            frames++;
            if (rate != 0) continue;

            var version = (data[i + 1] >> 3) & 0x03; // 3 = MPEG-1, 2 = MPEG-2
            var rateIndex = (data[i + 2] >> 2) & 0x03;
            if (rateIndex == 3) continue;

            rate = version == 3 ? mpeg1Rates[rateIndex] : mpeg2Rates[rateIndex];
            channels = ((data[i + 3] >> 6) & 0x03) == 3 ? 1 : 2; // mode 3 is single channel
        }

        return (rate, channels, frames);
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
