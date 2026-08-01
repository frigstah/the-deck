namespace Deck.Core.Audio;

/// <summary>
/// Where the meter changes colour, and where the coaching verdict changes (B1). Two questions, and
/// the difference between them is the whole of this file.
/// <para>
/// The <em>verdict</em> is a judgement: at this level, is the show all right? Those are the numbers
/// that decide whether Deck tells somebody to ease off, and they are not to be moved for the look of
/// the thing. They used to be written out three times - in the drawn control, in the live meter, and
/// in the sound check - which is three chances for them to drift apart, so they live here now.
/// </para>
/// <para>
/// The <em>band</em> is the scale, and a scale is orientation rather than judgement. Every meter on
/// every desk has its top end painted amber and red, and it is painted before the level is actually
/// a problem: that is what tells you at a glance how much room is left. A bar that stays green until
/// the instant something is wrong has no way of saying "getting close", which is the one thing a
/// meter is for.
/// </para>
/// <para>
/// So the paint runs ahead of the words on purpose, and never the other way round - amber can appear
/// while the verdict still says the level is good, but the verdict can never say a level is hot
/// while the bar under it is still green. <c>MeterChecks</c> holds that one-way rule.
/// </para>
/// </summary>
public static class MeterZones
{
    /// <summary>Below this there is not enough signal to broadcast.</summary>
    public const float QuietDb = -24f;

    /// <summary>From here up, the coaching says the level is getting hot.</summary>
    public const float LoudDb = -4f;

    /// <summary>From here up, the coaching calls it clipping.</summary>
    public const float ClipDb = -1f;

    /// <summary>Quieter than this and nothing is arriving at all.</summary>
    public const float NoSignalDb = -55f;

    /// <summary>
    /// Where the amber is painted. Lower than <see cref="LoudDb"/>, so the top of the scale is
    /// legible as a caution band rather than as a couple of segments that only appear once it is too
    /// late to use them.
    /// </summary>
    public const float BandLoudDb = -7f;

    /// <summary>Where the red is painted, for the same reason.</summary>
    public const float BandClipDb = -2.5f;

    /// <summary>
    /// What Deck thinks of this level. The verdict beside the bar, and the advice under it, both come
    /// from here.
    /// </summary>
    public static MeterZone Zone(float db) => db switch
    {
        >= ClipDb => MeterZone.Clip,
        >= LoudDb => MeterZone.Loud,
        >= QuietDb => MeterZone.Good,
        _ => MeterZone.Quiet,
    };

    /// <summary>
    /// What colour this part of the scale is painted. Only the drawn meter asks this; nothing that
    /// reaches a conclusion about the show does.
    /// </summary>
    public static MeterZone Band(float db) => db switch
    {
        >= BandClipDb => MeterZone.Clip,
        >= BandLoudDb => MeterZone.Loud,
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
