using NAudio.Wave;

namespace Deck.Core.Audio;

/// <summary>
/// Capture from a professional interface through its own ASIO driver (A8), presented as an ordinary
/// <see cref="IWaveIn"/> so that nothing above it has to know the difference.
/// <para>
/// Shippable without licensing trouble: NAudio reaches ASIO drivers through the COM interfaces they
/// already expose, so nothing of Steinberg's SDK is compiled in or redistributed. The driver itself
/// belongs to whoever made the interface and is already on the user's machine.
/// </para>
/// <para>
/// An adapter rather than a second path through <see cref="AudioSource"/>. ASIO differs from WASAPI
/// in how audio is fetched, not in what Deck then does with it, and threading a second capture kind
/// through the gain, metering, channel mapping and resampling would double the surface of the one
/// file that must not break.
/// </para>
/// <para>
/// Verified against real hardware as far as this layer goes: a Behringer X-AIR interface delivers
/// audio through it at the expected rate. What has <em>not</em> been done is a broadcast from an
/// ASIO input end to end, or any test of what happens when such an interface is unplugged mid-show.
/// It is offered as an option, never as a default.
/// </para>
/// </summary>
public sealed class AsioCapture : IWaveIn
{
    /// <summary>
    /// Marks a device id as ASIO. Driver names are free text and could collide with nothing else
    /// Deck stores, but a prefix means a saved setting can never be mistaken for a WASAPI endpoint
    /// id - which would send Deck looking for a device that does not exist.
    /// </summary>
    public const string IdPrefix = "asio:";

    private readonly string _driverName;
    private readonly int _requestedSampleRate;

    private readonly ManualResetEventSlim _opened = new(false);
    private readonly ManualResetEventSlim _stopRequested = new(false);

    private Thread? _driverThread;
    private Exception? _openFailure;
    private AsioOut? _asio;
    private float[] _interleaved = [];
    private byte[] _bytes = [];

    public AsioCapture(string driverName, int sampleRate)
    {
        _driverName = driverName;
        _requestedSampleRate = sampleRate;

        // Filled in properly by StartRecording, once the driver has said what it will actually do.
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    }

    public WaveFormat WaveFormat { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>Every ASIO driver registered on this machine. Usually none.</summary>
    public static IReadOnlyList<string> DriverNames()
    {
        try
        {
            return AsioOut.GetDriverNames();
        }
        catch (Exception)
        {
            // No ASIO registry key, or a broken entry left behind by an uninstall. An empty list is
            // the right answer, and this must never stop the device picker being built.
            return [];
        }
    }

    public static bool IsAsioId(string? deviceId) =>
        deviceId is not null && deviceId.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static string IdFor(string driverName) => IdPrefix + driverName;

    public static string DriverNameFrom(string deviceId) =>
        IsAsioId(deviceId) ? deviceId[IdPrefix.Length..] : deviceId;

    /// <summary>ASIO inputs offered as capture sources, or an empty list where there are none.</summary>
    public static IReadOnlyList<AudioDevice> Devices() =>
        DriverNames()
            .Select(name => new AudioDevice(IdFor(name), name, AudioDeviceKind.Asio, IsSystemDefault: false))
            .ToList();

    /// <summary>
    /// Opens the driver on a thread of its own, and holds it there.
    /// <para>
    /// ASIO drivers are COM objects that must be created on a single-threaded apartment. WPF's UI
    /// thread happens to be one, so an ASIO input opened by clicking would work - but the device
    /// watchdog that recovers an unplugged interface (A6) runs on a timer, which is a thread-pool
    /// thread and is not. Without this, ASIO would open when chosen and then fail to come back
    /// after any glitch, with an error message about apartment states.
    /// </para>
    /// <para>
    /// So the driver gets its own thread, created STA, which opens it and then sits waiting until
    /// asked to stop. Everything it is opened from is then irrelevant.
    /// </para>
    /// </summary>
    public void StartRecording()
    {
        StopRecording();

        _opened.Reset();
        _stopRequested.Reset();
        _openFailure = null;

        var thread = new Thread(DriverLoop)
        {
            IsBackground = true,
            Name = $"Deck ASIO ({_driverName})",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        _driverThread = thread;

        // Ten seconds is generous. Some drivers take a second or two to wake an interface up, and a
        // hung one must not hang Deck along with it.
        if (!_opened.Wait(TimeSpan.FromSeconds(10)))
        {
            _stopRequested.Set();

            throw new AudioDeviceUnavailableException(
                $"The ASIO driver \"{_driverName}\" did not respond. Check the interface is switched on.");
        }

        if (_openFailure is { } failure)
        {
            _driverThread = null;
            throw failure;
        }
    }

    private void DriverLoop()
    {
        AsioOut asio;

        try
        {
            asio = new AsioOut(_driverName);
        }
        catch (Exception ex)
        {
            _openFailure = new AudioDeviceUnavailableException(
                $"Deck could not open the ASIO driver \"{_driverName}\". " +
                "It may be in use by another program — ASIO drivers usually allow only one at a time. " +
                $"({ex.Message})");

            _opened.Set();
            return;
        }

        try
        {
            var channels = Math.Max(1, Math.Min(2, asio.DriverInputChannelCount));

            // Asked before opening, and refused rather than worked around. Plenty of ASIO drivers
            // ignore a rate they do not like and quietly keep the one set in their own control
            // panel - and since the resampler downstream is built from the rate Deck believes it
            // is getting, being wrong here puts the entire show out of pitch. Better to say so.
            if (!asio.IsSampleRateSupported(_requestedSampleRate))
            {
                throw new AudioDeviceUnavailableException(
                    $"\"{_driverName}\" will not run at {_requestedSampleRate} Hz. " +
                    "Open the interface's own control panel and set it to that rate, or choose a " +
                    "quality setting in Deck that matches the rate it is already on.");
            }

            // Null playback: Deck only ever records. Handing the driver a playback provider it would
            // then pull from is a way to end up feeding the studio monitors from an encoder.
            asio.InitRecordAndPlayback(null, channels, _requestedSampleRate);

            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_requestedSampleRate, channels);

            asio.AudioAvailable += OnAudioAvailable;
            asio.DriverResetRequest += OnDriverReset;

            _asio = asio;
            asio.Play();
        }
        catch (Exception ex)
        {
            asio.Dispose();

            _openFailure = ex as AudioDeviceUnavailableException ?? new AudioDeviceUnavailableException(
                $"Deck could not start recording from \"{_driverName}\": {ex.Message}");

            _opened.Set();
            return;
        }

        _opened.Set();

        // Held open here for as long as it is wanted. The driver's own thread delivers the audio;
        // this one exists only so that the COM object lives and dies on a single-threaded apartment.
        _stopRequested.Wait();

        asio.AudioAvailable -= OnAudioAvailable;
        asio.DriverResetRequest -= OnDriverReset;
        _asio = null;

        try
        {
            asio.Stop();
        }
        catch (Exception)
        {
            // Driver may already be gone.
        }

        asio.Dispose();
    }

    public void StopRecording()
    {
        var thread = _driverThread;
        if (thread is null) return;

        _driverThread = null;
        _stopRequested.Set();

        // Waited for, so that a restart cannot race the old driver's teardown - most ASIO drivers
        // refuse a second client, so overlapping the two would fail to reopen.
        if (thread.IsAlive) thread.Join(TimeSpan.FromSeconds(5));

        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    /// <summary>
    /// A driver reset means the user changed the buffer size or sample rate in the interface's own
    /// control panel. Reported as a stop rather than handled, so the watchdog that already recovers
    /// unplugged devices (A6) picks it up by the path it has been proven on.
    /// </summary>
    private void OnDriverReset(object? sender, EventArgs e) =>
        RecordingStopped?.Invoke(this, new StoppedEventArgs(
            new AudioDeviceUnavailableException($"The \"{_driverName}\" driver was reset.")));

    private void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        var handler = DataAvailable;
        if (handler is null) return;

        var required = e.SamplesPerBuffer * e.InputBuffers.Length;
        if (_interleaved.Length < required) _interleaved = new float[required];

        // NAudio converts from whatever the driver's native sample type is - 32-bit integer on most
        // interfaces, occasionally 24-bit packed - into float, which is what the rest of the
        // pipeline already speaks.
        var written = e.GetAsInterleavedSamples(_interleaved);
        if (written <= 0) return;

        var byteCount = written * sizeof(float);
        if (_bytes.Length < byteCount) _bytes = new byte[byteCount];

        Buffer.BlockCopy(_interleaved, 0, _bytes, 0, byteCount);
        handler(this, new WaveInEventArgs(_bytes, byteCount));
    }

    public void Dispose()
    {
        StopRecording();
        _opened.Dispose();
        _stopRequested.Dispose();
    }
}
