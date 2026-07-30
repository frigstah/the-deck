namespace Deck.Core.Streaming;

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

public static class StreamFailureInfo
{
    /// <summary>
    /// The failure in a few words, for somebody glancing at the deck mid-show.
    /// <para>
    /// Separate from the message because they answer different questions. The message says what to
    /// go and do about it and runs to a sentence or two; this says which of the handful of things
    /// went wrong, and has to be readable at the speed of a glance. Wrong password, stream already
    /// taken and server not answering are three completely different evenings, and a broadcaster who
    /// is on air needs to know which one they are having before they read anything.
    /// </para>
    /// <para>
    /// Every value is spelled out and there is no fallback arm, so a failure kind added later will
    /// not quietly inherit a vague line - the check suite fails until somebody writes its words.
    /// </para>
    /// </summary>
    public static string Headline(this StreamFailure failure) => failure switch
    {
        StreamFailure.Authentication => "The password was refused",
        StreamFailure.MountInUse => "Something else is already broadcasting",
        StreamFailure.Network => "The server is not answering",
        StreamFailure.Tls => "The secure connection failed",
        StreamFailure.FormatRejected => "The server will not accept this format",
        StreamFailure.Protocol => "The server answered in a way Deck did not expect",
        _ => "The connection failed",
    };

    /// <summary>
    /// Whether waiting could fix this on its own. A refused password never comes right by itself and
    /// Deck stops; a busy stream or a server that is down might, so it keeps trying - and the
    /// difference is worth saying out loud, because "still trying" and "given up" ask different
    /// things of the person reading it.
    /// </summary>
    public static bool WorthRetrying(this StreamFailure failure) =>
        failure is not StreamFailure.Authentication;
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
