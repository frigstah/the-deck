namespace Sirs.Core.Streaming;

/// <summary>Which stage of the handshake failed, so the tester can point at the right field.</summary>
public enum StreamFailure
{
    Network,
    Tls,
    Authentication,
    MountInUse,
    FormatRejected,
    Protocol,
}

/// <summary>
/// A connection failure carrying a message written for a broadcaster rather than a developer
/// (design principle 3). <see cref="Exception.Message"/> is safe to show in the UI verbatim.
/// </summary>
public sealed class StreamException(StreamFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public StreamFailure Failure { get; } = failure;

    /// <summary>The field the user most likely needs to correct, if there is an obvious one.</summary>
    public string? FieldHint => Failure switch
    {
        StreamFailure.Authentication => "Password",
        StreamFailure.MountInUse => "Stream address",
        StreamFailure.Network => "Server address",
        StreamFailure.Tls => "Secure connection",
        _ => null,
    };
}
