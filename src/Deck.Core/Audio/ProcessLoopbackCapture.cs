using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Deck.Core.Audio;

/// <summary>
/// Captures the sound of one program, and nothing else on the machine (A9).
/// <para>
/// This is the thing that makes karaoke work. Whole-desktop loopback takes everything - the backing
/// track, the Windows notification, the video call in the other window, and Deck's own monitoring
/// coming back around - so mixing a microphone with a backing track meant either broadcasting your
/// whole desktop or wiring the two together outside Deck. Windows can hand over one process's render
/// stream on its own, and that is exactly the second source a singer needs.
/// </para>
/// <para>
/// Hand-written interop, because there is no wrapper for it: the API is
/// <c>ActivateAudioInterfaceAsync</c> against a virtual device path with the target process id in the
/// activation parameters, rather than an endpoint that can be enumerated. Presented as an ordinary
/// <see cref="IWaveIn"/> for the same reason ASIO is - what Deck does with the audio afterwards is
/// identical, and threading a second capture kind through gain, metering and resampling would double
/// the surface of the one file that must not break.
/// </para>
/// <para>
/// Windows build 20348 or later. Deck does not offer process sources at all below that, rather than
/// offering them and failing at the moment somebody goes on air.
/// </para>
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    /// <summary>
    /// Marks a device id as a program rather than an endpoint. The part after the prefix is the
    /// executable's name, not its process id: a pid is different every time the program starts, and a
    /// saved setting that named one would be pointing at nothing by tomorrow.
    /// </summary>
    public const string IdPrefix = "process:";

    private const string VirtualDevicePath = "VAD\\Process_Loopback";

    /// <summary>
    /// The build that first understood process loopback. Checked at runtime rather than at compile
    /// time, because Deck is built against an older SDK on purpose and still has to run on 1809.
    /// </summary>
    private const int MinimumWindowsBuild = 20348;

    private const int ShareModeShared = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int StreamFlagsEventCallback = 0x00040000;
    private const uint StreamFlagsAutoConvertPcm = 0x80000000;
    private const int StreamFlagsSrcDefaultQuality = 0x08000000;

    /// <summary>Twenty milliseconds, the same as the WASAPI capture path uses.</summary>
    private const long BufferDurationHns = 200000;

    private const int BufferFlagsSilent = 0x2;

    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly int _processId;
    private readonly string _programName;
    private readonly int _requestedSampleRate;

    private readonly ManualResetEventSlim _opened = new(false);
    private readonly CancellationTokenSource _stop = new();

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private IntPtr _bufferEvent = IntPtr.Zero;
    private Thread? _captureThread;
    private Exception? _openFailure;
    private byte[] _bytes = [];

    public ProcessLoopbackCapture(int processId, string programName, int sampleRate)
    {
        _processId = processId;
        _programName = programName;
        _requestedSampleRate = sampleRate;

        // Replaced by whatever the audio engine actually accepted, once it has said.
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    }

    public WaveFormat WaveFormat { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>Whether this version of Windows can hand over a single program's audio.</summary>
    public static bool IsSupported => SupportedOn(Environment.OSVersion.Version.Build);

    /// <summary>
    /// The rule on its own, taking the build number rather than reading it, so it can be checked
    /// without being on the version in question - which is the only way to check the negative case.
    /// </summary>
    public static bool SupportedOn(int build) => build >= MinimumWindowsBuild;

    /// <summary>The id Deck stores for a program, and the name it stores inside it.</summary>
    public static string IdFor(string programName) => IdPrefix + programName.ToLowerInvariant();

    public static bool IsProcessId(string? deviceId) =>
        deviceId is not null && deviceId.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    public static string ProgramNameFrom(string deviceId) =>
        IsProcessId(deviceId) ? deviceId[IdPrefix.Length..] : deviceId;

    public void StartRecording()
    {
        if (!IsSupported)
        {
            throw new AudioDeviceUnavailableException(
                "Capturing one program on its own needs Windows 11, or Windows 10 build 20348. " +
                "On this version, choose \"Sound playing on this PC\" instead - it takes everything at once.");
        }

        // Its own thread, in the multi-threaded apartment: the activation completes on a pool thread
        // and the capture loop wants to sit on an event handle without the rest of Deck waiting.
        _captureThread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"Deck process capture ({_programName})",
            Priority = ThreadPriority.AboveNormal,
        };

        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();

        // Wait for the format to be settled before returning, so the caller can read WaveFormat and
        // build its resampler from what the engine actually gave us rather than what was asked for.
        if (!_opened.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new AudioDeviceUnavailableException(
                $"Windows did not hand over the sound of {_programName} in time. It may have closed.");
        }

        if (_openFailure is not null) throw _openFailure;
    }

    public void StopRecording()
    {
        _stop.Cancel();

        if (_bufferEvent != IntPtr.Zero) SetEvent(_bufferEvent);

        var thread = _captureThread;
        _captureThread = null;
        thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        Exception? failure = null;

        try
        {
            Open();
            _opened.Set();
            Pump();
        }
        catch (Exception ex)
        {
            _openFailure ??= ex;
            failure = ex;
            _opened.Set();
        }
        finally
        {
            Release();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }

    private void Open()
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = 1, // process loopback
            TargetProcessId = _processId,

            // Include the target's children. A browser does not render audio from the process you see
            // in the task bar - it hands that to a child - so targeting the one the user picked and
            // excluding its tree would capture silence from an application that is plainly playing.
            ProcessLoopbackMode = 0,
        };

        var blob = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var variant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());

        try
        {
            Marshal.StructureToPtr(parameters, blob, false);

            Marshal.StructureToPtr(
                new PropVariantBlob
                {
                    VariantType = 65, // VT_BLOB
                    Size = Marshal.SizeOf<AudioClientActivationParams>(),
                    Data = blob,
                },
                variant,
                false);

            var interfaceId = AudioClientId;
            var handler = new ActivationHandler();

            ActivateAudioInterfaceAsync(VirtualDevicePath, ref interfaceId, variant, handler, out var operation);

            if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new AudioDeviceUnavailableException(
                    $"Windows did not answer when Deck asked for the sound of {_programName}.");
            }

            var result = operation.GetActivateResult(out var activateResult, out var activated);
            Marshal.ThrowExceptionForHR(result);

            if (activateResult != 0)
            {
                throw new AudioDeviceUnavailableException(Explain(activateResult));
            }

            _client = (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(blob);
        }

        Initialise(_client);

        var service = GetCaptureClient(_client);
        _capture = service;

        Marshal.ThrowExceptionForHR(_client.Start());
    }

    /// <summary>
    /// Asks for a format rather than reading one. A process loopback client has no mix format to
    /// report - there is no endpoint behind it - so the caller states what it wants and the audio
    /// engine converts. Float first because that is what everything downstream already speaks; the
    /// sixteen-bit fallback is the shape Microsoft's own sample uses, kept for the case where a
    /// version of Windows refuses the first.
    /// </summary>
    private void Initialise(IAudioClient client)
    {
        var candidates = new[]
        {
            WaveFormat.CreateIeeeFloatWaveFormat(_requestedSampleRate, 2),
            new WaveFormat(_requestedSampleRate, 16, 2),
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2),
            new WaveFormat(44100, 16, 2),
        };

        var flags = StreamFlagsLoopback | StreamFlagsEventCallback
                    | unchecked((int)StreamFlagsAutoConvertPcm) | StreamFlagsSrcDefaultQuality;

        int last = 0;

        foreach (var format in candidates)
        {
            var native = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>() + 64);

            try
            {
                Marshal.StructureToPtr(WaveFormatExtensible.From(format), native, false);

                last = client.Initialize(ShareModeShared, flags, BufferDurationHns, 0, native, IntPtr.Zero);

                if (last == 0)
                {
                    WaveFormat = format;

                    _bufferEvent = CreateEventW(IntPtr.Zero, false, false, null);
                    if (_bufferEvent == IntPtr.Zero) throw new InvalidOperationException("No event handle.");

                    Marshal.ThrowExceptionForHR(client.SetEventHandle(_bufferEvent));
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        throw new AudioDeviceUnavailableException(
            $"Windows would not give Deck the sound of {_programName} in any format it can use. {Explain(last)}");
    }

    private static IAudioCaptureClient GetCaptureClient(IAudioClient client)
    {
        var id = AudioCaptureClientId;
        Marshal.ThrowExceptionForHR(client.GetService(ref id, out var service));
        return (IAudioCaptureClient)service;
    }

    private void Pump()
    {
        var capture = _capture!;
        var frameSize = WaveFormat.BlockAlign;

        while (!_stop.IsCancellationRequested)
        {
            // A program that is playing nothing still ticks: the engine delivers buffers marked silent
            // rather than going quiet, which is what lets a silent source sit in a mix without the
            // whole mix stalling on it.
            if (WaitForSingleObject(_bufferEvent, 200) is not (0 or 0x102)) break;
            if (_stop.IsCancellationRequested) break;

            while (true)
            {
                var hr = capture.GetNextPacketSize(out var frames);
                if (hr != 0 || frames == 0) break;

                hr = capture.GetBuffer(out var data, out var read, out var flags, out _, out _);
                if (hr != 0) break;

                var bytes = (int)read * frameSize;

                if (bytes > 0)
                {
                    if (_bytes.Length < bytes) _bytes = new byte[bytes * 2];

                    if ((flags & BufferFlagsSilent) != 0)
                    {
                        Array.Clear(_bytes, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(data, _bytes, 0, bytes);
                    }

                    DataAvailable?.Invoke(this, new WaveInEventArgs(_bytes, bytes));
                }

                capture.ReleaseBuffer(read);
            }
        }
    }

    private void Release()
    {
        try
        {
            _client?.Stop();
        }
        catch (Exception)
        {
            // Already gone; nothing to salvage.
        }

        if (_capture is not null)
        {
            Marshal.FinalReleaseComObject(_capture);
            _capture = null;
        }

        if (_client is not null)
        {
            Marshal.FinalReleaseComObject(_client);
            _client = null;
        }

        if (_bufferEvent != IntPtr.Zero)
        {
            CloseHandle(_bufferEvent);
            _bufferEvent = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Turns the handful of results that actually happen into something a broadcaster can act on. The
    /// rest are left as the code, because inventing a plain-language sentence for an error nobody has
    /// seen is how you end up with a confidently wrong explanation on screen.
    /// </summary>
    private string Explain(int hr) => unchecked((uint)hr) switch
    {
        0x80070005 => $"Windows refused to let Deck listen to {_programName}. Programs running as administrator cannot be captured by one that is not.",
        0x88890001 => $"{_programName} is not playing any sound Deck can take.",
        0x88890008 => $"{_programName} plays in a format Deck cannot ask for.",
        0x8889000A => $"Another program has exclusive use of the sound device, so {_programName} cannot be captured.",
        0x80070490 => $"{_programName} is no longer running.",
        _ => $"Windows returned 0x{hr:X8}.",
    };

    public void Dispose()
    {
        StopRecording();
        _stop.Dispose();
        _opened.Dispose();
    }

    // ------------------------------------------------------------------ interop

    /// <summary>
    /// Called back by Windows when the activation finishes. The result is not read here - the caller
    /// reads it from the operation - so all this has to do is say when.
    /// </summary>
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public ManualResetEventSlim Completed { get; } = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            Completed.Set();
            return 0;
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid interfaceId,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>
    /// The activation parameters, as one blob. Twelve bytes: the activation type, then the union that
    /// for process loopback holds the target and whether its children come with it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public int TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>
    /// A PROPVARIANT carrying a blob, laid out by hand. The union starts eight bytes in, which is why
    /// the size and the pointer are not simply the next two fields.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public int Size;
        public int Padding;
        public IntPtr Data;
    }

    /// <summary>WAVEFORMATEXTENSIBLE, which is what has to be passed for a float format.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExtensible
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
        public short ValidBitsPerSample;
        public int ChannelMask;
        public Guid SubFormat;

        private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

        public static WaveFormatExtensible From(WaveFormat format)
        {
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;

            return new WaveFormatExtensible
            {
                FormatTag = unchecked((short)0xFFFE), // WAVE_FORMAT_EXTENSIBLE
                Channels = (short)format.Channels,
                SamplesPerSecond = format.SampleRate,
                AverageBytesPerSecond = format.AverageBytesPerSecond,
                BlockAlign = (short)format.BlockAlign,
                BitsPerSample = (short)format.BitsPerSample,
                ExtraSize = 22,
                ValidBitsPerSample = (short)format.BitsPerSample,
                ChannelMask = format.Channels == 1 ? 0x4 : 0x3,
                SubFormat = isFloat ? FloatSubFormat : PcmSubFormat,
            };
        }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>
    /// Every method in vtable order, whether Deck calls it or not: leave one out and every call after
    /// it lands on the wrong function.
    /// </summary>
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionId);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrameCount);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr handle);

        [PreserveSig]
        int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags, out long devicePosition, out long qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint framesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint framesInNextPacket);
    }
}
