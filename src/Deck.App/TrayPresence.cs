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
    private bool _disposed;

    /// <summary>
    /// What size the notification area actually wants: 16 at 100% scaling and larger above it. Asking
    /// Windows rather than assuming 16 is what keeps the icon crisp on a scaled display - at 150% the
    /// tray asks for 24, and a 16-pixel icon stretched to fill that is the blurry one everybody has
    /// seen. Snapped to a size that has a hand-set cut rather than scaling one that has not.
    /// </summary>
    private static int TrayIconSize
    {
        get
        {
            var wanted = Forms.SystemInformation.SmallIconSize.Width;
            return DeckMark.IconSizes.OrderBy(size => Math.Abs(size - wanted)).First();
        }
    }

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

    /// <summary>
    /// Refreshes the icon and tooltip. Cheap enough to call from the UI timer.
    /// <para>
    /// Does nothing once disposed, and that guard is load-bearing rather than tidiness. Closing Deck
    /// while it is on air disposes this and then stops the broadcast, and stopping changes the
    /// connection state one last time - which is raised to the UI through
    /// <see cref="System.Windows.Threading.Dispatcher.BeginInvoke(Delegate, object[])"/>, so it is
    /// queued and arrives after everything here has been torn down.
    /// </para>
    /// <para>
    /// <see cref="Forms.NotifyIcon"/> answers that badly. Its property setters do not throw
    /// <see cref="ObjectDisposedException"/>; Dispose nulls the hidden window it talks to Windows
    /// through, and the next assignment walks straight into it - so the whole program went down with
    /// "Object reference not set to an instance of an object" from inside WinForms, on the way out,
    /// with nothing on screen to connect it to the tray icon. Reported by a user; reproduced by going
    /// on air to a server that will not answer and closing the window while it reconnects.
    /// </para>
    /// </summary>
    public void Update()
    {
        if (_disposed) return;

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
    /// Draws the icon rather than shipping a set of .ico files, so the state colour and the shape stay
    /// in one place.
    /// <para>
    /// The mark takes the state colour whole, rather than lighting the lamp inside the counter. The
    /// counter is six pixels across at this size, and a lamp in it is a detail nobody sees from across
    /// a room - whereas the letter turning from grey to red is caught out of the corner of an eye,
    /// which is the entire job of a tray icon. The lamp belongs in the sizes that have room for it.
    /// </para>
    /// <para>
    /// No ground, unlike the application icon: the notification area is the one place Windows tells
    /// you what colour it is, so the mark can sit on it directly, and a coloured tile there would look
    /// like a badge rather than a status light.
    /// </para>
    /// </summary>
    private static Drawing.Icon BuildIcon(Drawing.Color colour)
    {
        using var bitmap = DeckMark.Render(TrayIconSize, colour);

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
        if (_disposed) return;

        // Set before anything is torn down, not after: a queued update arriving part-way through
        // would otherwise find a half-disposed icon, which is the fault this guards against.
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }
}
