namespace Deck.Core.Audio;

/// <summary>
/// Where the meter changes colour, and where the coaching verdict changes with it (B1).
/// <para>
/// One set of numbers, because there is only one meter. These boundaries used to be written out
/// three times - in the drawn control, in the live level meter, and in the sound check - and three
/// copies of a threshold is three chances for the bar to turn amber while the words beside it still
/// say the level is good. The meter is meant to teach: a colour that disagrees with the verdict
/// teaches the wrong thing.
/// </para>
/// <para>
/// The caution band is deliberately wider than the level that actually clips. A peak meter reads
/// sample peaks, and a lossy encoder can overshoot those by a decibel or more on the way out, so the
/// point at which somebody should start easing off is well below the point at which the number goes
/// red. Amber from -7 dBFS gives a singer time to notice and back off a step; red from -2,5 dBFS is
/// close enough to the ceiling to mean it.
/// </para>
/// </summary>
public static class MeterZones
{
    /// <summary>Below this there is not enough signal to broadcast.</summary>
    public const float QuietDb = -24f;

    /// <summary>From here up, ease off - the amber part of the scale.</summary>
    public const float LoudDb = -7f;

    /// <summary>From here up, it is about to clip - the red part of the scale.</summary>
    public const float ClipDb = -2.5f;

    /// <summary>Quieter than this and nothing is arriving at all.</summary>
    public const float NoSignalDb = -55f;

    /// <summary>
    /// Which zone a level falls in. Used both to colour a segment and to reach a verdict, which is
    /// the point: they cannot drift apart if they are the same call.
    /// </summary>
    public static MeterZone Zone(float db) => db switch
    {
        >= ClipDb => MeterZone.Clip,
        >= LoudDb => MeterZone.Loud,
        >= QuietDb => MeterZone.Good,
        _ => MeterZone.Quiet,
    };
}

/// <summary>The four bands of the meter, quietest first.</summary>
public enum MeterZone
{
    Quiet,
    Good,
    Loud,
    Clip,
}
