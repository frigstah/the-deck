using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Sirs.Core.Control;

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

        // Before anything is created. A command line is a message for the copy already running, so
        // this one sends it and leaves without ever touching an audio device or opening a window.
        if (CommandLine.Parse(e.Args) is { } request)
        {
            RunCommandAndExit(request);
            return;
        }

        ApplySystemTheme();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"SIRS ran into an unexpected problem:\n\n{args.Exception.Message}",
                "SIRS",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            args.Handled = true;
        };

        // Explicit rather than StartupUri, so the command-line path above can exit before a window
        // is ever built. StartupUri would create one regardless of what OnStartup decided.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
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

    /// <summary>
    /// Follows the Windows light/dark setting (I5). The palette keys are declared in Theme.xaml and
    /// referenced with DynamicResource, so overwriting them here re-styles everything already built.
    /// </summary>
    private void ApplySystemTheme()
    {
        if (!IsSystemDark()) return;

        Set("BackgroundColor", "#FF14141A");
        Set("SurfaceColor", "#FF1E1E26");
        Set("BorderColor", "#FF33333F");
        Set("TextColor", "#FFF2F2F7");
        Set("MutedTextColor", "#FFA0A0B0");
        Set("AccentColor", "#FF4C8DFF");
        Set("OkColor", "#FF3DD07E");
        Set("WarnColor", "#FFE8A33D");
        Set("BadColor", "#FFFF6B6B");
        Set("LiveColor", "#FFFF4444");
    }

    private void Set(string key, string hex) =>
        Resources[key] = (Color)ColorConverter.ConvertFromString(hex)!;

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
