using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Deck.Core.Audio;

public enum SoundCheckState
{
    Idle,
    Recording,
    Ready,
    Playing,
}

/// <summary>Verdict on a finished sound check, in the same language as the live meter.</summary>
public sealed record SoundCheckSummary(
    float PeakDb,
    float AverageDb,
    LevelAdvice Advice,
    double Seconds)
{
    public string Headline => Advice.Headline();

    public string Detail => Advice switch
    {
        LevelAdvice.NoSignal =>
            "Deck did not hear anything at all. Check you picked the right input and that it is not muted in Windows.",
        LevelAdvice.Good =>
            $"Your loudest moment reached {PeakDb:0.0} dB, which is where it should be. Have a listen and check it sounds like you.",
        LevelAdvice.TooQuiet =>
            $"Your loudest moment only reached {PeakDb:0.0} dB. Raise the input level and try again.",
        _ =>
            $"Your loudest moment reached {PeakDb:0.0} dB, which is too close to the limit. Lower the input level and try again.",
    };
}

/// <summary>
/// Record a few seconds, then play it straight back (B3). No meter answers "do I actually sound
/// right?" - hearing yourself does, and neither BUTT nor Rocket Broadcaster offers it.
/// </summary>
public sealed class SoundCheck : IDisposable
{
    private readonly object _lock = new();
    private readonly List<float> _samples = [];

    private AudioFormat _format = AudioFormat.CdStereo;
    private int _maxSamples;
    private WasapiOut? _output;

    public SoundCheckState State { get; private set; } = SoundCheckState.Idle;

    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(10);

    public double RecordedSeconds
    {
        get
        {
            lock (_lock)
            {
                return _format.Channels == 0 ? 0 : (double)_samples.Count / _format.Channels / _format.SampleRate;
            }
        }
    }

    public double Progress => MaxDuration.TotalSeconds <= 0
        ? 0
        : Math.Clamp(RecordedSeconds / MaxDuration.TotalSeconds, 0, 1);

    public SoundCheckSummary? Summary { get; private set; }

    public event EventHandler? StateChanged;

    public void StartRecording(AudioFormat format)
    {
        StopPlayback();

        lock (_lock)
        {
            _format = format;
            _maxSamples = (int)(MaxDuration.TotalSeconds * format.SampleRate) * format.Channels;
            _samples.Clear();
            _samples.Capacity = _maxSamples;
        }

        Summary = null;
        SetState(SoundCheckState.Recording);
    }

    /// <summary>Called from the capture thread while recording. Stops itself at the time limit.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (State != SoundCheckState.Recording || interleaved.IsEmpty) return;

        bool full;
        lock (_lock)
        {
            var room = _maxSamples - _samples.Count;
            if (room <= 0)
            {
                full = true;
            }
            else
            {
                var take = Math.Min(room, interleaved.Length);
                for (var i = 0; i < take; i++) _samples.Add(interleaved[i]);
                full = _samples.Count >= _maxSamples;
            }
        }

        if (full) StopRecording();
    }

    public void StopRecording()
    {
        if (State != SoundCheckState.Recording) return;

        Summary = Analyse();
        SetState(SoundCheckState.Ready);
    }

    private SoundCheckSummary Analyse()
    {
        float[] samples;
        AudioFormat format;

        lock (_lock)
        {
            samples = _samples.ToArray();
            format = _format;
        }

        if (samples.Length == 0)
        {
            return new SoundCheckSummary(AudioMath.MinDb, AudioMath.MinDb, LevelAdvice.NoSignal, 0);
        }

        var peak = 0f;
        var sumSquares = 0.0;

        foreach (var sample in samples)
        {
            var magnitude = MathF.Abs(sample);
            if (magnitude > peak) peak = magnitude;
            sumSquares += sample * (double)sample;
        }

        var peakDb = AudioMath.ToDb(peak);
        var rmsDb = AudioMath.ToDb((float)Math.Sqrt(sumSquares / samples.Length));

        // Same boundaries as the live meter, from the same place: a recording played back should
        // reach the same verdict the bar showed while it was being made.
        var advice = peakDb < MeterZones.NoSignalDb
            ? LevelAdvice.NoSignal
            : MeterZones.Zone(peakDb) switch
            {
                MeterZone.Quiet => LevelAdvice.TooQuiet,
                MeterZone.Good => LevelAdvice.Good,
                MeterZone.Loud => LevelAdvice.Loud,
                _ => LevelAdvice.Clipping,
            };

        var seconds = (double)samples.Length / format.Channels / format.SampleRate;
        return new SoundCheckSummary(peakDb, rmsDb, advice, seconds);
    }

    public void Play(string? outputDeviceId)
    {
        if (State is not (SoundCheckState.Ready or SoundCheckState.Playing)) return;

        StopPlayback();

        float[] samples;
        AudioFormat format;
        lock (_lock)
        {
            samples = _samples.ToArray();
            format = _format;
        }

        if (samples.Length == 0) return;

        var device = AudioDevices.ResolveOrDefault(outputDeviceId, AudioDeviceKind.Output)
            ?? throw new AudioDeviceUnavailableException(
                "Deck could not find anything to play through. Check your speakers or headphones are connected.");

        var provider = new FloatArrayWaveProvider(samples, format);
        var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
        output.Init(provider);
        output.PlaybackStopped += (_, _) =>
        {
            if (State == SoundCheckState.Playing) SetState(SoundCheckState.Ready);
        };

        output.Play();
        _output = output;
        SetState(SoundCheckState.Playing);
    }

    public void StopPlayback()
    {
        if (_output is null) return;

        try
        {
            _output.Stop();
        }
        catch (Exception)
        {
            // Device gone; nothing to do.
        }

        _output.Dispose();
        _output = null;

        if (State == SoundCheckState.Playing) SetState(SoundCheckState.Ready);
    }

    public void Reset()
    {
        StopPlayback();
        lock (_lock)
        {
            _samples.Clear();
        }

        Summary = null;
        SetState(SoundCheckState.Idle);
    }

    private void SetState(SoundCheckState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => StopPlayback();

    /// <summary>Plays a float array back through NAudio, which wants bytes.</summary>
    private sealed class FloatArrayWaveProvider(float[] samples, AudioFormat format) : IWaveProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

        public int Read(byte[] buffer, int offset, int count)
        {
            var remainingSamples = samples.Length - _position;
            if (remainingSamples <= 0) return 0;

            var samplesToCopy = Math.Min(count / sizeof(float), remainingSamples);
            var source = samples.AsSpan(_position, samplesToCopy);
            MemoryMarshal.AsBytes(source).CopyTo(buffer.AsSpan(offset));

            _position += samplesToCopy;
            return samplesToCopy * sizeof(float);
        }
    }
}
