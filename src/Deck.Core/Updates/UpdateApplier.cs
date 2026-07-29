using System.Diagnostics;

namespace Deck.Core.Updates;

/// <summary>
/// The other half of an update: the new build, run out of the staging folder, waiting for the old
/// one to let go of its files so it can copy itself over the top.
/// <para>
/// A program cannot overwrite the executable it is running from, so the swap has to be done by
/// something else. That something is this same binary from the new download, started by the old
/// copy just before it quits — which means nothing extra has to be shipped, and the code doing the
/// copying is the code that was just verified against its checksum.
/// </para>
/// </summary>
public static class UpdateApplier
{
    /// <summary>
    /// Never copied over an existing install. It is the file that decides whether settings live
    /// beside the executable or in %APPDATA%, so carrying it across would silently move a normal
    /// install's servers and settings somewhere the user never put them — and carrying its absence
    /// across would do the same to a portable one. The install keeps whichever it already had.
    /// </summary>
    private const string PortableMarker = "deck-portable.txt";

    /// <summary>How long to wait for the old copy to exit before giving up on it.</summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    public sealed record Request(string Target, int WaitForProcessId);

    /// <summary>
    /// Reads <c>--apply-update --target &lt;dir&gt; --wait &lt;pid&gt;</c>, or null for an ordinary
    /// start. Deliberately absent from the help text: it is Deck talking to itself, not a command
    /// anyone should be typing.
    /// </summary>
    public static Request? Parse(string[] args)
    {
        if (!args.Any(a => a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase))) return null;

        var target = Value(args, "--target");
        var wait = Value(args, "--wait");

        if (string.IsNullOrWhiteSpace(target)) return null;

        return new Request(target, int.TryParse(wait, out var pid) ? pid : 0);
    }

    /// <summary>
    /// Waits, copies, and starts the new build. Returns a message if it failed; on success the
    /// process should simply exit.
    /// </summary>
    public static string? Apply(Request request)
    {
        var source = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var target = request.Target.TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return "The update was started from the folder it is meant to replace.";
        }

        if (!WaitForExit(request.WaitForProcessId))
        {
            // Not attempted. Copying over a running program means most files fail and a few
            // succeed, which is the one outcome worse than not updating at all.
            return "Deck did not close, so the update was not installed. " +
                   "Nothing has been changed. Close Deck and try again.";
        }

        try
        {
            Copy(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Half-copied is the worst outcome, so say so plainly rather than starting anyway.
            return $"The update could not be finished: {ex.Message}\n\n" +
                   $"Deck in {target} may be incomplete. Reinstall it from the release page.";
        }

        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(target, "Deck.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = target,
            });

            return null;
        }
        catch (Exception ex)
        {
            return $"The update was installed but Deck would not restart: {ex.Message}";
        }
    }

    /// <summary>
    /// Waits for the old copy to go. False means it is still running — never killed, because it
    /// might be a station that is still on air, and a stuck update is a far smaller problem than a
    /// broadcast cut off by its own encoder.
    /// </summary>
    private static bool WaitForExit(int processId)
    {
        if (processId <= 0) return true;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)ExitTimeout.TotalMilliseconds)) return false;
        }
        catch (ArgumentException)
        {
            // Already gone, which is the case this is waiting for.
        }
        catch (InvalidOperationException)
        {
            // Same.
        }

        // Windows releases file locks a moment after a process exits, not at the instant.
        Thread.Sleep(600);
        return true;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);

            if (Path.GetFileName(relative).Equals(PortableMarker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            CopyWithRetry(file, destination);
        }
    }

    /// <summary>
    /// A file can still be locked for a second or two after the process holding it exits, and an
    /// antivirus scanner will happily hold one for longer. Retrying beats failing an update on a
    /// timing accident.
    /// </summary>
    private static void CopyWithRetry(string source, string destination)
    {
        const int attempts = 8;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(250 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(250 * attempt);
            }
        }
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }
}
