using System.Windows;
using Sirs.Core.Servers;

namespace Sirs.App;

/// <summary>
/// The first-run wizard (I2): input, sound check, server, done. Four steps is the whole of the
/// "three minutes to your first broadcast" promise, and every step is skippable so nobody is
/// trapped in it.
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly (string Title, string Subtitle)[] _steps =
    [
        ("Let us hear you", "First, check SIRS is listening to the right thing."),
        ("How do you sound?", "Record yourself and play it straight back."),
        ("Where does it go?", "Add the server your listeners will connect to."),
        ("You are ready", "That is the setup done."),
    ];

    private int _step;

    public SetupWizardWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, _steps.Length - 1);

        Step1.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        StepTitle.Text = _steps[_step].Title;
        StepSubtitle.Text = _steps[_step].Subtitle;
        StepCounter.Text = $"Step {_step + 1} of {_steps.Length}";

        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == _steps.Length - 1 ? "Start broadcasting" : "Next";
        SkipButton.Visibility = _step == _steps.Length - 1 ? Visibility.Collapsed : Visibility.Visible;

        // Leaving the sound check step should not leave audio playing behind it.
        if (_step != 1) _viewModel.StopSoundCheckPlayback();
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _viewModel.ReloadDevices();

    private void OnAddServer(object sender, RoutedEventArgs e)
    {
        var editor = new ServerEditorWindow(new ServerProfile()) { Owner = this };
        if (editor.ShowDialog() != true) return;

        _viewModel.AddOrUpdateServer(editor.Profile);
        ServerSkipHint.Text = "Saved. You can add more servers later from the main window.";
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == _steps.Length - 1)
        {
            Close();
            return;
        }

        ShowStep(_step + 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Close();
}
