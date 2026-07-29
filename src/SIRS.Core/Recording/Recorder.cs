using System.Diagnostics;
using System.Runtime.InteropServices;
using Sirs.Core.Audio;
using Sirs.Core.Codecs;

namespace Sirs.Core.Recording;

/// <summary>
/// Records to disk while broadcasting, or on its own (G1, G2). It runs its own encoder rather than
/// tapping the broadcast's, which costs a little CPU but means recording works with no server
/// configured at all - a useful way in for someone who is not ready to go live yet.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly object _lock = new();

    private IAudioEncoder? _encoder;
    private FileStream? _file;
    private WavWriter? _wav;
    private FormatConverter? _converter;
    private long _startTicks;
    private long _fileStartTicks;

    private RecordingSettings? _settings;
    private EncoderSettings? _encoderSettings;
    private string _stationName = string.Empty;
    private int _blocksSinceSpaceCheck;
    private bool _lowSpaceWarned;

    public bool IsRecording { get; private set; }

    public string? CurrentFilePath { get; private set; }

    public long BytesWritten { get; private set; }

    public TimeSpan Elapsed => IsRecording ? Stopwatch.GetElapsedTime(_startTicks) : TimeSpan.Zero;

    public event EventHandler<RecordingFailedEventArgs>? Failed;

    /// <summary>
    /// Starts recording. <paramref name="captureFormat"/> is the format audio actually arrives in,
    /// which is not always the one being recorded: broadcasting to two servers captures at the
    /// higher of their rates, and recording lossless ignores the stream's settings entirely.
    /// </summary>
    public void Start(
        RecordingSettings settings,
        EncoderSettings encoderSettings,
        AudioFormat captureFormat,
        string stationName,
        string title)
    {
        lock (_lock)
        {
            StopInternal();

            _settings = settings;
            _encoderSettings = settings.EncoderFor(encoderSettings, captureFormat);
            _stationName = stationName;
            _lowSpaceWarned = false;

            // Built once, not once per file: the resampler carries filter state, and restarting it
            // at every auto-split would put a click at each boundary.
            _converter = new FormatConverter(captureFormat, _encoderSettings.Format);

            OpenFile(title);

            BytesWritten = 0;
            _startTicks = Stopwatch.GetTimestamp();
            IsRecording = true;
        }
    }

    /// <summary>Opens the next file. Used both at the start and at each auto-split boundary.</summary>
    private void OpenFile(string title)
    {
        var settings = _settings!;
        var encoderSettings = _encoderSettings!;

        Directory.CreateDirectory(settings.Folder);

        var baseName = FilenameTemplate.Build(settings.FilenameTemplate, _stationName, title, DateTime.Now);
        var extension = settings.Extension(encoderSettings.Codec);
        var path = FilenameTemplate.EnsureUnique(settings.Folder, baseName, extension);

        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);

        if (settings.Format == RecordingFormat.Wav)
        {
            _wav = new WavWriter(_file, encoderSettings.Format);
        }
        else
        {
            _encoder = EncoderFactory.Create(encoderSettings);
            if (_encoder.StreamHeader.Length > 0) _file.Write(_encoder.StreamHeader);
        }

        CurrentFilePath = path;
        _fileStartTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>Closes the current file cleanly and starts the next one (G3).</summary>
    private void RollFile()
    {
        var finished = CurrentFilePath;

        CloseFile();
        OpenFile(string.Empty);

        FileCompleted?.Invoke(this, new RecordingFileEventArgs(finished, CurrentFilePath));
    }

    private void CloseFile()
    {
        if (_encoder is not null && _file is not null)
        {
            try
            {
                var tail = _encoder.Finish();
                if (!tail.IsEmpty) _file.Write(tail);
            }
            catch (Exception)
            {
                // Nothing useful to do while closing down.
            }
        }

        _encoder?.Dispose();
        _encoder = null;

        _wav?.Finish();
        _wav = null;

        _file?.Dispose();
        _file = null;
    }

    /// <summary>Raised when auto-split closes one file and opens the next.</summary>
    public event EventHandler<RecordingFileEventArgs>? FileCompleted;

    /// <summary>Raised once when the drive is getting full, while there is still time to act.</summary>
    public event EventHandler<RecordingFailedEventArgs>? LowDiskSpace;

    /// <summary>Time recorded into the current file, which resets at each split.</summary>
    public TimeSpan FileElapsed => IsRecording ? Stopwatch.GetElapsedTime(_fileStartTicks) : TimeSpan.Zero;

    /// <summary>Called from the capture thread with audio already at the stream format.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (!IsRecording || interleaved.IsEmpty) return;

        lock (_lock)
        {
            if (!IsRecording || _file is null) return;

            try
            {
                var block = _converter is null ? interleaved : _converter.Process(interleaved);
                if (block.IsEmpty) return;

                if (_wav is not null)
                {
                    BytesWritten += _wav.Write(block);
                }
                else if (_encoder is not null)
                {
                    var encoded = _encoder.Encode(block);
                    if (!encoded.IsEmpty)
                    {
                        _file.Write(encoded);
                        BytesWritten += encoded.Length;
                    }
                }

                CheckSplit();
                CheckDiskSpace();
            }
            catch (IOException ex)
            {
                // Out of disk space is the usual cause. Stop cleanly and tell the user rather than
                // failing silently for the rest of the show.
                var path = CurrentFilePath;
                StopInternal();
                Failed?.Invoke(this, new RecordingFailedEventArgs(
                    $"SIRS had to stop recording: {ex.Message}. The file so far is saved at {path}."));
            }
        }
    }

    private void CheckSplit()
    {
        var minutes = _settings?.SplitMinutes ?? 0;
        if (minutes <= 0) return;
        if (FileElapsed < TimeSpan.FromMinutes(minutes)) return;

        RollFile();
    }

    /// <summary>
    /// Watches free space (G4). Checked every few hundred blocks rather than every block, because
    /// querying the drive is far more expensive than writing to it.
    /// </summary>
    private void CheckDiskSpace()
    {
        if (++_blocksSinceSpaceCheck < 500) return;
        _blocksSinceSpaceCheck = 0;

        var settings = _settings;
        if (settings is null) return;

        long free;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(settings.Folder))!).AvailableFreeSpace;
        }
        catch (Exception)
        {
            // Network paths and odd mounts do not always report free space; carry on recording.
            return;
        }

        if (free <= settings.MinimumFreeBytes)
        {
            var path = CurrentFilePath;
            StopInternal();
            Failed?.Invoke(this, new RecordingFailedEventArgs(
                $"SIRS stopped recording because the drive is nearly full ({Describe(free)} left). " +
                $"The recording so far is saved at {path}."));
            return;
        }

        if (!_lowSpaceWarned && free <= settings.LowSpaceWarningBytes)
        {
            _lowSpaceWarned = true;
            LowDiskSpace?.Invoke(this, new RecordingFailedEventArgs(
                $"The drive holding your recordings has {Describe(free)} left. SIRS will stop recording if it runs out."));
        }
    }

    private static string Describe(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
        _ => $"{bytes / 1024.0 / 1024:0} MB",
    };

    public string? Stop()
    {
        lock (_lock)
        {
            var path = CurrentFilePath;
            StopInternal();
            return path;
        }
    }

    private void StopInternal()
    {
        IsRecording = false;
        CloseFile();
        _converter = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            StopInternal();
        }
    }

    /// <summary>
    /// Writes a 16-bit PCM WAV, patching the header sizes on close. Kept here rather than pulled
    /// from NAudio so the recorder owns its own file handle and can report disk errors precisely.
    /// </summary>
    private sealed class WavWriter(FileStream file, AudioFormat format)
    {
        private const int HeaderBytes = 44;

        private byte[] _scratch = new byte[16384];
        private int _dataBytes;
        private bool _headerWritten;

        public int Write(ReadOnlySpan<float> interleaved)
        {
            if (!_headerWritten)
            {
                file.Write(new byte[HeaderBytes]); // placeholder, patched in Finish
                _headerWritten = true;
            }

            var byteCount = interleaved.Length * 2;
            if (_scratch.Length < byteCount) _scratch = new byte[byteCount * 2];

            var destination = MemoryMarshal.Cast<byte, short>(_scratch.AsSpan(0, byteCount));
            for (var i = 0; i < interleaved.Length; i++)
            {
                var sample = interleaved[i];
                var clamped = sample > 1f ? 1f : sample < -1f ? -1f : sample;
                destination[i] = (short)(clamped * 32767f);
            }

            file.Write(_scratch, 0, byteCount);
            _dataBytes += byteCount;
            return byteCount;
        }

        public void Finish()
        {
            if (!_headerWritten) return;

            var byteRate = format.SampleRate * format.Channels * 2;
            var header = new byte[HeaderBytes];
            var span = header.AsSpan();

            "RIFF"u8.CopyTo(span);
            BitConverter.TryWriteBytes(span[4..], 36 + _dataBytes);
            "WAVE"u8.CopyTo(span[8..]);
            "fmt "u8.CopyTo(span[12..]);
            BitConverter.TryWriteBytes(span[16..], 16);              // subchunk size
            BitConverter.TryWriteBytes(span[20..], (short)1);        // PCM
            BitConverter.TryWriteBytes(span[22..], (short)format.Channels);
            BitConverter.TryWriteBytes(span[24..], format.SampleRate);
            BitConverter.TryWriteBytes(span[28..], byteRate);
            BitConverter.TryWriteBytes(span[32..], (short)(format.Channels * 2)); // block align
            BitConverter.TryWriteBytes(span[34..], (short)16);       // bits per sample
            "data"u8.CopyTo(span[36..]);
            BitConverter.TryWriteBytes(span[40..], _dataBytes);

            try
            {
                file.Flush();
                file.Seek(0, SeekOrigin.Begin);
                file.Write(header);
                file.Flush();
            }
            catch (Exception)
            {
                // The audio is on disk either way; a WAV with a stale header is still recoverable.
            }
        }
    }
}

public sealed class RecordingFailedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class RecordingFileEventArgs(string? finishedPath, string? nextPath) : EventArgs
{
    public string? FinishedPath { get; } = finishedPath;

    public string? NextPath { get; } = nextPath;
}
