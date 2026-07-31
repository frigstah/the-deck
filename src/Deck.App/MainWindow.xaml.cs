using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Deck.Core.Servers;
using Deck.Core.Streaming;

namespace Deck.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly GlobalHotkeys _hotkeys = new();
    private LogWindow? _logWindow;
    private TrayPresence? _tray;

    /// <summary>
    /// The deck's own limits and caption, read from the window rather than repeated here. Mini mode
    /// replaces all three and has to be able to put them back exactly; a second copy of the numbers
    /// would be a second place to change them.
    /// </summary>
    private readonly double _deckMinWidth;
    private readonly double _deckMinHeight;
    private readonly double _deckCaptionHeight;
    private readonly WindowChrome _chrome;

    public MainWindow()
    {
        InitializeComponent();

        _deckMinWidth = MinWidth;
        _deckMinHeight = MinHeight;
        _chrome = WindowChrome.GetWindowChrome(this);
        _deckCaptionHeight = _chrome.CaptionHeight;

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSetupOpen)) SlideSetup(_viewModel.IsSetupOpen);
    }

    /// <summary>How long setup takes to arrive or leave. Long enough to read as movement, short
    /// enough that someone reaching for a setting mid-show is not waiting on it.</summary>
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(220));

    /// <summary>
    /// Slides the setup panel up over the deck, and back down again.
    /// <para>
    /// Done here rather than as a storyboard in XAML for one reason: the distance is the panel's own
    /// <see cref="FrameworkElement.ActualHeight"/>, which is only known at the moment it moves and
    /// changes whenever the window is resized. A storyboard would need a fixed number, which is
    /// either too small on a tall window - leaving setup half on screen when it should be gone - or
    /// needlessly far on a short one.
    /// </para>
    /// <para>
    /// Visibility is set here too, not bound. Bound, it would snap the panel away the instant the
    /// flag changed and the slide out would never be seen.
    /// </para>
    /// </summary>
    private void SlideSetup(bool open)
    {
        // Follow Windows by default: someone who turned animations off there has said what they want,
        // and a 220ms slide is exactly the kind of thing they turned off. But it is only the default -
        // that setting gets turned off for an old machine's sake, or by an IT policy, or by somebody who
        // never knew it existed, and none of those people have said anything about this slide. So Deck
        // takes an answer of its own if it has been given one.
        var animate = _viewModel.Settings.SetupMotion switch
        {
            Core.SetupMotion.Always => true,
            Core.SetupMotion.Never => false,
            _ => SystemParameters.ClientAreaAnimation,
        };

        if (!animate)
        {
            SetupOffset.BeginAnimation(TranslateTransform.YProperty, null);
            SetupOffset.Y = 0;
            SetupPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var distance = SetupPanel.ActualHeight > 0 ? SetupPanel.ActualHeight : ActualHeight;

        var slide = new DoubleAnimation
        {
            Duration = SlideDuration,
            // Decelerating on the way in and accelerating on the way out, so it settles rather than
            // stops dead, and leaves rather than vanishes.
            EasingFunction = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn },
            From = open ? distance : 0,
            To = open ? 0 : distance,
        };

        if (open)
        {
            // Visible before it moves, or the first frame of the slide is invisible.
            SetupPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // Collapsed only once it is off screen, so the deck underneath does not take the clicks
            // while the panel is still covering it.
            void Finished(object? s, EventArgs args)
            {
                slide.Completed -= Finished;
                if (!_viewModel.IsSetupOpen) SetupPanel.Visibility = Visibility.Collapsed;
            }

            slide.Completed += Finished;
        }

        SetupOffset.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Settings.MonitorEnabled) _viewModel.MonitorEnabled = true;

        _tray = new TrayPresence(this, () => _viewModel.StreamState, () => _viewModel.ToggleBroadcast());

        _hotkeys.Attach(this, () => _viewModel.ToggleBroadcast(), () => _viewModel.ToggleMute());
        if (_hotkeys.Problem is { } hotkeyProblem) _viewModel.StatusMessage = hotkeyProblem;

        // Drive the tray from state changes rather than a timer: the icon only ever changes when
        // the connection state does.
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StreamState)) _tray?.Update();
        };

        // An update has been downloaded, checked and staged, and the replacement is already waiting
        // for this process to exit. Close normally so settings and servers are saved on the way out.
        _viewModel.UpdateRequested += (_, _) => Close();

        // First run: walk the user through input, sound check and a server before they see the
        // full window (I2).
        if (_viewModel.NeedsSetup)
        {
            RunSetupWizard();
            return;
        }

        // Auto-connect (H6) only after setup, and only when there is something valid to connect to.
        if (_viewModel is { AutoConnectOnStart: true, SelectedServer: not null } &&
            _viewModel.SelectedServer.Validate().Count == 0)
        {
            _viewModel.ToggleBroadcast();
        }

        // Come back as whatever Deck was left as. Last, so the placement it remembers is the deck's
        // real size on this screen rather than the size in the XAML.
        if (_viewModel.Settings.MiniMode) SetMiniMode(true);
    }

    /// <summary>
    /// Minimising hides the window rather than leaving it on the taskbar, if asked to (I4). Off by
    /// default - see <see cref="Deck.Core.AppSettings.MinimiseToTray"/> for why that changed.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximiseButton();

        if (WindowState != WindowState.Minimized || !_viewModel.MinimiseToTray) return;
        Hide();
    }

    // ---------------------------------------------------------------- mini mode

    /// <summary>
    /// The strip's height, and it is fixed rather than a minimum. There is one row of content on it;
    /// a taller strip would be the same row with space above and below, which is what the deck is
    /// for. The width is free, and starts narrow so pressing Mini visibly produces a strip.
    /// </summary>
    private const double MiniHeight = 56;

    /// <summary>
    /// Wide enough that the meter is still a meter. The controls and the state block take a fixed
    /// amount of the row and the meter gets what is left, so a narrower strip does not make Deck
    /// smaller - it makes the one thing on the strip worth watching too small to read.
    /// <para>
    /// Everything on this row is fixed width except the meter, so anything added to the strip comes out
    /// of the meter unless the strip grows with it. It went up by sixty for the mark, which is what the
    /// mark and its gap take, and by a hundred and twenty for the listener count in the state block -
    /// "ON AIR WITH 999 LISTENERS" is a hundred and fourteen pixels wider than "OFF AIR", and the block
    /// reserves the widest case so it does not resize during a show.
    /// </para>
    /// <para>
    /// This is only the width the strip starts at. The width is free afterwards, so anyone who wants a
    /// shorter strip can have one and the meter shrinks rather than anything being cut off.
    /// </para>
    /// </summary>
    private const double MiniWidth = 1040;

    /// <summary>Where the deck was, so the strip can put it back rather than guess at it.</summary>
    private Rect _deckPlacement = Rect.Empty;

    private bool _deckWasMaximised;

    private void OnEnterMiniMode(object sender, RoutedEventArgs e) => SetMiniMode(true);

    private void OnLeaveMiniMode(object sender, RoutedEventArgs e) => SetMiniMode(false);

    private void SetMiniMode(bool mini)
    {
        if (_viewModel.IsMiniMode == mini) return;

        if (mini) EnterMiniMode();
        else LeaveMiniMode();
    }

    private void EnterMiniMode()
    {
        _deckWasMaximised = WindowState == WindowState.Maximized;

        // Come out of maximised before reading the placement, or what gets remembered is the whole
        // screen and the deck returns filling it when it was not filling it before.
        if (_deckWasMaximised) WindowState = WindowState.Normal;

        _deckPlacement = new Rect(
            double.IsNaN(Left) ? 0 : Left,
            double.IsNaN(Top) ? 0 : Top,
            Width,
            Height);

        _viewModel.IsMiniMode = true;

        // Setup cannot be open here - the only way in is a button on the deck - but if that ever
        // changes, the panel has to go immediately rather than sliding for 220ms over a window that
        // is about to be 56 pixels tall.
        SetupOffset.BeginAnimation(TranslateTransform.YProperty, null);
        SetupOffset.Y = 0;
        SetupPanel.Visibility = Visibility.Collapsed;

        MinHeight = MiniHeight;
        MaxHeight = MiniHeight;
        MinWidth = MiniWidth;
        Height = MiniHeight;
        Width = MiniWidth;

        // The strip is the title bar now. Short of the bottom edge, so there is still a border to
        // drag when the strip is the whole window.
        _chrome.CaptionHeight = Math.Max(0, MiniHeight - _chrome.ResizeBorderThickness.Bottom);

        // The reason to want a strip at all: it stays where you can see it while the screen belongs
        // to whatever is playing the music.
        Topmost = true;
    }

    private void LeaveMiniMode()
    {
        _viewModel.IsMiniMode = false;

        Topmost = false;
        _chrome.CaptionHeight = _deckCaptionHeight;

        // Order matters. The ceiling has to come off before the height can grow past it, and the
        // deck's own floor has to be back before its size is asked for.
        MaxHeight = double.PositiveInfinity;
        MinWidth = _deckMinWidth;
        MinHeight = _deckMinHeight;

        if (_deckPlacement.IsEmpty)
        {
            // Deck started as a strip and has never had a deck-sized placement to remember.
            Width = _deckMinWidth;
            Height = _deckMinHeight;
        }
        else
        {
            Left = _deckPlacement.X;
            Top = _deckPlacement.Y;
            Width = _deckPlacement.Width;
            Height = _deckPlacement.Height;
        }

        if (_deckWasMaximised) WindowState = WindowState.Maximized;
        _deckWasMaximised = false;
    }

    // ---------------------------------------------------------------- the window's own title bar

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MaximiseBounds.Keep(this);

        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    private const int WmSysCommand = 0x0112;
    private const int ScMaximise = 0xF030;

    /// <summary>
    /// Turns "maximise" into "give me the deck back" while Deck is a strip.
    /// <para>
    /// Double-clicking the caption is a maximise, and the strip is the caption - so the gesture
    /// arrives whether or not it makes sense, and asking a 56-pixel strip to fill the screen is only
    /// ever a way of asking for the deck. Answered here rather than in StateChanged, because letting
    /// the maximise happen and undoing it afterwards moves the window to the maximised origin first
    /// and the deck then comes back eight pixels off the corner of the screen. Measured, not
    /// theorised: that is exactly what it did.
    /// </para>
    /// <para>
    /// Only SC_MAXIMIZE. SC_RESTORE arrives when coming back from the notification area, and
    /// swallowing that would strand the window.
    /// </para>
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmSysCommand || !_viewModel.IsMiniMode) return IntPtr.Zero;
        if ((wParam.ToInt32() & 0xFFF0) != ScMaximise) return IntPtr.Zero;

        handled = true;
        SetMiniMode(false);
        return IntPtr.Zero;
    }

    private void OnMinimiseWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseOrRestoreWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// The middle button is two buttons wearing one coat, so its glyph, its tooltip and the name a
    /// screen reader announces all have to follow the window rather than being set once. The system
    /// title bar did this for free; drawing our own means doing it ourselves.
    /// </summary>
    private void UpdateMaximiseButton()
    {
        var maximised = WindowState == WindowState.Maximized;

        MaximiseButton.Content = maximised ? "\uE923" : "\uE922";
        MaximiseButton.ToolTip = maximised ? "Restore down" : "Maximise";
        AutomationProperties.SetName(MaximiseButton, maximised ? "Restore down" : "Maximise");
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.StreamState.IsBroadcasting())
        {
            var answer = MessageBox.Show(
                "You are still on air. Close Deck and end the broadcast?",
                "The Deck",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _hotkeys.Dispose();

        // The view model before the tray. Disposing the view model stops the broadcast, and stopping
        // it changes the connection state one last time - which drives the tray icon. With the tray
        // gone first, that final update had nothing left to talk to. TrayPresence refuses updates
        // after disposal now, so this is no longer what stands between Deck and a crash on the way
        // out; it is here so the icon is still correct while the broadcast is being wound down,
        // rather than merely surviving being asked.
        _viewModel.Dispose();
        _tray?.Dispose();

        // The tray keeps the process alive once the window is hidden, so shut down explicitly.
        System.Windows.Application.Current.Shutdown();
    }

    private void RunSetupWizard()
    {
        var wizard = new SetupWizardWindow(_viewModel) { Owner = this };
        wizard.ShowDialog();
        _viewModel.MarkSetupCompleted();
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _viewModel.ReloadDevices();

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        // Non-modal: a log you have to close before touching anything is useless while on air.
        if (_logWindow is { IsVisible: true })
        {
            _logWindow.Activate();
            return;
        }

        _logWindow = new LogWindow(_viewModel) { Owner = this };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    private void OnToggleBroadcast(object sender, RoutedEventArgs e) => _viewModel.ToggleBroadcast();

    private void OnToggleRecording(object sender, RoutedEventArgs e) => _viewModel.ToggleRecording();

    private void OnAddServer(object sender, RoutedEventArgs e)
    {
        var editor = new ServerEditorWindow(new ServerProfile()) { Owner = this };
        if (editor.ShowDialog() == true) _viewModel.AddOrUpdateServer(editor.Profile);
    }

    private void OnEditServer(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            MessageBox.Show("Choose a server to edit first.", "The Deck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Edit a copy so cancelling really does leave the saved server untouched.
        var working = selected.Clone();
        working.Id = selected.Id;

        var editor = new ServerEditorWindow(working) { Owner = this };
        if (editor.ShowDialog() == true) _viewModel.AddOrUpdateServer(editor.Profile);
    }

    private void OnDuplicateServer(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected) return;

        var copy = selected.Clone();
        copy.Name = $"{selected.Name} (copy)";
        _viewModel.AddOrUpdateServer(copy);
    }

    private void OnDeleteServer(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected) return;

        var answer = MessageBox.Show(
            $"Remove \"{selected.Name}\"?",
            "The Deck",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes) _viewModel.RemoveServer(selected);
    }

    /// <summary>
    /// Writes the whole English catalogue out as a file a translator can work through (I8). It is
    /// saved into the languages folder by default, so a finished translation is already installed.
    /// </summary>
    private void OnExportLanguage(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the English text for translating",
            InitialDirectory = Deck.Core.Localisation.Strings.Directory,
            FileName = "my-language.json",
            Filter = "Deck language file (*.json)|*.json|All files (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var code = Path.GetFileNameWithoutExtension(dialog.FileName);
            File.WriteAllText(dialog.FileName, _viewModel.ExportLanguageTemplate(code, code));

            MessageBox.Show(
                $"Saved to {Path.GetFileName(dialog.FileName)}.\n\n" +
                "Change the \"name\" to the language's own name, then translate the right-hand side " +
                "of each line. Leave anything you have not done yet — Deck falls back to English.\n\n" +
                $"Put the finished file in {Deck.Core.Localisation.Strings.Directory} and restart Deck.",
                "The Deck",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Deck could not save that file: {ex.Message}", "The Deck");
        }
    }

    private void OnExportServers(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Servers.Count == 0)
        {
            MessageBox.Show("There are no servers to share yet.", "The Deck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save your server settings",
            FileName = "deck-servers.json",
            Filter = "Deck server settings (*.json)|*.json|All files (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, _viewModel.ExportServers());

            // Passwords are encrypted against this Windows account, so they cannot travel. Saying
            // so here avoids the other DJ wondering why their connection is rejected.
            MessageBox.Show(
                $"Saved {_viewModel.Servers.Count} server(s) to {Path.GetFileName(dialog.FileName)}.\n\n" +
                "Passwords are not included — they are encrypted for your Windows account only. " +
                "Whoever imports this will need to type the password in once.",
                "The Deck",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Deck could not save that file: {ex.Message}", "The Deck");
        }
    }

    /// <summary>
    /// One button for both kinds of file. Deck's own share file and a BUTT configuration are told
    /// apart by looking at what was opened rather than by asking the user to know which is which -
    /// somebody arriving from BUTT with a config their host emailed them should not have to work out
    /// that it counts as a different sort of import.
    /// </summary>
    private void OnImportServers(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a server settings file",
            Filter =
                "Server settings from Deck or BUTT|*.json;*.cfg;*.conf;*.txt|" +
                "Deck server settings (*.json)|*.json|" +
                "BUTT configuration (*.cfg;*.conf;*.txt)|*.cfg;*.conf;*.txt|" +
                "All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var text = File.ReadAllText(dialog.FileName);

            if (ButtImport.Looks(text)) ImportFromButt(text);
            else ImportSharedServers(text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Deck could not read that file. It may not be a server settings file.\n\n{ex.Message}",
                "The Deck");
        }
    }

    private void ImportSharedServers(string text)
    {
        var added = _viewModel.ImportServers(text);

        if (added == 0)
        {
            MessageBox.Show("That file did not contain any servers.", "The Deck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show($"Added {added} server(s).{PasswordNote()}", "The Deck", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ImportFromButt(string text)
    {
        var (added, result) = _viewModel.ImportFromButt(text);

        if (added == 0)
        {
            MessageBox.Show(
                "That looks like a BUTT configuration, but there were no servers in it.",
                "The Deck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var message = $"Added {added} server(s) from BUTT.";

        // Said out loud rather than left to be discovered. A SHOUTcast entry in BUTT does not record
        // which SHOUTcast it is, so those servers arrive undecided - which is a normal state for
        // Deck and an odd one to meet without explanation.
        message += "\n\nBUTT stores your passwords in plain text and Deck does not, so they were " +
                   "protected on the way in. Anything BUTT recorded as SHOUTcast will work out which " +
                   "kind it is the first time you press Test or go on air.";

        if (result.Skipped.Count > 0)
        {
            message += $"\n\nSkipped {result.Skipped.Count} entr(y/ies) with no address: " +
                       string.Join(", ", result.Skipped.Take(6)) +
                       (result.Skipped.Count > 6 ? "…" : string.Empty);
        }

        if (result.Duplicates.Count > 0)
        {
            message += $"\n\n{result.Duplicates.Count} of them point at the same place as another " +
                       "under a different name. They were all imported — delete the ones you do not want.";
        }

        message += PasswordNote();

        MessageBox.Show(message, "The Deck", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string PasswordNote()
    {
        var missing = _viewModel.ServersMissingPassword;

        return missing > 0
            ? $"\n\n{missing} server(s) still need a password. Open each one with Edit and type it in."
            : string.Empty;
    }

    private void OnListen(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ListenUrl is not { } url) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Deck could not open your stream: {ex.Message}", "The Deck");
        }
    }

    private void OnNowPlayingKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _viewModel.UpdateNowPlayingCommand.Execute(null);
    }

    /// <summary>
    /// Opens the deck footer's own title box, on the "Set" chip or on the title itself.
    /// <para>
    /// Focus has to wait for a layout pass. The box comes in through a visibility binding, and until
    /// the binding has been evaluated and the box arranged there is nothing there to focus - calling
    /// Focus() in this handler simply returns false and the user is left typing into nothing.
    /// </para>
    /// </summary>
    private void OnEditNowPlayingTitle(object sender, RoutedEventArgs e)
    {
        _viewModel.BeginEditNowPlaying();

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            NowPlayingQuickBox.Focus();

            // Selected rather than at the end: the common edit is a new show name over the old one.
            NowPlayingQuickBox.SelectAll();
        }));
    }

    private void OnQuickTitleKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.CommitEditNowPlaying();
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.CancelEditNowPlaying();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Clicking away closes the box and leaves the title alone. Deliberate: what this box sends goes
    /// straight out to listeners, so it takes a keypress to send it and never a stray click.
    /// </summary>
    private void OnQuickTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        _viewModel.CancelEditNowPlaying();

    private void OnChooseMetadataFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the file your playout software updates",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == true) _viewModel.UseMetadataFile(dialog.FileName);
    }

    private void OnUseManualMetadata(object sender, RoutedEventArgs e) => _viewModel.UseManualMetadata();

    private async void OnUseMediaSession(object sender, RoutedEventArgs e) => await _viewModel.UseMediaSessionAsync();

    private void OnChooseRecordingFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Where should recordings go?",
            InitialDirectory = _viewModel.RecordingFolder,
        };

        if (dialog.ShowDialog(this) == true) _viewModel.RecordingFolder = dialog.FolderName;
    }

    private void OnOpenRecordingFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_viewModel.RecordingFolder);
            Process.Start(new ProcessStartInfo(_viewModel.RecordingFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Deck could not open that folder: {ex.Message}", "The Deck");
        }
    }
}
