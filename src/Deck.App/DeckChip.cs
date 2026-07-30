using System.Windows;

namespace Deck.App;

/// <summary>
/// Settings for the pickers that live on the deck's chip row.
/// </summary>
/// <remarks>
/// <para>
/// An attached property rather than a second copy of <c>DeckChipComboStyle</c>, because the two
/// variants differ by one element out of thirty - the popup, the placement, the chevron and the
/// disabled handling are all the same, and a duplicated template is a second thing to remember
/// when one of them changes.
/// </para>
/// </remarks>
public static class DeckChip
{
    /// <summary>
    /// Whether a chip's picker shows the chosen value when closed, or only the chip's own name.
    /// <para>
    /// True is the obvious behaviour and stays the default. False exists because a value's width is
    /// whatever the value happens to be: a device called "IN 1-8 (BEHRINGER X-AIR) - default" made
    /// the input chip three times the width of any other, and the row reflowed onto a second line as
    /// soon as anything grew. A chip that says "Input" is the same width forever, and the row is a
    /// row. What is chosen is one click away, ticked in the list.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty ShowsValueProperty = DependencyProperty.RegisterAttached(
        "ShowsValue",
        typeof(bool),
        typeof(DeckChip),
        new PropertyMetadata(true));

    public static void SetShowsValue(DependencyObject element, bool value) =>
        element.SetValue(ShowsValueProperty, value);

    public static bool GetShowsValue(DependencyObject element) =>
        (bool)element.GetValue(ShowsValueProperty);
}
