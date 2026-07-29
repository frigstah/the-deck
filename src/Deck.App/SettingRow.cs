using System.Windows;
using System.Windows.Controls;

namespace Deck.App;

/// <summary>
/// One setting: what it is on the left, the control that changes it on the right, a hairline under.
/// <para>
/// This is the whole of the pane language. The panes inherited from the rail layout put a label
/// above each control in a single left-hand column, which is how a form is built - and a form reads
/// as something to be filled in, top to bottom, whether or not you care about any of it. A list of
/// rows reads as something to be scanned: the eye runs down the left edge looking for the one thing
/// it came for, and every control lines up on the right where the hand already is.
/// </para>
/// <para>
/// A control rather than a style because a style cannot add column definitions, and because there
/// are around sixty of these. Written once, every row is identically spaced and ruled; written as
/// markup sixty times, they would not be.
/// </para>
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ContentWidthProperty = DependencyProperty.Register(
        nameof(ContentWidth), typeof(double), typeof(SettingRow), new PropertyMetadata(260.0));

    public static readonly DependencyProperty ShowRuleProperty = DependencyProperty.Register(
        nameof(ShowRule), typeof(bool), typeof(SettingRow), new PropertyMetadata(true));

    /// <summary>What the setting is, in the words a broadcaster would use for it.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// The sentence under the label. Optional, and deliberately so: a hint on every row is a wall of
    /// grey text that stops being read. It is for the settings whose consequence is not obvious.
    /// </summary>
    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>
    /// How much of the row the control takes. Pickers and sliders need a fixed width to line up down
    /// the right edge; a switch or a button wants only the space it asks for, so those set it small.
    /// </summary>
    public double ContentWidth
    {
        get => (double)GetValue(ContentWidthProperty);
        set => SetValue(ContentWidthProperty, value);
    }

    /// <summary>Off for the last row in a group, so a rule never sits directly above a heading.</summary>
    public bool ShowRule
    {
        get => (bool)GetValue(ShowRuleProperty);
        set => SetValue(ShowRuleProperty, value);
    }
}
