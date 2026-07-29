using System.Diagnostics;

namespace Deck.Core.Audio;

/// <summary>
/// Peak + RMS metering with display ballistics, plus the windowed judgement that drives the
/// traffic-light coaching. Process() runs on the audio thread; the properties are read from the UI
/// thread. Float reads/writes are atomic on .NET, and a meter that is one block stale is harmless.
/// </summary>
public sealed class LevelMeter
{
    private const float ClipThreshold = 0.997f;
    private const float PeakDecayDbPerSecond = 26f;
    private const int MaxChannels = 2;

    // A two-bucket sliding window: advice looks at max(current, previous), each bucket covering
    // AdviceBucketSeconds. Gives a stable ~1-2s view in O(1) with no ring buffer.
    private const double AdviceBucketSeconds = 1.0;

    private readonly float[] _displayPeak = new float[MaxChannels];
    private readonly float[] _rmsAccumulator = new float[MaxChannels];
    private int _rmsSampleCount;

    private float _currentBucketPeak;
    private float _previousBucketPeak;
    private long _bucketStartTicks;
    private long _lastProcessTicks;

    private int _consecutiveClippedSamples;
    private bool _clipDetectedInWindow;

    public LevelMeter()
    {
        _bucketStartTicks = Stopwatch.GetTimestamp();
        _lastProcessTicks = _bucketStartTicks;
    }

    /// <summary>Decaying peak per channel in dBFS, suitable for drawing a bar.</summary>
    public float PeakDbLeft { get; private set; } = AudioMath.MinDb;

    public float PeakDbRight { get; private set; } = AudioMath.MinDb;

    /// <summary>Short-window RMS in dBFS - the "how loud does this feel" number.</summary>
    public float RmsDb { get; private set; } = AudioMath.MinDb;

    /// <summary>True peak over the advice window, in dBFS. This is what Advice is derived from.</summary>
    public float WindowPeakDb { get; private set; } = AudioMath.MinDb;

    public LevelAdvice Advice { get; private set; } = LevelAdvice.NoSignal;

    public void Process(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 0 || interleaved.Length == 0) return;

        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _lastProcessTicks) / (double)Stopwatch.Frequency;
        _lastProcessTicks = now;

        Span<float> blockPeak = stackalloc float[MaxChannels];
        blockPeak.Clear();

        var frames = interleaved.Length / channels;
        var clipRun = _consecutiveClippedSamples;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * channels;
            for (var ch = 0; ch < channels; ch++)
            {
                var sample = interleaved[baseIndex + ch];
                var magnitude = MathF.Abs(sample);

                if (magnitude >= ClipThreshold)
                {
                    clipRun++;
                    // Three consecutive samples at full scale is the classic "this is really
                    // clipped, not just a loud transient" test.
                    if (clipRun >= 3) _clipDetectedInWindow = true;
                }
                else
                {
                    clipRun = 0;
                }

                var meterCh = ch < MaxChannels ? ch : MaxChannels - 1;
                if (magnitude > blockPeak[meterCh]) blockPeak[meterCh] = magnitude;
                _rmsAccumulator[meterCh] += sample * sample;
            }
        }

        _consecutiveClippedSamples = clipRun;
        _rmsSampleCount += frames;

        // Peak ballistics: instant attack, timed release.
        var decay = AudioMath.FromDb(-PeakDecayDbPerSecond * (float)elapsedSeconds);
        var overallPeak = 0f;
        for (var ch = 0; ch < MaxChannels; ch++)
        {
            var decayed = _displayPeak[ch] * decay;
            _displayPeak[ch] = MathF.Max(blockPeak[ch], decayed);
            if (blockPeak[ch] > overallPeak) overallPeak = blockPeak[ch];
        }

        PeakDbLeft = AudioMath.ToDb(_displayPeak[0]);
        PeakDbRight = AudioMath.ToDb(channels > 1 ? _displayPeak[1] : _displayPeak[0]);

        // RMS over roughly 300 ms of audio.
        if (_rmsSampleCount > 0)
        {
            var meterChannels = Math.Min(channels, MaxChannels);
            var meanSquare = 0f;
            for (var ch = 0; ch < meterChannels; ch++) meanSquare += _rmsAccumulator[ch];
            meanSquare /= _rmsSampleCount * meterChannels;
            RmsDb = AudioMath.ToDb(MathF.Sqrt(meanSquare));

            if (_rmsSampleCount >= 13230) // ~300 ms at 44.1 kHz
            {
                Array.Clear(_rmsAccumulator);
                _rmsSampleCount = 0;
            }
        }

        if (overallPeak > _currentBucketPeak) _currentBucketPeak = overallPeak;

        if ((now - _bucketStartTicks) / (double)Stopwatch.Frequency >= AdviceBucketSeconds)
        {
            _previousBucketPeak = _currentBucketPeak;
            _currentBucketPeak = 0f;
            _bucketStartTicks = now;
            _clipDetectedInWindow = false;
        }

        var windowPeak = MathF.Max(_currentBucketPeak, _previousBucketPeak);
        WindowPeakDb = AudioMath.ToDb(windowPeak);
        Advice = Judge(WindowPeakDb, _clipDetectedInWindow);
    }

    /// <summary>
    /// Thresholds are chosen for speech and music heading into a lossy encoder: aim for peaks
    /// around -12 to -6 dBFS, which leaves headroom without sounding thin.
    /// </summary>
    private static LevelAdvice Judge(float windowPeakDb, bool clipped)
    {
        if (clipped) return LevelAdvice.Clipping;
        return windowPeakDb switch
        {
            < -55f => LevelAdvice.NoSignal,
            < -24f => LevelAdvice.TooQuiet,
            < -4f => LevelAdvice.Good,
            < -1f => LevelAdvice.Loud,
            _ => LevelAdvice.Clipping,
        };
    }

    public void Reset()
    {
        Array.Clear(_displayPeak);
        Array.Clear(_rmsAccumulator);
        _rmsSampleCount = 0;
        _currentBucketPeak = 0f;
        _previousBucketPeak = 0f;
        _consecutiveClippedSamples = 0;
        _clipDetectedInWindow = false;
        _bucketStartTicks = Stopwatch.GetTimestamp();
        _lastProcessTicks = _bucketStartTicks;
        PeakDbLeft = PeakDbRight = RmsDb = WindowPeakDb = AudioMath.MinDb;
        Advice = LevelAdvice.NoSignal;
    }
}
