namespace Deck.Core.Streaming;

/// <summary>What came back when the server was asked how many people are listening.</summary>
public enum ListenerStatus
{
    /// <summary>The server said. The number is the number, including when it is nought.</summary>
    Counted,

    /// <summary>The server answered but does not publish the figure for this mount.</summary>
    NotPublished,

    /// <summary>Nothing answered - the address, the port or the network.</summary>
    Unreachable,

    /// <summary>Deck does not know how to ask this kind of server.</summary>
    Unsupported,
}

/// <summary>
/// A listener count, or the reason there is not one (H4).
/// <para>
/// This exists because a bare number could not tell two very different things apart. "Nought people
/// are listening" is news about the show. "This server does not publish a listener count" is news
/// about the server, and it used to look identical: nothing on screen, no explanation, and no way to
/// tell which of the two it was. That was the whole complaint - a station owner watching an empty
/// space cannot know whether to worry.
/// </para>
/// <para>
/// <see cref="Detail"/> is a sentence for a person, not a code. It names what was tried, because
/// "your host publishes nothing on the public endpoints" is the only useful thing Deck can say when
/// it genuinely cannot find out.
/// </para>
/// </summary>
public readonly record struct ListenerReport(ListenerStatus Status, int Count, string Detail)
{
    public static ListenerReport Counted(int count, string detail) =>
        new(ListenerStatus.Counted, Math.Max(0, count), detail);

    public static ListenerReport NotPublished(string detail) => new(ListenerStatus.NotPublished, 0, detail);

    public static ListenerReport Unreachable(string detail) => new(ListenerStatus.Unreachable, 0, detail);

    public static ListenerReport Unsupported(string detail) => new(ListenerStatus.Unsupported, 0, detail);

    /// <summary>The count when there is one, and null when the number would be a guess.</summary>
    public int? Value => Status == ListenerStatus.Counted ? Count : null;

    public bool Known => Status == ListenerStatus.Counted;
}

/// <summary>
/// One figure for a broadcast going to several places at once (C12, H4).
/// <para>
/// Separated from the querying so the arithmetic can be checked without a server, because it is the
/// part with a judgement in it rather than a protocol.
/// </para>
/// </summary>
public static class ListenerTally
{
    public static ListenerReport Combine(IReadOnlyList<ListenerReport> reports)
    {
        if (reports.Count == 0) return ListenerReport.NotPublished("Nothing is on air.");

        var total = 0;
        var counted = 0;
        var firstDecline = -1;
        var firstUnreachable = -1;

        for (var i = 0; i < reports.Count; i++)
        {
            if (reports[i].Known)
            {
                total += reports[i].Count;
                counted++;
                continue;
            }

            if (firstDecline < 0) firstDecline = i;
            if (firstUnreachable < 0 && reports[i].Status == ListenerStatus.Unreachable) firstUnreachable = i;
        }

        // Nobody would say. Pass on a real explanation rather than inventing a summary of nothing, and
        // prefer "could not be reached" over "does not publish it": one is worth acting on.
        if (counted == 0) return reports[firstUnreachable >= 0 ? firstUnreachable : firstDecline];

        // Summed across the destinations that answer. Someone listening to the backup relay is still a
        // listener, and a server that stays silent must not drag the total to nought - but a total that
        // is short has to say so, or it reads as everybody.
        var silent = reports.Count - counted;

        return ListenerReport.Counted(total, silent == 0
            ? counted == 1
                ? "Counted on the server you are broadcasting to."
                : $"Counted on all {counted} servers you are broadcasting to."
            : $"Counted on {counted} of the {reports.Count} servers you are broadcasting to; " +
              $"{(silent == 1 ? "the other one does" : $"the other {silent} do")} not publish a figure, " +
              "so the real number may be higher.");
    }
}
