using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Deck.Core.Audio;

/// <summary>
/// Headphone monitoring (B4). Audio is pushed in from the capture thread and played out on a device
/// the user chooses separately from the Windows default, so they can listen on headphones while the
/// speakers stay silent.
/// </summary>
public sealed class MonitorPlayer : IDisposable
{
    private readonly object _lock = new();

    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private byte[] _scratch = new byte[16384];

    public bool IsRunning { get; private set; }

    /// <summary>Monitor volume, 0 to 1. Independent of the level going out to listeners.</summary>
    public float Volume { get; set; } = 0.8f;

    public void Start(string? outputDeviceId, AudioFormat format)
    {
        lock (_lock)
        {
            StopInternal();

            var device = AudioDevices.ResolveOrDefault(outputDeviceId, AudioDeviceKind.Output)
                ?? throw new AudioDeviceUnavailableException(
                    "Deck could not find anything to play through. Check your speakers or headphones are connected.");

            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

            _buffer = new BufferedWaveProvider(waveFormat)
            {
                // A short buffer keeps monitoring close to real time; overflow is dropped rather
                // than queued, because latency that grows over time is worse than a glitch.
                BufferDuration = TimeSpan.FromMilliseconds(400),
                DiscardOnBufferOverflow = true,
            };

            var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 60);
            output.Init(_buffer);
            output.Play();

            _output = output;
            IsRunning = true;
        }
    }

    /// <summary>Called from the capture thread with audio already at the stream format.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (!IsRunning || interleaved.IsEmpty) return;

        var buffer = _buffer;
        if (buffer is null) return;

        var volume = Volume;
        var byteCount = interleaved.Length * sizeof(float);
        if (_scratch.Length < byteCount) _scratch = new byte[byteCount * 2];

        if (volume >= 0.999f)
        {
            MemoryMarshal.AsBytes(interleaved).CopyTo(_scratch);
        }
        else
        {
            var destination = MemoryMarshal.Cast<byte, float>(_scratch.AsSpan(0, byteCount));
            for (var i = 0; i < interleaved.Length; i++) destination[i] = interleaved[i] * volume;
        }

        try
        {
            buffer.AddSamples(_scratch, 0, byteCount);
        }
        catch (InvalidOperationException)
        {
            // Buffer full and discard disabled - cannot happen with the settings above, but a
            // monitoring hiccup must never propagate into the broadcast path.
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        IsRunning = false;

        if (_output is not null)
        {
            try
            {
                _output.Stop();
            }
            catch (Exception)
            {
                // Device may already be gone.
            }

            _output.Dispose();
            _output = null;
        }

        _buffer = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Warning shown when monitoring could feed back into the capture. Deck cannot tell headphones
    /// from speakers, so with a live microphone it warns rather than guessing.
    /// <para>
    /// Loopback capture has a sharper failure: monitoring to the very device being captured feeds
    /// the monitor output straight back into the loopback, which builds instantly rather than
    /// needing a room. That case is certain, not a maybe, so it is worded as such.
    /// </para>
    /// </summary>
    public static string? FeedbackWarning(AudioDeviceKind inputKind, string? inputDeviceId, string? monitorDeviceId)
    {
        if (inputKind == AudioDeviceKind.Loopback)
        {
            if (!string.IsNullOrEmpty(inputDeviceId) && inputDeviceId == monitorDeviceId)
            {
                return "Monitoring through the same device you are capturing will feed back on itself. Choose a different output, or turn monitoring off — you can already hear this sound.";
            }

            return "You are broadcasting this PC's own sound, so you can already hear it. Monitoring is not usually needed here.";
        }

        // A program's own stream carries nothing but that program, so Deck's monitoring cannot get into
        // it however it is played. That is the quiet advantage of capturing one program rather than the
        // whole desktop, and it is the reason there is no warning here.
        if (inputKind == AudioDeviceKind.Process) return null;

        return inputKind == AudioDeviceKind.Input
            ? "Use headphones while monitoring. If this plays through speakers, your microphone will pick it up and howl."
            : null;
    }
}
