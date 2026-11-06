using System.Windows;
using System.Windows.Controls;
using ChatPulse.Desktop.ViewModels;

namespace ChatPulse.Desktop.Views.Onboarding;

public partial class OnboardingStepOneView : UserControl
{
    private readonly ProfileSetupViewModel _viewModel = new();

    public OnboardingStepOneView()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public event EventHandler? ContinueRequested;

    public event EventHandler? BackRequested;

    public string DisplayName => _viewModel.Name.Trim();

    public void FocusName()
    {
        Dispatcher.BeginInvoke(new Action(() => NameInput.Focus()));
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        // Возвращаемся на приветственный экран, не закрывая приложение.
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsNameValid)
        {
            return;
        }

        ContinueRequested?.Invoke(this, EventArgs.Empty);
    }
}
