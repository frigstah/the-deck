using System.Windows;

namespace Deck.App;

/// <summary>
/// Settings for the pickers that make up the deck's chip row.
/// </summary>
/// <remarks>
/// <para>
/// An attached property rather than a second copy of <c>DeckChipComboStyle</c>: the variants differ by
/// one element out of thirty, and a duplicated template is a second thing to remember when one of
/// them changes.
/// </para>
/// </remarks>
public static class DeckChip
{
    /// <summary>
    /// What the chip shows after its label when it is closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A string from the view model, rather than the picker's own selection box. The closed chip and
    /// the open list want to say different things about the same selection: the list has room for
    /// "IN 1-8 (BEHRINGER X-AIR) — default" and needs it to tell devices apart, while the chip has
    /// room for "IN 1-8" and would otherwise decide the width of the whole row. Letting the view
    /// model name the short form also puts the shortening rule somewhere it can be checked, instead
    /// of in a template.
    /// </para>
    /// <para>
    /// Unset or empty collapses the value, and the chip carries only its label.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.RegisterAttached(
        "ValueText",
        typeof(string),
        typeof(DeckChip),
        new PropertyMetadata(null));

    public static void SetValueText(DependencyObject element, string? value) =>
        element.SetValue(ValueTextProperty, value);

    public static string? GetValueText(DependencyObject element) =>
        (string?)element.GetValue(ValueTextProperty);
}
