using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sirs.App;

/// <summary>
/// Keeps a window that draws its own title bar from losing its edges when it is maximised.
/// <para>
/// A maximised window is deliberately positioned by Windows so that its resize border falls outside
/// the screen and its client area exactly fills the work area - eight pixels past every edge on this
/// machine. That is invisible on an ordinary window, whose outermost eight pixels are frame that
/// nobody draws in. It is not invisible here: with WindowChrome the client area is the whole window,
/// so the right of the close button and the bottom of the status strip go off the screen, measured
/// rather than guessed at.
/// </para>
/// <para>
/// Answering WM_GETMINMAXINFO with the work area looks like the tidier fix and was tried first. The
/// window reports the corrected limits and is then repositioned anyway, so it changes nothing except
/// the amount of code. What does work is to give the content the same margin as the overhang.
/// </para>
/// <para>
/// The overhang is measured each time rather than assumed to be eight, because it is a system metric
/// that scales with the display: a second monitor at a different DPI has a different one, and the
/// window can be maximised onto either.
/// </para>
/// </summary>
internal sealed class MaximiseBounds
{
    private const uint MonitorDefaultToNearest = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private readonly Window _window;
    private readonly FrameworkElement _content;

    private MaximiseBounds(Window window, FrameworkElement content)
    {
        _window = window;
        _content = content;
    }

    /// <summary>
    /// Call once the window has a handle and its content is built. The window's own content is what
    /// gets the margin, so a window only has to say that it wants this - it does not have to name a
    /// particular element, and cannot name the wrong one.
    /// </summary>
    public static void Keep(Window window)
    {
        if (window.Content is not FrameworkElement content) return;

        var fit = new MaximiseBounds(window, content);
        window.StateChanged += (_, _) => fit.Apply();
        fit.Apply();
    }

    private void Apply()
    {
        if (_window.WindowState != WindowState.Maximized)
        {
            _content.Margin = default;
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var window)) return;

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        // Everything above is in physical pixels and a margin is not, so the difference has to be
        // scaled back before it can be used.
        var toDip = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformFromDevice
                    ?? System.Windows.Media.Matrix.Identity;

        _content.Margin = new Thickness(
            Overhang(info.Work.Left - window.Left) * toDip.M11,
            Overhang(info.Work.Top - window.Top) * toDip.M22,
            Overhang(window.Right - info.Work.Right) * toDip.M11,
            Overhang(window.Bottom - info.Work.Bottom) * toDip.M22);
    }

    /// <summary>Only ever pushes content inwards; a window inside the work area needs no margin.</summary>
    private static double Overhang(int pixels) => Math.Max(0, pixels);
}
