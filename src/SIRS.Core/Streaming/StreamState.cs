using Sirs.Core.Localisation;

namespace Sirs.Core.Streaming;

/// <summary>
/// The connection states the user sees (H3). Kept deliberately small: anything more granular is
/// detail the broadcaster does not need while they are on air.
/// </summary>
public enum StreamState
{
    Idle,
    Connecting,
    Live,
    Reconnecting,
    Failed,
}

public static class StreamStateInfo
{
    public static string Headline(this StreamState state) => state switch
    {
        StreamState.Idle => Strings.Get(StringId.StateIdle),
        StreamState.Connecting => Strings.Get(StringId.StateConnecting),
        StreamState.Live => Strings.Get(StringId.StateLive),
        StreamState.Reconnecting => Strings.Get(StringId.StateReconnecting),
        StreamState.Failed => Strings.Get(StringId.StateFailed),
        _ => state.ToString(),
    };

    public static bool IsBusy(this StreamState state) =>
        state is StreamState.Connecting or StreamState.Reconnecting;

    /// <summary>True while SIRS is encoding, whether or not the network is currently up.</summary>
    public static bool IsBroadcasting(this StreamState state) =>
        state is StreamState.Connecting or StreamState.Live or StreamState.Reconnecting;
}
