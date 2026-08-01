namespace Deck.Core.Audio;

/// <summary>
/// Where the meter changes colour, and where the coaching verdict changes with it (B1).
/// <para>
/// One set of numbers, because the colour of the bar and the words beside it are one decision. They
/// used to be written out three times - in the drawn control, in the live level meter, and in the
/// sound check - and three copies of a threshold is three chances for the bar to turn amber while
/// the verdict still says the level is good. The meter is meant to teach, and a colour that
/// disagrees with the words next to it teaches the wrong thing.
/// </para>
/// <para>
/// The boundaries themselves are chosen for speech and music heading into a lossy encoder: aim for
/// peaks around -12 to -6 dBFS, which leaves headroom without sounding thin. Widening the amber and
/// red parts of the scale was tried twice and reverted both times - once by moving these numbers,
/// which quietly made Deck warn three decibels earlier than it ever had, and once by painting the
/// scale on different boundaries from the verdict, which split one idea into two. Neither was worth
/// what it cost, and both are recorded here so the third attempt starts better informed.
/// </para>
/// </summary>
public static class MeterZones
{
    /// <summary>Below this there is not enough signal to broadcast.</summary>
    public const float QuietDb = -24f;

    /// <summary>From here up, ease off - the amber part of the scale.</summary>
    public const float LoudDb = -4f;

    /// <summary>From here up, it is about to clip - the red part of the scale.</summary>
    public const float ClipDb = -1f;

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
