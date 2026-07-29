using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Sirs.App;

public partial class LogWindow : Window
{
    private readonly MainViewModel _viewModel;

    public LogWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.LogEntries.CollectionChanged += OnEntriesChanged;
        Closed += (_, _) => viewModel.LogEntries.CollectionChanged -= OnEntriesChanged;

        Loaded += (_, _) => ScrollToEnd();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (FollowTail.IsChecked != true) return;

        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (_viewModel.LogEntries.Count == 0) return;
        EntryList.ScrollIntoView(_viewModel.LogEntries[^1]);
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.Log.ToText());
        }
        catch (Exception ex)
        {
            // The clipboard is occasionally locked by another process.
            MessageBox.Show($"SIRS could not copy the log: {ex.Message}", "SIRS");
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var directory = _viewModel.Log.Directory;
        if (directory is null || !Directory.Exists(directory))
        {
            MessageBox.Show("There is no log folder on this computer yet.", "SIRS");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SIRS could not open the log folder: {ex.Message}", "SIRS");
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
