namespace Deck.Core.Audio.Dsp;

/// <summary>
/// Loudness to ITU-R BS.1770-4 / EBU R128 (B8): K-weighted, gated, in LUFS.
/// <para>
/// Peak metering answers "am I clipping"; this answers "how loud does it feel", which is the
/// question every platform now normalises against. A show mastered to -14 LUFS and one at -24 will
/// both look fine on a peak meter and sound a full stop apart to a listener.
/// </para>
/// <para>
/// Process() runs on the audio thread and the readouts are read from the UI thread. The values are
/// doubles rather than a lock: a torn read would show a nonsense number for one frame of a meter
/// that updates twenty times a second, and taking a lock on the audio thread to avoid that would be
/// a far worse trade.
/// </para>
/// </summary>
public sealed class LoudnessMeter
{
    /// <summary>Nothing quieter than this counts towards the integrated figure (the absolute gate).</summary>
    private const double AbsoluteGateLufs = -70.0;

    /// <summary>Blocks more than this far below the ungated average are dropped (the relative gate).</summary>
    private const double RelativeGateLu = 10.0;

    private const double SubBlockSeconds = 0.1;
    private const int MomentarySubBlocks = 4;   // 400 ms
    private const int ShortTermSubBlocks = 30;  // 3 s

    private const double BinWidth = 0.1;
    private const double BinFloor = AbsoluteGateLufs;
    private const int BinCount = 1000; // up to +30 LUFS, which normalised audio cannot reach

    private readonly int _channels;
    private readonly int _subBlockFrames;
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;

    private readonly double[] _squareSum;
    private readonly double[] _history = new double[ShortTermSubBlocks];

    // Per-bin totals rather than a growing list of blocks: an eight-hour show would otherwise hold
    // a third of a million numbers to answer one question.
    private readonly long[] _binCounts = new long[BinCount];
    private readonly double[] _binPower = new double[BinCount];

    private int _historyFill;
    private int _historyNext;
    private int _frameFill;

    public LoudnessMeter(int sampleRate, int channels)
    {
        _channels = Math.Max(1, channels);
        _subBlockFrames = Math.Max(1, (int)Math.Round(sampleRate * SubBlockSeconds));
        _squareSum = new double[_channels];

        _shelf = new Biquad[_channels];
        _highPass = new Biquad[_channels];

        for (var ch = 0; ch < _channels; ch++)
        {
            _shelf[ch] = Biquad.KWeightingShelf(sampleRate);
            _highPass[ch] = Biquad.KWeightingHighPass(sampleRate);
        }
    }

    /// <summary>Loudness over the last 400 ms - the number that moves with the performance.</summary>
    public double MomentaryLufs { get; private set; } = double.NegativeInfinity;

    /// <summary>Loudness over the last 3 seconds. Steady enough to mix against.</summary>
    public double ShortTermLufs { get; private set; } = double.NegativeInfinity;

    /// <summary>
    /// Gated loudness across everything measured so far - what a platform would normalise against.
    /// </summary>
    public double IntegratedLufs { get; private set; } = double.NegativeInfinity;

    /// <summary>Whether enough audio has been measured for the integrated figure to mean anything.</summary>
    public bool HasIntegrated { get; private set; }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.IsEmpty) return;

        var frames = interleaved.Length / _channels;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            for (var ch = 0; ch < _channels; ch++)
            {
                var weighted = _highPass[ch].Process(_shelf[ch].Process(interleaved[baseIndex + ch]));
                _squareSum[ch] += weighted * weighted;
            }

            if (++_frameFill < _subBlockFrames) continue;

            CompleteSubBlock();
            _frameFill = 0;
            Array.Clear(_squareSum);
        }
    }

    private void CompleteSubBlock()
    {
        // Mean square for this 100 ms, summed across channels. Every channel we handle has a
        // BS.1770 weight of 1.0; the 1.41 surround weights only apply to channels Deck never sees.
        var power = 0.0;
        for (var ch = 0; ch < _channels; ch++) power += _squareSum[ch] / _subBlockFrames;

        _history[_historyNext] = power;
        _historyNext = (_historyNext + 1) % ShortTermSubBlocks;
        if (_historyFill < ShortTermSubBlocks) _historyFill++;

        MomentaryLufs = WindowLoudness(MomentarySubBlocks);
        ShortTermLufs = WindowLoudness(ShortTermSubBlocks);

        // The integrated figure is built from 400 ms blocks overlapping by 75%, which is exactly one
        // new block per 100 ms sub-block once the window has filled.
        if (_historyFill >= MomentarySubBlocks) Accumulate(MomentaryLufs);
    }

    private double WindowLoudness(int subBlocks)
    {
        var available = Math.Min(_historyFill, subBlocks);
        if (available == 0) return double.NegativeInfinity;

        var total = 0.0;
        for (var i = 1; i <= available; i++)
        {
            var index = (_historyNext - i + ShortTermSubBlocks) % ShortTermSubBlocks;
            total += _history[index];
        }

        return ToLufs(total / available);
    }

    private void Accumulate(double blockLufs)
    {
        if (double.IsNegativeInfinity(blockLufs) || blockLufs <= AbsoluteGateLufs) return;

        var bin = (int)((blockLufs - BinFloor) / BinWidth);
        if (bin < 0) return;
        if (bin >= BinCount) bin = BinCount - 1;

        _binCounts[bin]++;
        _binPower[bin] += FromLufs(blockLufs);

        UpdateIntegrated();
    }

    /// <summary>
    /// The two-pass gate from BS.1770: average everything above the absolute gate, then average
    /// again keeping only what is within 10 LU of that first answer. The second pass is what stops a
    /// long silence between songs from dragging the figure down.
    /// </summary>
    private void UpdateIntegrated()
    {
        long count = 0;
        var power = 0.0;

        for (var i = 0; i < BinCount; i++)
        {
            count += _binCounts[i];
            power += _binPower[i];
        }

        if (count == 0)
        {
            IntegratedLufs = double.NegativeInfinity;
            HasIntegrated = false;
            return;
        }

        var relativeThreshold = ToLufs(power / count) - RelativeGateLu;
        var firstBin = (int)Math.Ceiling((relativeThreshold - BinFloor) / BinWidth);
        if (firstBin < 0) firstBin = 0;

        long gatedCount = 0;
        var gatedPower = 0.0;

        for (var i = firstBin; i < BinCount; i++)
        {
            gatedCount += _binCounts[i];
            gatedPower += _binPower[i];
        }

        if (gatedCount == 0)
        {
            IntegratedLufs = double.NegativeInfinity;
            HasIntegrated = false;
            return;
        }

        IntegratedLufs = ToLufs(gatedPower / gatedCount);
        HasIntegrated = true;
    }

    /// <summary>Starts the integrated measurement over, e.g. at the top of a show.</summary>
    public void Reset()
    {
        for (var ch = 0; ch < _channels; ch++)
        {
            _shelf[ch].Reset();
            _highPass[ch].Reset();
        }

        Array.Clear(_squareSum);
        Array.Clear(_history);
        Array.Clear(_binCounts);
        Array.Clear(_binPower);

        _historyFill = 0;
        _historyNext = 0;
        _frameFill = 0;

        MomentaryLufs = ShortTermLufs = IntegratedLufs = double.NegativeInfinity;
        HasIntegrated = false;
    }

    /// <summary>The BS.1770 offset that puts a full-scale sine at roughly -3 LUFS.</summary>
    private static double ToLufs(double power) =>
        power <= 0 ? double.NegativeInfinity : -0.691 + (10 * Math.Log10(power));

    private static double FromLufs(double lufs) => Math.Pow(10, (lufs + 0.691) / 10);

    /// <summary>
    /// Direct-form-I biquad in double precision. The K-weighting coefficients are defined in the
    /// standard for 48 kHz; these are derived from the same analog prototype so they hold at any
    /// rate, which matters because most stations run 44.1.
    /// </summary>
    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1, _x2, _y1, _y2;

        public double Process(double x)
        {
            var y = (b0 * x) + (b1 * _x1) + (b2 * _x2) - (a1 * _y1) - (a2 * _y2);

            _x2 = _x1;
            _x1 = x;
            _y2 = _y1;
            _y1 = y;

            return y;
        }

        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;

        /// <summary>Stage 1: the high-frequency shelf that stands in for the head's acoustics.</summary>
        public static Biquad KWeightingShelf(int sampleRate)
        {
            const double f0 = 1681.974450955533;
            const double gainDb = 3.999843853973347;
            const double q = 0.7071752369554196;

            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var vh = Math.Pow(10, gainDb / 20);
            var vb = Math.Pow(vh, 0.4996667741545416);
            var a0 = 1 + (k / q) + (k * k);

            return new Biquad(
                (vh + (vb * k / q) + (k * k)) / a0,
                2 * ((k * k) - vh) / a0,
                (vh - (vb * k / q) + (k * k)) / a0,
                2 * ((k * k) - 1) / a0,
                (1 - (k / q) + (k * k)) / a0);
        }

        /// <summary>Stage 2: the RLB high-pass, which discounts bass the ear does not weight heavily.</summary>
        public static Biquad KWeightingHighPass(int sampleRate)
        {
            const double f0 = 38.13547087602444;
            const double q = 0.5003270373238773;

            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var denominator = 1 + (k / q) + (k * k);

            return new Biquad(
                1.0,
                -2.0,
                1.0,
                2 * ((k * k) - 1) / denominator,
                (1 - (k / q) + (k * k)) / denominator);
        }
    }
}

/// <summary>
/// The loudness a station is aiming for. Platforms normalise to their own number, so the useful
/// question is not "what is correct" but "where is this going".
/// </summary>
public sealed record LoudnessTarget(string Name, double Lufs, string Detail)
{
    public static readonly LoudnessTarget Streaming = new(
        "Streaming radio", -16, "The usual target for internet radio and podcasts.");

    public static readonly LoudnessTarget Platforms = new(
        "Music platforms", -14, "What Spotify, YouTube and Apple Music normalise to.");

    public static readonly LoudnessTarget Broadcast = new(
        "Broadcast (EBU R128)", -23, "Required by most European broadcasters.");

    public static IReadOnlyList<LoudnessTarget> All { get; } = [Streaming, Platforms, Broadcast];

    public static LoudnessTarget Default => Streaming;

    /// <summary>How this measurement sits against the target, in plain words.</summary>
    public string Verdict(double lufs)
    {
        if (double.IsNegativeInfinity(lufs)) return "Not enough audio yet.";

        var difference = lufs - Lufs;

        return Math.Abs(difference) switch
        {
            <= 1.0 => $"About right for {Name.ToLowerInvariant()}.",
            <= 3.0 => difference > 0
                ? $"A little loud — about {difference:0.#} LU above target."
                : $"A little quiet — about {-difference:0.#} LU below target.",
            _ => difference > 0
                ? $"Too loud — {difference:0.#} LU above target. Pull the level down."
                : $"Too quiet — {-difference:0.#} LU below target. Bring the level up.",
        };
    }

    public AdviceSeverity Severity(double lufs)
    {
        if (double.IsNegativeInfinity(lufs)) return AdviceSeverity.Neutral;

        return Math.Abs(lufs - Lufs) switch
        {
            <= 1.0 => AdviceSeverity.Ok,
            <= 3.0 => AdviceSeverity.Warning,
            _ => AdviceSeverity.Bad,
        };
    }
}
