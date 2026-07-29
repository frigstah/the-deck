namespace Sirs.Core.Audio;

public static class AudioMath
{
    /// <summary>Floor used everywhere we display a level; below this we call it silence.</summary>
    public const float MinDb = -90f;

    public static float ToDb(float linear) =>
        linear <= 1e-7f ? MinDb : Math.Max(MinDb, 20f * MathF.Log10(linear));

    public static float FromDb(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>Maps a dBFS value onto 0..1 for meter drawing, with the usable range expanded.</summary>
    public static float DbToMeterScale(float db, float floorDb = -60f)
    {
        if (db <= floorDb) return 0f;
        if (db >= 0f) return 1f;
        var linear = (db - floorDb) / -floorDb;
        // Slight curve so the -20..0 dB region - where users actually work - gets more width.
        return MathF.Pow(linear, 0.65f);
    }

    public static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;
}
