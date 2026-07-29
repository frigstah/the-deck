using System.Diagnostics;
using Deck.Core.Audio.Dsp;

namespace Deck.Core.Audio;

/// <summary>Receives a block of audio already converted to the stream format.</summary>
public delegate void AudioBlockHandler(ReadOnlySpan<float> interleaved, AudioFormat format);

/// <summary>
/// The input half of the pipeline: one or two <see cref="AudioSource"/>s mixed together, then the
/// mix meter, dead-air watch and the always-on safety limiter.
/// <para>
/// The primary source owns the clock. Its callback is what drives a block through the mix, and the
/// secondary is read from a ring buffer to match. Two WASAPI devices run off separate clocks that
/// drift apart over minutes, so the read is nudged by a single frame at a time to hold the buffer
/// near its target - small enough to be inaudible, unlike dropping a whole block.
/// </para>
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    /// <summary>How much secondary audio to hold. Also the drift correction target.</summary>
    private const double TargetBufferSeconds = 0.08;

    private readonly FloatRingBuffer _secondaryBuffer;
    private SafetyLimiter? _limiter;
    private LoudnessMeter? _loudness;
    private ToneControl? _tone;
    private MultibandCompressor? _processor;
    private SpectrumAnalyser? _spectrum;
    private CorrelationMeter? _correlation;

    private float _lowGainDb;
    private float _midGainDb;
    private float _highGainDb;
    private ProcessingPreset _preset = ProcessingPreset.Off;

    private float[] _mixBuffer = new float[16384];
    private float[] _secondaryScratch = new float[16384];

    private AudioFormat _streamFormat = AudioFormat.CdStereo;
    private long _lastBlockTicks;
    private int _targetBufferSamples;

    public CaptureEngine()
    {
        // A second of headroom: far more than the target, so a brief stall does not lose audio.
        _secondaryBuffer = new FloatRingBuffer(48000 * 2);

        Primary = new AudioSource("the microphone");
        Secondary = new AudioSource("the computer sound");

        Primary.BlockReady += OnPrimaryBlock;
        Secondary.BlockReady += OnSecondaryBlock;

        Primary.Failed += (_, e) => CaptureFailed?.Invoke(this, e);
        Secondary.Failed += (_, e) => CaptureFailed?.Invoke(this, e);
    }

    /// <summary>The source that drives the clock. With one input, this is the only one running.</summary>
    public AudioSource Primary { get; }

    /// <summary>Optional second input, mixed on top of the primary (A5).</summary>
    public AudioSource Secondary { get; }

    /// <summary>Meter on the mix - what listeners actually hear.</summary>
    public LevelMeter InputMeter { get; } = new();

    /// <summary>
    /// Loudness of the finished mix in LUFS (B8), measured after the limiter so it reflects what
    /// actually leaves the machine rather than what went in.
    /// </summary>
    public LoudnessMeter? Loudness => _loudness;

    /// <summary>Frequency content of the mix (B9), or null while nothing is running.</summary>
    public SpectrumAnalyser? Spectrum => _spectrum;

    /// <summary>How well the two channels agree, and what mono listeners will hear (B9).</summary>
    public CorrelationMeter? Correlation => _correlation;

    public SilenceDetector Silence { get; } = new();

    public bool IsRunning => Primary.IsRunning;

    public bool IsMixing => Secondary.IsRunning;

    public AudioFormat DeviceFormat => Primary.DeviceFormat;

    public AudioFormat StreamFormat => _streamFormat;

    /// <summary>Input trim for the primary source, kept for callers that only use one input.</summary>
    public float InputGainDb
    {
        get => Primary.GainDb;
        set => Primary.GainDb = value;
    }

    public bool VoiceEnhanceEnabled
    {
        get => Primary.VoiceEnhanceEnabled;
        set => Primary.VoiceEnhanceEnabled = value;
    }

    public float LimiterReductionDb => _limiter?.GainReductionDb ?? 0f;

    // ---------------------------------------------------------------- programme processing (E4, E5)

    /// <summary>Bass, middle and treble on the mix (E5), in dB. Safe to change while on air.</summary>
    public float ToneLowDb
    {
        get => _lowGainDb;
        set { _lowGainDb = value; if (_tone is not null) _tone.LowGainDb = value; }
    }

    public float ToneMidDb
    {
        get => _midGainDb;
        set { _midGainDb = value; if (_tone is not null) _tone.MidGainDb = value; }
    }

    public float ToneHighDb
    {
        get => _highGainDb;
        set { _highGainDb = value; if (_tone is not null) _tone.HighGainDb = value; }
    }

    /// <summary>The three-band compressor preset (E4).</summary>
    public ProcessingPreset ProcessingPreset
    {
        get => _preset;
        set { _preset = value; if (_processor is not null) _processor.Preset = value; }
    }

    public float ProcessorReductionDb => _processor?.GainReductionDb ?? 0f;

    /// <summary>Secondary audio discarded because the mix could not keep up. Should stay at zero.</summary>
    public long SecondaryDroppedSamples => _secondaryBuffer.DroppedSamples;

    public event AudioBlockHandler? BlockCaptured;

    /// <summary>Raised when a source stops unexpectedly - unplugged interface, driver reset, etc.</summary>
    public event EventHandler<CaptureFailedEventArgs>? CaptureFailed;

    public void Start(string? deviceId, AudioDeviceKind kind, AudioFormat streamFormat)
    {
        Stop();

        _streamFormat = streamFormat;
        _limiter = new SafetyLimiter(streamFormat.SampleRate, streamFormat.Channels);
        _loudness = new LoudnessMeter(streamFormat.SampleRate, streamFormat.Channels);

        // Rebuilt at the new rate, then handed back the settings the user already had, so restarting
        // the input never quietly undoes their processing choices.
        _tone = new ToneControl(streamFormat.SampleRate, streamFormat.Channels)
        {
            LowGainDb = _lowGainDb,
            MidGainDb = _midGainDb,
            HighGainDb = _highGainDb,
        };

        _processor = new MultibandCompressor(streamFormat.SampleRate, streamFormat.Channels)
        {
            Preset = _preset,
        };

        _spectrum = new SpectrumAnalyser(streamFormat.SampleRate, streamFormat.Channels);
        _correlation = new CorrelationMeter(streamFormat.SampleRate);
        _targetBufferSamples = (int)(streamFormat.SampleRate * TargetBufferSeconds) * streamFormat.Channels;

        InputMeter.Reset();
        Silence.Reset();
        _secondaryBuffer.Clear();
        _lastBlockTicks = Stopwatch.GetTimestamp();

        Primary.Start(deviceId, kind, streamFormat);
    }

    /// <summary>Adds a second input to the mix. The stream format is already fixed by the primary.</summary>
    public void StartSecondary(string? deviceId, AudioDeviceKind kind)
    {
        if (!Primary.IsRunning)
        {
            throw new InvalidOperationException("Start the main input before adding a second one.");
        }

        _secondaryBuffer.Clear();
        Secondary.Start(deviceId, kind, _streamFormat);
    }

    public void StopSecondary()
    {
        Secondary.Stop();
        _secondaryBuffer.Clear();
    }

    public void Stop()
    {
        Secondary.Stop();
        Primary.Stop();
        _secondaryBuffer.Clear();
        _limiter = null;
        _loudness = null;
        _tone = null;
        _processor = null;
        _spectrum = null;
        _correlation = null;
    }

    private void OnSecondaryBlock(ReadOnlySpan<float> interleaved, AudioFormat format) =>
        _secondaryBuffer.Write(interleaved);

    private void OnPrimaryBlock(ReadOnlySpan<float> interleaved, AudioFormat format)
    {
        var length = interleaved.Length;
        EnsureCapacity(ref _mixBuffer, length);

        var mix = _mixBuffer.AsSpan(0, length);
        interleaved.CopyTo(mix);

        if (Secondary.IsRunning) MixInSecondary(mix);

        InputMeter.Process(mix, format.Channels);

        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastBlockTicks) / (double)Stopwatch.Frequency;
        _lastBlockTicks = now;
        Silence.Update(InputMeter.WindowPeakDb, elapsed);

        // The classic broadcast order: shape the tone, then even out the dynamics, then catch
        // whatever is still over. Doing it the other way round would have the compressor react to
        // frequencies the EQ is about to remove.
        _tone?.Process(mix);
        _processor?.Process(mix);

        // Last stage before anything downstream sees the audio: summing two sources can exceed full
        // scale even when neither source does on its own.
        _limiter?.Process(mix);

        // After the limiter: the loudness figure should describe what goes out, not what came in.
        // The same argument puts the spectrum and the phase reading here - the EQ and the compressor
        // both change the answer, and showing the input would be showing the wrong thing (B9).
        _loudness?.Process(mix);
        _spectrum?.Process(mix);
        _correlation?.Process(mix, format.Channels);

        BlockCaptured?.Invoke(mix, format);
    }

    private void MixInSecondary(Span<float> mix)
    {
        CorrectDrift(mix.Length);

        EnsureCapacity(ref _secondaryScratch, mix.Length);
        var secondary = _secondaryScratch.AsSpan(0, mix.Length);
        _secondaryBuffer.Read(secondary);

        for (var i = 0; i < mix.Length; i++) mix[i] += secondary[i];
    }

    /// <summary>
    /// Keeps the secondary buffer near its target depth. Normal drift is corrected one frame at a
    /// time; a large excess means something stalled badly enough that a clean jump beats minutes of
    /// creeping delay.
    /// </summary>
    private void CorrectDrift(int blockSamples)
    {
        var channels = _streamFormat.Channels;
        var excess = _secondaryBuffer.Count - (_targetBufferSamples + blockSamples);
        if (excess <= 0) return;

        if (excess > _targetBufferSamples * 4)
        {
            _secondaryBuffer.Skip(excess - _targetBufferSamples);
            return;
        }

        if (excess >= channels) _secondaryBuffer.Skip(channels);
    }

    private static void EnsureCapacity(ref float[] buffer, int required)
    {
        if (buffer.Length < required) buffer = new float[required * 2];
    }

    public void Dispose()
    {
        Stop();
        Primary.Dispose();
        Secondary.Dispose();
    }
}

public sealed class CaptureFailedEventArgs(string message, Exception? exception) : EventArgs
{
    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}

public sealed class AudioDeviceUnavailableException(string message) : Exception(message);
