using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sirs.App;

/// <summary>
/// System-wide hotkeys (I3). These work while another program has focus, which is the whole point:
/// going live or muting yourself should not require finding the SIRS window first.
/// <para>
/// Registration can fail if another program already owns the combination. That is reported rather
/// than swallowed, because a hotkey the user believes in but that does nothing is worse than none.
/// </para>
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;

    [Flags]
    private enum Modifiers : uint
    {
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int ToggleBroadcastId = 1;
    private const int ToggleMuteId = 2;

    private readonly Dictionary<int, Action> _actions = [];
    private readonly List<int> _registered = [];
    private HwndSource? _source;
    private IntPtr _handle;

    public string? Problem { get; private set; }

    /// <summary>Human-readable description of the bindings, for the UI to show.</summary>
    public static string Description => "Ctrl+Shift+G goes live or stops. Ctrl+Shift+M mutes the microphone.";

    public void Attach(Window window, Action toggleBroadcast, Action toggleMute)
    {
        var helper = new WindowInteropHelper(window);
        _handle = helper.Handle;

        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(HandleMessage);

        _actions[ToggleBroadcastId] = toggleBroadcast;
        _actions[ToggleMuteId] = toggleMute;

        var failures = new List<string>();

        // G for "go live", M for "mute". Ctrl+Shift keeps them clear of ordinary typing.
        if (!Register(ToggleBroadcastId, Modifiers.Control | Modifiers.Shift, 'G')) failures.Add("Ctrl+Shift+G");
        if (!Register(ToggleMuteId, Modifiers.Control | Modifiers.Shift, 'M')) failures.Add("Ctrl+Shift+M");

        if (failures.Count > 0)
        {
            Problem = $"SIRS could not claim {string.Join(" or ", failures)} — another program is already using it.";
        }
    }

    private bool Register(int id, Modifiers modifiers, char key)
    {
        if (!RegisterHotKey(_handle, id, (uint)modifiers, key)) return false;

        _registered.Add(id);
        return true;
    }

    private IntPtr HandleMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        if (_actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered) UnregisterHotKey(_handle, id);
        _registered.Clear();

        _source?.RemoveHook(HandleMessage);
        _source = null;
    }
}
