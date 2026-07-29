using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using Microsoft.Win32;
using Sirs.Core;
using Sirs.Core.Control;
using Sirs.Core.Updates;

namespace Sirs.App;

public partial class App : Application
{
    /// <summary>Borrows the terminal SIRS was launched from, so --status has somewhere to print.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    private const uint AttachParentProcess = 0xFFFFFFFF;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // First of all: this copy may have been started by the previous one purely to replace it.
        // Nothing else must happen on that path - no window, no audio device, no settings written.
        if (UpdateApplier.Parse(e.Args) is { } update)
        {
            ApplyUpdateAndExit(update);
            return;
        }

        // Before anything is created. A command line is a message for the copy already running, so
        // this one sends it and leaves without ever touching an audio device or opening a window.
        if (CommandLine.Parse(e.Args) is { } request)
        {
            RunCommandAndExit(request);
            return;
        }

        // The stored choice has to be known before the first window is built, so the settings are
        // read here rather than waiting for the view model to load them. Reading the file twice
        // costs nothing; showing the wrong palette for a moment and then correcting it is a flash
        // the user would see.
        _preference = new SettingsStore().Load().Theme;
        ApplySystemTheme();

        // Windows can be switched between light and dark while SIRS is running, and a broadcaster
        // who does it at dusk should not have to restart the encoder to see it. The palette is all
        // DynamicResource, so re-applying it repaints the windows that are already open. Ignored
        // while the user has chosen light or dark outright.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        // Whatever the last update left in the staging folder — several hundred megabytes of it.
        UpdateInstaller.Cleanup();

        DispatcherUnhandledException += OnUnhandledException;

        // Explicit rather than StartupUri, so the command-line path above can exit before a window
        // is ever built. StartupUri would create one regardless of what OnStartup decided.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Replaces the previous install with this one and starts it, then quits (I9).
    /// <para>
    /// Only reports failure. On success the copy in the install folder is already starting, and two
    /// SIRS windows appearing at once would be worse than none.
    /// </para>
    /// </summary>
    private void ApplyUpdateAndExit(UpdateApplier.Request request)
    {
        var problem = UpdateApplier.Apply(request);

        if (problem is not null)
        {
            MessageBox.Show(problem, "SIRS — update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Shutdown(problem is null ? 0 : 1);
    }

    private bool _reportingCrash;
    private int _crashCount;

    /// <summary>
    /// Last resort for an exception that reached the dispatcher.
    /// <para>
    /// The first version of this simply showed a message box. That turned out to be a way of
    /// converting one recoverable fault into a hard crash: a modal box pumps the dispatcher, so a
    /// fault that recurs during layout or render raises again <em>inside</em> the box, which shows
    /// another box, forty levels deep until the stack overflows. The process then dies with no
    /// message at all — the opposite of what the handler was for.
    /// </para>
    /// <para>
    /// So: re-entrancy is refused, every exception goes to a file whether or not anyone is looking,
    /// and the user is told once. A stream that is on air keeps running, because <see
    /// cref="DispatcherUnhandledExceptionEventArgs.Handled"/> stays true.
    /// </para>
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;

        // Raised again from inside the message box below. Swallow it: reporting it would recurse.
        if (_reportingCrash) return;

        _reportingCrash = true;

        try
        {
            _crashCount++;
            Log(args.Exception);

            // Told once. A fault that repeats every render would otherwise make the program
            // unusable through sheer volume of dialogs.
            if (_crashCount == 1)
            {
                MessageBox.Show(
                    $"SIRS ran into an unexpected problem:\n\n{args.Exception.Message}\n\n" +
                    "Details have been written to the logs folder. If you are on air, you still are.",
                    "SIRS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception)
        {
            // Nothing useful is left to do; never throw out of the handler of last resort.
        }
        finally
        {
            _reportingCrash = false;
        }
    }

    private static void Log(Exception exception)
    {
        try
        {
            var path = Path.Combine(Sirs.Core.AppPaths.LogDirectory, "crash.log");
            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {exception.GetType().Name}: {exception.Message}\n" +
                $"{exception.StackTrace}\n\n");
        }
        catch (Exception)
        {
            // A crash we cannot write down is still a crash we survived.
        }
    }

    /// <summary>
    /// Answers a command line and quits (I10).
    /// <para>
    /// SIRS is a windowed program, so it has no console of its own; it attaches to the one it was
    /// launched from. When there is not one - a shortcut, a scheduled task - the write simply goes
    /// nowhere, and the exit code carries the answer instead. That is why the code is set from the
    /// result rather than always zero: a script needs to be able to tell that --live did not work.
    /// </para>
    /// </summary>
    private void RunCommandAndExit(CommandLineRequest request)
    {
        var reply = ControlClient.SendAsync(request).GetAwaiter().GetResult();

        AttachConsole(AttachParentProcess);

        try
        {
            // Titles are full of accents and dashes, and a console left on the machine's ancient
            // code page turns "Sigur Rós" into "Sigur R?s" on its way to a log.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception)
        {
            // No console, or one that will not take it. Not worth failing the command over.
        }

        try
        {
            Console.WriteLine();
            Console.WriteLine(reply.Text);
        }
        catch (IOException)
        {
            // No console to attach to. The exit code still reports the outcome.
        }

        Shutdown(reply.Ok ? 0 : 1);
    }

    /// <summary>Where Theme.xaml came from, taken from the dictionary App.xaml already merged.</summary>
    private Uri? _paletteSource;

    private bool? _appliedDark;

    private static AppTheme _preference = AppTheme.System;

    /// <summary>
    /// Changes the palette and keeps it changed. Called from the SIRS pane; the caller is
    /// responsible for storing the choice, since this class has no business writing settings.
    /// </summary>
    public static void UseTheme(AppTheme theme)
    {
        _preference = theme;
        (Current as App)?.ApplySystemTheme();
    }

    /// <summary>
    /// Follows the Windows light/dark setting (I5), at any time rather than only at startup.
    /// <para>
    /// The palette is applied by loading Theme.xaml afresh, writing the dark colours into that copy
    /// while nothing is using it yet, and swapping it in for the one already merged. The obvious
    /// alternative - writing colours into <c>Application.Resources</c> so the DynamicResource
    /// references pick them up - does not work, and the way it fails is worth recording. Theme.xaml
    /// declares its brushes as <c>SolidColorBrush Color="{DynamicResource BackgroundColor}"</c>, and
    /// a brush living inside a resource dictionary resolves that reference once, when the dictionary
    /// realises it. Overwriting the colour afterwards leaves every already-realised brush pointing
    /// at the old value. It appeared to work for years only because at startup nothing had realised
    /// the brushes yet, so they were built against whatever had just been written - which is exactly
    /// why the theme used to need a restart to change.
    /// </para>
    /// <para>
    /// Designed rather than inverted. A naive inversion of the light palette gives a muddy accent
    /// and unreadable soft fills: the petrol teal has to come up in lightness to hold against a dark
    /// ground, and the pill backgrounds have to go to deep tints of their own hue rather than to
    /// pale ones darkened. The rail goes <em>darker</em> than the window rather than lighter, so it
    /// still reads as a rail instead of merging with the pane beside it.
    /// </para>
    /// </summary>
    private void ApplySystemTheme()
    {
        var dark = _preference switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => IsSystemDark(),
        };

        // Windows raises its preference-changed event for a great many things that are not the
        // theme, and rebuilding the palette on each of them would be wasteful and visible. This also
        // makes the system's changes free to ignore while the user has chosen a palette outright.
        if (_appliedDark == dark) return;

        _paletteSource ??= Resources.MergedDictionaries.FirstOrDefault()?.Source;
        if (_paletteSource is null) return;

        _appliedDark = dark;

        // A fresh copy is the light palette by definition, so light needs no code of its own and the
        // two themes cannot drift apart.
        var palette = new ResourceDictionary { Source = _paletteSource };
        if (dark) ApplyDarkPalette(palette);

        Resources.MergedDictionaries[0] = palette;
    }

    private static void ApplyDarkPalette(ResourceDictionary palette)
    {
        Set("BackgroundColor", "#FF15181B");
        Set("SurfaceColor", "#FF1C2024");
        Set("BorderColor", "#FF2C3238");
        Set("TextColor", "#FFE7E9E7");
        Set("MutedTextColor", "#FF939BA2");
        Set("AccentColor", "#FF5FB6B4");
        Set("OnAccentColor", "#FF10211F");
        Set("OkColor", "#FF57C295");
        Set("WarnColor", "#FFDFA84A");
        Set("BadColor", "#FFE8574C");
        Set("LiveColor", "#FFE8574C");

        Set("OkSoftColor", "#FF17352A");
        Set("WarnSoftColor", "#FF33290F");
        Set("BadSoftColor", "#FF3A1F1D");
        Set("NeutralSoftColor", "#FF23282C");

        // Darker than the window, not lighter. On a dark theme a lighter rail would read as a
        // raised panel; darker keeps it reading as the edge of the window.
        Set("RailColor", "#FF0F1215");
        Set("RailSelectedColor", "#FF1A2025");
        Set("RailTextColor", "#FF79828A");
        Set("StatusBarColor", "#FF101317");

        // Caption hover: a light wash on a dark window, where the light theme uses a dark one.
        // Black at 9% over #15181B is very nearly nothing at all.
        Set("CaptionHoverColor", "#20FFFFFF");
        Set("CaptionPressedColor", "#38FFFFFF");

        // Lit segments stay close to their light-theme hues - a green meter is a green meter - but
        // the unlit ones become deep tints instead of pale ones, or the whole scale would glow.
        Set("MeterQuietColor", "#FF6E7A78");
        Set("MeterGoodColor", "#FF3F9E76");
        Set("MeterLoudColor", "#FFD7A64A");
        Set("MeterClipColor", "#FFDD5A4F");
        Set("MeterQuietOffColor", "#FF262B2E");
        Set("MeterGoodOffColor", "#FF1E332B");
        Set("MeterLoudOffColor", "#FF332C1C");
        Set("MeterClipOffColor", "#FF351F1D");

        void Set(string key, string hex) =>
            palette[key] = (Color)ColorConverter.ConvertFromString(hex)!;
    }

    /// <summary>
    /// Raised on a background thread, and for every kind of preference rather than just this one -
    /// so it hops to the UI thread and lets <see cref="ApplySystemTheme"/> decide whether anything
    /// actually changed. Which category carries a theme change has moved between Windows versions;
    /// re-reading the setting is cheap and does not have to be kept up to date with that.
    /// </summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ApplySystemTheme);

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
