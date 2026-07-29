using Deck.Core.Control;

namespace Deck.App;

/// <summary>
/// One row in the MIDI list (I11): a thing Deck can do, and which control on the desk does it.
/// <para>
/// Rebuilt rather than mutated whenever a binding changes. The list is six rows long and only
/// changes when someone presses Learn, so replacing it costs nothing and removes any chance of a
/// row showing a binding that has since been reassigned.
/// </para>
/// </summary>
public sealed class MidiBindingRow(
    MidiAction action,
    string? assignment,
    RelayCommand learnCommand,
    RelayCommand clearCommand)
{
    public MidiAction Action { get; } = action;

    public string Label { get; } = action.Label();

    public string Assignment { get; } = assignment ?? "Not set";

    public bool HasAssignment { get; } = assignment is not null;

    public RelayCommand LearnCommand { get; } = learnCommand;

    public RelayCommand ClearCommand { get; } = clearCommand;
}
