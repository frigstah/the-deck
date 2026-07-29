using Sirs.Core.Localisation;

namespace Sirs.Core.Audio;

/// <summary>
/// The traffic-light verdict on the input level. This is the thing users read instead of
/// interpreting dBFS themselves, so the wording is part of the feature.
/// </summary>
public enum LevelAdvice
{
    NoSignal,
    TooQuiet,
    Good,
    Loud,
    Clipping,
}

public enum AdviceSeverity
{
    Neutral,
    Ok,
    Warning,
    Bad,
}

public static class LevelAdviceText
{
    public static string Headline(this LevelAdvice advice) => advice switch
    {
        LevelAdvice.NoSignal => Strings.Get(StringId.AdviceNoSignal),
        LevelAdvice.TooQuiet => Strings.Get(StringId.AdviceTooQuiet),
        LevelAdvice.Good => Strings.Get(StringId.AdviceGood),
        LevelAdvice.Loud => Strings.Get(StringId.AdviceLoud),
        LevelAdvice.Clipping => Strings.Get(StringId.AdviceClipping),
        _ => string.Empty,
    };

    public static string Hint(this LevelAdvice advice) => advice switch
    {
        LevelAdvice.NoSignal => Strings.Get(StringId.AdviceNoSignalHint),
        LevelAdvice.TooQuiet => Strings.Get(StringId.AdviceTooQuietHint),
        LevelAdvice.Good => Strings.Get(StringId.AdviceGoodHint),
        LevelAdvice.Loud => Strings.Get(StringId.AdviceLoudHint),
        LevelAdvice.Clipping => Strings.Get(StringId.AdviceClippingHint),
        _ => string.Empty,
    };

    public static AdviceSeverity Severity(this LevelAdvice advice) => advice switch
    {
        LevelAdvice.NoSignal => AdviceSeverity.Neutral,
        LevelAdvice.TooQuiet => AdviceSeverity.Warning,
        LevelAdvice.Good => AdviceSeverity.Ok,
        LevelAdvice.Loud => AdviceSeverity.Warning,
        LevelAdvice.Clipping => AdviceSeverity.Bad,
        _ => AdviceSeverity.Neutral,
    };
}
