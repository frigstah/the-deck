using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Deck.Core.Audio;

/// <summary>
/// The programs currently playing sound on this machine (A9), so one of them can be picked as a
/// source by name rather than by process id.
/// <para>
/// Read from the audio sessions on the default output - the same list Windows' own volume mixer
/// shows, which means what Deck offers is what the user already recognises. A program with no session
/// is not offered: if Windows does not think it is playing anything, Deck asking for its audio would
/// get silence and no explanation.
/// </para>
/// </summary>
public static class AudioProcesses
{
    /// <summary>
    /// Programs that could be captured, most recently useful first. Empty on a version of Windows that
    /// cannot do it at all, so nothing is offered that cannot work.
    /// </summary>
    public static IReadOnlyList<AudioDevice> Playing()
    {
        if (!ProcessLoopbackCapture.IsSupported) return [];

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // Every output, not just the default: a backing track playing to headphones while the
            // system default is the speakers is exactly the karaoke setup this is for.
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    Collect(device, found);
                }
            }
        }
        catch (Exception)
        {
            // No audio service, no sessions, or a driver that will not enumerate. Not worth surfacing:
            // the picker simply offers no programs.
            return [];
        }

        return found
            .OrderBy(entry => entry.Value, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new AudioDevice(
                ProcessLoopbackCapture.IdFor(entry.Key),
                entry.Value,
                AudioDeviceKind.Process,
                IsSystemDefault: false))
            .ToList();
    }

    /// <summary>
    /// A program by name, whether or not it is making a noise right now.
    /// <para>
    /// Needed because <see cref="Playing"/> only lists programs Windows says are playing, and a saved
    /// choice has to survive the backing-track player being paused - or closed between shows. Without
    /// this, refreshing the list while KaraFun was quiet would silently move the second source to
    /// something the user never picked, which they would discover on stage.
    /// </para>
    /// </summary>
    public static AudioDevice Named(string programNameOrId)
    {
        var name = ProcessLoopbackCapture.ProgramNameFrom(programNameOrId);
        var running = ResolveProcessId(name);

        var label = running is { } pid ? Friendly(pid, name) : $"{name} — not playing now";

        return new AudioDevice(ProcessLoopbackCapture.IdFor(name), label, AudioDeviceKind.Process, IsSystemDefault: false);
    }

    private static void Collect(MMDevice device, Dictionary<string, string> found)
    {
        var sessions = device.AudioSessionManager.Sessions;
        if (sessions is null) return;

        var self = Environment.ProcessId;

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];

            // Windows' own beeps and alerts share one session that belongs to no process worth naming.
            if (session.IsSystemSoundsSession) continue;

            var pid = (int)session.GetProcessID;
            if (pid == 0 || pid == self) continue;

            var name = ProgramName(pid);
            if (name is null) continue;

            // Keyed by executable name, so a browser's several processes appear once. The friendliest
            // name wins: one of them usually has a window title and the rest do not.
            var friendly = Friendly(pid, name);

            if (!found.TryGetValue(name, out var existing) || friendly.Length > existing.Length)
            {
                found[name] = friendly;
            }
        }
    }

    /// <summary>
    /// Which process id to actually capture for a saved program name.
    /// <para>
    /// A pid cannot be stored, so it has to be found again at the moment capture starts. Preference
    /// goes to a process that Windows says is playing - that is the one the user chose in the picker -
    /// and then to the earliest-started process of that name, which for a program that splits itself
    /// across several processes is the parent. The parent is the right target because capture includes
    /// the process tree: aim at the child and a later child gets missed.
    /// </para>
    /// </summary>
    public static int? ResolveProcessId(string programName)
    {
        var wanted = ProcessLoopbackCapture.ProgramNameFrom(programName);

        var playing = PlayingIds(wanted);
        if (playing.Count > 0) return Earliest(playing);

        var running = SafeGetProcessesByName(wanted);
        return running.Count > 0 ? Earliest(running) : null;
    }

    private static List<int> PlayingIds(string programName)
    {
        var ids = new List<int>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    if (sessions is null) continue;

                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        if (session.IsSystemSoundsSession) continue;

                        var pid = (int)session.GetProcessID;
                        if (pid == 0) continue;

                        if (string.Equals(ProgramName(pid), programName, StringComparison.OrdinalIgnoreCase))
                        {
                            ids.Add(pid);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the process list.
        }

        return ids;
    }

    private static List<int> SafeGetProcessesByName(string programName)
    {
        try
        {
            return Process.GetProcessesByName(programName).Select(p =>
            {
                using (p) return p.Id;
            }).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static int Earliest(List<int> ids)
    {
        var best = ids[0];
        var bestStart = DateTime.MaxValue;

        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.StartTime < bestStart)
                {
                    bestStart = process.StartTime;
                    best = id;
                }
            }
            catch (Exception)
            {
                // Exited between the enumeration and now, or protected. Leave it out of the running.
            }
        }

        return best;
    }

    private static string? ProgramName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The name to show. A window title says far more than an executable name - "KaraFun Player" beats
    /// "karafun" - but it changes with whatever is on screen, so the executable name is what gets
    /// stored and this is only what gets drawn.
    /// </summary>
    private static string Friendly(int pid, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception)
        {
            // A protected or 32-bit-versus-64-bit process will not open its module. The name will do.
        }

        return processName;
    }
}
