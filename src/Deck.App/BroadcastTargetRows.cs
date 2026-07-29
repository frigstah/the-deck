using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.App;

/// <summary>
/// One row in the "also send to" list (C12): a saved server and whether it joins the broadcast.
/// The primary server never appears here - it is the one already chosen above.
/// </summary>
public sealed class ExtraTargetRow(ServerProfile profile, bool isSelected, Action onChanged) : ObservableObject
{
    private bool _isSelected = isSelected;

    public ServerProfile Profile { get; } = profile;

    public string Name => Profile.Name;

    /// <summary>The quality this server would get, since running two bitrates is half the point.</summary>
    public string Detail => Profile.Encoder.Summary;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) onChanged();
        }
    }
}

/// <summary>
/// A live destination as the status list shows it: name, state, and whatever the server last said.
/// Only shown once a broadcast has more than one destination, so the ordinary case stays uncluttered.
/// </summary>
public sealed class TargetStatusRow(BroadcastTarget target)
{
    public string Name => target.IsPrimary ? target.Name : $"{target.Name} (backup)";

    public string State => target.State.Headline();

    public string Detail => target.Connection.LastError
        ?? target.Connection.ConnectionNote
        ?? target.Settings.Summary;

    public bool IsLive => target.State == StreamState.Live;

    public Brush StateBrush => target.State switch
    {
        StreamState.Live => Resource("LiveBrush"),
        StreamState.Connecting or StreamState.Reconnecting => Resource("WarnBrush"),
        StreamState.Failed => Resource("BadBrush"),
        _ => Resource("MutedTextBrush"),
    };

    private static Brush Resource(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
