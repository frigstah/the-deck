using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sirs.Core.Audio.Dsp;

namespace Sirs.Core.Audio;

/// <summary>
/// One capture device, delivering audio already converted to the stream format: WASAPI capture,
/// gain, metering, optional Voice Enhance, channel mapping and resampling.
/// <para>
/// Voice Enhance lives here rather than on the mix bus on purpose. It is speech processing; running
/// it across a mic mixed with music would compress the music every time the presenter talks.
/// </para>
/// </summary>
public sealed class AudioSource : IDisposable
{
    private readonly object _lifecycleLock = new();

    private IWaveIn? _capture;
    private MMDevice? _device;
    private Resampler? _resampler;
    private VoiceEnhance? _voiceEnhance;
    private AutoGain? _autoGain;

    private float[] _floatBuffer = new float[16384];
    private float[] _mappedBuffer = new float[16384];
    private float[] _outputBuffer = new float[16384];

    private AudioFormat _deviceFormat;
    private AudioFormat _streamFormat = AudioFormat.CdStereo;
    private int _deviceBitsPerSample;
    private bool _deviceIsFloat;
    private float _gainLinear = 1f;
    private float _gainDb;

    public AudioSource(string name) => Name = name;

    /// <summary>Which fader this is, for logs and messages: "Microphone" or "Computer sound".</summary>
    public string Name { get; }

    public LevelMeter Meter { get; } = new();

    public bool IsRunning { get; private set; }

    public string? DeviceId { get; private set; }

    public AudioDeviceKind Kind { get; private set; } = AudioDeviceKind.Input;

    public AudioFormat DeviceFormat => _deviceFormat;

    /// <summary>Fader position in dB, applied before metering so the meter shows what the user set.</summary>
    public float GainDb
    {
        get => _gainDb;
        set
        {
            _gainDb = value;
            _gainLinear = AudioMath.FromDb(value);
        }
    }

    public bool Muted { get; set; }

    public bool VoiceEnhanceEnabled { get; set; }

    /// <summary>Automatic gain control (E3). Off by default; rides the level towards a target.</summary>
    public bool AutoGainEnabled { get; set; }

    /// <summary>Which of the device's inputs feed the stream (A7).</summary>
    public ChannelSelection Channels { get; set; } = ChannelSelection.Default;

    /// <summary>The choices to offer for the device currently open.</summary>
    public IReadOnlyList<(ChannelSelection Selection, string Label)> ChannelOptions =>
        ChannelSelection.For(Math.Max(1, _deviceFormat.Channels));

    /// <summary>Gain the AGC is currently applying, for display.</summary>
    public float AutoGainDb => _autoGain?.GainDb ?? 0f;

    /// <summary>Audio at the stream format, after this source's gain and processing.</summary>
    public event AudioBlockHandler? BlockReady;

    public event EventHandler<CaptureFailedEventArgs>? Failed;

    public void Start(string? deviceId, AudioDeviceKind kind, AudioFormat streamFormat)
    {
        lock (_lifecycleLock)
        {
            StopInternal();

            DeviceId = deviceId;
            Kind = kind;
            _streamFormat = streamFormat;

            IWaveIn capture;

            if (kind == AudioDeviceKind.Asio)
            {
                // ASIO has no MMDevice behind it - the driver is the device - so there is nothing to
                // resolve and nothing to dispose alongside it (A8).
                capture = new AsioCapture(
                    AsioCapture.DriverNameFrom(deviceId ?? string.Empty), streamFormat.SampleRate);
            }
            else
            {
                var lookupKind = kind == AudioDeviceKind.Loopback ? AudioDeviceKind.Output : kind;
                var device = AudioDevices.ResolveOrDefault(deviceId, lookupKind)
                    ?? throw new AudioDeviceUnavailableException(
                        $"SIRS could not find the device for {Name}. Choose a different one, or check it is plugged in and enabled in Windows.");

                _device = device;

                capture = kind == AudioDeviceKind.Loopback
                    ? new WasapiLoopbackCapture(device)
                    : new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
            }

            Meter.Reset();

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;

            // Started before the format is read, not after. WASAPI knows its format up front, but an
            // ASIO driver only settles on one when it opens - and building the resampler from a rate
            // the driver then refused would put the whole show out of pitch. Any block that arrives
            // during the next few lines is dropped by the guard in OnDataAvailable.
            capture.StartRecording();

            var waveFormat = capture.WaveFormat;
            _deviceFormat = new AudioFormat(waveFormat.SampleRate, waveFormat.Channels);
            _deviceBitsPerSample = waveFormat.BitsPerSample;
            _deviceIsFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                || (waveFormat.Encoding == WaveFormatEncoding.Extensible && waveFormat.BitsPerSample == 32);

            _voiceEnhance = new VoiceEnhance(streamFormat.SampleRate, streamFormat.Channels);
            _autoGain = new AutoGain(streamFormat.SampleRate, streamFormat.Channels);

            // Assigned last: it is what OnDataAvailable waits for.
            _resampler = new Resampler(_deviceFormat.SampleRate, streamFormat.SampleRate, streamFormat.Channels);

            IsRunning = true;
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        IsRunning = false;

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                _capture.StopRecording();
            }
            catch (Exception)
            {
                // Device may already be gone.
            }

            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        _resampler = null;
        _voiceEnhance = null;
        _autoGain = null;

        Meter.Reset();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        // A block that arrived while Start was still setting up. A few milliseconds of audio at the
        // very beginning is not worth the alternative, which is reading a half-built chain.
        var resampler = _resampler;
        if (resampler is null) return;

        var deviceChannels = _deviceFormat.Channels;
        var streamChannels = _streamFormat.Channels;

        var sampleCount = ConvertToFloat(e.Buffer, e.BytesRecorded);
        if (sampleCount == 0) return;

        var samples = _floatBuffer.AsSpan(0, sampleCount);

        var gain = Muted ? 0f : _gainLinear;
        if (gain != 1f)
        {
            for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
        }

        var frames = sampleCount / deviceChannels;
        EnsureCapacity(ref _mappedBuffer, frames * streamChannels);
        var mappedLength = ChannelMapper.Map(samples, deviceChannels, _mappedBuffer, streamChannels, Channels);

        // Metered after the channel selection, not before: choosing input 3 and then watching
        // input 1's level would be worse than having no meter at all.
        Meter.Process(_mappedBuffer.AsSpan(0, mappedLength), streamChannels);

        var resampled = resampler.Process(_mappedBuffer.AsSpan(0, mappedLength));
        if (resampled.IsEmpty) return;

        // The resampler hands back its own scratch buffer and the DSP mutates in place, so copy
        // into a buffer this source owns.
        EnsureCapacity(ref _outputBuffer, resampled.Length);
        var output = _outputBuffer.AsSpan(0, resampled.Length);
        resampled.CopyTo(output);

        if (VoiceEnhanceEnabled) _voiceEnhance!.Process(output);

        // After Voice Enhance: the compressor has already evened out the peaks, so the AGC is
        // riding a steadier signal and does not fight it.
        if (AutoGainEnabled) _autoGain!.Process(output);

        BlockReady?.Invoke(output, _streamFormat);
    }

    private int ConvertToFloat(byte[] buffer, int bytesRecorded)
    {
        if (_deviceIsFloat)
        {
            var count = bytesRecorded / 4;
            EnsureCapacity(ref _floatBuffer, count);
            Buffer.BlockCopy(buffer, 0, _floatBuffer, 0, count * 4);
            return count;
        }

        switch (_deviceBitsPerSample)
        {
            case 16:
            {
                var count = bytesRecorded / 2;
                EnsureCapacity(ref _floatBuffer, count);
                for (var i = 0; i < count; i++) _floatBuffer[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
                return count;
            }

            case 24:
            {
                var count = bytesRecorded / 3;
                EnsureCapacity(ref _floatBuffer, count);
                for (var i = 0; i < count; i++)
                {
                    var offset = i * 3;
                    var value = (buffer[offset] << 8) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 24);
                    _floatBuffer[i] = (value >> 8) / 8388608f;
                }

                return count;
            }

            case 32:
            {
                var count = bytesRecorded / 4;
                EnsureCapacity(ref _floatBuffer, count);
                for (var i = 0; i < count; i++) _floatBuffer[i] = BitConverter.ToInt32(buffer, i * 4) / 2147483648f;
                return count;
            }

            default:
                return 0;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (!IsRunning) return;

        IsRunning = false;
        Failed?.Invoke(this, new CaptureFailedEventArgs(
            e.Exception is null
                ? $"{Name} stopped unexpectedly."
                : $"SIRS lost {Name}. It may have been unplugged or taken over by another program.",
            e.Exception));
    }

    private static void EnsureCapacity(ref float[] buffer, int required)
    {
        if (buffer.Length < required) buffer = new float[required * 2];
    }

    public void Dispose() => Stop();
}
