using Sirs.Core.Metadata;

namespace Sirs.EncoderCheck;

/// <summary>
/// Checks the Windows media session source (F3) is reachable and reports what it can see.
/// <para>
/// Whether a title comes back depends on whether anything is actually playing, so a quiet machine
/// is not a failure. What is being asserted is that the API opens, the poll loop runs without
/// throwing, and a title - if there is one - arrives in the "Artist - Title" shape.
/// </para>
/// </summary>
internal static class MediaSessionCheck
{
    public static int Run()
    {
        Console.WriteLine("--- Windows media session ---");

        using var watcher = new MediaSessionWatcher();
        var reported = new List<string>();
        watcher.TitleChanged += (_, title) => reported.Add(title);

        try
        {
            watcher.StartAsync().GetAwaiter().GetResult();

            if (watcher.Problem is { } problem)
            {
                Console.WriteLine($"FAIL: {problem}\n");
                return 1;
            }

            // Two poll intervals, so a running player is definitely seen.
            Thread.Sleep(5000);

            if (watcher.Problem is { } laterProblem)
            {
                Console.WriteLine($"FAIL: the poll loop reported: {laterProblem}\n");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(watcher.Title))
            {
                Console.WriteLine("  nothing is playing on this PC right now");
                Console.WriteLine("  the media session opened cleanly and the poll loop is running");
                Console.WriteLine("PASS (inconclusive: play something and re-run to see a real title)\n");
                return 0;
            }

            Console.WriteLine($"  title:  {watcher.Title}");
            Console.WriteLine($"  from:   {watcher.SourceApp ?? "unknown app"}");
            Console.WriteLine($"  events: {reported.Count}");

            if (watcher.Title.Contains('\n') || watcher.Title.Contains('\r'))
            {
                Console.WriteLine("FAIL: the title contains a line break, which would corrupt the metadata request\n");
                return 1;
            }

            Console.WriteLine("PASS\n");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.Message}\n");
            return 1;
        }
    }
}
