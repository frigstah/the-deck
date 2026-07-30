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

    public const string PlayingNow = "One program on this PC";

    public const string AlsoOpen = "Other programs you have open";

    /// <summary>
    /// Programs that are open but not currently making a noise.
    /// <para>
    /// This exists because of the obvious question, asked the moment the feature shipped: "why can't I
    /// see Chrome?" Chrome was running with a tab open, and Windows had no audio session for it at all -
    /// a browser tears its output stream down when nothing is playing, so there was nothing to list.
    /// Offering only what Windows says is playing means the answer to "capture my browser" is "first go
    /// and make it play something, then come back", which is not an answer.
    /// </para>
    /// <para>
    /// Capturing a program that is not playing yet works: the stream delivers silence until the program
    /// starts, which is exactly right - you set it up first and press play afterwards.
    /// </para>
    /// </summary>
    public static IReadOnlyList<AudioDevice> Open()
    {
        if (!ProcessLoopbackCapture.IsSupported) return [];

        var playing = Playing()
            .Select(d => ProcessLoopbackCapture.ProgramNameFrom(d.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var self = Environment.ProcessId;

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == self) continue;

                        // A visible window is the test for "a program the user thinks of as open". It
                        // keeps out the hundred services and background hosts, without Deck having to
                        // keep a list of which names are boring - a list that would be wrong the moment
                        // somebody wanted one of them.
                        if (process.MainWindowHandle == IntPtr.Zero) continue;
                        if (string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;

                        var name = process.ProcessName;
                        if (playing.Contains(name) || found.ContainsKey(name)) continue;

                        found[name] = Friendly(process.Id, name);
                    }
                    catch (Exception)
                    {
                        // Exited while being looked at, or protected. Not offerable either way.
                    }
                }
            }
        }
        catch (Exception)
        {
            return [];
        }

        return found
            .OrderBy(entry => entry.Value, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new AudioDevice(
                ProcessLoopbackCapture.IdFor(entry.Key),
                entry.Value,
                AudioDeviceKind.Process,
                IsSystemDefault: false,
                Category: AlsoOpen))
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
        // The session's own process first, then the earliest process of the same name. That second try
        // is what makes a browser read properly: the audio comes from a sandboxed child whose module
        // cannot be opened, while the browser process it belongs to says "Google Chrome" quite happily.
        if (Describe(pid) is { } fromSession) return fromSession;

        foreach (var id in SafeGetProcessesByName(processName).OrderBy(id => id))
        {
            if (Describe(id) is { } fromSibling) return fromSibling;
        }

        return processName;
    }

    private static string? Describe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception)
        {
            // Protected, or a different bitness. There is another way in.
        }

        // Reading a process's module list needs permission a browser will not grant; asking Windows for
        // its image path needs far less, and the file itself carries the name. This is the difference
        // between "chrome" and "Google Chrome" in the picker.
        return FromImagePath(pid);
    }

    private static string? FromImagePath(int pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            var size = buffer.Capacity;

            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return null;

            var description = System.Diagnostics.FileVersionInfo.GetVersionInfo(buffer.ToString()).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const int QueryLimitedInformation = 0x1000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
