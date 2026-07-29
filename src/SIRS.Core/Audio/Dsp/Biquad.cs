namespace Sirs.Core.Audio.Dsp;

/// <summary>
/// Direct-form-I biquad with coefficients that can be replaced while it is running.
/// <para>
/// That last part is the reason this exists rather than NAudio's filter: moving a tone control has
/// to change the response without clearing the delay line, because clearing it mid-programme puts an
/// audible click on air. State is kept in double precision, which costs nothing at these sample
/// counts and keeps low-frequency sections well behaved.
/// </para>
/// </summary>
internal sealed class Biquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public float Process(float input)
    {
        double x = input;
        var y = (_b0 * x) + (_b1 * _x1) + (_b2 * _x2) - (_a1 * _y1) - (_a2 * _y2);

        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;

        return (float)y;
    }

    public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;

    public void SetPassthrough()
    {
        _b0 = 1;
        _b1 = _b2 = _a1 = _a2 = 0;
    }

    public void SetLowPass(int sampleRate, double frequency, double q)
    {
        var (cos, alpha) = Common(sampleRate, frequency, q);
        Normalise(
            (1 - cos) / 2, 1 - cos, (1 - cos) / 2,
            1 + alpha, -2 * cos, 1 - alpha);
    }

    public void SetHighPass(int sampleRate, double frequency, double q)
    {
        var (cos, alpha) = Common(sampleRate, frequency, q);
        Normalise(
            (1 + cos) / 2, -(1 + cos), (1 + cos) / 2,
            1 + alpha, -2 * cos, 1 - alpha);
    }

    public void SetPeaking(int sampleRate, double frequency, double q, double gainDb)
    {
        var a = Math.Pow(10, gainDb / 40);
        var (cos, alpha) = Common(sampleRate, frequency, q);
        Normalise(
            1 + (alpha * a), -2 * cos, 1 - (alpha * a),
            1 + (alpha / a), -2 * cos, 1 - (alpha / a));
    }

    public void SetLowShelf(int sampleRate, double frequency, double gainDb, double slope = 1.0)
    {
        var a = Math.Pow(10, gainDb / 40);
        var w0 = 2 * Math.PI * frequency / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / 2 * Math.Sqrt(((a + (1 / a)) * ((1 / slope) - 1)) + 2);
        var sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;

        Normalise(
            a * ((a + 1) - ((a - 1) * cos) + sqrtA2Alpha),
            2 * a * ((a - 1) - ((a + 1) * cos)),
            a * ((a + 1) - ((a - 1) * cos) - sqrtA2Alpha),
            (a + 1) + ((a - 1) * cos) + sqrtA2Alpha,
            -2 * ((a - 1) + ((a + 1) * cos)),
            (a + 1) + ((a - 1) * cos) - sqrtA2Alpha);
    }

    public void SetHighShelf(int sampleRate, double frequency, double gainDb, double slope = 1.0)
    {
        var a = Math.Pow(10, gainDb / 40);
        var w0 = 2 * Math.PI * frequency / sampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / 2 * Math.Sqrt(((a + (1 / a)) * ((1 / slope) - 1)) + 2);
        var sqrtA2Alpha = 2 * Math.Sqrt(a) * alpha;

        Normalise(
            a * ((a + 1) + ((a - 1) * cos) + sqrtA2Alpha),
            -2 * a * ((a - 1) + ((a + 1) * cos)),
            a * ((a + 1) + ((a - 1) * cos) - sqrtA2Alpha),
            (a + 1) - ((a - 1) * cos) + sqrtA2Alpha,
            2 * ((a - 1) - ((a + 1) * cos)),
            (a + 1) - ((a - 1) * cos) - sqrtA2Alpha);
    }

    private static (double Cos, double Alpha) Common(int sampleRate, double frequency, double q)
    {
        var w0 = 2 * Math.PI * frequency / sampleRate;
        return (Math.Cos(w0), Math.Sin(w0) / (2 * q));
    }

    private void Normalise(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }
}
