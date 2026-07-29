using System.Runtime.InteropServices;
using System.Windows;
using Deck.Core.Streaming;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Deck.App;

/// <summary>
/// Keeps Deck in the notification area while it is minimised (I4), with the icon colour showing
/// whether you are on air. A broadcaster who has tucked the window away still needs to be able to
/// tell at a glance, and to get out of trouble without hunting for the window.
/// </summary>
public sealed class TrayPresence : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Window _window;
    private readonly Func<StreamState> _stateProvider;
    private readonly Action _toggleBroadcast;

    private Drawing.Icon? _currentIcon;
    private StreamState _lastState = (StreamState)(-1);

    public TrayPresence(Window window, Func<StreamState> stateProvider, Action toggleBroadcast)
    {
        _window = window;
        _stateProvider = stateProvider;
        _toggleBroadcast = toggleBroadcast;

        _notifyIcon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "The Deck",
        };

        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Deck", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Go live / Stop", null, (_, _) => _toggleBroadcast());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _window.Close());
        _notifyIcon.ContextMenuStrip = menu;

        Update();
    }

    /// <summary>Refreshes the icon and tooltip. Cheap enough to call from the UI timer.</summary>
    public void Update()
    {
        var state = _stateProvider();
        if (state == _lastState) return;

        _lastState = state;
        _notifyIcon.Text = Truncate($"Deck — {state.Headline()}");

        var previous = _currentIcon;
        _currentIcon = BuildIcon(ColourFor(state));
        _notifyIcon.Icon = _currentIcon;

        // The icon must stay assigned until after the swap, or the tray briefly shows nothing.
        previous?.Dispose();
    }

    /// <summary>Notification-area tooltips are capped at 63 characters.</summary>
    private static string Truncate(string text) => text.Length <= 63 ? text : text[..63];

    private static Drawing.Color ColourFor(StreamState state) => state switch
    {
        StreamState.Live => Drawing.Color.FromArgb(255, 208, 27, 27),
        StreamState.Connecting or StreamState.Reconnecting => Drawing.Color.FromArgb(255, 224, 163, 46),
        StreamState.Failed => Drawing.Color.FromArgb(255, 150, 40, 40),
        _ => Drawing.Color.FromArgb(255, 130, 130, 145),
    };

    /// <summary>
    /// Draws the icon rather than shipping a set of .ico files, so the state colour and the shape
    /// stay in one place.
    /// </summary>
    private static Drawing.Icon BuildIcon(Drawing.Color colour)
    {
        using var bitmap = new Drawing.Bitmap(16, 16);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);

            using var brush = new Drawing.SolidBrush(colour);
            graphics.FillEllipse(brush, 2, 2, 12, 12);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon survives destroying the handle we just created.
            using var temporary = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }
}
