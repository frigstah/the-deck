// Qualified throughout: the project uses WinForms as well (for the tray icon and folder pickers),
// and both frameworks have a UserControl and a Window.
using System.Windows;
using System.Windows.Automation;

namespace Sirs.App;

/// <summary>
/// A drawn title bar for SIRS's dialogs, so they do not sit under a white system caption while the
/// main window has none.
/// <para>
/// Everything is worked out from the window it finds itself in: which buttons make sense comes from
/// the window's <see cref="Window.ResizeMode"/>, exactly as Windows decides it for a real title bar,
/// and the maximised-bounds correction is applied here too so that a window only has to place this
/// control to get the whole treatment.
/// </para>
/// </summary>
public partial class WindowTitleBar : System.Windows.Controls.UserControl
{
    private Window? _window;

    public WindowTitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is null) return;

        MaximiseBounds.Keep(_window);

        // A window that cannot be resized has no business offering to maximise, and one that cannot
        // be minimised should not offer that either. Same rules the system caption follows.
        var resizable = _window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
        MaximiseButton.Visibility = resizable ? Visibility.Visible : Visibility.Collapsed;
        MinimiseButton.Visibility = _window.ResizeMode is ResizeMode.NoResize
            ? Visibility.Collapsed
            : Visibility.Visible;

        _window.StateChanged += (_, _) => UpdateMaximiseButton();
        UpdateMaximiseButton();
    }

    private void OnMinimise(object sender, RoutedEventArgs e)
    {
        if (_window is not null) _window.WindowState = WindowState.Minimized;
    }

    private void OnMaximiseOrRestore(object sender, RoutedEventArgs e)
    {
        if (_window is null) return;

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>
    /// Closes rather than cancels. A dialog that validates on the way out does that in its own
    /// Closing handler, which this goes through like any other close.
    /// </summary>
    private void OnClose(object sender, RoutedEventArgs e) => _window?.Close();

    private void UpdateMaximiseButton()
    {
        var maximised = _window?.WindowState == WindowState.Maximized;

        // Escaped rather than pasted: these are private-use codepoints in the system icon font.
        MaximiseButton.Content = maximised ? "\uE923" : "\uE922";
        MaximiseButton.ToolTip = maximised ? "Restore down" : "Maximise";
        AutomationProperties.SetName(MaximiseButton, maximised ? "Restore down" : "Maximise");
    }
}
