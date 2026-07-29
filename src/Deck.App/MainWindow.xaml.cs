using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
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

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
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
    }

    /// <summary>Minimising hides the window rather than leaving it on the taskbar (I4).</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximiseButton();

        if (WindowState != WindowState.Minimized || !_viewModel.MinimiseToTray) return;
        Hide();
    }

    // ---------------------------------------------------------------- the window's own title bar

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MaximiseBounds.Keep(this);
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
        _tray?.Dispose();
        _viewModel.Dispose();

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
            FileName = "sirs-servers.json",
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

    private void OnImportServers(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a shared server settings file",
            Filter = "Deck server settings (*.json)|*.json|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var added = _viewModel.ImportServers(File.ReadAllText(dialog.FileName));

            if (added == 0)
            {
                MessageBox.Show("That file did not contain any servers.", "The Deck", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var missing = _viewModel.ServersMissingPassword;
            var passwordNote = missing > 0
                ? $"\n\n{missing} server(s) still need a password. Open each one with Edit and type it in."
                : string.Empty;

            MessageBox.Show($"Added {added} server(s).{passwordNote}", "The Deck", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Deck could not read that file. It may not be a Deck settings file.\n\n{ex.Message}",
                "The Deck");
        }
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
