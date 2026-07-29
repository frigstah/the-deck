using System.Windows;
using System.Windows.Controls;
using Deck.Core.Servers;

namespace Deck.App;

public partial class ServerEditorWindow : Window
{
    private readonly ServerEditorViewModel _viewModel;
    private bool _passwordVisible;
    private bool _syncingPassword;

    public ServerEditorWindow(ServerProfile profile)
    {
        InitializeComponent();

        _viewModel = new ServerEditorViewModel(profile);
        DataContext = _viewModel;

        PasswordField.Password = _viewModel.Password;
    }

    public ServerProfile Profile => _viewModel.Profile;

    private void OnApplyPaste(object sender, RoutedEventArgs e)
    {
        _viewModel.ApplyPaste();

        // The paste may have supplied a password, so mirror it back into the masked box.
        _syncingPassword = true;
        PasswordField.Password = _viewModel.Password;
        _syncingPassword = false;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        _viewModel.Password = ((PasswordBox)sender).Password;
    }

    /// <summary>
    /// Reveal is a real feature here, not a nicety: a mistyped broadcast password is the single
    /// most common reason a first connection fails.
    /// </summary>
    private void OnTogglePasswordVisibility(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;

        if (_passwordVisible)
        {
            PasswordPlain.Text = _viewModel.Password;
            PasswordPlain.Visibility = Visibility.Visible;
            PasswordField.Visibility = Visibility.Collapsed;
            ShowPasswordButton.Content = "Hide";
        }
        else
        {
            _syncingPassword = true;
            PasswordField.Password = _viewModel.Password;
            _syncingPassword = false;

            PasswordPlain.Visibility = Visibility.Collapsed;
            PasswordField.Visibility = Visibility.Visible;
            ShowPasswordButton.Content = "Show";
        }
    }

    private async void OnTest(object sender, RoutedEventArgs e) => await _viewModel.TestAsync();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var problems = _viewModel.Validate();
        if (problems.Count > 0)
        {
            MessageBox.Show(
                string.Join("\n\n", problems),
                "Not quite ready",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
